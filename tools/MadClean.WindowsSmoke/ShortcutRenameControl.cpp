// Development-only, independently SDK-compiled control. No application imports it.
// One bounded relative Win32 rename; no NT, path resolution, fallback or extra I/O.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <cstddef>

static_assert(sizeof(void*) == 8, "The owned control is validated for x64 only.");
static_assert(sizeof(FILE_RENAME_INFO) == 24, "Unexpected SDK FILE_RENAME_INFO size.");
static_assert(offsetof(FILE_RENAME_INFO, RootDirectory) == 8, "Unexpected root offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileNameLength) == 16, "Unexpected length offset.");
static_assert(offsetof(FILE_RENAME_INFO, FileName) == 20, "Unexpected name offset.");

extern "C" __declspec(dllexport) DWORD WINAPI ShortcutRenameControlAbiVersion() noexcept
{
    return 1;
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
