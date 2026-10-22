#!/usr/bin/env bash
# UNTESTED: written on Windows, never run on a Mac. Builds the macOS agent .app and zips it.
# Run from anywhere: bash Tools/LaptopAgent/packaging/build_mac.sh
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's/^VERSION = "\(.*\)"/\1/p' app.py)
echo "Building Spatial Launcher Agent $VERSION"

[ -d build/venv ] || python3 -m venv build/venv
PY=build/venv/bin/python
$PY -m pip install --quiet -r requirements.txt "pyinstaller>=6.22" pip-licenses

$PY -m unittest discover -p "test_*.py"
$PY packaging/gen_licenses.py

rm -rf dist/SpatialLauncherAgent dist/SpatialLauncherAgent.app
$PY -m PyInstaller --noconfirm --distpath dist --workpath build/pyi packaging/agent_mac.spec

APP=dist/SpatialLauncherAgent.app
ZIP="dist/SpatialLauncherAgent-$VERSION-mac-arm64.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "Zip: $ZIP"
cp THIRD_PARTY_LICENSES.txt dist/THIRD_PARTY_LICENSES-mac.txt

"$APP/Contents/MacOS/SpatialLauncherAgent" --version
echo "Smoke: --version OK"
"$APP/Contents/MacOS/SpatialLauncherAgent" --selftest
echo "Smoke: --selftest OK"
