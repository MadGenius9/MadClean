# Temporary internal neutral-test transfer

The coordinator explicitly authorized this temporary transport file after its UI worker verified the existing task's private **New tab → Files → file Open dropdown → Save as** route. It is not a final release ZIP or user deliverable. No outbound upload tool was added, and this task has not downloaded/transferred/executed it or controlled the VM.

The coordinator subsequently reports a successful Windows-host download with matching archive SHA-256. The coordinator subsequently verified the specifically approved frozen neutral test passed with exit0, then restored the clean offline snapshot and shut down the VM. [Exact evidence scope and report hash](COORDINATED-NEUTRAL-VM-EVIDENCE.md). This task has not received/parsed the report JSON; the result qualifies neither later source changes nor a Windows executable.

- Source: `/workspace/MadClean/artifacts/neutral-vm-test/MadClean-neutral-test-cd1718b-internal-transfer.zip`
- Exact size: **8,530,211 bytes**
- Archive SHA-256: `d2fffc8e9f6e02956f5c9453f433c542c6abd0a2d010a64cad07a950146ebf6f`
- Original manifest SHA-256: `bdf64578325c4e5cdb95cac6651f2315bc44272dadf740d0e681ce5477b07439`
- Frozen source: `cd1718b8a989e5d21b535a6d17db1d2d09bf1113`
- Contents: **132 regular entries** = **131 payload files** plus the original manifest, no README or directory entries. Payload bytes total32,162,491.

The entire central directory was enumerated and checked for duplicates, case collisions, unsafe/absolute/traversing/ADS paths, symlink/nonregular/encrypted entries and unexpected names. CRC decompression passed. Each file was extracted into a fresh owned temporary directory and compared to the original manifest's size/SHA-256; manifest bytes matched exactly. The source payload and manifest were reverified unchanged afterward. No secrets, .git, caches, unrelated files or mutable workspace outputs were added; the build files are the existing frozen copies only.

## Extraction layout and neutral invocation

Extraction produces `manifest.json` beside `payload/`. Manifest row paths resolve below that `payload/`; the manifest's frozen_payload_directory field remains the historical cloud-source path and must not be used as a guest destination.

The revised working directory is **`<extraction-root>\payload`**, on the guest's writable fixed local drive after the coordinator stages/extracts it. The existing host/path/report guards reject running directly from a CD-ROM/network path. For example, if extraction root is `C:\MadCleanNeutralTransfer\cd1718b`, the proposed command is:

```powershell
Set-Location -LiteralPath 'C:\MadCleanNeutralTransfer\cd1718b\payload'
& 'C:\Program Files\dotnet\dotnet.exe' '.\tools\MadClean.WindowsSmoke\bin\Release\net10.0-windows10.0.22000.0\MadClean.WindowsSmoke.dll' '--local-ui-validation'
```

This documents the already scoped neutral mode; it is not authorization for this task to execute it. All [original runtime/profile/permission limits](NEUTRAL-VM-TEST-BUILD.md) still apply. Verify existing x64 .NET10 Core and WindowsDesktop runtimes in the guest. No Windows EXE was produced; extensionless apphosts in the frozen output are Linux ELF and must not be invoked on Windows.

The only expected intentional report write is `<extraction-root>\payload\artifacts\local-ui-validation\<fresh-GUID>\report.json`. The manifest stays at the extraction root. No inventory, online query, maintenance command or physical input is part of this neutral mode. The newer public repair card and separate repair-review validation mode are **not in this older frozen payload**. The archive is not a tested release and must not be described as one.
