import re
import sys
import json
import hashlib
import pefile
import argparse

from datetime import datetime, UTC
from pathlib import Path

SIGS_DIR = Path("sigs")
ANCHOR_N = 32
DEFAULT_PATH = Path("C:/Program Files (x86)/SquareEnix/FINAL FANTASY XIV - A Realm Reborn/game/ffxiv_dx11.exe")

PLUGIN_DIR = Path("../HoliestFluffiness")
SIGS_CS = PLUGIN_DIR / "Utils/Sigs.cs"

# Matches a `public const string Name = "AA BB ?? CC";` entry in Sigs.cs. The format is one we
# impose on ourselves, so a strict pattern is fine; anything that stops matching should be loud.
SIG_FIELD_RE = re.compile(r'public\s+const\s+string\s+(\w+)\s*=\s*"([0-9A-Fa-f?][0-9A-Fa-f? ]*)"\s*;')

# Any string literal that looks like a byte sig, used to catch sigs living outside Sigs.cs.
SIG_LITERAL_RE = re.compile(r'"((?:[0-9A-F?]{2} ){3,}[0-9A-F?]{2})"')


def load_table() -> dict[str, str]:
    """Parse Sigs.cs into a {name: pattern} table, the single source of truth for patterns."""
    if not SIGS_CS.exists():
        sys.exit(f"Sigs.cs not found at {SIGS_CS.resolve()}. Run from the SigTracker directory.")

    table = dict(SIG_FIELD_RE.findall(SIGS_CS.read_text(encoding="utf-8")))
    if not table:
        # A silently-empty table would report "all clear" while tracking nothing, which is the
        # exact failure this whole setup exists to prevent.
        sys.exit(f"Parsed 0 sigs from {SIGS_CS}. The file format changed, fix SIG_FIELD_RE.")

    return table


def load_sig(path: Path) -> dict:
    """Load a single sig history file."""
    return json.loads(path.read_text(encoding="utf-8"))


def save_sig(entry: dict) -> None:
    """Save a sig history entry to its corresponding JSON file in SIGS_DIR."""
    path = SIGS_DIR / f"{entry['name']}.json"
    path.write_text(json.dumps(entry, indent=2), encoding="utf-8")


def sig_to_regex(sig: str) -> re.Pattern:
    """Convert a sig string like 'E8 ?? ?? 4C' into a compiled regex pattern."""
    parts = sig.split()
    pat = b"".join(b"." if p == "??" else re.escape(bytes([int(p, 16)])) for p in parts)
    return re.compile(pat, re.DOTALL)


def suggest_pattern(old_pattern: str, new_bytes: bytes) -> str:
    """
    Diff the current pattern's literal bytes against the anchor's new location, wildcarding whatever changed.

    Existing wildcard positions stay wildcarded; this only ever loosens the pattern, never guesses a byte back in.
    """
    new_tokens = new_bytes.hex(" ").upper().split()
    suggested = []
    for i, tok in enumerate(old_pattern.split()):
        if tok == "??" or i >= len(new_tokens) or tok != new_tokens[i]:
            suggested.append("??")
        else:
            suggested.append(tok)
    return " ".join(suggested)


def short_hash(path: Path) -> str:
    """Return an 8-char MD5 hash of the first 4KB of a file, used as a game version identifier."""
    return hashlib.md5(path.read_bytes()[:4096]).hexdigest()[:8]


def scan(exe_path: Path) -> None:
    """Scan the given exe against every sig in Sigs.cs, recording new RVAs and anchor bytes."""
    if not exe_path.exists():
        print(f"exe not found: {exe_path}")
        print("Use 'scan --exe <path>' to specify a different location.")
        return

    table = load_table()
    data = pefile.PE(str(exe_path)).get_memory_mapped_image()
    ehash = short_hash(exe_path)

    for name, pattern in sorted(table.items()):
        path = SIGS_DIR / f"{name}.json"
        entry = load_sig(path) if path.exists() else {"name": name, "history": []}
        last = entry["history"][-1] if entry["history"] else {}
        m = sig_to_regex(pattern).search(data)

        if m:
            rva = hex(m.start())
            anchor = data[m.start():m.start() + ANCHOR_N].hex(" ").upper()
            if last.get("exe_hash") != ehash or last.get("pattern") != pattern:
                SIGS_DIR.mkdir(exist_ok=True)
                entry["history"].append({
                    "pattern": pattern,
                    "rva": rva,
                    "bytes_at_rva": anchor,
                    "exe_hash": ehash,
                    "date": datetime.now(UTC).strftime("%Y-%m-%d"),
                })
                save_sig(entry)
            print(f"  OK  {name}: {rva}{'  (new)' if not last else ''}")
        else:
            print(f"FAIL  {name}: sig broken")
            if "bytes_at_rva" in last:
                anchor = bytes.fromhex(last["bytes_at_rva"].replace(" ", ""))
                m2 = re.search(re.escape(anchor), data, re.DOTALL)
                if m2:
                    new_bytes = data[m2.start():m2.start() + ANCHOR_N]
                    suggestion = suggest_pattern(pattern, new_bytes)
                    print(f"      anchor hit at {hex(m2.start())}, function moved, update pattern in Sigs.cs")
                    print(f"      suggested pattern: {suggestion}")
                else:
                    print("      anchor also gone, function was rewritten, manual Ghidra needed")
            else:
                print("      no baseline recorded yet, nothing to anchor against")

    for path in sorted(SIGS_DIR.glob("*.json")):
        if path.stem not in table:
            print(f" ORPH {path.stem}: history exists but no field in Sigs.cs, renamed or retired?")


def check() -> None:
    """Fail if any sig literal lives outside Sigs.cs, keeping Sigs.cs the only source."""
    table = load_table()

    # The stray sweep below skips Sigs.cs, so on its own nothing would notice a sig declared there
    # in a shape SIG_FIELD_RE does not match (`static readonly string`, say). That sig would be
    # invisible to both the parser and the sweep, which is the exact drift this tool exists to
    # catch. Every sig-shaped literal in the file must therefore also be a parsed field value.
    unparsed = set(SIG_LITERAL_RE.findall(SIGS_CS.read_text(encoding="utf-8"))) - set(table.values())
    if unparsed:
        print(f"Sig literals in {SIGS_CS.name} that did not parse as `public const string` fields:")
        for lit in sorted(unparsed):
            print(f'  "{lit}"')
        sys.exit(1)

    strays: list[str] = []

    for path in sorted(PLUGIN_DIR.rglob("*.cs")):
        if path.resolve() == SIGS_CS.resolve() or "obj" in path.parts or "bin" in path.parts:
            continue
        for lineno, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            for lit in SIG_LITERAL_RE.findall(line):
                strays.append(f'  {path.as_posix()}:{lineno}  "{lit}"')

    if strays:
        print(f"Sig literals found outside {SIGS_CS.name}, move them into the table:")
        print("\n".join(strays))
        sys.exit(1)

    print(f"OK, {len(table)} sigs, all confined to {SIGS_CS.name}")


def list_sigs() -> None:
    """Print every sig in Sigs.cs with its last known RVA and scan date."""
    for name, pattern in sorted(load_table().items()):
        path = SIGS_DIR / f"{name}.json"
        last = load_sig(path)["history"][-1] if path.exists() else {}
        rva = last.get("rva") or "never scanned"
        date = last.get("date") or "..."
        stale = "  [pattern changed since last scan]" if last and last.get("pattern") != pattern else ""
        print(f"  {name:<25} pattern: {pattern}")
        print(f"  {'':25} last rva: {rva}  ({date}){stale}")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Track FFXIV sig patterns across patches")
    sub = p.add_subparsers(dest="cmd")

    s = sub.add_parser("scan", help="scan exe against every sig in Sigs.cs")
    s.add_argument("--exe", type=Path, default=DEFAULT_PATH, help="path to ffxiv_dx11.exe")

    sub.add_parser("check", help="fail if any sig literal lives outside Sigs.cs")
    sub.add_parser("list", help="list all tracked sigs")

    args = p.parse_args()

    match args.cmd:
        case "scan":
            scan(args.exe)
        case "check":
            check()
        case "list":
            list_sigs()
        case _:
            p.print_help()
