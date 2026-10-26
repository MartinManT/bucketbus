#!/usr/bin/env python3
"""Check the published SQLite asset; Windows header inspection is not execution."""
import ctypes
from pathlib import Path
import struct
import sys
import tempfile

payload, rid = Path(sys.argv[1]), sys.argv[2]
if rid == "win-x64":
    data = (payload / "e_sqlite3.dll").read_bytes()
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    assert data[:2] == b"MZ" and data[offset:offset + 4] == b"PE\0\0"
    assert struct.unpack_from("<H", data, offset + 4)[0] == 0x8664
    print("SQLite win-x64 native asset present; execution BLOCKED pending Windows VM.")
elif rid == "linux-x64":
    native = payload / "libe_sqlite3.so"
    data = native.read_bytes()
    assert data[:4] == b"\x7fELF" and data[4] == 2
    assert struct.unpack_from("<H", data, 18)[0] == 62
    lib = ctypes.CDLL(str(native.resolve()))
    lib.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    lib.sqlite3_close.argtypes = [ctypes.c_void_p]
    lib.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    lib.sqlite3_libversion.restype = ctypes.c_char_p
    with tempfile.TemporaryDirectory(prefix="bucketbus-sqlite-package-") as root:
        database = ctypes.c_void_p()
        assert lib.sqlite3_open(str(Path(root) / "fixture.db").encode(), ctypes.byref(database)) == 0
        try:
            assert lib.sqlite3_exec(database, b"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE fixture(id INTEGER); BEGIN; INSERT INTO fixture VALUES(1); COMMIT;", None, None, None) == 0
        finally:
            assert lib.sqlite3_close(database) == 0
    print("Published SQLite linux-x64 loaded; WAL/FULL transaction passed; version " + lib.sqlite3_libversion().decode())
else:
    raise ValueError("Unsupported qualification target")
