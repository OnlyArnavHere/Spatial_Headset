"""Tests for the Windows shell backend: the shortcut parser, the PNG encoder, which Start Menu shortcuts are apps, the
taskbar pin order, Store apps from Get-StartApps and icon alpha, all with made-up data; plus a read-only live check on
Windows (lists apps and windows and draws icons; launches nothing, touches no window).

  python -m unittest test_shell_windows -v        (from Tools/LaptopAgent)
"""
import os
import struct
import sys
import tempfile
import unittest
import zlib

from hostos import _lnk, _png
from hostos import _shell_windows_pure as pure
from hostos._shell_types import AppEntry


class Shortcuts(unittest.TestCase):
    def test_unicode_and_ansi_link_info(self):
        for unicode_info in (True, False):
            data = _lnk.build(r"C:\Program Files\Google\Chrome\Application\chrome.exe", arguments="--profile 1",
                              unicode_info=unicode_info)
            target = _lnk.parse(data)
            self.assertEqual(target.path, r"C:\Program Files\Google\Chrome\Application\chrome.exe")
            self.assertEqual(target.arguments, "--profile 1")
            self.assertFalse(target.advertised)

    def test_non_ascii_path(self):
        target = _lnk.parse(_lnk.build(r"C:\Programme\Übersetzer\über.exe"))
        self.assertEqual(target.path, r"C:\Programme\Übersetzer\über.exe")

    def test_id_list_is_skipped(self):
        target = _lnk.parse(_lnk.build(r"C:\a.exe", id_list=b"\x14\x00" + b"\x01" * 18 + b"\x00\x00"))
        self.assertEqual(target.path, r"C:\a.exe")

    def test_environment_block_target(self):
        target = _lnk.parse(_lnk.build(r"%ProgramFiles%\App\app.exe", env_only=True))
        self.assertEqual(target.path, r"%ProgramFiles%\App\app.exe")

    def test_advertised_shortcut(self):
        target = _lnk.parse(_lnk.build("", advertised=True))
        self.assertIsNone(target.path)
        self.assertTrue(target.advertised)

    def test_garbage_and_truncation_raise_value_error(self):
        good = _lnk.build(r"C:\a.exe", arguments="x")
        for bad in (b"", b"x" * 100, good[:80], good[:100], b"\x4c\x00\x00\x00" + b"\x00" * 100,
                    good[:20] + b"\xff\xff\xff\xff" + good[24:60]):
            with self.assertRaises(ValueError):
                _lnk.parse(bad)
        with self.assertRaises(ValueError):
            _lnk.parse(good + b"\x00" * _lnk.MAX_FILE)

    def test_hostile_offsets_dont_crash(self):
        data = bytearray(_lnk.build(r"C:\a.exe"))
        # point the local base path past the end of the link info
        info = _lnk.HEADER_SIZE
        struct.pack_into("<I", data, info + 28, 0xFFFF)
        target = _lnk.parse(bytes(data))
        self.assertIsInstance(target, _lnk.LinkTarget)


class StartMenu(unittest.TestCase):
    def entry(self, name, target, **kw):
        return pure.shortcut_entry(rf"C:\Start\{name}.lnk", _lnk.build(target, **kw), expand=lambda p: p.replace("%X%", r"C:\X"))

    def test_apps_are_kept(self):
        e = self.entry("Google Chrome", r"C:\Program Files\Google\Chrome\chrome.exe")
        self.assertEqual(e.name, "Google Chrome")
        self.assertEqual(e.program, r"c:\program files\google\chrome\chrome.exe")
        self.assertEqual(e.launch, ("lnk", r"C:\Start\Google Chrome.lnk"))
        self.assertTrue(e.key.startswith("lnk:"))

    def test_environment_targets_are_expanded(self):
        self.assertEqual(self.entry("App", r"%X%\app.exe", env_only=True).program, r"c:\x\app.exe")

    def test_documents_uninstallers_and_help_are_left_out(self):
        self.assertIsNone(self.entry("Readme", r"C:\App\readme.txt"))
        self.assertIsNone(self.entry("Manual", r"C:\App\app.exe"))
        self.assertIsNone(self.entry("Uninstall App", r"C:\App\unins000.exe"))
        self.assertIsNone(self.entry("App Website", r"C:\App\site.url"))
        self.assertIsNone(self.entry("App", r"C:\App\uninstall.exe"))
        self.assertIsNone(pure.shortcut_entry(r"C:\Start\Broken.lnk", b"not a shortcut"))

    def test_advertised_and_shell_item_shortcuts_are_apps_without_a_program(self):
        e = self.entry("Word", "", advertised=True)
        self.assertIsNotNone(e)
        self.assertIsNone(e.program)
        explorer = pure.shortcut_entry(r"C:\Start\File Explorer.lnk", _shell_item_only_link())
        self.assertEqual(explorer.program, r"%windir%\explorer.exe")

    def test_dedupe_keeps_the_first(self):
        a, b = AppEntry("lnk:a", "Notepad"), AppEntry("lnk:b", "notepad")
        self.assertEqual(pure.dedupe([a, None, b]), [a])


def _shell_item_only_link() -> bytes:
    """A shortcut with only a shell item (like File Explorer's): no link info, no environment block."""
    header = struct.pack("<I16sI", _lnk.HEADER_SIZE, _lnk.LINK_CLSID, _lnk.HAS_ID_LIST | _lnk.IS_UNICODE)
    header += b"\x00" * (_lnk.HEADER_SIZE - len(header))
    id_list = b"\x14\x00" + b"\x1f\x50" + b"\x00" * 16 + b"\x00\x00"
    return header + struct.pack("<H", len(id_list)) + id_list + b"\x00\x00\x00\x00"


class StoreApps(unittest.TestCase):
    def test_only_valid_aumids(self):
        doc = ('[{"Name":"Microsoft Store","AppID":"Microsoft.WindowsStore_8wekyb3d8bbwe!App"},'
               '{"Name":"Chrome","AppID":"Chrome"},'
               '{"Name":"Evil","AppID":"x_8wekyb3d8bbwe!App & calc"},'
               '{"Name":"Evil2","AppID":"..\\\\..\\\\cmd_8wekyb3d8bbwe!App"},'
               '{"Name":"Calculator","AppID":"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"},'
               '{"Name":"","AppID":"A_8wekyb3d8bbwe!App"}, 5, null]')
        apps = pure.packaged_apps(doc)
        self.assertEqual([a.name for a in apps], ["Microsoft Store", "Calculator"])
        self.assertEqual(apps[0].launch, ("aumid", "Microsoft.WindowsStore_8wekyb3d8bbwe!App"))
        self.assertIsNone(apps[0].program)

    def test_single_object_and_garbage(self):
        self.assertEqual(len(pure.packaged_apps('{"Name":"A","AppID":"A.B_8wekyb3d8bbwe!App"}')), 1)
        self.assertEqual(pure.packaged_apps("not json"), [])
        self.assertEqual(pure.packaged_apps(""), [])


class PinOrder(unittest.TestCase):
    def test_sorted_by_the_favorites_blob(self):
        chrome = AppEntry("lnk:c", "Google Chrome", launch=("lnk", r"C:\P\Google Chrome.lnk"))
        explorer = AppEntry("lnk:e", "File Explorer", launch=("lnk", r"C:\P\File Explorer.lnk"))
        edge = AppEntry("lnk:m", "Microsoft Edge", launch=("lnk", r"C:\P\Microsoft Edge.lnk"))
        store = AppEntry("uwp:s", "Microsoft Store", launch=("aumid", "Microsoft.WindowsStore_8wekyb3d8bbwe!App"))
        calc = AppEntry("uwp:c", "Calculator", launch=("aumid", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"))
        blob = (b"\x00junk" + "File Explorer.lnk".encode("utf-16-le") + b"\x01" * 20 +
                "Microsoft.WindowsStore_8wekyb3d8bbwe!App".encode("utf-16-le") + b"\x02" * 9 +
                "Google Chrome.lnk".encode("utf-16-le"))
        order = pure.pin_order([chrome, edge, explorer], [store, calc], blob)
        self.assertEqual([e.name for e in order], ["File Explorer", "Microsoft Store", "Google Chrome", "Microsoft Edge"])

    def test_without_a_blob_shortcuts_are_alphabetical_and_store_apps_left_out(self):
        b = AppEntry("lnk:b", "Beta", launch=("lnk", "Beta.lnk"))
        a = AppEntry("lnk:a", "alpha", launch=("lnk", "alpha.lnk"))
        s = AppEntry("uwp:s", "Store", launch=("aumid", "S_8wekyb3d8bbwe!App"))
        self.assertEqual([e.name for e in pure.pin_order([b, a], [s], None)], ["alpha", "Beta"])


class Icons(unittest.TestCase):
    def test_png_round_trip(self):
        rgba = bytes(range(256)) * 4  # 16x16
        png = _png.encode_rgba(16, 16, rgba)
        self.assertEqual(_png.decode_size(png), (16, 16))
        idat = png.index(b"IDAT")
        (length,) = struct.unpack(">I", png[idat - 4:idat])
        raw = zlib.decompress(png[idat + 4:idat + 4 + length])
        self.assertEqual(b"".join(raw[y * 65 + 1:(y + 1) * 65] for y in range(16)), rgba)
        with self.assertRaises(ValueError):
            _png.encode_rgba(2, 2, b"\x00" * 15)
        with self.assertRaises(ValueError):
            _png.decode_size(b"GIF89a")

    def test_downscale_weights_alpha(self):
        # 2x2 -> 1x1: one opaque red pixel, three transparent black ones -> red at quarter alpha, not dark red
        rgba = b"\xff\x00\x00\xff" + b"\x00\x00\x00\x00" * 3
        w, h, out = _png.downscale(2, 2, rgba, 1)
        self.assertEqual((w, h), (1, 1))
        self.assertEqual(out, b"\xff\x00\x00\x3f")
        self.assertEqual(_png.downscale(4, 4, b"\x00" * 64, 48), (4, 4, b"\x00" * 64))
        w, h, _ = _png.downscale(256, 128, b"\x00" * 256 * 128 * 4, 48)
        self.assertEqual((w, h), (48, 24))

    def test_icon_alpha_from_the_mask_when_the_colour_has_none(self):
        color = b"\x10\x20\x30\x00" * 2                     # BGRA, alpha all zero
        mask = b"\xff\xff\xff\x00" + b"\x00\x00\x00\x00"    # first pixel transparent
        self.assertEqual(pure.icon_rgba(color, mask, 2, 1), b"\x30\x20\x10\x00" + b"\x30\x20\x10\xff")
        self.assertEqual(pure.icon_rgba(b"\x10\x20\x30\x80", None, 1, 1), b"\x30\x20\x10\x80")
        self.assertEqual(pure.icon_rgba(b"\x10\x20\x30\x00", None, 1, 1), b"\x30\x20\x10\xff")


@unittest.skipUnless(sys.platform == "win32", "Windows only")
class LiveWindows(unittest.TestCase):
    """Read-only: lists and draws, never launches or changes a window."""

    @classmethod
    def setUpClass(cls):
        from hostos.shell_windows import WindowsShell
        cls.shell = WindowsShell()

    def test_listing_and_icons(self):
        installed = self.shell.installed()
        self.assertTrue(installed, "the Start Menu lists apps")
        for app in installed:
            self.assertTrue(app.name)
            if app.launch[0] == "aumid":
                self.assertRegex(app.launch[1], pure.AUMID)
        pinned = self.shell.pinned()
        self.assertIsInstance(pinned, list)
        windows = self.shell.windows()
        self.assertFalse(any(w.pid == os.getpid() for w in windows), "the agent's own windows are never listed")
        png = None
        for app in installed[:10]:
            png = self.shell.app_icon(app, 48) or png
        self.assertIsNotNone(png, "some app has an icon")
        w, h = _png.decode_size(png)
        self.assertTrue(w <= 48 and h <= 48)

    def test_launch_refuses_what_it_did_not_find(self):
        from hostos.shell_windows import WindowsShell
        with tempfile.TemporaryDirectory() as folder:
            outside = os.path.join(folder, "evil.lnk")
            with open(outside, "wb") as f:
                f.write(_lnk.build(r"C:\Windows\System32\calc.exe"))
            for spec in (("lnk", outside), ("lnk", r"C:\Windows\System32\calc.exe"), ("aumid", "calc & whoami"),
                         ("exe", r"C:\Windows\System32\calc.exe"), ("aumid", "x_8wekyb3d8bbwe!App /c calc")):
                with self.assertRaises(OSError, msg=spec):
                    WindowsShell().launch(AppEntry("x", "x", launch=spec))
            with self.assertRaises(OSError):
                WindowsShell().launch(AppEntry("x", "x"))

    def test_actions_refuse_windows_that_are_not_on_the_taskbar(self):
        self.assertFalse(self.shell.window_action(1, "close"))  # not a window
        self.assertFalse(self.shell.window_action(1, "explode"))


if __name__ == "__main__":
    unittest.main()
