"""Single instance: the first holds the lock, a second signals it, a stale lock is taken over."""
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

import instance


class InstanceTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.dir = Path(tmp.name)

    def new(self):
        one = instance.SingleInstance(self.dir)
        self.addCleanup(one.release)
        return one

    def test_second_instance_cannot_acquire_and_signals_the_first(self):
        first, second = self.new(), self.new()
        self.assertTrue(first.acquire())
        shown = threading.Event()
        first.serve(shown.set)
        self.assertFalse(second.acquire())
        self.assertTrue(second.signal_existing())
        self.assertTrue(shown.wait(3))

    def test_release_lets_a_new_instance_in(self):
        first = self.new()
        self.assertTrue(first.acquire())
        first.release()
        self.assertTrue(self.new().acquire())

    def test_stale_lock_file_is_taken_over(self):
        # a dead process left a lock file with its old port in it; nothing holds the OS lock any more
        (self.dir / instance.LOCK_NAME).write_bytes(b"54321".ljust(instance.PORT_FIELD))
        one = self.new()
        self.assertTrue(one.acquire())
        shown = threading.Event()
        one.serve(shown.set)
        self.assertTrue(self.new().signal_existing())
        self.assertTrue(shown.wait(3))

    def test_lock_held_by_a_dead_process_is_released_by_the_os(self):
        code = ("import sys, instance, pathlib; i = instance.SingleInstance(pathlib.Path(sys.argv[1])); "
                "sys.exit(0 if i.acquire() else 1)")
        result = subprocess.run([sys.executable, "-c", code, str(self.dir)], cwd=Path(instance.__file__).parent)
        self.assertEqual(result.returncode, 0)  # that process exited without releasing
        self.assertTrue(self.new().acquire())

    def test_signal_with_nobody_listening_is_false(self):
        self.assertFalse(self.new().signal_existing(0.2))  # no lock file
        (self.dir / instance.LOCK_NAME).write_bytes(b"1".ljust(instance.PORT_FIELD))  # port 1: refused
        self.assertFalse(self.new().signal_existing(0.2))

    def test_only_show_is_acted_on(self):
        first = self.new()
        first.acquire()
        shown = threading.Event()
        first.serve(shown.set)
        with open(first.path, "rb", buffering=0) as f:
            port = int(f.read(instance.PORT_FIELD).decode().strip())
        with socket.create_connection(("127.0.0.1", port), timeout=2) as conn:
            conn.sendall(b"hello\n")
        self.assertFalse(shown.wait(0.3))

    def test_acquire_blanks_an_old_port_and_signal_waits_for_the_new_one(self):
        (self.dir / instance.LOCK_NAME).write_bytes(b"1".ljust(instance.PORT_FIELD))  # stale port
        first = self.new()
        self.assertTrue(first.acquire())
        shown = threading.Event()
        threading.Timer(0.5, first.serve, args=(shown.set,)).start()  # the port shows up late
        self.assertTrue(self.new().signal_existing(5.0))
        self.assertTrue(shown.wait(3))

    def test_signal_gives_up_when_no_port_appears(self):
        first = self.new()
        first.acquire()
        start = time.monotonic()
        self.assertFalse(self.new().signal_existing(0.3))
        self.assertLess(time.monotonic() - start, 3)

    def test_a_failing_on_show_does_not_stop_the_listener(self):
        first = self.new()
        first.acquire()
        calls = []

        def on_show():
            calls.append(1)
            if len(calls) == 1:
                raise RuntimeError("boom")

        first.serve(on_show)
        self.assertTrue(self.new().signal_existing())
        self.assertTrue(self.new().signal_existing())
        for _ in range(50):
            if len(calls) == 2:
                break
            time.sleep(0.05)
        self.assertEqual(len(calls), 2)

    def test_server_is_loopback_only(self):
        first = self.new()
        first.acquire()
        first.serve(lambda: None)
        self.assertEqual(first._server.getsockname()[0], "127.0.0.1")


if __name__ == "__main__":
    unittest.main()
