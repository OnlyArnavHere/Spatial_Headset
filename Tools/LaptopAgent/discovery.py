"""Finding the headset over Bonjour. Nothing scans the network. (The USB cable is usbmux.py.)

The headset advertises `_spatiallauncher._tcp.local.` (handshake.SERVICE_TYPE) under a random instance name with the
TXT record `v=1`, plus `m=pair` while it shows a pairing code. browse() yields each (address, port) once: normally
only services without `m=pair`, with pairing=True only those with it. Everything found is only a place to connect to: the
handshake decides whether the peer is trusted.

Questions are QM (multicast reply), not QU: on Windows many sockets share UDP 5353, so a unicast reply reaches only one
of them and an already-advertising headset would never be found.
"""
import asyncio
import ipaddress
import secrets

from zeroconf import IPVersion, ServiceStateChange
from zeroconf.asyncio import AsyncServiceBrowser, AsyncServiceInfo, AsyncZeroconf

from handshake import SERVICE_TYPE

RESOLVE_TIMEOUT_MS = 3000
MAX_RESOLVES = 32  # concurrent resolve tasks
MAX_PENDING = 256  # queued results, and remembered (address, port) pairs


def random_instance_name() -> str:
    """A Bonjour instance name from a CSPRNG with 64 bits: 'SL-' + 16 lowercase hex characters."""
    return "SL-" + secrets.token_hex(8)


async def browse(zeroconf: AsyncZeroconf | None = None, *, interfaces=None, pairing: bool = False,
                 instance: str | None = None):
    """Async generator of deduplicated (address, port) for every advertised headset. Runs until closed or cancelled;
    wrap it in asyncio.timeout / wait_for to stop. pairing=False yields headsets that are not showing a pairing code
    (no `m=pair`), pairing=True only those that are. instance keeps only the service with that instance name (the
    headset that scanned a QR advertises as "SL-" + its qr_id). Pass an AsyncZeroconf to reuse one, else one is created (and closed).

    Bounded against a flooding network: a name already being resolved is skipped, at most MAX_RESOLVES resolves run
    at once (further announcements are ignored), the result queue holds MAX_PENDING (further results are dropped) and
    the dedupe set remembers the newest MAX_PENDING entries."""
    own = zeroconf is None
    azc = zeroconf or (AsyncZeroconf(interfaces=interfaces) if interfaces is not None else AsyncZeroconf())
    found: asyncio.Queue = asyncio.Queue(MAX_PENDING)
    seen: dict[tuple[str, int], None] = {}  # insertion-ordered
    tasks: set[asyncio.Task] = set()
    resolving: set[str] = set()
    closed = False

    async def resolve(name: str) -> None:
        info = AsyncServiceInfo(SERVICE_TYPE, name)
        if not await info.async_request(azc.zeroconf, RESOLVE_TIMEOUT_MS):
            return
        if info.properties.get(b"v") != b"1" or not info.port:
            return
        if (info.properties.get(b"m") == b"pair") != pairing:
            return
        for address in info.parsed_addresses(IPVersion.All):
            ip = ipaddress.ip_address(address)
            if ip.is_link_local and ip.version == 6:
                continue  # no scope to connect with
            try:
                found.put_nowait((address, info.port))
            except asyncio.QueueFull:
                return

    def finished(name: str, task: asyncio.Task) -> None:
        tasks.discard(task)
        resolving.discard(name)

    def on_change(zeroconf, service_type, name, state_change):
        if closed or state_change is ServiceStateChange.Removed:
            return
        if instance is not None and name.split(".", 1)[0] != instance:
            return
        if name in resolving or len(tasks) >= MAX_RESOLVES:
            return
        resolving.add(name)
        task = asyncio.ensure_future(resolve(name))
        tasks.add(task)
        task.add_done_callback(lambda t, n=name: finished(n, t))

    browser = AsyncServiceBrowser(azc.zeroconf, SERVICE_TYPE, handlers=[on_change])
    try:
        while True:
            item = await found.get()
            if item not in seen:
                seen[item] = None
                if len(seen) > MAX_PENDING:
                    del seen[next(iter(seen))]
                yield item
    finally:
        closed = True
        try:
            await browser.async_cancel()
            pending = list(tasks)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
        finally:
            if own:
                await azc.async_close()
