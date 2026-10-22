# Builds the Windows agent: venv, unit tests, licences, PyInstaller, zip, optional Inno installer, smoke tests.
# Run from anywhere: powershell -File Tools\LaptopAgent\packaging\build_win.ps1
param([switch]$SkipTests)  # -SkipTests: for local reruns only; CI always runs them
$ErrorActionPreference = "Stop"
$la = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $la

function Run($exe, [string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "$exe $($a -join ' ') failed with exit code $LASTEXITCODE" }
}

$version = (Select-String -Path "app.py" -Pattern '^VERSION = "([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Building Spatial Launcher Agent $version"

$basePy = if (Get-Command python -ErrorAction SilentlyContinue) { "python" } elseif (Get-Command py -ErrorAction SilentlyContinue) { "py" } else { throw "Python not found on PATH" }
if (-not (Test-Path "build\venv")) { Run $basePy @("-m", "venv", "build\venv") }
$py = Join-Path $la "build\venv\Scripts\python.exe"

# Some python.org installs can't find Tcl from inside a venv, and PyInstaller then silently drops tkinter (the GUI).
$ErrorActionPreference = "Continue"; & $py -c "import tkinter; tkinter.Tk().destroy()" 2>$null; $ErrorActionPreference = "Stop"
if ($LASTEXITCODE -ne 0) {
    $base = (& $py -c "import sys; print(sys.base_prefix)").Trim()
    $env:TCL_LIBRARY = Join-Path $base "tcl\tcl8.6"
    $env:TK_LIBRARY = Join-Path $base "tcl\tk8.6"
    & $py -c "import tkinter; tkinter.Tk().destroy()"
    if ($LASTEXITCODE -ne 0) { throw "tkinter does not work in the build venv; the GUI would be missing from the bundle" }
    Write-Host "tkinter needed TCL_LIBRARY/TK_LIBRARY from $base"
}
Run $py @("-m", "pip", "install", "--quiet", "-r", "requirements.txt", "pyinstaller>=6.22", "pip-licenses")

if (-not $SkipTests) { Run $py @("-m", "unittest", "discover", "-p", "test_*.py") }
Run $py @("packaging\gen_licenses.py")

if (Test-Path "dist\SpatialLauncherAgent") { Remove-Item -Recurse -Force "dist\SpatialLauncherAgent" }
Run $py @("-m", "PyInstaller", "--noconfirm", "--distpath", "dist", "--workpath", "build\pyi", "packaging\agent_win.spec")

$exe = Join-Path $la "dist\SpatialLauncherAgent\SpatialLauncherAgent.exe"
$zip = Join-Path $la "dist\SpatialLauncherAgent-$version-win64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "dist\SpatialLauncherAgent" -DestinationPath $zip
Write-Host "Zip: $zip"
Copy-Item "THIRD_PARTY_LICENSES.txt" "dist\THIRD_PARTY_LICENSES-win.txt" -Force

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if ($iscc) {
    Run $iscc.Source @("/DAppVersion=$version", "packaging\installer.iss")
} else {
    Write-Host "SKIP: iscc (Inno Setup) not on PATH, installer not built."
}

# Smoke tests
Run $exe @("--version")
Write-Host "Smoke: --version OK"
Run $exe @("--selftest")
Write-Host "Smoke: --selftest OK"
Write-Host "Build done."
