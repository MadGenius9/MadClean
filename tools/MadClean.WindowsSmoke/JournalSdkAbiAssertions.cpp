// Compile only against the installed SDK. No linking, entry point or API calls.
#include <sdkddkver.h>
#ifndef NTDDI_WIN11_GE
#error The installed SDK does not declare the required Windows 11 platform.
#endif
#undef NTDDI_VERSION
#define NTDDI_VERSION NTDDI_WIN11_GE
#include <windows.h>
#include <cstddef>
#include <type_traits>

static_assert(sizeof(HANDLE) == 8 && sizeof(DWORD) == 4 && sizeof(BOOL) == 4,
    "The journal candidate supports the x64 Windows ABI only.");
using DirectorySignature = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, DIRECTORY_FLAGS, LPSECURITY_ATTRIBUTES);
using FileSignature = HANDLE (WINAPI*)(LPCWSTR, DWORD, DWORD, DWORD, LPCREATEFILE3_EXTENDED_PARAMETERS);
static_assert(std::is_same<decltype(&CreateDirectory2W), DirectorySignature>::value,
    "CreateDirectory2W declaration differs.");
static_assert(std::is_same<decltype(&CreateFile3), FileSignature>::value,
    "CreateFile3 declaration differs.");
static_assert(sizeof(CREATEFILE3_EXTENDED_PARAMETERS) == 32, "Extended parameters size differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, dwSize) == 0, "Size offset differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, dwFileAttributes) == 4, "Attributes offset differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, dwFileFlags) == 8, "Flags offset differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, dwSecurityQosFlags) == 12, "QoS offset differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, lpSecurityAttributes) == 16, "Security offset differs.");
static_assert(offsetof(CREATEFILE3_EXTENDED_PARAMETERS, hTemplateFile) == 24, "Template offset differs.");
static_assert(sizeof(SECURITY_ATTRIBUTES) == 24, "Security attributes size differs.");
static_assert(offsetof(SECURITY_ATTRIBUTES, nLength) == 0, "Security length offset differs.");
static_assert(offsetof(SECURITY_ATTRIBUTES, lpSecurityDescriptor) == 8, "Descriptor offset differs.");
static_assert(offsetof(SECURITY_ATTRIBUTES, bInheritHandle) == 16, "Inheritance offset differs.");
static_assert(DIRECTORY_FLAGS_DISALLOW_PATH_REDIRECTS == 1, "Directory no-redirect flag differs.");
static_assert(FILE_FLAG_DISALLOW_PATH_REDIRECTS == 0x00010000, "File no-redirect flag differs.");
