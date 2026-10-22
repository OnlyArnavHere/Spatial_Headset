"""Kills the agent's child process groups when the agent goes away, however it goes (Ctrl+C, crash, kill -9).

Run as `python -I _reaper.py <agent pid>` (-I keeps this folder, which has a types.py, off sys.path). The agent writes
one process-group id per line to stdin as it starts children. When the agent exits (kqueue NOTE_EXIT on macOS, a pidfd on Linux) or stdin reaches
EOF, every group gets SIGTERM, then SIGKILL after a grace period. Importing this module has no side effects.

Known gap: a child started in the instant before the agent is SIGKILLed (after it was spawned, before its group id
reached this process) is not killed.
"""
import os
import signal
import sys
import time

GRACE_S = 2.0
DRAIN_S = 0.5


class Groups:
    """Process-group ids from stdin. A read can end in the middle of a line, so only complete lines count; a partial
    last line (the agent died mid-write) is dropped rather than guessed."""

    def __init__(self) -> None:
        self.ids: list[int] = []
        self._rest = b""

    def feed(self, data: bytes) -> None:
        self._rest += data
        complete, _, self._rest = self._rest.rpartition(b"\n")
        self.ids += parse_lines(complete)


def parse_lines(data: bytes) -> list[int]:
    """The process-group ids in complete lines; anything that isn't an integer above 1 is ignored."""
    ids = []
    for line in data.decode("ascii", "replace").splitlines():
        line = line.strip()
        if line.isdigit() and int(line) > 1:
            ids.append(int(line))
    return ids


def reads_first(events, is_read) -> list:
    """One kevent batch in the order to handle it: stdin reads before the agent's exit, so the last group ids are
    known before anything is killed."""
    return sorted(events, key=lambda event: not is_read(event))


def drain(groups: Groups, read, ready, budget: float = DRAIN_S, clock=time.monotonic) -> None:
    """After the agent has exited: take what is still in the pipe, without blocking, until EOF, until nothing more is
    readable, or until the budget is used up."""
    end = clock() + budget
    while clock() < end and ready():
        data = read()
        if not data:
            return
        groups.feed(data)


def _alive(group: int) -> bool:
    try:
        os.killpg(group, 0)
        return True
    except OSError:
        return False


def _signal_all(groups, sig) -> None:
    for group in groups:
        try:
            os.killpg(group, sig)
        except OSError:
            pass


def kill_groups(groups, grace: float = GRACE_S) -> None:
    _signal_all(groups, signal.SIGTERM)
    deadline = time.monotonic() + grace
    while time.monotonic() < deadline and any(_alive(g) for g in groups):
        time.sleep(0.05)
    _signal_all(groups, signal.SIGKILL)


def main(agent_pid: int) -> None:
    import select
    groups = Groups()
    kq = select.kqueue()
    try:
        kq.control([select.kevent(0, select.KQ_FILTER_READ, select.KQ_EV_ADD),
                    select.kevent(agent_pid, select.KQ_FILTER_PROC, select.KQ_EV_ADD | select.KQ_EV_ONESHOT,
                                  select.KQ_NOTE_EXIT)], 0)
    except OSError:  # the agent is already gone
        return
    while True:
        exited = eof = False
        for event in reads_first(kq.control(None, 4), lambda e: e.filter == select.KQ_FILTER_READ):
            if event.filter == select.KQ_FILTER_PROC:
                exited = True
            else:
                data = os.read(0, 4096)
                if data:
                    groups.feed(data)
                else:
                    eof = True
        if exited and not eof:
            drain(groups, lambda: os.read(0, 4096), lambda: bool(select.select([0], [], [], 0)[0]))
        if exited or eof:
            kill_groups(groups.ids)
            return


def main_linux(agent_pid: int) -> None:
    """Linux: wait for stdin EOF or for the agent's pidfd to become readable (it does when the agent exits). Without
    pidfd_open (kernel < 5.3) the agent's pid is polled twice a second instead."""
    import select
    groups = Groups()
    try:
        pidfd = os.pidfd_open(agent_pid)
    except ProcessLookupError:  # the agent is already gone
        return
    except (AttributeError, OSError):
        pidfd = None
    while True:
        ready = select.select([0] + ([pidfd] if pidfd is not None else []), [], [], None if pidfd is not None else 0.5)[0]
        if 0 in ready:
            data = os.read(0, 4096)
            if data:
                groups.feed(data)
                continue
            kill_groups(groups.ids)  # EOF: the agent closed the pipe or died
            return
        if pidfd is not None and pidfd in ready or pidfd is None and not _alive_pid(agent_pid):
            drain(groups, lambda: os.read(0, 4096), lambda: bool(select.select([0], [], [], 0)[0]))
            kill_groups(groups.ids)
            return


def _alive_pid(pid: int) -> bool:
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except OSError:
        return True


if __name__ == "__main__":
    (main_linux if sys.platform.startswith("linux") else main)(int(sys.argv[1]))
