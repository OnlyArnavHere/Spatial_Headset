"""Writes THIRD_PARTY_LICENSES.txt from pip-licenses, and fails on any GPL/AGPL Python dependency.

Run from anywhere: python packaging/gen_licenses.py [output]. Needs pip-licenses in the running interpreter.
Exit codes: 0 ok, 1 a forbidden licence was found, 2 pip-licenses failed.
"""
import json
import re
import subprocess
import sys
from pathlib import Path

LA = Path(__file__).resolve().parent.parent
DEFAULT_OUT = LA / "THIRD_PARTY_LICENSES.txt"

# PyInstaller's bootloader/runtime carries an explicit exception that allows bundling into any app.
GPL_EXEMPT = {"pyinstaller", "pyinstaller-hooks-contrib"}

# Hand-kept: components pip-licenses cannot see. Update when the bundle or the downloaded tools change.
NATIVE_COMPONENTS = [
    ("Python", "PSF-2.0", "https://docs.python.org/3/license.html"),
    ("Tcl/Tk", "Tcl/Tk (BSD-style)", "https://www.tcl.tk/software/tcltk/license.html"),
    ("OpenSSL", "Apache-2.0", "https://www.openssl.org/source/license.html"),
    ("libsrtp (inside pylibsrtp)", "BSD-3-Clause", "https://github.com/cisco/libsrtp/blob/main/LICENSE"),
    ("FFmpeg libraries (inside PyAV)", "LGPL-2.1-or-later (some builds GPL, see warning)", "https://ffmpeg.org/legal.html"),
    ("ffmpeg (downloaded at first run, not bundled)", "LGPL-2.1-or-later / GPL-2.0-or-later, per build",
     "https://ffmpeg.org/legal.html"),
    ("mediamtx (downloaded at first run, not bundled)", "MIT", "https://github.com/bluenviron/mediamtx/blob/main/LICENSE"),
]

AV_WARNING = ("The PyPI av wheel bundles GPL x264/x265 in its native libraries, which pip-licenses cannot see. "
              "This build must stay a pre-release until the LGPL-only FFmpeg/PyAV build lands (stage 9).")


def norm(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def classify(license_text: str) -> str:
    """"lgpl", "gpl" (GPL or AGPL) or "other"."""
    low = license_text.lower()
    if "lgpl" in low or "lesser general public" in low or "library general public" in low:
        return "lgpl"
    if "gpl" in low or "general public license" in low or "affero" in low:
        return "gpl"
    return "other"


def check(entries: list[dict]) -> tuple[list[dict], list[dict]]:
    """(forbidden GPL/AGPL entries, LGPL entries)."""
    bad, lgpl = [], []
    for e in entries:
        kind = classify(str(e.get("License", "")))
        if kind == "gpl" and norm(str(e.get("Name", ""))) not in GPL_EXEMPT:
            bad.append(e)
        elif kind == "lgpl":
            lgpl.append(e)
    return bad, lgpl


def render(entries: list[dict], lgpl: list[dict]) -> str:
    out = ["THIRD-PARTY LICENSES", "=" * 20, "", "WARNING: " + AV_WARNING, "", "Native components", "-" * 17]
    for name, lic, url in NATIVE_COMPONENTS:
        out.append(f"- {name}: {lic} ({url})")
    if lgpl:
        out += ["", "LGPL components (replaceable: the sources are readable in the install folder or on PyPI)"]
        out += [f"- {e['Name']} {e.get('Version', '')}: {e.get('License', '')}" for e in lgpl]
    out += ["", "Python packages", "-" * 15]
    for e in sorted(entries, key=lambda x: norm(str(x.get("Name", "")))):
        out += ["", f"{e.get('Name')} {e.get('Version', '')}", f"License: {e.get('License', '')}",
                f"URL: {e.get('URL', '')}"]
        for label, key in (("License text", "LicenseText"), ("Notice", "NoticeText")):
            text = str(e.get(key) or "")
            if text.strip() and text.strip() != "UNKNOWN":
                out += [f"--- {label} ---", text.strip()]
    return "\n".join(out) + "\n"


def run_pip_licenses() -> list[dict]:
    cmd = [sys.executable, "-m", "piplicenses", "--format=json", "--with-license-file", "--with-notice-file",
           "--with-urls"]
    return json.loads(subprocess.run(cmd, check=True, capture_output=True, text=True, encoding="utf-8").stdout)


def main(argv: list[str] | None = None, loader=run_pip_licenses) -> int:
    argv = sys.argv[1:] if argv is None else argv
    out_path = Path(argv[0]) if argv else DEFAULT_OUT
    try:
        entries = loader()
    except (subprocess.CalledProcessError, OSError, ValueError) as ex:
        print(f"pip-licenses failed: {ex}", file=sys.stderr)
        return 2
    bad, lgpl = check(entries)
    if bad:
        for e in bad:
            print(f"FORBIDDEN licence: {e['Name']} {e.get('Version', '')}: {e.get('License')}", file=sys.stderr)
        return 1
    out_path.write_text(render(entries, lgpl), encoding="utf-8")
    print(f"Wrote {out_path} ({len(entries)} packages, {len(lgpl)} LGPL)")
    print("WARNING: " + AV_WARNING, file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
