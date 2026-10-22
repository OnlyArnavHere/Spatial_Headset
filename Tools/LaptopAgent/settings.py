"""The app's saved settings: one small JSON file in the per-user data folder.

Safe by default: control is off. A missing or corrupt file, a wrong type or a bad address gives the default for that
field (and never turns control on)."""
import json
import logging
from dataclasses import asdict, dataclass, fields
from pathlib import Path

import autostart
import discovery
import identity
import paths

log = logging.getLogger("settings")


@dataclass
class Settings:
    allow_control: bool = False   # mouse and keyboard from the headset (each grant is still asked on this laptop)
    cast_only: bool = False       # view only plus media controls; excludes allow_control
    start_at_login: bool = False
    headset_address: str = ""     # IP[:PORT] to dial instead of Bonjour
    wired: bool = False

    def validate(self) -> "Settings":
        """Returns self, or raises ValueError (same rules as the command line)."""
        if self.allow_control and self.cast_only:
            raise ValueError("Allow control and Cast only can't both be on.")
        self.headset_address = self.headset_address.strip()
        if self.headset_address:
            discovery.parse_headset(self.headset_address)  # raises ValueError
        return self


def settings_path() -> Path:
    return paths.data_dir() / "settings.json"


def from_dict(data) -> Settings:
    """Unknown keys are ignored; anything of the wrong type or invalid falls back to the default."""
    result = Settings()
    if not isinstance(data, dict):
        return result
    for field in fields(Settings):
        value = data.get(field.name)
        if isinstance(value, type(getattr(result, field.name))):
            setattr(result, field.name, value)
    try:
        return result.validate()
    except ValueError:
        if result.allow_control and result.cast_only:
            result.allow_control = False
        if result.headset_address:
            try:
                discovery.parse_headset(result.headset_address)
            except ValueError:
                result.headset_address = ""
        return result


def load(path: Path | None = None, login_status=None) -> Settings:
    """Reads the file. start_at_login is not trusted from the file: it mirrors the system (the Run value / LaunchAgent),
    which the user can also change outside the app."""
    path = path or settings_path()
    try:
        loaded = from_dict(json.loads(path.read_text(encoding="utf-8")))
    except FileNotFoundError:
        loaded = Settings()
    except (OSError, ValueError) as e:
        log.warning("Ignoring unreadable settings file %s (%s)", path, e)
        loaded = Settings()
    try:
        loaded.start_at_login = (login_status or autostart.status)()[0] == autostart.ENABLED
    except Exception:
        log.debug("Couldn't read the start-at-login state", exc_info=True)
        loaded.start_at_login = False
    return loaded


def save(settings: Settings, path: Path | None = None) -> None:
    """Validates, then writes atomically. Raises ValueError for invalid settings."""
    settings.validate()
    data = json.dumps(asdict(settings), indent=2).encode("utf-8")
    identity.atomic_write(path or settings_path(), data)
