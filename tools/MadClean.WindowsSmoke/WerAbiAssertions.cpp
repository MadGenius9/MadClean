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
