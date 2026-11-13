// Compile-only development probe: cl /nologo /c /W4 /WX /Fo:<owned-output.obj> WerAbiAssertions.cpp
// Use an x64 Windows SDK developer command prompt. No linking or execution is needed.
#include <windows.h>
#include <werapi.h>
#include <wincrypt.h>
#include <mssip.h>
#include <mscat.h>
#include <wintrust.h>
#include <winternl.h>
#include <aclapi.h>
#include <setupapi.h>
#include <devpropdef.h>
#include <cstddef>
#include <type_traits>

static_assert(sizeof(void*) == 8, "The supported WER adapter ABI is 64-bit only.");
static_assert(E_STORE_MACHINE_ARCHIVE == 2 && E_STORE_MACHINE_QUEUE == 3, "WER store enumeration values differ.");
static_assert(sizeof(WER_REPORT_PARAMETER) == 778, "WER parameter layout differs.");
static_assert(sizeof(WER_REPORT_SIGNATURE) == 7910, "WER signature layout differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, BucketId) == 7912, "WER bucket offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, ReportId) == 7928, "WER report offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, CreationTime) == 7944, "WER timestamp offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, SizeInBytes) == 7952, "WER size offset differs.");
static_assert(sizeof(WER_REPORT_METADATA_V1) == 7960, "WER V1 allocation size differs.");
using WerCountSignature = HRESULT (*)(HREPORTSTORE, DWORD*);
static_assert(sizeof(DWORD) == 4 && sizeof(HRESULT) == 4, "WER count/result widths differ.");
static_assert(std::is_same<decltype(&WerStoreGetReportCount), WerCountSignature>::value,
    "WER report-count function signature differs.");
// Independently checked against current official WinTrust.h, mscat.h and mssip.h.
// These are compile-only x64 SDK assertions, not a native catalog/signature success claim.
static_assert(sizeof(WINTRUST_CATALOG_INFO) == 72, "Catalog trust layout differs.");
static_assert(offsetof(WINTRUST_CATALOG_INFO, pbCalculatedFileHash) == 40, "Catalog hash offset differs.");
static_assert(offsetof(WINTRUST_CATALOG_INFO, hCatAdmin) == 64, "Catalog admin offset differs.");
static_assert(offsetof(CRYPTCATMEMBER, gSubjectType) == 24, "Catalog member subject offset differs.");
static_assert(offsetof(CRYPTCATMEMBER, pIndirectData) == 48, "Catalog member indirect-data offset differs.");
static_assert(offsetof(SIP_INDIRECT_DATA, DigestAlgorithm) == 24, "SIP algorithm offset differs.");
static_assert(offsetof(SIP_INDIRECT_DATA, Digest) == 48, "SIP digest offset differs.");
// Current official WinBase.h: BOOLEAN/flags union, aligned HANDLE, DWORD, WCHAR[1].
static_assert(offsetof(FILE_RENAME_INFO, RootDirectory) == 8, "Handle rename root offset differs.");
static_assert(offsetof(FILE_RENAME_INFO, FileNameLength) == 16, "Handle rename length offset differs.");
static_assert(offsetof(FILE_RENAME_INFO, FileName) == 20, "Handle rename name offset differs.");
// Current public SDK winternl.h declares these ABI types, not NtSetInformationFile.
// The dynamic native function signature requires separate official-document/source review.
static_assert(sizeof(NTSTATUS) == 4 && sizeof(ULONG) == 4, "Native status/length widths differ.");
static_assert(sizeof(IO_STATUS_BLOCK) == 16, "Native IO status block layout differs.");
static_assert(offsetof(IO_STATUS_BLOCK, Status) == 0, "Native IO status offset differs.");
static_assert(offsetof(IO_STATUS_BLOCK, Information) == 8, "Native IO information offset differs.");
static_assert(sizeof(SECURITY_DESCRIPTOR_RELATIVE) == 20, "Relative security descriptor header differs.");
static_assert(sizeof(SECURITY_DESCRIPTOR_CONTROL) == 2, "Descriptor control width differs.");
static_assert(sizeof(ACL) == 8 && sizeof(ACL_SIZE_INFORMATION) == 12 && sizeof(ACL_REVISION_INFORMATION) == 4,
    "Aggregate ACL metadata layouts differ.");
using DaclQuery = DWORD (WINAPI*)(HANDLE, SE_OBJECT_TYPE, SECURITY_INFORMATION, PSID*, PSID*, PACL*, PACL*, PSECURITY_DESCRIPTOR*);
static_assert(std::is_same<decltype(&GetSecurityInfo), DaclQuery>::value, "DACL query signature differs.");

// Owned negative-case setup and same-handle control observation only.
using DaclSet = DWORD (WINAPI*)(HANDLE, SE_OBJECT_TYPE, SECURITY_INFORMATION, PSID, PSID, PACL, PACL);
using DescriptorControlQuery = BOOL (WINAPI*)(PSECURITY_DESCRIPTOR, PSECURITY_DESCRIPTOR_CONTROL, LPDWORD);
using DescriptorDaclQuery = BOOL (WINAPI*)(PSECURITY_DESCRIPTOR, LPBOOL, PACL*, LPBOOL);
static_assert(std::is_same<decltype(&SetSecurityInfo), DaclSet>::value, "Owned DACL setup signature differs.");
static_assert(std::is_same<decltype(&GetSecurityDescriptorControl), DescriptorControlQuery>::value, "Descriptor control query signature differs.");
static_assert(std::is_same<decltype(&GetSecurityDescriptorDacl), DescriptorDaclQuery>::value, "Descriptor DACL query signature differs.");
static_assert(DACL_SECURITY_INFORMATION == 0x00000004 && UNPROTECTED_DACL_SECURITY_INFORMATION == 0x20000000,
    "Owned unprotected-case setup flags differ.");
static_assert(SE_DACL_PROTECTED == 0x1000 && READ_CONTROL == 0x20000 && WRITE_DAC == 0x40000 && FILE_READ_ATTRIBUTES == 0x80,
    "Owned parent protection/access constants differ.");

// Present-device property reader: compile-only checks against the actual SDK,
// independent of managed declarations. No device enumeration or property read.
static_assert(sizeof(SP_DEVINFO_DATA) == 32, "Device information layout differs.");
static_assert(offsetof(SP_DEVINFO_DATA, ClassGuid) == 4, "Device class offset differs.");
static_assert(offsetof(SP_DEVINFO_DATA, DevInst) == 20, "Device instance offset differs.");
static_assert(offsetof(SP_DEVINFO_DATA, Reserved) == 24, "Device reserved offset differs.");
static_assert(sizeof(DEVPROPKEY) == 20 && offsetof(DEVPROPKEY, pid) == 16,
    "Device property key layout differs.");
static_assert(sizeof(DEVPROPTYPE) == 4 && DEVPROP_TYPE_STRING == 0x12 && DEVPROP_TYPE_FILETIME == 0x10,
    "Device property type width or constants differ.");
static_assert(DIGCF_PRESENT == 2 && DIGCF_ALLCLASSES == 4, "Present/all-class flags differ.");
using DevicePropertyQuery = BOOL (WINAPI*)(HDEVINFO, PSP_DEVINFO_DATA, const DEVPROPKEY*,
    DEVPROPTYPE*, PBYTE, DWORD, PDWORD, DWORD);
static_assert(std::is_same<decltype(&SetupDiGetDevicePropertyW), DevicePropertyQuery>::value,
    "Device property query signature differs.");

// Same SetupAPI read function, new exact string-list property type; no device query.
static_assert(DEVPROP_TYPE_STRING_LIST == 0x2012, "Device identifier list property type differs.");
