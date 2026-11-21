"""Handle-relative staging and non-replacing Windows directory publication."""

from contextlib import contextmanager
import ctypes as c
from ctypes import wintypes as w
from uuid import uuid4

from .contract import require


class UnicodeString(c.Structure):
    _fields_ = [("length", w.USHORT), ("maximum_length", w.USHORT), ("buffer", w.LPWSTR)]


class ObjectAttributes(c.Structure):
    _fields_ = [("length", w.ULONG), ("root", w.HANDLE),
                ("name", c.POINTER(UnicodeString)), ("attributes", w.ULONG),
                ("security_descriptor", c.c_void_p), ("security_qos", c.c_void_p)]


class IoStatusBlock(c.Structure):
    _fields_ = [("status_or_pointer", c.c_void_p), ("information", c.c_size_t)]


class FileRenameInfo(c.Structure):
    _fields_ = [("replace_existing", w.BOOLEAN), ("root", w.HANDLE),
                ("name_length", w.ULONG), ("name", w.WCHAR * 1)]


DIRECTORY = 0x01
SYNCHRONOUS = 0x20
NON_DIRECTORY = 0x40
OPEN_REPARSE_POINT = 0x00200000
OPEN_EXISTING = 1
CREATE_NEW = 2
SYNCHRONIZE = 0x00100000
DELETE = 0x00010000
READ_ATTRIBUTES = 0x80
LIST_DIRECTORY = 0x01
WRITE_DATA = 0x02
SHARE_READ = 1
SHARE_WRITE = 2


def close_handle(handle):
    close = c.WinDLL("kernel32").CloseHandle
    close.argtypes = (w.HANDLE,)
    close.restype = w.BOOL
    close(handle)


def open_handle(root, name, *, access, share, disposition, options, error):
    native = c.create_unicode_buffer(name)
    string = UnicodeString(c.sizeof(native) - 2, c.sizeof(native), c.cast(native, w.LPWSTR))
    # OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE. A changed path fails closed.
    attributes = ObjectAttributes(c.sizeof(ObjectAttributes), root, c.pointer(string), 0x1040, None, None)
    handle = w.HANDLE()
    io = IoStatusBlock()
    create = c.WinDLL("ntdll").NtCreateFile
    create.argtypes = (c.POINTER(w.HANDLE), w.DWORD, c.POINTER(ObjectAttributes),
                       c.POINTER(IoStatusBlock), c.c_void_p, w.ULONG, w.ULONG, w.ULONG,
                       w.ULONG, c.c_void_p, w.ULONG)
    create.restype = w.LONG
    status = create(c.byref(handle), access | SYNCHRONIZE, c.byref(attributes), c.byref(io),
                    None, 0, share, disposition, options | SYNCHRONOUS, None, 0)
    require(status == 0, error)
    return handle


@contextmanager
def open_parent(path):
    # The caller holds ancestry guards and has already validated the local volume.
    handle = open_handle(None, "\\??\\" + str(path), access=LIST_DIRECTORY | READ_ATTRIBUTES,
                         share=SHARE_READ | SHARE_WRITE, disposition=OPEN_EXISTING,
                         options=DIRECTORY, error="OUTPUT_PARENT_OPEN_FAILED")
    try:
        yield handle
    finally:
        close_handle(handle)


@contextmanager
def create_stage(parent):
    handle = open_handle(parent, ".lab-preflight-" + uuid4().hex, access=DELETE | LIST_DIRECTORY,
                         share=SHARE_READ, disposition=CREATE_NEW, options=DIRECTORY,
                         error="STAGING_CREATE_FAILED")
    try:
        yield handle
    finally:
        close_handle(handle)


def create_report(directory, name):
    """The caller owns the returned handle and writes via its file descriptor."""
    return open_handle(directory, name, access=WRITE_DATA, share=0, disposition=CREATE_NEW,
                       options=NON_DIRECTORY, error="REPORT_CREATE_FAILED")


def rename_stage(handle, parent, name):
    # FileRenameInformation resolves the destination name relative to parent.
    encoded = name.encode("utf-16-le")
    offset = FileRenameInfo.name.offset
    buffer = c.create_string_buffer(max(c.sizeof(FileRenameInfo), offset + len(encoded) + 2))
    header = FileRenameInfo.from_buffer(buffer)
    header.replace_existing = False
    header.root = parent
    header.name_length = len(encoded)
    c.memmove(c.addressof(buffer) + offset, encoded, len(encoded))
    change = c.WinDLL("ntdll").NtSetInformationFile
    change.argtypes = (w.HANDLE, c.POINTER(IoStatusBlock), c.c_void_p, w.ULONG, c.c_int)
    change.restype = w.LONG
    io = IoStatusBlock()
    require(change(handle, c.byref(io), buffer, len(buffer), 10) == 0, "REPORT_PUBLICATION_FAILED")


def discard_stage(handle, files):
    for name in files:
        leaf = open_handle(handle, name, access=DELETE, share=0, disposition=OPEN_EXISTING,
                           options=NON_DIRECTORY | OPEN_REPARSE_POINT, error="STAGING_CLEANUP_FAILED")
        try:
            delete_on_close(leaf)
        finally:
            close_handle(leaf)
    delete_on_close(handle)


def delete_on_close(handle):
    change = c.WinDLL("kernel32").SetFileInformationByHandle
    change.argtypes = (w.HANDLE, c.c_int, c.c_void_p, w.DWORD)
    change.restype = w.BOOL
    delete_file = w.BOOLEAN(True)
    require(change(handle, 4, c.byref(delete_file), c.sizeof(delete_file)), "STAGING_CLEANUP_FAILED")
