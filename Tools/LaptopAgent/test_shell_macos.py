"""Tests for the macOS shell backend: Dock pins, app bundles, which Quartz windows are listed, all with made-up data;
plus a read-only live check on a Mac (lists apps, pins and windows and draws icons; launches nothing, touches no
window).

  python -m unittest test_shell_macos -v        (from Tools/LaptopAgent)
"""
import os
import sys
import unittest

from hostos import _png
from hostos import _shell_macos_pure as pure
from hostos._shell_types import AppEntry


def dock(*urls):
    return {"persistent-apps": [{"tile-data": {"file-data": {"_CFURLString": u, "_CFURLStringType": 15}}} for u in urls]}


class DockPins(unittest.TestCase):
    def test_order_finder_first_and_url_decoding(self):
        doc = dock("file:///Applications/Safari.app/", "file:///System/Applications/Notes.app/",
                   "file:///Applications/Visual%20Studio%20Code.app/", "file:///Applications/Safari.app/")
        self.assertEqual(pure.dock_pins(doc), [pure.FINDER, "/Applications/Safari.app", "/System/Applications/Notes.app",
                                               "/Applications/Visual Studio Code.app"])

    def test_junk_is_skipped(self):
        doc = {"persistent-apps": [{"tile-data": {}}, 5, {"tile-data": {"file-data": {"_CFURLString": 7}}},
                                   {"tile-data": {"file-data": {"_CFURLString": "file:///Users/me/Documents/"}}},
                                   {"tile-data": {"file-data": {"_CFURLString": "https://example.com/x.app"}}},
                                   {"tile-data": {"file-data": {"_CFURLString": "/Applications/Mail.app"}}}]}
        self.assertEqual(pure.dock_pins(doc), [pure.FINDER, "/Applications/Mail.app"])
        self.assertEqual(pure.dock_pins(None), [pure.FINDER])
        self.assertEqual(pure.dock_pins({"persistent-apps": "x"}), [pure.FINDER])

    def test_stale_and_translocated_pins_are_found_by_bundle_id(self):
        def pin(url, bundle_id=None):
            tile = {"file-data": {"_CFURLString": url}}
            if bundle_id:
                tile["bundle-identifier"] = bundle_id
            return {"tile-data": tile}
        moved = "file:///private/var/folders/x/T/AppTranslocation/66B0/d/MixPad.app/"
        doc = {"persistent-apps": [pin("file:///Applications/Safari.app/", "com.apple.Safari"),
                                   pin(moved, "com.nchsoftware.mixpadfree"),
                                   pin("file:///Applications/Gone.app/", "com.example.gone"),
                                   pin("file:///Applications/Old%20Name.app/", "com.example.renamed"),
                                   pin("file:///Applications/NoId.app/"),
                                   pin("file:///Applications/Safari.app/", "com.apple.Safari")]}
        there = {pure.FINDER, "/Applications/Safari.app", "/Applications/MixPad.app", "/Applications/New Name.app",
                 "/private/var/folders/x/T/AppTranslocation/66B0/d/MixPad.app"}
        located = {"com.nchsoftware.mixpadfree": "/Applications/MixPad.app/", "com.example.renamed": "/Applications/New Name.app",
                   "com.apple.Safari": "/Applications/Safari.app"}
        self.assertEqual(pure.dock_pins(doc, there.__contains__, located.get),
                         [pure.FINDER, "/Applications/Safari.app", "/Applications/MixPad.app", "/Applications/New Name.app"])
        # Another edition is installed under another identifier: a translocated pin is matched by its bundle name, a
        # pin that is only missing is not.
        named = {"MixPad.app": "/Applications/MixPad.app", "Gone.app": "/Applications/Gone.app"}.get
        doc = {"persistent-apps": [pin(moved, "com.nchsoftware.mixpadfree"),
                                   pin("file:///Applications/Gone.app/", "com.example.gone")]}
        self.assertEqual(pure.dock_pins(doc, there.__contains__, lambda b: None, named),
                         [pure.FINDER, "/Applications/MixPad.app"])
        # Nothing to look it up with: a translocated pin that still exists is kept as it is.
        self.assertEqual(pure.dock_pins({"persistent-apps": [pin(moved, "com.nchsoftware.mixpadfree")]}, there.__contains__),
                         [pure.FINDER, "/private/var/folders/x/T/AppTranslocation/66B0/d/MixPad.app"])


class Bundles(unittest.TestCase):
    def test_scan_one_level_deep_without_entering_bundles(self):
        tree = {"/Applications": ["Safari.app", "Utilities", ".hidden", "notes.txt", "Adobe"],
                "/Applications/Utilities": ["Terminal.app", "readme"],
                "/Applications/Adobe": ["Photoshop.app"],
                "/System/Applications": ["Notes.app"]}

        def listdir(path):
            if path not in tree:
                raise OSError(path)
            return tree[path]
        found = pure.scan_bundles(["/Applications", "/missing", "/System/Applications"], listdir, lambda p: p in tree)
        self.assertEqual(found, ["/Applications/Adobe/Photoshop.app", "/Applications/Safari.app",
                                 "/Applications/Utilities/Terminal.app", "/System/Applications/Notes.app"])

    def test_entries(self):
        e = pure.bundle_entry("/Applications/Visual Studio Code.app/")
        self.assertEqual((e.name, e.program, e.launch), ("Visual Studio Code", "/applications/visual studio code.app",
                                                         ("bundle", "/Applications/Visual Studio Code.app")))
        self.assertEqual(e.key, "bundle:/applications/visual studio code.app")
        self.assertIsNone(pure.bundle_entry("relative/Thing.app"))
        self.assertIsNone(pure.bundle_entry("/Applications/notes.txt"))
        self.assertEqual(pure.program_key("/Applications/Safari.app/"), "/applications/safari.app")
        self.assertIsNone(pure.program_key("/usr/bin/python3"))

    def test_pins_and_folders_share_keys(self):
        pinned = pure.bundle_entry(pure.dock_pins(dock("file:///Applications/Safari.app/"))[1])
        self.assertEqual(pinned.key, pure.bundle_entry("/Applications/Safari.app").key)


def info(number, pid, name="", layer=0, w=800, h=600, alpha=1.0):
    return {"kCGWindowNumber": number, "kCGWindowOwnerPID": pid, "kCGWindowName": name, "kCGWindowLayer": layer,
            "kCGWindowAlpha": alpha, "kCGWindowBounds": {"X": 0, "Y": 0, "Width": w, "Height": h}}


class Windows(unittest.TestCase):
    APPS = {10: pure.RunningApp(10, "Safari", "/Applications/Safari.app", 100.0, True),
            11: pure.RunningApp(11, "Helper", "/Applications/Helper.app", 50.0, False),
            12: pure.RunningApp(12, "Notes", "/System/Applications/Notes.app", 200.0, True),
            99: pure.RunningApp(99, "Agent", None, 1.0, True)}

    def test_which_windows_are_listed(self):
        infos = [info(1, 10, "Apple"), info(2, 10, ""), info(3, 10, "", layer=25), info(4, 11, "Helper window"),
                 info(5, 12, "Shopping"), info(6, 12, ""), info(7, 10, "Tiny", w=20, h=20), info(8, 10, "Clear", alpha=0),
                 info(9, 99, "Agent status"), info(13, 55, "Unknown app"), {"kCGWindowNumber": "x"}]
        windows = pure.shell_windows(infos, {1, 2, 3, 4}, self.APPS, own_pid=99, focused_id=1)
        self.assertEqual([(w.handle, w.title, w.minimized) for w in windows],
                         [(1, "Apple", False), (2, "Safari", False), (5, "Shopping", True)])
        first = windows[0]
        self.assertTrue(first.focused)
        self.assertEqual((first.program, first.app_name, first.started), ("/applications/safari.app", "Safari", 100.0))
        self.assertEqual(first.icon, ("bundle", "/Applications/Safari.app"))

    def test_windows_match_installed_apps(self):
        windows = pure.shell_windows([info(5, 12, "Shopping")], {5}, self.APPS, 99, 0)
        self.assertEqual(windows[0].program, pure.bundle_entry("/System/Applications/Notes.app").program)


class Service(unittest.IsolatedAsyncioTestCase):
    """The shared protocol over macOS-shaped data: Dock order, then launch order."""

    async def test_dock_order(self):
        import shell
        from test_shell import FakeBackend, direct
        backend = FakeBackend()
        finder, safari, notes = (pure.bundle_entry(p) for p in
                                 (pure.FINDER, "/Applications/Safari.app", "/System/Applications/Notes.app"))
        terminal = pure.bundle_entry("/Applications/Utilities/Terminal.app")
        backend.os_name = "macos"
        backend.installed_apps = [safari, notes, terminal]
        backend.pinned_apps = [finder, safari]
        apps = {1: pure.RunningApp(1, "Finder", pure.FINDER, 1.0, True),
                2: pure.RunningApp(2, "Terminal", "/Applications/Utilities/Terminal.app", 300.0, True),
                3: pure.RunningApp(3, "Notes", "/System/Applications/Notes.app", 200.0, True)}
        backend.open_windows = pure.shell_windows([info(20, 2, "zsh"), info(21, 3, "Shopping"), info(22, 1, "Home")],
                                                  {20, 21, 22}, apps, 99, 20)
        sent = []
        service = shell.ShellService(backend, sent.append, lambda: None, run_blocking=direct)
        await service.handle("apps.running", {"type": "apps.running"})
        self.assertEqual([a["name"] for a in sent[0]["apps"]], ["Finder", "Notes", "Terminal"])
        self.assertEqual(service.info, {"os": "macos", "available": True})


@unittest.skipUnless(sys.platform == "darwin", "macOS only")
class LiveMac(unittest.TestCase):
    """Read-only: lists and draws, never launches or changes a window."""

    @classmethod
    def setUpClass(cls):
        from hostos.shell_macos import MacShell
        cls.shell = MacShell()

    def test_listing_and_icons(self):
        installed = self.shell.installed()
        names = [a.name for a in installed]
        print(f"\n  installed: {len(installed)}, e.g. {names[:8]}")
        self.assertTrue(any(n in names for n in ("Safari", "Notes", "Calendar", "System Settings")))
        pinned = self.shell.pinned()
        print(f"  Dock pins: {[p.name for p in pinned]}")
        self.assertEqual(pinned[0].name, "Finder")
        windows = self.shell.windows()
        print(f"  windows: {[(w.app_name, w.title[:30], w.minimized) for w in windows][:10]}")
        self.assertFalse(any(w.pid == os.getpid() for w in windows))
        png = self.shell.app_icon(installed[0], 48)
        self.assertIsNotNone(png, "an app icon is drawn")
        self.assertEqual(_png.decode_size(png), (48, 48))
        print(f"  icon: {len(png)} bytes")

    def test_launch_refuses_what_it_did_not_find(self):
        for spec in (("bundle", "/bin/sh"), ("bundle", "/tmp/Evil.app"), ("exe", "/Applications/Safari.app"),
                     ("bundle", "/Applications/../tmp/Evil.app")):
            with self.assertRaises(OSError, msg=spec):
                self.shell.launch(AppEntry("x", "x", launch=spec))

    def test_actions_refuse_unlisted_windows(self):
        self.assertFalse(self.shell.window_action(1, "close"))
        self.assertFalse(self.shell.window_action(1, "explode"))


if __name__ == "__main__":
    unittest.main()
