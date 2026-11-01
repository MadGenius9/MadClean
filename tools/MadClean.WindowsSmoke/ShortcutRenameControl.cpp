// Development-only, independently SDK-compiled control. No application imports it.
// Separate bounded relative Win32/NT controls; no automatic fallback or target action.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <winternl.h>
#include <cstddef>
#include <cstring>

static_assert(sizeof(void*) == 8, "The owned control is validated for x64 only.");
static_assert(sizeof(FILE_RENAME_INFO) == 24, "Unexpected SDK FILE_RENAME_INFO size.");
static_assert(offsetof(FILE_RENAME_INFO, RootDirectory) == 8, "Unexpected root offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileNameLength) == 16, "Unexpected length offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileName) == 20, "Unexpected name offset.");
static_assert(sizeof(NTSTATUS) == 4, "Unexpected NTSTATUS width.");
static_assert(sizeof(IO_STATUS_BLOCK) == 16, "Unexpected status-block size.");
static_assert(offsetof(IO_STATUS_BLOCK, Information) == 8, "Unexpected status-block information offset.");

extern "C" __declspec(dllexport) DWORD WINAPI ShortcutRenameControlAbiVersion() noexcept
{
    return 2;
}

extern "C" __declspec(dllexport) BOOL WINAPI RelativeRename(HANDLE source, HANDLE directory,
    const WCHAR* leaf, DWORD characters, DWORD* win32Error, BOOL* apiCalled) noexcept
{
    if (win32Error == nullptr || apiCalled == nullptr) return FALSE;
    *win32Error = ERROR_INVALID_PARAMETER;
    *apiCalled = FALSE;
    if (source == nullptr || source == INVALID_HANDLE_VALUE || directory == nullptr ||
        directory == INVALID_HANDLE_VALUE || leaf == nullptr || characters < 5 || characters > 100)
        return FALSE;
    for (DWORD i = 0; i < characters; ++i)
    {
        const WCHAR value = leaf[i];
        if (!((value >= L'a' && value <= L'z') || (value >= L'A' && value <= L'Z') ||
            (value >= L'0' && value <= L'9') || value == L'-' || value == L'.')) return FALSE;
    }
    if (leaf[characters - 4] != L'.' || leaf[characters - 3] != L'l' ||
        leaf[characters - 2] != L'n' || leaf[characters - 1] != L'k') return FALSE;

    alignas(FILE_RENAME_INFO) BYTE storage[sizeof(FILE_RENAME_INFO) + 100 * sizeof(WCHAR)]{};
    auto* information = reinterpret_cast<FILE_RENAME_INFO*>(storage);
    information->ReplaceIfExists = FALSE;
    information->RootDirectory = directory;
    information->FileNameLength = characters * static_cast<DWORD>(sizeof(WCHAR));
    for (DWORD i = 0; i < characters; ++i) information->FileName[i] = leaf[i];
    const DWORD bufferBytes = static_cast<DWORD>(sizeof(FILE_RENAME_INFO)) + information->FileNameLength;
    *apiCalled = TRUE;
    const BOOL result = SetFileInformationByHandle(source, FileRenameInfo, information, bufferBytes);
    *win32Error = result ? ERROR_SUCCESS : GetLastError();
    return result;
}

// This typedef/class10 are tied to Microsoft's explicit user-mode NtSetInformationFile
// WDK contract, not inferred from a Win32 enum or claimed to be a Win32 SDK declaration:
// https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile
using SetInformationNative = NTSTATUS (NTAPI*)(HANDLE, PIO_STATUS_BLOCK, PVOID, ULONG, LONG);
constexpr LONG NativeFileRenameInformation = 10;
constexpr NTSTATUS NativeStatusPending = 0x00000103L;

extern "C" __declspec(dllexport) BOOL WINAPI RelativeRenameNt(HANDLE source, HANDLE directory,
    const WCHAR* leaf, DWORD characters, BOOL* apiCalled, LONG* immediateStatus,
    LONG* completionStatus, BOOL* completionObserved, BOOL* pendingObserved,
    BOOL* terminalObserved, DWORD* setupWin32Error) noexcept
{
    if (apiCalled == nullptr || immediateStatus == nullptr || completionStatus == nullptr ||
        completionObserved == nullptr || pendingObserved == nullptr || terminalObserved == nullptr || setupWin32Error == nullptr)
        return FALSE;
    *apiCalled = FALSE; *immediateStatus = 0; *completionStatus = 0;
    *completionObserved = FALSE; *pendingObserved = FALSE; *terminalObserved = FALSE; *setupWin32Error = 0;
    if (source == nullptr || source == INVALID_HANDLE_VALUE || directory == nullptr ||
        directory == INVALID_HANDLE_VALUE || leaf == nullptr || characters < 5 || characters > 100) return FALSE;
    for (DWORD i = 0; i < characters; ++i)
    {
        const WCHAR value = leaf[i];
        if (!((value >= L'a' && value <= L'z') || (value >= L'A' && value <= L'Z') ||
            (value >= L'0' && value <= L'9') || value == L'-' || value == L'.')) return FALSE;
    }
    if (leaf[characters - 4] != L'.' || leaf[characters - 3] != L'l' ||
        leaf[characters - 2] != L'n' || leaf[characters - 1] != L'k') return FALSE;

    WCHAR expected[MAX_PATH]{}; WCHAR actual[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(expected, MAX_PATH);
    if (systemLength == 0) { *setupWin32Error = GetLastError(); return FALSE; }
    constexpr WCHAR suffix[] = L"\\ntdll.dll";
    constexpr UINT suffixLength = static_cast<UINT>(sizeof(suffix) / sizeof(WCHAR)) - 1;
    if (systemLength + suffixLength >= MAX_PATH) return FALSE;
    for (UINT i = 0; i <= suffixLength; ++i) expected[systemLength + i] = suffix[i];
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(0, L"ntdll.dll", &module)) { *setupWin32Error = GetLastError(); return FALSE; }
    const DWORD actualLength = GetModuleFileNameW(module, actual, MAX_PATH);
    if (actualLength == 0) { *setupWin32Error = GetLastError(); FreeLibrary(module); return FALSE; }
    if (actualLength >= MAX_PATH) { FreeLibrary(module); return FALSE; }
    const int samePath = CompareStringOrdinal(expected, -1, actual, -1, TRUE);
    if (samePath == 0) { *setupWin32Error = GetLastError(); FreeLibrary(module); return FALSE; }
    if (samePath != CSTR_EQUAL) { FreeLibrary(module); return FALSE; }
    const FARPROC address = GetProcAddress(module, "NtSetInformationFile");
    if (address == nullptr) { *setupWin32Error = GetLastError(); FreeLibrary(module); return FALSE; }
    SetInformationNative setInformation = nullptr;
    static_assert(sizeof(setInformation) == sizeof(address), "Unexpected export-pointer width.");
    std::memcpy(&setInformation, &address, sizeof(setInformation));

    alignas(FILE_RENAME_INFO) BYTE storage[sizeof(FILE_RENAME_INFO) + 100 * sizeof(WCHAR)]{};
    auto* information = reinterpret_cast<FILE_RENAME_INFO*>(storage);
    information->ReplaceIfExists = FALSE; information->RootDirectory = directory;
    information->FileNameLength = characters * static_cast<DWORD>(sizeof(WCHAR));
    for (DWORD i = 0; i < characters; ++i) information->FileName[i] = leaf[i];
    const ULONG bufferBytes = static_cast<ULONG>(sizeof(FILE_RENAME_INFO)) + information->FileNameLength;
    IO_STATUS_BLOCK statusBlock{};
    statusBlock.Status = NativeStatusPending; // A zero completion must be written by the operation.
    *apiCalled = TRUE;
    const NTSTATUS immediate = setInformation(source, &statusBlock, information, bufferBytes, NativeFileRenameInformation);
    *immediateStatus = immediate;
    NTSTATUS terminal = immediate;
    if (immediate == NativeStatusPending)
    {
        *pendingObserved = TRUE;
        const DWORD waited = WaitForSingleObject(source, INFINITE);
        if (waited != WAIT_OBJECT_0 || statusBlock.Status == NativeStatusPending)
        {
            // Never return a stack/handle/module while an unconfirmed kernel request
            // can still use it. The dev worker remains owner; its caller expires at45s.
            for (;;) Sleep(INFINITE);
        }
        terminal = statusBlock.Status;
        *completionStatus = terminal; *completionObserved = TRUE;
    }
    // For immediate completion rely on the returned NTSTATUS, not an untouched IOSB.
    *terminalObserved = TRUE;
    if (!FreeLibrary(module)) { *setupWin32Error = GetLastError(); return FALSE; }
    return terminal == 0 ? TRUE : FALSE;
}
