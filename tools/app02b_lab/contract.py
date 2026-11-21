"""Strict parsing and protected, write-once evidence I/O."""

from contextlib import contextmanager
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import tempfile


class LabError(ValueError):
    def __init__(self, code: str, location: str = ""):
        self.code = code
        self.location = location
        super().__init__(code if not location else f"{code} ({location})")


def require(condition: bool, code: str, location: str = "") -> None:
    if not condition:
        raise LabError(code, location)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=True, allow_nan=False, sort_keys=True,
                       separators=(",", ":")) + "\n").encode("ascii")


def strict_json(data: bytes) -> object:
    def object_pairs(pairs):
        result = {}
        for key, value in pairs:
            check_text(key)
            require(key not in result, "DUPLICATE_JSON_PROPERTY")
            result[key] = value
        return result

    def reject_constant(_):
        raise LabError("NONFINITE_JSON_NUMBER")

    def finite(number):
        value = float(number)
        require(math.isfinite(value), "NONFINITE_JSON_NUMBER")
        return value

    def check_text(value):
        value.encode("utf-8", errors="strict")

    def check_strings(value):
        if isinstance(value, str):
            check_text(value)
        elif isinstance(value, dict):
            for item in value.values():
                check_strings(item)
        elif isinstance(value, list):
            for item in value:
                check_strings(item)

    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=object_pairs,
                           parse_constant=reject_constant, parse_float=finite)
        check_strings(value)
        return value
    except LabError:
        raise
    except (ValueError, RecursionError):
        raise LabError("INVALID_JSON") from None


def safe_path(path: Path, *, missing=False) -> Path:
    """Reject links/reparse points anywhere in the path, including the root."""
    path = path.absolute()
    require(".." not in path.parts and not any(":" in p or p.endswith((" ", "."))
                                               for p in path.parts[1:]),
            "UNSAFE_PATH")
    for part in (*reversed(path.parents), path):
        try:
            info = part.lstat()
        except FileNotFoundError:
            require(missing and part == path, "PATH_MISSING")
            break
        require(not stat.S_ISLNK(info.st_mode) and not (
            getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
        ), "LINK_OR_REPARSE_POINT")
        if part != path:
            require(stat.S_ISDIR(info.st_mode), "NOT_A_DIRECTORY")
    return path


_COMPONENT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", re.ASCII)
_RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(10)),
             *(f"LPT{i}" for i in range(10))}


def relative_parts(relative: str) -> tuple[str, ...]:
    parts = tuple(relative.split("/"))
    require(all(_COMPONENT.fullmatch(part) and not part.endswith(".")
                and part.split(".")[0].upper() not in _RESERVED for part in parts),
            "UNSAFE_RELATIVE_PATH")
    return parts


def child(root: Path, relative: str, *, missing=False) -> Path:
    return safe_path(root.joinpath(*relative_parts(relative)), missing=missing)


def read_bytes(path: Path) -> bytes:
    path = safe_path(path)
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "UNSAFE_FILE_TYPE")
    with path.open("rb") as stream:
        require(os.path.samestat(info, os.fstat(stream.fileno())), "FILE_CHANGED_DURING_OPEN")
        return stream.read()


def sync_directory(path: Path) -> None:
    if os.name != "nt":
        descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)


@contextmanager
def guarded_directory(path: Path):
    """Keep a write destination inside a trusted local directory hierarchy."""
    path = safe_path(path)
    require(path.is_dir(), "NOT_A_DIRECTORY")
    chain = (*reversed(path.parents), path)
    if os.name == "nt":
        # Open every directory without FILE_SHARE_DELETE. Windows then denies
        # rename or replacement of those directories until publication finishes.
        import ctypes
        from ctypes import wintypes

        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        open_directory = kernel.CreateFileW
        open_directory.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                   wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD,
                                   wintypes.HANDLE)
        open_directory.restype = wintypes.HANDLE
        close = kernel.CloseHandle
        close.argtypes = (wintypes.HANDLE,)
        close.restype = wintypes.BOOL
        invalid_handle = ctypes.c_void_p(-1).value
        handles = []
        try:
            for part in chain:
                # Share read/write; OPEN_EXISTING; BACKUP_SEMANTICS and
                # OPEN_REPARSE_POINT so a replaced leaf is never followed.
                handle = open_directory(str(part), 0, 3, None, 3, 0x02200000, None)
                require(handle not in (None, invalid_handle), "DIRECTORY_GUARD_FAILED")
                handles.append(handle)
                safe_path(part)
            yield
        finally:
            for handle in reversed(handles):
                close(handle)
    else:
        uid = os.getuid()
        for part in chain:
            info = part.lstat()
            require(stat.S_ISDIR(info.st_mode) and info.st_uid in (0, uid),
                    "UNTRUSTED_DIRECTORY_OWNER")
            # A sticky ancestor such as /tmp protects operator-owned entries.
            require(not (info.st_mode & 0o022) or
                    (part != path and info.st_mode & stat.S_ISVTX),
                    "UNTRUSTED_DIRECTORY_PERMISSIONS")
        yield


def publish(path: Path, data: bytes) -> None:
    """Publish complete bytes without replacing a destination, even in a race."""
    with guarded_directory(path.parent):
        _publish(path, data)


def _publish(path: Path, data: bytes) -> None:
    path = safe_path(path, missing=True)
    fd, temporary = tempfile.mkstemp(prefix=".lab-tmp-", dir=path.parent)
    staging = Path(temporary)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        if os.name == "nt":
            # Windows rename fails if the destination exists.
            staging.rename(path)
        else:
            # The link operation, unlike POSIX rename, cannot replace a file.
            os.link(staging, path)
            staging.unlink()
        sync_directory(path.parent)
    finally:
        staging.unlink(missing_ok=True)


def read_json(path: Path) -> object:
    return strict_json(read_bytes(path))
