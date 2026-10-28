using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace HoliestFluffiness;

// Bare-bones WinRT access through raw vtable slots. The Dalamud SDK targets plain net10.0-windows,
// so the Windows.* projections are not available without shipping the CsWinRT runtime alongside us.
// Slot numbers are the method order in each interface's IDL, offset by the 6 IInspectable slots.
internal static unsafe class WinRt
{
    private static readonly Guid AsyncInfoId = new("00000036-0000-0000-c000-000000000046");

    private const int ReleaseSlot              = 2;
    private const int QueryInterfaceSlot       = 0;
    private const int AsyncInfoStatusSlot      = 7;
    private const int AsyncInfoCancelSlot      = 9;
    private const int AsyncOperationResultSlot = 8;
    private const int ChangedModeError         = unchecked((int)0x80010106);

    internal static bool Succeeded(int hr) => hr >= 0;

    internal static bool EnterMta()
    {
        var hr = RoInitialize(1);
        return hr != ChangedModeError && Succeeded(hr);
    }

    internal static void LeaveMta() => RoUninitialize();

    internal static int GetActivationFactory(string className, Guid interfaceId, out nint factory)
    {
        nint classId = 0;
        fixed (char* src = className)
            Marshal.ThrowExceptionForHR(WindowsCreateString(src, (uint)className.Length, &classId));
        try
        {
            nint value = 0;
            var hr = RoGetActivationFactory(classId, &interfaceId, &value);
            factory = value;
            return hr;
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    internal static void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged<nint, uint>)Slot(instance, ReleaseSlot))(instance);
    }

    internal static void Release(ref nint instance)
    {
        Release(instance);
        instance = 0;
    }

    internal static int GetPointer(nint instance, int slot, out nint result)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged<nint, nint*, int>)Slot(instance, slot))(instance, &value);
        result = value;
        return hr;
    }

    internal static int GetPointerAt(nint instance, int slot, uint index, out nint result)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged<nint, uint, nint*, int>)Slot(instance, slot))(instance, index, &value);
        result = value;
        return hr;
    }

    internal static int GetPointerWithInt64(nint instance, int slot, long argument, out nint result)
    {
        nint value = 0;
        var hr = ((delegate* unmanaged<nint, long, nint*, int>)Slot(instance, slot))(instance, argument, &value);
        result = value;
        return hr;
    }

    internal static int GetInt32(nint instance, int slot, out int result)
    {
        var value = 0;
        var hr = ((delegate* unmanaged<nint, int*, int>)Slot(instance, slot))(instance, &value);
        result = value;
        return hr;
    }

    internal static int GetInt64(nint instance, int slot, out long result)
    {
        long value = 0;
        var hr = ((delegate* unmanaged<nint, long*, int>)Slot(instance, slot))(instance, &value);
        result = value;
        return hr;
    }

    internal static bool GetBool(nint instance, int slot)
    {
        byte value = 0;
        var hr = ((delegate* unmanaged<nint, byte*, int>)Slot(instance, slot))(instance, &value);
        return Succeeded(hr) && value != 0;
    }

    // Reads an HSTRING property and frees it; empty on failure
    internal static string GetString(nint instance, int slot)
    {
        if (!Succeeded(GetPointer(instance, slot, out var handle)) || handle == 0) return string.Empty;
        try
        {
            uint length = 0;
            var buffer = WindowsGetStringRawBuffer(handle, &length);
            return new string(buffer, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(handle);
        }
    }

    // Fires an IAsyncOperation-returning method and drops the operation, for commands we never read back
    internal static bool Fire(nint instance, int slot)
    {
        var hr = GetPointer(instance, slot, out var operation);
        Release(operation);
        return Succeeded(hr);
    }

    // Blocks the calling (worker) thread until an IAsyncOperation<T> completes, then returns its result
    internal static bool Await(nint operation, int timeoutMs, out nint result)
    {
        result = 0;
        if (operation == 0) return false;

        nint info = 0;
        var iid = AsyncInfoId;
        if (!Succeeded(((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(operation, QueryInterfaceSlot))(operation, &iid, &info)))
            return false;

        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                if (!Succeeded(GetInt32(info, AsyncInfoStatusSlot, out var status))) return false;
                // 0 = Started, 1 = Completed, anything else is Canceled or Error
                if (status != 0)
                    return status == 1 && Succeeded(GetPointer(operation, AsyncOperationResultSlot, out result));
                if (Environment.TickCount64 >= deadline)
                {
                    ((delegate* unmanaged<nint, int>)Slot(info, AsyncInfoCancelSlot))(info);
                    return false;
                }
                Thread.Sleep(4);
            }
        }
        finally
        {
            Release(info);
        }
    }

    // Reads an IRandomAccessStreamReference (album art and the like) fully into memory
    internal static byte[]? ReadStreamReference(nint reference, int timeoutMs, int maxBytes)
    {
        const int OpenReadSlot = 6;
        const int SizeSlot     = 6;
        const int ReadSlot     = 3;

        nint operation = 0, content = 0, randomAccess = 0, stream = 0;
        try
        {
            if (!Succeeded(GetPointer(reference, OpenReadSlot, out operation))
                || !Await(operation, timeoutMs, out content) || content == 0)
                return null;

            var iid = RandomAccessStreamId;
            if (!Succeeded(((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(content, QueryInterfaceSlot))(content, &iid, &randomAccess)))
                return null;

            ulong size = 0;
            if (!Succeeded(((delegate* unmanaged<nint, ulong*, int>)Slot(randomAccess, SizeSlot))(randomAccess, &size))
                || size == 0 || size > (ulong)maxBytes)
                return null;

            // Classic IStream over the WinRT stream, so a plain synchronous Read works
            var streamId = StreamId;
            if (!Succeeded(CreateStreamOverRandomAccessStream(randomAccess, &streamId, &stream))) return null;

            var bytes  = new byte[size];
            var offset = 0;
            fixed (byte* dest = bytes)
            {
                while (offset < bytes.Length)
                {
                    uint read = 0;
                    var hr = ((delegate* unmanaged<nint, byte*, uint, uint*, int>)Slot(stream, ReadSlot))(
                        stream, dest + offset, (uint)(bytes.Length - offset), &read);
                    if (!Succeeded(hr) || read == 0) break;
                    offset += (int)read;
                }
            }
            return offset == bytes.Length ? bytes : null;
        }
        finally
        {
            Release(stream);
            Release(randomAccess);
            Release(content);
            Release(operation);
        }
    }

    private static readonly Guid RandomAccessStreamId = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid StreamId             = new("0000000c-0000-0000-c000-000000000046");

    private static void* Slot(nint instance, int slot) => (*(void***)instance)[slot];

    [DllImport("shcore.dll")] private static extern int CreateStreamOverRandomAccessStream(nint randomAccessStream, Guid* iid, nint* stream);

    [DllImport("combase.dll")] private static extern int RoInitialize(int initType);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, nint* factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* src, uint length, nint* handle);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint handle);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint handle, uint* length);
}
