# SigTracker
Tracks FFXIV byte signatures across game patches. When a patch drops and a sig breaks, the tool attempts to locate the function automatically using saved anchor bytes, without needing the old exe.

## Where sigs live
`HoliestFluffiness/Utils/Sigs.cs` is the single source of truth. Every pattern is a `public const string` field there, the plugin scans with those constants, and SigTracker parses the same file. A sig cannot be tracked here but stale in the plugin, or vice versa:

```csharp
public const string ReceiveEvent = "44 0F B7 C2 4D 8B D1";
```

The field name is the sig name, and it is what `sigs/<Name>.json` is keyed on. Renaming a field orphans its history file, so rename the JSON to match; `scan` prints `ORPH` for any history with no matching field.

## How it works
Each sig gets a `sigs/<Name>.json` holding the history of every patch it was resolved against. These files are history only, never a source of patterns. On each scan the resolved RVA and the first 32 bytes at that address (`bytes_at_rva`) are recorded. If a future patch breaks the pattern, those saved bytes are used to search for where the function moved to.

Three outcomes when scanning after a patch:
- **OK**, sig still matches, nothing to do
- **FAIL + anchor hit**, sig broke but function just moved, tool prints the new address so you can update the pattern in `Sigs.cs`
- **FAIL + anchor gone**, function was rewritten, needs manual re-finding in Ghidra/x64dbg

## Setup
```bash
uv sync
```

Default exe path is `C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/ffxiv_dx11.exe`. Override with `--exe` if your install is elsewhere.

## Usage
```bash
# Scan against default path
make scan

# Scan against a custom path
make scan-custom EXE="D:/SquareEnix/.../ffxiv_dx11.exe"

# Verify Sigs.cs is still the only place sigs live
make check

# List all tracked sigs
make list
```

Both `make scan` and `make check` are also reachable from the repo root.

## Adding a sig
Add a `public const string` field to `Sigs.cs`, use it at the call site, then run `make scan` once to capture the baseline RVA and anchor bytes. There is no `add` command; editing `Sigs.cs` is how a sig comes into existence.

## What `check` guards
`check` fails if a sig ever drifts out of `Sigs.cs`, which is the thing that silently rots between patches. It catches both directions:

- a sig-shaped string literal in any other `.cs` file under `HoliestFluffiness/`
- a sig inside `Sigs.cs` declared in a shape the parser does not recognise, for example `static readonly string` instead of `const string`

It also hard-fails if `Sigs.cs` parses to zero sigs, since a silently empty table would report all-clear while tracking nothing.

Note that `check` does not understand comments, so a sig-shaped string in a comment inside `Sigs.cs` will trip it.
