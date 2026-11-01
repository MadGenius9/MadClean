// Compile-only development probe: cl /nologo /c /W4 /WX /Fo:<owned-output.obj> WerAbiAssertions.cpp
// Use an x64 Windows SDK developer command prompt. No linking or execution is needed.
#include <windows.h>
#include <werapi.h>
#include <wincrypt.h>
#include <mssip.h>
#include <mscat.h>
#include <wintrust.h>
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
