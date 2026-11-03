// Development-only, independently SDK-compiled control. No application imports it.
// Separate bounded relative Win32/NT controls; no automatic fallback or target action.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <winternl.h>
#include <cstddef>
#include <cstring>
#include <Aclapi.h>
#pragma comment(lib, "Advapi32.lib")

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

// Independent directory-creation control. Existing rename exports/ABI above are unchanged.
static_assert(sizeof(OBJECT_ATTRIBUTES) == 48, "Unexpected SDK object-attributes size.");
static_assert(offsetof(OBJECT_ATTRIBUTES, RootDirectory) == 8, "Unexpected SDK root offset.");
static_assert(offsetof(OBJECT_ATTRIBUTES, SecurityDescriptor) == 32, "Unexpected SDK SD offset.");
static_assert(sizeof(UNICODE_STRING) == 16, "Unexpected SDK Unicode-string size.");
static_assert(sizeof(FILE_ID_INFO) == 24, "Unexpected SDK full file-ID size.");
static_assert(sizeof(FILE_DISPOSITION_INFO) == 1, "Unexpected SDK directory-disposition size.");
using CreateFileNativeControl = decltype(&NtCreateFile);

struct RelativeDirectoryResult
{
    DWORD Version, Stage, SetupErrorObserved, SetupError;
    DWORD ParentCalled; LONG ParentStatus, ParentIoStatus; DWORD ParentInformation, ParentIoObserved;
    DWORD ParentMatched;
    DWORD CreateCalled; LONG CreateStatus, CreateIoStatus; DWORD CreateInformation, CreateIoObserved;
    DWORD CreatedHandle, DescriptorMatched;
    DWORD CollisionCalled; LONG CollisionStatus, CollisionIoStatus; DWORD CollisionInformation, CollisionIoObserved;
    DWORD CollisionRefused, ParentBookend, ChildBookend;
    DWORD DispositionCalled, DispositionSucceeded, HandlesClosed, DescriptorReleased, Complete;
};
static_assert(sizeof(RelativeDirectoryResult) == 120, "Unexpected development result ABI.");

extern "C" __declspec(dllexport) DWORD WINAPI RelativeDirectoryCreateControlAbiVersion() noexcept { return 1; }
extern "C" __declspec(dllexport) DWORD WINAPI RelativeDirectoryCreateControlResultBytes() noexcept { return static_cast<DWORD>(sizeof(RelativeDirectoryResult)); }

// Never release native input/output/IOSB/SD/module/handle storage underneath an unknown
// request or close. The owning development worker/caller has a separate bounded wait.
[[noreturn]] static void RetainDirectoryControl() noexcept { for (;;) Sleep(INFINITE); }
static bool SameDirectoryId(HANDLE first, HANDLE second) noexcept
{
    FILE_ID_INFO a{}, b{}; FILE_STANDARD_INFO sa{}, sb{}; FILE_ATTRIBUTE_TAG_INFO ta{}, tb{};
    if (!GetFileInformationByHandleEx(first, FileIdInfo, &a, sizeof(a)) ||
        !GetFileInformationByHandleEx(second, FileIdInfo, &b, sizeof(b)) ||
        !GetFileInformationByHandleEx(first, FileStandardInfo, &sa, sizeof(sa)) ||
        !GetFileInformationByHandleEx(second, FileStandardInfo, &sb, sizeof(sb)) ||
        !GetFileInformationByHandleEx(first, FileAttributeTagInfo, &ta, sizeof(ta)) ||
        !GetFileInformationByHandleEx(second, FileAttributeTagInfo, &tb, sizeof(tb))) return false;
    constexpr DWORD denied = FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_ENCRYPTED;
    bool firstIdKnown = false, secondIdKnown = false;
    for (const BYTE value : a.FileId.Identifier) firstIdKnown = firstIdKnown || value != 0;
    for (const BYTE value : b.FileId.Identifier) secondIdKnown = secondIdKnown || value != 0;
    return sa.Directory && sb.Directory && !sa.DeletePending && !sb.DeletePending &&
        firstIdKnown && secondIdKnown &&
        (ta.FileAttributes & denied) == 0 && (tb.FileAttributes & denied) == 0 &&
        a.VolumeSerialNumber == b.VolumeSerialNumber && std::memcmp(a.FileId.Identifier, b.FileId.Identifier, sizeof(a.FileId.Identifier)) == 0;
}
static bool SameFileId(const FILE_ID_INFO& a, const FILE_ID_INFO& b) noexcept
{
    return a.VolumeSerialNumber == b.VolumeSerialNumber && std::memcmp(a.FileId.Identifier, b.FileId.Identifier, sizeof(a.FileId.Identifier)) == 0;
}
static bool CaptureDirectoryIo(const IO_STATUS_BLOCK& io, LONG* status, DWORD* information) noexcept
{
    if (io.Status == NativeStatusPending || io.Information > MAXDWORD) return false;
    *status = io.Status; *information = static_cast<DWORD>(io.Information); return true;
}

extern "C" __declspec(dllexport) BOOL WINAPI OwnedRelativeDirectoryBaseline(HANDLE win32Parent,
    const WCHAR* path, DWORD characters, PSECURITY_DESCRIPTOR requestedDescriptor,
    DWORD descriptorBytes, RelativeDirectoryResult* result, DWORD resultBytes) noexcept
{
    if (result == nullptr || resultBytes != sizeof(RelativeDirectoryResult)) return FALSE;
    *result = {}; result->Version = 1; result->Stage = 1;
    result->HandlesClosed = 1; result->DescriptorReleased = 1; // No native-owned resource yet.
    if (win32Parent == nullptr || win32Parent == INVALID_HANDLE_VALUE || path == nullptr ||
        characters < 3 || characters > 1000 || requestedDescriptor == nullptr || descriptorBytes < SECURITY_DESCRIPTOR_MIN_LENGTH || descriptorBytes > 4096 ||
        !IsValidSecurityDescriptor(requestedDescriptor) || GetSecurityDescriptorLength(requestedDescriptor) != descriptorBytes) return FALSE;
    if (!((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) || path[1] != L':' || path[2] != L'\\') return FALSE;
    for (DWORD i = 0; i < characters; ++i) if (path[i] == 0 || path[i] == L'/' || (i != 1 && path[i] == L':')) return FALSE;
    constexpr WCHAR ownedPrefix[] = L"\\artifacts\\windows-validation-fixtures-";
    constexpr DWORD prefixLength = static_cast<DWORD>(sizeof(ownedPrefix) / sizeof(WCHAR)) - 1;
    // The original marked GUID root and one fixed owned private-parent case are the
    // only accepted parents. No arbitrary descendants or caller-provided parent roots.
    constexpr WCHAR privateParentSuffix[] = L"\\relative-parent-qualified";
    constexpr DWORD privateParentLength = static_cast<DWORD>(sizeof(privateParentSuffix) / sizeof(WCHAR)) - 1;
    DWORD rootCharacters = characters;
    if (characters >= privateParentLength && std::memcmp(path + characters - privateParentLength, privateParentSuffix, privateParentLength * sizeof(WCHAR)) == 0)
        rootCharacters -= privateParentLength;
    if (rootCharacters <= prefixLength + 32 || std::memcmp(path + rootCharacters - prefixLength - 32, ownedPrefix, prefixLength * sizeof(WCHAR)) != 0) return FALSE;
    for (DWORD i = rootCharacters - 32; i < rootCharacters; ++i)
        if (!((path[i] >= L'0' && path[i] <= L'9') || (path[i] >= L'a' && path[i] <= L'f'))) return FALSE;

    // All storage remains live until the terminal/close path returns, including on pending.
    WCHAR nativeName[1010]{}; constexpr WCHAR ntPrefix[] = L"\\??\\";
    std::memcpy(nativeName, ntPrefix, 4 * sizeof(WCHAR));
    std::memcpy(nativeName + 4, path, characters * sizeof(WCHAR));
    UNICODE_STRING parentName{}; parentName.Buffer = nativeName;
    parentName.Length = static_cast<USHORT>((characters + 4) * sizeof(WCHAR)); parentName.MaximumLength = parentName.Length;
    OBJECT_ATTRIBUTES parentAttributes{}; parentAttributes.Length = sizeof(parentAttributes); parentAttributes.ObjectName = &parentName;
    // General OBJECT_ATTRIBUTES documents DONT_REPARSE; the desktop NtCreateFile inline
    // attribute list is narrower. This owned control tests that combination, not a universal theorem.
    parentAttributes.Attributes = OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE;
    constexpr WCHAR leaf[] = L"relative-private-directory-baseline";
    UNICODE_STRING childName{}; childName.Buffer = const_cast<PWSTR>(leaf);
    childName.Length = static_cast<USHORT>(sizeof(leaf) - sizeof(WCHAR)); childName.MaximumLength = childName.Length;
    OBJECT_ATTRIBUTES childAttributes{}; childAttributes.Length = sizeof(childAttributes);
    childAttributes.ObjectName = &childName; childAttributes.Attributes = OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE;
    childAttributes.SecurityDescriptor = requestedDescriptor;
    IO_STATUS_BLOCK parentIo{}, childIo{}, collisionIo{};
    parentIo.Status = NativeStatusPending; childIo.Status = NativeStatusPending; collisionIo.Status = NativeStatusPending;
    HANDLE parent = nullptr, child = nullptr, collision = nullptr;
    PSECURITY_DESCRIPTOR actualDescriptor = nullptr;
    FILE_ID_INFO parentInitial{}, childInitial{}, parentFinal{}, childFinal{};
    HMODULE module = nullptr; bool ownedChild = false;
    WCHAR expected[MAX_PATH]{}, actual[MAX_PATH]{};
    const UINT systemLength = GetSystemDirectoryW(expected, MAX_PATH);
    constexpr WCHAR suffix[] = L"\\ntdll.dll";
    if (systemLength == 0 || systemLength + sizeof(suffix) / sizeof(WCHAR) > MAX_PATH) return FALSE;
    std::memcpy(expected + systemLength, suffix, sizeof(suffix));
    if (!GetModuleHandleExW(0, L"ntdll.dll", &module)) { result->SetupErrorObserved = 1; result->SetupError = GetLastError(); return FALSE; }
    const DWORD actualLength = GetModuleFileNameW(module, actual, MAX_PATH);
    if (actualLength == 0 || actualLength >= MAX_PATH || CompareStringOrdinal(expected, -1, actual, -1, TRUE) != CSTR_EQUAL)
    { if (!FreeLibrary(module)) RetainDirectoryControl(); return FALSE; }
    const FARPROC address = GetProcAddress(module, "NtCreateFile");
    if (address == nullptr) { result->SetupErrorObserved = 1; result->SetupError = GetLastError(); if (!FreeLibrary(module)) RetainDirectoryControl(); return FALSE; }
    CreateFileNativeControl create = nullptr;
    static_assert(sizeof(create) == sizeof(address), "Unexpected SDK function-pointer width.");
    std::memcpy(&create, &address, sizeof(create));

    do
    {
        result->Stage = 2; result->ParentCalled = 1; result->HandlesClosed = 0;
        result->ParentStatus = create(&parent, FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY | READ_CONTROL | SYNCHRONIZE,
            &parentAttributes, &parentIo, nullptr, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_OPEN,
            FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT, nullptr, 0);
        if (result->ParentStatus == NativeStatusPending) RetainDirectoryControl();
        result->ParentIoObserved = CaptureDirectoryIo(parentIo, &result->ParentIoStatus, &result->ParentInformation);
        if (result->ParentStatus != 0 || !result->ParentIoObserved || result->ParentIoStatus != 0 || result->ParentInformation != FILE_OPENED || parent == nullptr || parent == INVALID_HANDLE_VALUE) break;
        result->Stage = 3; result->ParentMatched = SameDirectoryId(parent, win32Parent);
        if (!result->ParentMatched || !GetFileInformationByHandleEx(parent, FileIdInfo, &parentInitial, sizeof(parentInitial))) break;
        childAttributes.RootDirectory = parent;
        result->Stage = 4; result->CreateCalled = 1;
        result->CreateStatus = create(&child, FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY | READ_CONTROL | SYNCHRONIZE | DELETE,
            &childAttributes, &childIo, nullptr, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_CREATE,
            FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT, nullptr, 0);
        if (result->CreateStatus == NativeStatusPending) RetainDirectoryControl();
        result->CreateIoObserved = CaptureDirectoryIo(childIo, &result->CreateIoStatus, &result->CreateInformation);
        if (result->CreateStatus != 0 || !result->CreateIoObserved || result->CreateIoStatus != 0 || result->CreateInformation != FILE_CREATED || child == nullptr || child == INVALID_HANDLE_VALUE) break;
        ownedChild = true; result->CreatedHandle = 1;
        if (!GetFileInformationByHandleEx(child, FileIdInfo, &childInitial, sizeof(childInitial)) || !SameDirectoryId(child, child)) break;
        result->Stage = 5;
        PSID expectedOwner = nullptr, actualOwner = nullptr; BOOL expectedDefault = FALSE, expectedPresent = FALSE, actualPresent = FALSE;
        PACL expectedDacl = nullptr, actualDacl = nullptr; SECURITY_DESCRIPTOR_CONTROL requestedControl{}, actualControl{}; DWORD revision = 0;
        if (!GetSecurityDescriptorOwner(requestedDescriptor, &expectedOwner, &expectedDefault) || expectedOwner == nullptr || !IsValidSid(expectedOwner) ||
            !GetSecurityDescriptorDacl(requestedDescriptor, &expectedPresent, &expectedDacl, &expectedDefault) || !expectedPresent || expectedDacl == nullptr ||
            !GetSecurityDescriptorControl(requestedDescriptor, &requestedControl, &revision) || !(requestedControl & SE_DACL_PROTECTED)) break;
        result->DescriptorReleased = 0;
        const DWORD security = GetSecurityInfo(child, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, &actualOwner, nullptr, &actualDacl, nullptr, &actualDescriptor);
        if (security != ERROR_SUCCESS) { result->SetupErrorObserved = 1; result->SetupError = security; break; }
        if (actualDescriptor == nullptr || !IsValidSecurityDescriptor(actualDescriptor) || GetSecurityDescriptorLength(actualDescriptor) > 4096 ||
            actualOwner == nullptr || !IsValidSid(actualOwner) || !EqualSid(expectedOwner, actualOwner) ||
            !GetSecurityDescriptorControl(actualDescriptor, &actualControl, &revision) || !(actualControl & SE_DACL_PROTECTED) ||
            !GetSecurityDescriptorDacl(actualDescriptor, &actualPresent, &actualDacl, &expectedDefault) || !actualPresent || actualDacl == nullptr ||
            !IsValidAcl(expectedDacl) || !IsValidAcl(actualDacl) || expectedDacl->AclSize > 4096 || actualDacl->AclSize != expectedDacl->AclSize ||
            std::memcmp(expectedDacl, actualDacl, expectedDacl->AclSize) != 0) break;
        result->DescriptorMatched = 1;
        result->Stage = 6; result->CollisionCalled = 1;
        result->CollisionStatus = create(&collision, FILE_READ_ATTRIBUTES | READ_CONTROL | SYNCHRONIZE,
            &childAttributes, &collisionIo, nullptr, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, FILE_CREATE,
            FILE_DIRECTORY_FILE | FILE_SYNCHRONOUS_IO_NONALERT, nullptr, 0);
        if (result->CollisionStatus == NativeStatusPending) RetainDirectoryControl();
        result->CollisionIoObserved = CaptureDirectoryIo(collisionIo, &result->CollisionIoStatus, &result->CollisionInformation);
        result->CollisionRefused = result->CollisionStatus == static_cast<NTSTATUS>(0xC0000035UL) && (collision == nullptr || collision == INVALID_HANDLE_VALUE);
        if (!result->CollisionRefused) break;
        result->Stage = 7;
        result->ParentBookend = SameDirectoryId(parent, win32Parent) && GetFileInformationByHandleEx(parent, FileIdInfo, &parentFinal, sizeof(parentFinal)) && SameFileId(parentInitial, parentFinal);
        result->ChildBookend = SameDirectoryId(child, child) && GetFileInformationByHandleEx(child, FileIdInfo, &childFinal, sizeof(childFinal)) && SameFileId(childInitial, childFinal);
        if (!result->ParentBookend || !result->ChildBookend) break;
        result->Complete = 1;
    } while (false);

    // No by-name delete fallback. Only the exact newly created directory handle may be
    // marked; class4 refuses a nonempty directory. This is cleanup, not creation proof.
    if (ownedChild)
    {
        result->DispositionCalled = 1; FILE_DISPOSITION_INFO disposition{}; disposition.DeleteFile = TRUE;
        result->DispositionSucceeded = SetFileInformationByHandle(child, FileDispositionInfo, &disposition, sizeof(disposition));
        if (!result->DispositionSucceeded) { result->SetupErrorObserved = 1; result->SetupError = GetLastError(); RetainDirectoryControl(); }
    }
    else if (child != nullptr && child != INVALID_HANDLE_VALUE) RetainDirectoryControl();
    if (collision != nullptr && collision != INVALID_HANDLE_VALUE) RetainDirectoryControl();
    if (child != nullptr && child != INVALID_HANDLE_VALUE && !CloseHandle(child)) RetainDirectoryControl();
    if (parent != nullptr && parent != INVALID_HANDLE_VALUE && !CloseHandle(parent)) RetainDirectoryControl();
    result->HandlesClosed = 1;
    if (actualDescriptor != nullptr && LocalFree(actualDescriptor) != nullptr) RetainDirectoryControl();
    result->DescriptorReleased = 1;
    if (!FreeLibrary(module)) RetainDirectoryControl();
    if (result->Complete) result->Stage = 8;
    return result->Complete ? TRUE : FALSE;
}
