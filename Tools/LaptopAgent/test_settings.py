"""Settings: defaults, round trip, validation, corrupt files."""
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import settings
from settings import Settings


class SettingsTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.path = Path(tmp.name) / "sub" / "settings.json"

    def test_defaults_are_safe(self):
        s = Settings()
        self.assertFalse(s.allow_control)
        self.assertFalse(s.cast_only)
        self.assertFalse(s.start_at_login)
        self.assertFalse(s.wired)
        self.assertEqual(s.headset_address, "")

    def test_missing_file_gives_defaults(self):
        self.assertEqual(settings.load(self.path), Settings())

    def test_round_trip(self):
        original = Settings(allow_control=True, headset_address="192.168.1.5:4000", wired=True)
        settings.save(original, self.path)
        self.assertEqual(settings.load(self.path), original)

    def test_path_is_in_the_data_dir(self):
        with mock.patch("identity.default_home", return_value=Path("H")):
            self.assertEqual(settings.settings_path(), Path("H") / "settings.json")

    def test_unknown_keys_are_ignored(self):
        self.path.parent.mkdir()
        self.path.write_text(json.dumps({"cast_only": True, "future": 1}))
        self.assertEqual(settings.load(self.path), Settings(cast_only=True))

    def test_bad_address_is_rejected_on_save(self):
        with self.assertRaises(ValueError):
            settings.save(Settings(headset_address="not a host:::"), self.path)
        self.assertFalse(self.path.exists())

    def test_control_and_cast_only_exclude_each_other(self):
        with self.assertRaises(ValueError):
            settings.save(Settings(allow_control=True, cast_only=True), self.path)

    def test_corrupt_file_gives_defaults(self):
        self.path.parent.mkdir()
        for text in ("{not json", "[]", "null", ""):
            self.path.write_text(text)
            self.assertEqual(settings.load(self.path), Settings(), text)

    def test_bad_values_fall_back_per_field_and_never_turn_control_on(self):
        self.path.parent.mkdir()
        self.path.write_text(json.dumps({"allow_control": "yes", "wired": True, "headset_address": "bad:::"}))
        self.assertEqual(settings.load(self.path), Settings(wired=True))
        self.path.write_text(json.dumps({"allow_control": True, "cast_only": True}))
        loaded = settings.load(self.path)
        self.assertFalse(loaded.allow_control)
        self.assertTrue(loaded.cast_only)

    def test_address_is_trimmed(self):
        self.assertEqual(Settings(headset_address=" 10.0.0.2 ").validate().headset_address, "10.0.0.2")


if __name__ == "__main__":
    unittest.main()
