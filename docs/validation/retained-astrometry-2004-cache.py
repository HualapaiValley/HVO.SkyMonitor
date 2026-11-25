import ctypes
import math
import os
from pathlib import Path
import sys

root = Path(os.environ["HVO_RETAINED_SCAN_FIXTURE"])
if "--inspect" not in sys.argv:
    files = 0
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        with path.open("rb") as handle:
            os.posix_fadvise(handle.fileno(), 0, 0, os.POSIX_FADV_DONTNEED)
        files += 1
    print(f"Advised DONTNEED on {files} fixture files.", flush=True)

db = root / "journal" / "raw-ingress.db"
size = db.stat().st_size
page_size = os.sysconf("SC_PAGE_SIZE")
pages = math.ceil(size / page_size)
libc = ctypes.CDLL(None, use_errno=True)
libc.mmap.restype = ctypes.c_void_p
libc.mmap.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int64]
libc.mincore.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_ubyte)]
libc.munmap.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
with db.open("rb") as handle:
    address = libc.mmap(None, size, 0, 1, handle.fileno(), 0)
    if address == ctypes.c_void_p(-1).value:
        raise OSError(ctypes.get_errno(), "mmap")
    try:
        residency = (ctypes.c_ubyte * pages)()
        if libc.mincore(address, size, residency) != 0:
            raise OSError(ctypes.get_errno(), "mincore")
        print(f"Journal residency: {sum(page & 1 for page in residency)} / {pages} pages ({size} bytes total)")
    finally:
        libc.munmap(address, size)
