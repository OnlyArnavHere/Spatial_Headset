"""Tests for macOS input. The pure ones (key table, modifiers, media, text chunks, wheel, drag, click count) run
everywhere; the ones that touch the real system run on a Mac only."""
import sys
import unittest

import keys
from hostos import _macos_input as inp

ON_MAC = sys.platform == "darwin"


class KeyTableTests(unittest.TestCase):
    def test_every_key_and_media_code_has_a_kvk(self):
        for name, (vk, _) in keys.KEY_CODES.items():
            self.assertIn(vk, inp.VK_TO_KVK, name)
        for action, (vk, _) in keys.MEDIA_ACTIONS.items():
            self.assertTrue(vk in inp.MEDIA_VK_TO_NX or vk in inp.VK_TO_KVK, action)
        self.assertEqual({vk for vk, _ in keys.MEDIA_ACTIONS.values() if vk in inp.MEDIA_VK_TO_NX},
                         {0xB3, 0xB0, 0xB1, 0xAF, 0xAE, 0xAD})

    def test_kvks_are_unique(self):
        self.assertEqual(len(set(inp.VK_TO_KVK.values())), len(inp.VK_TO_KVK))

    def test_spot_values(self):
        expected = {"A": 0x00, "Z": 0x06, "1": 0x12, "0": 0x1D, "Enter": 0x24, "Backspace": 0x33, "Tab": 0x30,
                    "Escape": 0x35, "Space": 0x31, "Left": 0x7B, "Right": 0x7C, "Down": 0x7D, "Up": 0x7E,
                    "Insert": 0x72, "Delete": 0x75, "Home": 0x73, "End": 0x77, "PageUp": 0x74, "PageDown": 0x79,
                    "F1": 0x7A, "F12": 0x6F, "Semicolon": 0x29, "Backquote": 0x32, "Shift": 0x38, "Ctrl": 0x37,
                    "Alt": 0x3A}
        for name, kvk in expected.items():
            self.assertEqual(inp.kvk_for(keys.KEY_CODES[name][0]), kvk, name)

    def test_unknown_vk_raises(self):
        with self.assertRaises(ValueError):
            inp.kvk_for(0x5B)  # the Windows key


class ModifierTests(unittest.TestCase):
    def test_masks(self):
        m = inp.Modifiers()
        self.assertEqual(m.update(0x11, True), 0x100000)  # Ctrl -> Command
        self.assertEqual(m.update(0x12, True), 0x100000 | 0x80000)
        self.assertEqual(m.update(0x10, True), 0x100000 | 0x80000 | 0x20000)
        self.assertEqual(m.update(0x41, True), m.mask)  # a letter changes nothing
        self.assertEqual(m.update(0x11, False), 0x80000 | 0x20000)
        m.update(0x12, False)
        self.assertEqual(m.update(0x10, False), 0)


class MediaTests(unittest.TestCase):
    def test_data1_and_flags(self):
        self.assertEqual(inp.media_data1(16, True), 0x100A00)
        self.assertEqual(inp.media_data1(16, False), 0x100B00)
        self.assertEqual(inp.media_data1(0, True), 0x0A00)
        self.assertEqual(inp.media_data1(7, False), 0x070B00)
        self.assertEqual((inp.media_flags(True), inp.media_flags(False)), (0xA00, 0xB00))
        self.assertEqual(inp.MEDIA_VK_TO_NX, {0xB3: 16, 0xB0: 17, 0xB1: 18, 0xAF: 0, 0xAE: 1, 0xAD: 7})


class TextTests(unittest.TestCase):
    def test_chunks_respect_the_limit_and_pairs(self):
        text = "a" * 19 + "\U0001F600" + "b"  # the emoji starts at unit 19 and would end at 21
        chunks = inp.unicode_chunks(text)
        self.assertEqual(chunks, ["a" * 19, "\U0001F600" + "b"])
        self.assertEqual("".join(chunks), text)
        self.assertEqual(inp.unicode_chunks("a" * 18 + "\U0001F600"), ["a" * 18 + "\U0001F600"])  # exactly 20

    def test_every_chunk_fits(self):
        text = "x\U0001F600y" * 30 + "é" * 7
        chunks = inp.unicode_chunks(text)
        self.assertTrue(all(0 < inp.utf16_length(c) <= 20 for c in chunks))
        self.assertEqual("".join(chunks), text)
        self.assertEqual(inp.unicode_chunks(""), [])


class MouseTests(unittest.TestCase):
    def test_wheel_carries_the_remainder(self):
        w = inp.WheelAccumulator()
        self.assertEqual([w.lines(n) for n in (1, 0.5, 0.5, -1)], [3, 1, 2, -3])
        self.assertEqual(w.lines(0.1), 0)

    def test_drag_selection(self):
        b = inp.ButtonState()
        self.assertEqual(b.move_event(), inp.MOUSE_MOVED)
        b.set("right", True)
        self.assertEqual(b.move_event(), inp.RIGHT_DRAGGED)
        b.set("left", True)
        self.assertEqual(b.move_event(), inp.LEFT_DRAGGED)
        b.set("left", False)
        b.set("right", False)
        self.assertEqual(b.move_event(), inp.MOUSE_MOVED)
        self.assertEqual((inp.button_event("left", True), inp.button_event("left", False),
                          inp.button_event("right", True), inp.button_event("right", False)), (1, 2, 3, 4))

    def test_click_count_by_time_and_distance(self):
        c = inp.ClickCounter()
        self.assertEqual(c.press("left", 100, 100, 0.0, 0.5), 1)
        self.assertEqual(c.release(), 1)
        self.assertEqual(c.press("left", 103, 98, 0.3, 0.5), 2)
        self.assertEqual(c.release(), 2)
        self.assertEqual(c.press("left", 103, 98, 0.5, 0.5), 3)
        self.assertEqual(c.press("left", 103, 98, 1.5, 0.5), 1)   # too late
        self.assertEqual(c.press("left", 110, 98, 1.6, 0.5), 1)   # too far
        self.assertEqual(c.press("right", 110, 98, 1.7, 0.5), 1)  # another button
        self.assertEqual(c.press("right", 110, 98, 1.8, 0.5), 2)


class WindowListTests(unittest.TestCase):
    @staticmethod
    def cg(number, layer=0, alpha=1.0, x=0, y=0, w=100, h=100, pid=1):
        return {"kCGWindowNumber": number, "kCGWindowLayer": layer, "kCGWindowAlpha": alpha, "kCGWindowOwnerPID": pid,
                "kCGWindowBounds": {"X": x, "Y": y, "Width": w, "Height": h}}

    def test_window_at_front_to_back(self):
        infos = [self.cg(1, alpha=0.0), self.cg(2, layer=-5), self.cg(3, x=50, w=10), self.cg(4)]
        self.assertEqual(inp.window_at(55, 5, infos), 3)
        self.assertEqual(inp.window_at(5, 5, infos), 4)
        self.assertEqual(inp.window_at(100, 5, infos), 0)  # the right edge is outside

    def test_foreground_is_the_frontmost_layer_zero_window_of_the_pid(self):
        infos = [self.cg(1, layer=25, pid=7), self.cg(2, pid=8), self.cg(3, pid=7), self.cg(4, pid=7)]
        self.assertEqual(inp.frontmost_window(7, infos), 3)
        self.assertEqual(inp.frontmost_window(9, infos), 0)

    def test_ax_window_matching(self):
        bounds = {"X": 10.0, "Y": 20.0, "Width": 800.0, "Height": 600.0}
        self.assertTrue(inp.ax_matches("Doc", (10.5, 19.5), (800, 600.9), "Doc", bounds))
        self.assertFalse(inp.ax_matches("Doc", (12, 20), (800, 600), "Doc", bounds))
        self.assertFalse(inp.ax_matches("Other", (10, 20), (800, 600), "Doc", bounds))
        self.assertTrue(inp.ax_matches(None, (10, 20), (800, 600), "", bounds))


class CleanupTests(unittest.TestCase):
    def test_pick_ax_window(self):
        bounds = {"X": 10.0, "Y": 20.0, "Width": 800.0, "Height": 600.0}
        fits = ((10, 20), (800, 600))
        self.assertEqual(inp.pick_ax_window([("a", "Other", *fits), ("b", "Doc", *fits)], "Doc", bounds), "b")
        self.assertEqual(inp.pick_ax_window([("a", "", *fits), ("b", "", (500, 500), (800, 600))], "Doc", bounds), "a")
        self.assertIsNone(inp.pick_ax_window([("a", "", *fits), ("b", "", *fits)], "Doc", bounds))  # ambiguous
        self.assertIsNone(inp.pick_ax_window([("a", "Other", *fits)], "Doc", bounds))

    def test_release_list(self):
        self.assertEqual(inp.release_list({0x12, 0x11}, {"right", "left"}),
                         [("key", 0x11), ("key", 0x12), ("button", "left"), ("button", "right")])
        self.assertEqual(inp.release_list(set(), set()), [])
        m, b = inp.Modifiers(), inp.ButtonState()
        m.update(0x11, True)
        b.set("left", True)
        self.assertEqual(inp.release_list(m.held, b.held), [("key", 0x11), ("button", "left")])

    def test_ttl_cache(self):
        now, calls = [0.0], []
        cache = inp.TtlCache(1.0, lambda: now[0])
        make = lambda: calls.append(1) or len(calls)
        self.assertEqual((cache.get(make), cache.get(make)), (1, 1))
        now[0] = 0.9
        self.assertEqual(cache.get(make), 1)
        now[0] = 1.0
        self.assertEqual(cache.get(make), 2)


@unittest.skipUnless(ON_MAC, "needs macOS")
class MacOnlyTests(unittest.TestCase):
    def test_preflight_has_both_rows(self):
        from hostos import macos
        rows = macos.preflight()
        self.assertEqual([r[0] for r in rows], ["Screen Recording permission", "Accessibility permission"])
        self.assertTrue(all("Privacy & Security" in r[2] for r in rows))

    def test_window_at_the_centre_of_the_front_window(self):
        from hostos import macos
        front = next((i for i in macos._onscreen() if i.get("kCGWindowLayer", 0) >= 0 and i.get("kCGWindowAlpha", 1) > 0
                      and i["kCGWindowBounds"]["Width"] > 2 and i["kCGWindowBounds"]["Height"] > 2), None)
        if front is None:
            self.skipTest("no window on screen")
        b = front["kCGWindowBounds"]
        self.assertEqual(macos.window_at(b["X"] + b["Width"] / 2, b["Y"] + b["Height"] / 2), front["kCGWindowNumber"])


if __name__ == "__main__":
    unittest.main()
