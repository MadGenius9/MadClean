# Owned mapped-file disposition experiment

This development-only harness experiment is a prerequisite investigation for later reviewed cleanup. It has no production caller, deletion permission or execution switch. It creates only fixed small files under the exact GUID ownership-marked repository artifact tree; it never selects client files or actual temporary folders.

A native unnamed, non-executable writable mapping holds a 64-byte owned source. The original file handle is checked closed before testing the live view. One byte is read, changed, read back and restored through ReadProcessMemory/WriteProcessMemory using only this process’s borrowed pseudo-handle and its exact owned view. No foreign process is opened and no memory protection is changed. A separate unchanged control and full root/file identities constrain the experiment.

The exact source is opened with READ_ATTRIBUTES|DELETE and share zero. The one-byte classic FileDispositionInfo request is separate from API acceptance, optional delete-pending metadata, checked handle close, and actual name observations while the view remains live. The writable/restore witness is repeated, then the view and mapping are separately checked released and final name state observed. File-byte audits happen only after release. No EX/POSIX, readonly override, delete-on-close, recycle or path-deletion fallback is used.

Actual sharing refusal32/33 is a narrow observation, not universal mapped-file protection. Accepted disposition plus a writable live view and actual name absence is explicitly a counterexample to inactivity inference. Other request failures remain Unknown. Request acceptance does not prove reclaimed bytes. Mapped-versus-file I/O coherence and physical persistence are not inferred from FlushViewOfFile.

Caller cancellation never releases worker resources. Pending native work, uncertain view restoration, failed unmap/close, or uncertain audits retains the worker and exact marked tree; there is no retry of uncertain handles/pointers. Pre-cancellation creates nothing. The parent harness cannot continue later mutations or recursively remove a retained tree. The required export is bounded; incomplete/oversized evidence cannot pass and known invariant failures stay failed.

Official references rechecked before implementation:

- [CreateFileMappingW](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw), [MapViewOfFile](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-mapviewoffile), [UnmapViewOfFile](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-unmapviewoffile) and [FlushViewOfFile](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-flushviewoffile).
- [ReadProcessMemory](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory), [WriteProcessMemory](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-writeprocessmemory) and [GetCurrentProcess](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentprocess).

UnmapViewOfFile documents that a view can outlive the original file handle and that Windows keeps the file open with no sharing restrictions until the last view is unmapped. This motivates the experiment; it is not a prediction of the observed disposition result. Actual source-bound Windows results are pending. Production CanDelete/ExecutionSupported remain false.
