import re
import json
import hashlib
import pefile
import argparse

from datetime import datetime, UTC
from pathlib import Path

SIGS_DIR = Path("sigs")
ANCHOR_N = 32
DEFAULT_PATH = Path("C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/ffxiv_dx11.exe")


def load_sig(path: Path) -> dict:
    """Load a single sig entry from a JSON file."""
    return json.loads(path.read_text(encoding="utf-8"))


def save_sig(entry: dict) -> None:
    """Save a sig entry to its corresponding JSON file in SIGS_DIR."""
    path = SIGS_DIR / f"{entry['name']}.json"
    path.write_text(json.dumps(entry, indent=2), encoding="utf-8")


def all_sigs() -> list[Path]:
    """Return all sig JSON files sorted by name."""
    return sorted(SIGS_DIR.glob("*.json")) if SIGS_DIR.exists() else []


def sig_to_regex(sig: str) -> re.Pattern:
    """Convert a sig string like 'E8 ?? ?? 4C' into a compiled regex pattern."""
    parts = sig.split()
    pat = b"".join(b"." if p == "??" else re.escape(bytes([int(p, 16)])) for p in parts)
    return re.compile(pat, re.DOTALL)


def short_hash(path: Path) -> str:
    """Return an 8-char MD5 hash of the first 4KB of a file, used as a game version identifier."""
    return hashlib.md5(path.read_bytes()[:4096]).hexdigest()[:8]


def scan(exe_path: Path) -> None:
    """Scan the given exe against all known sigs, recording new RVAs and anchor bytes."""
    if not exe_path.exists():
        print(f"exe not found: {exe_path}")
        print("Use 'scan --exe <path>' to specify a different location.")
        return

    data = pefile.PE(str(exe_path)).get_memory_mapped_image()
    ehash = short_hash(exe_path)

    for path in all_sigs():
        entry = load_sig(path)
        pattern = entry["history"][-1]["pattern"] if entry["history"] else entry.get("pattern", "")
        m = sig_to_regex(pattern).search(data)

        if m:
            rva = hex(m.start())
            anchor = data[m.start():m.start() + ANCHOR_N].hex(" ").upper()
            last = entry["history"][-1] if entry["history"] else {}
            if last.get("exe_hash") != ehash:
                entry["history"].append({
                    "pattern": pattern,
                    "rva": rva,
                    "bytes_at_rva": anchor,
                    "exe_hash": ehash,
                    "date": datetime.now(UTC).strftime("%Y-%m-%d"),
                })
                save_sig(entry)
            print(f"  OK  {entry['name']}: {rva}")
        else:
            print(f"FAIL  {entry['name']}: sig broken")
            last = entry["history"][-1] if entry["history"] else {}
            if "bytes_at_rva" in last:
                anchor = bytes.fromhex(last["bytes_at_rva"].replace(" ", ""))
                m2 = re.search(re.escape(anchor), data, re.DOTALL)
                if m2:
                    print(f"      anchor hit at {hex(m2.start())}, function moved, update pattern")
                else:
                    print("      anchor also gone, function was rewritten, manual Ghidra needed")


def add(name: str, pattern: str) -> None:
    """Add a new sig to track, creating its JSON file in SIGS_DIR."""
    SIGS_DIR.mkdir(exist_ok=True)
    path = SIGS_DIR / f"{name}.json"
    if path.exists():
        print(f"'{name}' already exists")
        return
    save_sig({
        "name": name,
        "history": [{
            "pattern": pattern,
            "rva": None,
            "bytes_at_rva": None,
            "exe_hash": None,
            "date": None
        }]
    })
    print(f"Added '{name}'. Run scan to capture first RVA + anchor bytes.")


def list_sigs() -> None:
    """Print all tracked sigs with their last known RVA and date."""
    paths = all_sigs()
    if not paths:
        print("No sigs found. Use 'add' to add one.")
        return
    for path in paths:
        entry = load_sig(path)
        last = entry["history"][-1] if entry["history"] else {}
        rva = last.get("rva") or "never scanned"
        date = last.get("date") or "..."
        print(f"  {entry['name']:<25} pattern: {last.get('pattern', '?')}")
        print(f"  {'':25} last rva: {rva}  ({date})")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Track FFXIV sig patterns across patches")
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("scan", help="scan exe against all known sigs")
    s.add_argument("--exe", type=Path, default=DEFAULT_PATH, help="path to ffxiv_dx11.exe")

    a = sub.add_parser("add", help="add a new sig to track")
    a.add_argument("name", help="friendly name, e.g. AddToScreenLog")
    a.add_argument("pattern", help="sig pattern, e.g. 'E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 39'")

    sub.add_parser("list", help="list all tracked sigs")

    args = p.parse_args()

    match args.cmd:
        case "scan":
            scan(args.exe)
        case "add":
            add(args.name, args.pattern)
        case "list":
            list_sigs()
        case _:
            p.print_help()
