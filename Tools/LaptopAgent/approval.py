"""The laptop's own question before a headset may control it: a top-most Yes/No box (default No, 30 s, then deny).
LaptopApprover matches dispatch.Approver: it returns True (Yes) or False (No), raises asyncio.TimeoutError when the box
timed out, and closes the box if the request is cancelled (session closed, kill switch). One box at a time."""
import asyncio
import logging
import threading
import unicodedata

import hostos

log = logging.getLogger("approval")

CAPTION = "Spatial Launcher"
PROMPT_TIMEOUT_MS = 30000
NAME_MAX = 32
DISMISS_WAIT_S = 2.0
_DROPPED = ("Cc", "Cf", "Zl", "Zp", "Co", "Cs", "Cn", "Pi", "Pf", "Ps", "Pe", "Mn")  # incl. quotes, brackets, marks
_one_box = threading.Lock()  # process-wide: a second request is denied at once, without a second box


def sanitize_name(name) -> str:
    """The headset's name is attacker-controlled text shown in a security prompt: plain, one line, short."""
    text = unicodedata.normalize("NFKC", str(name or ""))
    text = "".join(" " if c.isspace() else c for c in text if unicodedata.category(c) not in _DROPPED or c.isspace() or c in "()[]{}")
    text = " ".join(text.replace('"', " ").replace("'", " ").split())
    if not text:
        return "Unnamed headset"
    return text[:NAME_MAX] + "…" if len(text) > NAME_MAX else text


def prompt_text(info: dict, keyboard: bool) -> str:
    return (f'Headset "{sanitize_name(info.get("name"))}" ({info.get("fingerprint", "unknown")}) wants to control this '
            f"laptop's mouse{' and keyboard' if keyboard else ''}. Allow?\n\nNo input is sent unless you click Yes.")


class LaptopApprover:
    def __init__(self, keyboard: bool = False, timeout_ms: int = PROMPT_TIMEOUT_MS):
        self.keyboard = keyboard
        self.timeout_ms = timeout_ms

    async def __call__(self, info: dict) -> bool:
        if not _one_box.acquire(blocking=False):
            log.info("Another approval prompt is open; denying this request")
            return False
        loop = asyncio.get_running_loop()
        future: asyncio.Future = loop.create_future()
        prompts = []
        text = prompt_text(info, self.keyboard)

        def deliver(kind: str, value) -> None:
            if not future.done():
                future.set_result((kind, value))

        def ask() -> None:
            try:
                prompt = hostos.open_prompt(text, CAPTION, self.timeout_ms)
                prompts.append(prompt)
                answer = ("ok", prompt.wait())
            except BaseException as e:
                answer = ("error", e)
            finally:
                _one_box.release()
            try:
                loop.call_soon_threadsafe(deliver, *answer)
            except RuntimeError:
                pass  # the loop is gone

        thread = threading.Thread(target=ask, name="approval-prompt", daemon=True)
        try:
            thread.start()
        except BaseException:
            _one_box.release()
            raise
        try:
            kind, value = await future
        except asyncio.CancelledError:
            await self._dismiss(thread, prompts)
            raise
        if kind == "error":
            log.warning("The approval prompt failed: %s", value)
            return False
        if value == "timeout":
            raise asyncio.TimeoutError()
        return value == "yes"

    @staticmethod
    async def _dismiss(thread: threading.Thread, prompts: list) -> None:
        """Answers No on the box, every 50 ms until its thread is gone (it may not exist yet), for up to 2 s."""
        loop = asyncio.get_running_loop()
        end = loop.time() + DISMISS_WAIT_S
        while thread.is_alive() and loop.time() < end:
            if prompts:
                prompts[0].dismiss()
            await asyncio.sleep(0.05)
