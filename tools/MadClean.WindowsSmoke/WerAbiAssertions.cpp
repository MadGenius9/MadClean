// Compile-only development probe: cl /nologo /c /W4 /WX /Fo:<owned-output.obj> WerAbiAssertions.cpp
// Use an x64 Windows SDK developer command prompt. No linking or execution is needed.
#include <windows.h>
#include <werapi.h>
#include <cstddef>

static_assert(sizeof(void*) == 8, "The supported WER adapter ABI is 64-bit only.");
static_assert(E_STORE_MACHINE_ARCHIVE == 2 && E_STORE_MACHINE_QUEUE == 3, "WER store enumeration values differ.");
static_assert(sizeof(WER_REPORT_PARAMETER) == 778, "WER parameter layout differs.");
static_assert(sizeof(WER_REPORT_SIGNATURE) == 7910, "WER signature layout differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, BucketId) == 7912, "WER bucket offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, ReportId) == 7928, "WER report offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, CreationTime) == 7944, "WER timestamp offset differs.");
static_assert(offsetof(WER_REPORT_METADATA_V1, SizeInBytes) == 7952, "WER size offset differs.");
static_assert(sizeof(WER_REPORT_METADATA_V1) == 7960, "WER V1 allocation size differs.");
