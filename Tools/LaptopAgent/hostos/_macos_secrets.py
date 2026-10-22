"""The macOS secret store, without pyobjc: identity.key is AES-256-GCM encrypted under a random wrap key that lives in
the login Keychain (through the keyring package, which the caller passes in, so tests can use a fake).

Blob layout: version byte 1, then a 12-byte nonce + AES-GCM ciphertext and tag. The wrap key is never regenerated once a blob may
exist: a missing entry on unprotect is an error, not a reason to make a new key.
"""
import base64
import os

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

ACCOUNT = "wrap-key"
VERSION = b"\x01"
NONCE_LEN, KEY_LEN, TAG_LEN = 12, 32, 16


def service() -> str:
    """Read at call time so a test can point it at a throwaway entry and stay off the real key."""
    return os.environ.get("SPATIAL_LAUNCHER_KEYCHAIN_SERVICE", "SpatialLauncher.agent.identity.v1")


def wrap(data: bytes, key: bytes) -> bytes:
    nonce = os.urandom(NONCE_LEN)
    return VERSION + nonce + AESGCM(key).encrypt(nonce, data, None)


def unwrap(blob: bytes, key: bytes) -> bytes:
    if len(blob) < 1 + NONCE_LEN + TAG_LEN:
        raise ValueError("identity.key is too short to be an encrypted key")
    if blob[:1] != VERSION:
        raise ValueError(f"identity.key has an unknown format version {blob[0]}")
    try:
        return AESGCM(key).decrypt(blob[1:1 + NONCE_LEN], blob[1 + NONCE_LEN:], None)
    except InvalidTag:
        raise ValueError("identity.key doesn't decrypt with the Keychain key (corrupt, or the key changed)") from None


def load_key(kr, create: bool) -> bytes | None:
    """The wrap key from the Keychain; None if it isn't there and create is False. Keychain failures are OSErrors."""
    keyring_error = getattr(getattr(kr, "errors", None), "KeyringError", ())
    try:
        stored = kr.get_password(service(), ACCOUNT)
        if stored is None:
            if not create:
                return None
            stored = base64.b64encode(os.urandom(KEY_LEN)).decode("ascii")
            kr.set_password(service(), ACCOUNT, stored)
    except keyring_error as e:
        raise OSError(f"the Keychain isn't usable: {e}") from e
    try:
        key = base64.b64decode(stored, validate=True)
    except ValueError:
        key = b""
    if len(key) != KEY_LEN:
        raise ValueError(f"Keychain entry {service()} is not a 32-byte key")
    return key


def protect(kr, data: bytes) -> bytes:
    try:
        key = load_key(kr, create=True)
    except ValueError as e:  # an existing but malformed entry: never overwritten
        raise OSError(str(e)) from e
    return wrap(data, key)


def unprotect(kr, blob: bytes) -> bytes:
    key = load_key(kr, create=False)
    if key is None:
        raise ValueError(f"Keychain entry {service()} missing; identity.key cannot be decrypted")
    return unwrap(blob, key)
