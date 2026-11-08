# Handoff: the laptop agent on a Mac

For whoever picks up the work on a MacBook. Written 2026-11-07 on the Windows machine, where none of the macOS
code below could be run. Read `PROGRESS.md` first for the overall state.

## Rules for this session

- Do not commit, push or tag. Stop after each stage below and give the exact `git add` and `git commit` commands.
- Fix macOS-specific problems only, and keep Windows behaviour unchanged. Guard Mac-only code with
  `sys.platform == "darwin"` (or put it in `hostos/macos.py`). Windows tests must still pass.
- At the end, list what is still untested.

## Getting the code onto the Mac

The work is **uncommitted** on branch `pairing-fixes` on the Windows machine (3 commits ahead of `main`, not pushed),
so a fresh clone does not have it. Pick one:

- On Windows, commit and push `pairing-fixes`, then on the Mac:
  `git clone https://github.com/OnlyArnavHere/Spatial_Headset.git && cd Spatial_Headset && git checkout pairing-fixes`
- Or copy the whole working folder (USB stick or cloud drive) to the Mac. Leave out `Library/`, `Temp/`, `Builds/`,
  `Tools/LaptopAgent/build/` and `dist/`, which are large and rebuilt anyway.

Check: `Tools/LaptopAgent/gui/glass.py` and `Tools/LaptopAgent/textfocus.py` exist, and `Tools/LaptopAgent/network.py`
does not.

## What changed since the last Mac check (all untested on macOS)

1. **The app is a desktop widget.** It is frameless and transparent, 330×330 pt, with glass tiles in the style of
   Control Center (`gui/web/index.html`, `app.css`, `app.js`). There is no title bar: you drag the widget by the status
   pill, and the minimize and close buttons are in the foot row.
   - `app.py` creates it with `frameless`, `transparent`, `vibrancy=True` (darwin only), `easy_drag=False` and
     `resizable=False`.
   - `gui/glass.py` → `_macos()`:
     - sets `NSWindowSharingNone`, so the window is not captured,
     - uses a vibrant dark appearance,
     - gives the content view a 16 pt corner radius,
     - sets a clear background.
   - `gui/glass.py` → `set_shape()` masks pywebview's `NSVisualEffectView` to the tile rectangles that the page sends
     (`api.set_shape`), so the gaps between tiles show the desktop clearly.
2. **Hidden from the headset feed.** The widget must not appear in a "Whole desktop" stream, and it must not be in the
   window list the headset sees. `winapi.py` filters its own pid on Windows; macOS already left its own windows out.
3. **The keyboard pops up for text fields.** `textfocus.py` → macOS path: it reads `AXFocusedUIElement` and checks
   the role (`TEXT_ROLES`) or `AXEditable`. `session.py` polls it every 0.3 s after a click, and only while control
   with keyboard is granted. It sends the control message `{"type":"text-focus","editable":…,"slot":…}`.
4. **Connection methods.** Only Wi-Fi (Bonjour) and the USB cable (usbmux) remain:
   - typing a headset address (`--headset`) is gone,
   - Personal Hotspot is gone,
   - the agent can no longer reach the Unity editor at 127.0.0.1.

   `usbmux.py` on a Mac talks to `/var/run/usbmuxd`.

## Stages

### 1. Run from source

```
cd Tools/LaptopAgent
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python -m unittest discover -p "test_*.py"
```

Expect everything to pass except possibly `test_laptop_finds_the_scanned_headset_by_its_instance_name`, which already
failed on Windows before these changes. Any macOS-only failure is a bug to fix.

Then run `python app.py`. Check:
- The widget appears with rounded glass tiles, white labels, and clear gaps between the tiles. If the whole square is
  blurred, `set_shape` didn't find the effect view: look for "no vibrancy view to shape" in the log.
- Dragging the status pill moves the widget, and clicking a button does not move it.
- Minimize works. Close hides the widget and the agent keeps running in the menu-bar icon; reopen it from the icon.
- Pair opens the QR card. Settings opens the settings card. Done closes it.
- Take a screenshot with ⌘⇧3: the widget **should be missing** from the image. Note: macOS 15 and later may still
  show `NSWindowSharingNone` windows to some capture APIs. If the screenshot shows it, check the agent's own stream in
  step 3 before deciding it's broken.

### 2. Build the .app

```
bash Tools/LaptopAgent/packaging/build_mac.sh
```

`build_mac.sh` has **never been run** (it was written on Windows). Expected output:
- `Tools/LaptopAgent/dist/SpatialLauncherAgent.app`
- `Tools/LaptopAgent/dist/SpatialLauncherAgent-<version>-mac-arm64.zip`

The script also smoke-tests `--version` and `--selftest`. Report both full paths.

Likely trouble:
- Gatekeeper blocks the unsigned app: right-click > Open, or `xattr -dr com.apple.quarantine <app>`.
- `packaging/agent_mac.spec` misses hidden imports. It must include `gui/web/*`, `gui/glass.py`, `textfocus.py`,
  `usbmux.py`, `stream_link.py`, `qr_image.py` and the `segno` data.
- The bundled ffmpeg isn't executable.

### 3. Use it like a user

Launch the .app from Finder, not from Terminal: the permissions must belong to the app.

1. **Permissions.** Open System Settings > Privacy & Security. **Screen Recording**, **Accessibility** and **Input
   Monitoring** should each list *Spatial Launcher Agent* by name, not Python or Terminal. Grant them, then use
   *Quit and reopen* in the Settings card. Also check the macOS permissions section in the Settings card.
2. **Pair** from the widget's QR card with the iPhone app (Home > Laptop > pair).
3. **Stream over Wi-Fi.**
   - Open a window, then **Whole desktop**. The widget must not be in the picture, and it must not be in the headset's
     window list.
4. **Control.**
   - Turn on Control, tap Allow control on the headset, and approve on the Mac.
   - Use the Laptop Shell: search, the Dock list, opening apps, and minimize/raise/close of windows.
5. **Keyboard pop-up.**
   - Click a text field (Notes, Safari's address bar) from the headset: the keyboard should open docked to that
     window.
   - Click outside the field: the keyboard should close.
   - Typing must not push any window back and forth.
6. **USB cable.**
   - Turn on the USB cable button in the widget, plug in the iPhone (any cable), tap Trust, and repeat steps 3–4.
   - **Known open bug:** over the cable, nothing happens after Allow control. Capture
     `python agent.py --wired --input --keyboard --verbose` from the moment of the tap. Fix it if the cause is clear;
     otherwise bring the log back.

## Report back

- The `.app` and `.zip` paths.
- For each permission, the program macOS lists.
- For each check above: pass, fail (with the log), or not tried.
- The `git add` / `git commit` commands for any fixes, uncommitted.
