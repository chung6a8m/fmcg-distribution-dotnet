"""Windows paths used by LAB-D01, independent of the launching environment."""

import os
from pathlib import Path
from uuid import UUID

from .contract import require


def windows_folders():
    import ctypes
    from ctypes import wintypes

    require(ctypes.sizeof(ctypes.c_void_p) == 8, "WINDOWS_64_BIT_REQUIRED")
    # LOAD_LIBRARY_SEARCH_SYSTEM32 excludes DLLs supplied through the shell's PATH.
    kernel = ctypes.WinDLL("kernel32", winmode=0x800)
    shell = ctypes.WinDLL("shell32", winmode=0x800)
    ole = ctypes.WinDLL("ole32", winmode=0x800)
    ole.CoTaskMemFree.argtypes = [ctypes.c_void_p]
    ole.CoTaskMemFree.restype = None
    known_folder = shell.SHGetKnownFolderPath
    known_folder.argtypes = [ctypes.c_void_p, wintypes.DWORD, wintypes.HANDLE,
                             ctypes.POINTER(ctypes.c_void_p)]
    known_folder.restype = ctypes.c_long

    def known(identifier):
        value = ctypes.c_void_p()
        guid = ctypes.create_string_buffer(UUID(identifier).bytes_le)
        try:
            require(known_folder(guid, 0, None, ctypes.byref(value)) == 0 and value,
                    "WINDOWS_ROOT_UNAVAILABLE")
            return Path(ctypes.wstring_at(value))
        finally:
            ole.CoTaskMemFree(value)

    def system(get_directory):
        buffer = ctypes.create_unicode_buffer(32768)
        get_directory.argtypes = [wintypes.LPWSTR, wintypes.UINT]
        get_directory.restype = wintypes.UINT
        require(0 < get_directory(buffer, len(buffer)) < len(buffer), "WINDOWS_ROOT_UNAVAILABLE")
        return Path(buffer.value)

    windows = system(kernel.GetSystemWindowsDirectoryW)
    return {
        "SystemRoot": windows,
        "windir": windows,
        "SystemDirectory": system(kernel.GetSystemDirectoryW),
        "ProgramFiles": known("905e63b6-c1bf-494e-b29c-65b732d3d21a"),
        "ProgramData": known("62ab5d82-fdc1-4dc3-a9dd-070d1d495d97"),
        "USERPROFILE": known("5e6c858f-0e22-4760-9afe-ea3317b67173"),
        "APPDATA": known("3eb685db-65f9-4cf6-a03a-e3ef65729f3d"),
        "LOCALAPPDATA": known("f1b32785-6fba-4fcf-9d55-7b8e7f157091"),
    }


def verified_windows_folders():
    paths = windows_folders()
    for name, path in paths.items():
        if name != "SystemDirectory":
            supplied = os.environ.get(name)
            require(supplied and Path(supplied) == path, "WINDOWS_ROOT_ENV_OVERRIDE")
    return paths
