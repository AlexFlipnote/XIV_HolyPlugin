# SigTracker
Tracks FFXIV byte signatures across game patches. When a patch drops and a sig breaks, the tool attempts to locate the function automatically using saved anchor bytes, without needing the old exe.

## How it works
Each sig is stored as `sigs/<Name>.json` with a history of every patch it was resolved against. On each scan it records the resolved RVA and the first 32 bytes at that address (`bytes_at_rva`). If a future patch breaks the sig pattern, those saved bytes are used to search for where the function moved to.

Three outcomes when scanning after a patch:
- **OK**, sig still matches, nothing to do
- **FAIL + anchor hit**, sig broke but function just moved, tool prints the new address so you can update the pattern
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

# List all tracked sigs
make list

# Add a new sig
uv run index.py add MyFunc "E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 39"
```

Run `make scan` once after adding sigs to capture the baseline RVAs and anchor bytes.
