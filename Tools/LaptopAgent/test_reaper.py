"""Regression test: _reaper.py must start its main when it is run as a script (the watcher is launched that way)."""
import runpy
import select
import sys
import unittest
from pathlib import Path
from unittest import mock

REAPER = Path(__file__).resolve().parent / "hostos" / "_reaper.py"


class Sentinel(Exception):
    pass


class ReaperScriptTests(unittest.TestCase):
    def test_running_as_a_script_calls_the_macos_main_with_the_agent_pid(self):
        seen = []

        def fake_kqueue():
            seen.append(sys.argv[1])
            raise Sentinel

        with mock.patch.object(sys, "argv", [str(REAPER), "4242"]), mock.patch.object(sys, "platform", "darwin"), \
                mock.patch.object(select, "kqueue", fake_kqueue, create=True):
            with self.assertRaises(Sentinel):
                runpy.run_path(str(REAPER), run_name="__main__")
        self.assertEqual(seen, ["4242"])

    def test_importing_has_no_side_effects(self):
        runpy.run_path(str(REAPER), run_name="not_main")


if __name__ == "__main__":
    unittest.main()
