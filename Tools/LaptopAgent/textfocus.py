"""Is the laptop's keyboard focus in a text field? The headset uses the answer to pop up its keyboard on its own.

editable() -> True, False, or None when this OS can't tell. It is read-only (nothing is clicked or focused) and cheap
enough to poll a few times a second. Blocking: call it off the event loop.

- Windows: UI Automation (the accessibility API screen readers use) through its COM interface with ctypes, so there is
  nothing to install. The focused element counts as a text field when it is an Edit control, or any control whose
  Value can be written (a combo box you can type into, a web page's contenteditable, a search box), and is not
  read-only. A whole web page (a Document whose Value is read-only) is not a text field.
- macOS: the Accessibility API (the same permission the agent already needs for control): the focused element's role
  is a text field, text area, search field or combo box, or the element says it is editable.
- Linux: None (no check).
"""
import sys
import threading

_local = threading.local()


def editable() -> bool | None:
    try:
        if sys.platform == "win32":
            return _windows()
        if sys.platform == "darwin":
            return _macos()
    except Exception:
        return None
    return None


# --- Windows -----------------------------------------------------------------------------------------------------

UIA_PROCESS_ID = 30002
UIA_CONTROL_TYPE = 30003
UIA_IS_ENABLED = 30010
UIA_IS_VALUE_PATTERN_AVAILABLE = 30043
UIA_VALUE_IS_READ_ONLY = 30046
CONTROL_EDIT = 50004
CONTROL_DOCUMENT = 50030
CONTROL_COMBO_BOX = 50003

# Vtable slots (IUnknown takes 0-2).
_GET_FOCUSED_ELEMENT = 8          # IUIAutomation::GetFocusedElement
_GET_CURRENT_PROPERTY_VALUE = 10  # IUIAutomationElement::GetCurrentPropertyValue


def _windows_automation():
    """One IUIAutomation per thread (COM objects belong to the thread that made them)."""
    automation = getattr(_local, "automation", None)
    if automation is not None:
        return automation
    import ctypes
    from ctypes import wintypes

    ole32 = ctypes.OleDLL("ole32")
    try:
        ole32.CoInitializeEx(None, 0)  # COINIT_MULTITHREADED
    except OSError as e:
        if (getattr(e, "winerror", 0) or 0) & 0xFFFFFFFF != 0x80010106:  # RPC_E_CHANGED_MODE: already set up, usable
            raise

    class GUID(ctypes.Structure):
        _fields_ = [("Data1", wintypes.DWORD), ("Data2", wintypes.WORD), ("Data3", wintypes.WORD),
                    ("Data4", ctypes.c_ubyte * 8)]

    def guid(text: str) -> GUID:
        g = GUID()
        ole32.CLSIDFromString(ctypes.c_wchar_p(text), ctypes.byref(g))
        return g

    clsid = guid("{ff48dba4-60ef-4201-aa87-54103eef594e}")  # CUIAutomation
    iid = guid("{30cbe57d-d9d0-452a-ab13-7ac5ac4825ee}")    # IUIAutomation
    pointer = ctypes.c_void_p()
    ole32.CoCreateInstance(ctypes.byref(clsid), None, 1 | 4, ctypes.byref(iid), ctypes.byref(pointer))  # INPROC|LOCAL
    _local.automation = pointer
    return pointer


class _Variant:
    """A minimal VARIANT for integer and boolean properties (no strings, so nothing to free)."""

    def __init__(self):
        import ctypes

        class VARIANT(ctypes.Structure):
            _fields_ = [("vt", ctypes.c_ushort), ("r1", ctypes.c_ushort), ("r2", ctypes.c_ushort),
                        ("r3", ctypes.c_ushort), ("value", ctypes.c_longlong), ("extra", ctypes.c_longlong)]
        self.raw = VARIANT()


def _call(pointer, index: int, restype, *argtypes):
    import ctypes
    vtable = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
    prototype = ctypes.WINFUNCTYPE(restype, ctypes.c_void_p, *argtypes)
    return prototype(vtable[index])


def _release(pointer) -> None:
    import ctypes
    if pointer:
        _call(pointer, 2, ctypes.c_ulong)(pointer)


def _property(element, property_id: int):
    """An integer/boolean property: int, bool, or None when it is empty or of another type."""
    import ctypes
    variant = _Variant().raw
    hr = _call(element, _GET_CURRENT_PROPERTY_VALUE, ctypes.c_long, ctypes.c_int, ctypes.c_void_p)(
        element, property_id, ctypes.byref(variant))
    if hr != 0:
        return None
    if variant.vt == 3:   # VT_I4
        return ctypes.c_int32(variant.value & 0xFFFFFFFF).value
    if variant.vt == 11:  # VT_BOOL: -1 is true
        return (variant.value & 0xFFFF) != 0
    if variant.vt == 0x2003 or variant.vt == 8:  # an array or a string: free it properly
        ctypes.OleDLL("oleaut32").VariantClear(ctypes.byref(variant))
    return None


def is_text_field(control_type, value_available, read_only, enabled=True) -> bool:
    """The Windows rule on plain values (tested without UI Automation)."""
    if enabled is False:
        return False
    if control_type == CONTROL_EDIT:
        return read_only is not True
    return bool(value_available) and read_only is False


def _windows() -> bool | None:
    import ctypes
    automation = _windows_automation()
    element = ctypes.c_void_p()
    hr = _call(automation, _GET_FOCUSED_ELEMENT, ctypes.c_long, ctypes.c_void_p)(automation, ctypes.byref(element))
    if hr != 0 or not element:
        return False
    try:
        return is_text_field(_property(element, UIA_CONTROL_TYPE), _property(element, UIA_IS_VALUE_PATTERN_AVAILABLE),
                             _property(element, UIA_VALUE_IS_READ_ONLY), _property(element, UIA_IS_ENABLED))
    finally:
        _release(element)


# --- macOS -------------------------------------------------------------------------------------------------------

TEXT_ROLES = {"AXTextField", "AXTextArea", "AXSearchField", "AXComboBox", "AXSecureTextField"}


def _macos() -> bool | None:
    from ApplicationServices import (AXUIElementCopyAttributeValue, AXUIElementCreateSystemWide,
                                     kAXFocusedUIElementAttribute, kAXRoleAttribute)
    system = AXUIElementCreateSystemWide()
    error, focused = AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute, None)
    if error != 0 or focused is None:
        return False
    error, role = AXUIElementCopyAttributeValue(focused, kAXRoleAttribute, None)
    if error == 0 and role in TEXT_ROLES:
        return True
    error, editable_flag = AXUIElementCopyAttributeValue(focused, "AXEditable", None)
    return bool(error == 0 and editable_flag)


if __name__ == "__main__":  # python textfocus.py: prints the answer twice a second; click around to check it
    import time
    while True:
        print(editable(), flush=True)
        time.sleep(0.5)
