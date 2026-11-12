# Local recovery baseline

Development resumes at local recovery checkpoint `e089efe` on `recovery/validated-20261112`. Its parent is the cached `fd4d64f` history. It is reconstructed from this task's recorded source evidence, not a claim to the missing historical `b3bd541` tree. The original repository and recovery evidence remain preserved separately; no GitHub fetch, push or Actions run was used.

The checkpoint's 642 source inputs match manifest SHA-256 `50c069b3c10e8cb6fd536433cd0485fc6ae93f8f1d8b74ac2b3b77943e6a0f1f`. Two explicitly commented fixture suffixes in `SystemRepairStoredJournalTests.cs` and `SystemRepairServicingBackendTests.cs` are new, independently reviewed code. Other truncated tests were recovered from exact complementary views/overlaps. The WUA fixture constructor correction matches a recorded historical edit; it changes no assertion or production behavior.

The authenticated original setup request is turn `01a1760a-75a1-7221-acd8-c698beb693aa`, message `01a1760a-793e-733c-85c0-7c5ca068e79b`: “Use $cloud-environment-onboarding:setup to set up this cloud environment.” The following build request asks for a supported SDK verified in the environment. This authorized restoring the pinned development dependency in the task-local workspace, not installing updates on the user's PC.

Microsoft's SDK 10.0.403 linux-x64 archive was installed at `/workspace/.dotnet` after matching its official release-metadata SHA-512:

```text
b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5
```

The release metadata and archive used `https://builds.dotnet.microsoft.com/dotnet/`. No environment configuration draft, PATH or system settings were changed. Existing build commands and process-local CLI-home/package-cache variables remain documented in AGENTS.md.

Checkpoint validation on Linux: locked restore passed; full Release solution build had zero warnings/errors; Core 2,162 passed/one Windows-only skipped; Python 297 passed/one skipped. Matching historical counts do not prove source equivalence. No current Windows UI/provider/action or executable qualification follows. The separately supplied old `cd1718b` neutral report matched its 572-byte hash and passed passive consistency checking; it remains old frozen-build evidence only.
