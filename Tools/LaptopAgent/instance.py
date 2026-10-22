"""One app per user. The first instance holds an OS file lock in the data folder and listens on a loopback "show"
socket; its port is written in the lock file. A second launch sends "show" to it and exits. The OS drops a dead
process's lock, so a stale lock file (with an old port in it) is simply taken over.
"""
import logging
import os
import socket
import sys
import threading
import time
from pathlib import Path
from typing import Callable

import paths

log = logging.getLogger("instance")
LOCK_NAME = "instance.lock"
LOCK_OFFSET = 1000  # the locked byte lies past the port text, so other processes can still read the port
PORT_FIELD = 16
SHOW = b"show\n"


def _lock(fd: int) -> bool:
    """Takes the exclusive lock without blocking; False if another process holds it."""
    try:
        if sys.platform == "win32":
            import msvcrt
            os.lseek(fd, LOCK_OFFSET, os.SEEK_SET)
            msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return True
    except OSError:
        return False


def _unlock(fd: int) -> None:
    try:
        if sys.platform == "win32":
            import msvcrt
            os.lseek(fd, LOCK_OFFSET, os.SEEK_SET)
            msvcrt.locking(fd, msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(fd, fcntl.LOCK_UN)
    except OSError:
        pass


class SingleInstance:
    def __init__(self, directory: Path | None = None):
        self.path = (directory or paths.data_dir()) / LOCK_NAME
        self._fd: int | None = None
        self._server: socket.socket | None = None
        self._thread: threading.Thread | None = None

    def acquire(self) -> bool:
        """True if this process is now the one instance; False if another holds the lock."""
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(self.path, os.O_RDWR | os.O_CREAT, 0o600)
        if not _lock(fd):
            os.close(fd)
            return False
        self._fd = fd
        os.lseek(fd, 0, os.SEEK_SET)  # blank any old port now, so a second launch waits for ours
        os.write(fd, b" " * PORT_FIELD)
        return True

    def serve(self, on_show: Callable[[], None]) -> None:
        """Listens for "show" on 127.0.0.1 and calls on_show() (on the listener thread) for each. Call after acquire()."""
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.bind(("127.0.0.1", 0))
        server.listen(4)
        self._server = server
        os.lseek(self._fd, 0, os.SEEK_SET)
        os.write(self._fd, f"{server.getsockname()[1]:<{PORT_FIELD}}".encode("ascii"))
        self._thread = threading.Thread(target=self._accept, args=(server, on_show), daemon=True, name="instance")
        self._thread.start()

    @staticmethod
    def _accept(server: socket.socket, on_show: Callable[[], None]) -> None:
        while True:
            try:
                conn, _ = server.accept()
            except OSError:
                return  # closed
            with conn:
                try:
                    conn.settimeout(1.0)
                    if conn.recv(16).strip() == SHOW.strip():
                        on_show()
                except OSError:
                    pass
                except Exception:
                    log.exception("on_show failed")

    def signal_existing(self, wait_s: float = 4.0) -> bool:
        """Asks the running instance to show its window. The other instance may still be starting (its port appears
        a moment after it takes the lock), so this retries for up to wait_s seconds. False if it never answers."""
        deadline = time.monotonic() + wait_s
        while True:
            try:
                with open(self.path, "rb", buffering=0) as f:  # unbuffered: a buffered read would touch the locked byte
                    port = int(f.read(PORT_FIELD).decode("ascii").strip())
                with socket.create_connection(("127.0.0.1", port), timeout=2.0) as conn:
                    conn.sendall(SHOW)
                return True
            except (OSError, ValueError):
                if time.monotonic() >= deadline:
                    return False
                time.sleep(0.1)

    def release(self) -> None:
        server, self._server = self._server, None
        if server is not None:
            server.close()
        fd, self._fd = self._fd, None
        if fd is not None:
            _unlock(fd)
            os.close(fd)
