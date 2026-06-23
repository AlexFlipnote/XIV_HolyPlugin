using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace HoliestFluffiness;

internal static class SoundEngine
{
    internal static void Play(string path, float volume)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        new Thread(() =>
        {
            try
            {
                using var reader  = new MediaFoundationReader(path);
                using var channel = new WaveChannel32(reader) { Volume = Math.Clamp(volume, 0f, 1f), PadWithZeroes = false };
                using var output  = new DirectSoundOut();
                output.Init(channel);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(50);
            }
            catch { }
        }) { IsBackground = true }.Start();
    }
}
