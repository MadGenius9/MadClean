using System.Runtime.InteropServices;
using System.Security.Principal;
using MadClean.Core;

namespace MadClean.Windows;

// Source-only fixed adapter. All callable native entry points check the immutable
// qualification gate before inspecting tokens or touching Windows.
internal sealed class WindowsSystemRepairDirectLaunchApi : ISystemRepairDirectLaunchApi
{
    internal static bool ProductionExecutionValidated => false;
    private readonly SystemRepairExecutionContext context;
    private readonly Target target;
    private readonly Handle[] handles;
    private readonly List<Allocation> allocations = [];
    private readonly List<Reader> readers = [];
    private IReadOnlyList<SystemRepairDirectHandle>? children;
    private bool createAttempted;

    internal WindowsSystemRepairDirectLaunchApi(SystemRepairExecutionContext context, SystemToolTrustedLease identity)
    {
        this.context = context;
        target = new Target(context, identity);
        handles = Enum.GetValues<SystemRepairDirectHandleRole>().Select(role => new Handle(context, role)).ToArray();
    }
    internal SystemRepairDirectLaunchTarget HeldTarget => target;
    private static void Guard()
    {
        if (!ProductionExecutionValidated || !SystemToolJournalPrivateStore.ProductionCreationValidated)
            throw new InvalidOperationException("Direct native repair execution is not qualified.");
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The direct adapter requires 64-bit Windows.");
        VerifyLayout();
    }
    private static void VerifyLayout()
    {
        // Managed layout sanity only; this is not Windows runtime qualification.
        if (Marshal.SizeOf<StartupInfo>() != 104 || Marshal.SizeOf<StartupInfoEx>() != 112 ||
            Marshal.SizeOf<ProcessInformation>() != 24 ||
            Marshal.OffsetOf<StartupInfo>(nameof(StartupInfo.Reserved)).ToInt32() != 8 ||
            Marshal.OffsetOf<StartupInfo>(nameof(StartupInfo.Flags)).ToInt32() != 60 ||
            Marshal.OffsetOf<StartupInfo>(nameof(StartupInfo.Reserved2)).ToInt32() != 72 ||
            Marshal.OffsetOf<StartupInfo>(nameof(StartupInfo.Stdin)).ToInt32() != 80 ||
            Marshal.OffsetOf<StartupInfo>(nameof(StartupInfo.Stderr)).ToInt32() != 96 ||
            Marshal.OffsetOf<StartupInfoEx>(nameof(StartupInfoEx.Attributes)).ToInt32() != 104 ||
            Marshal.OffsetOf<ProcessInformation>(nameof(ProcessInformation.ProcessId)).ToInt32() != 16)
            throw new InvalidOperationException("The managed native-call layout is unsupported.");
    }
    private void Context(SystemRepairExecutionContext value)
    { Guard(); if (!ReferenceEquals(value, context)) throw new InvalidOperationException("Foreign repair context."); }
    private Handle Owned(SystemRepairDirectHandle value)
    {
        Guard();
        Context(value.Context);
        if (value is not Handle handle || !handles.Contains(handle) || !handle.Owned || handle.CloseAttempted)
            throw new InvalidOperationException("Handle ownership is unavailable.");
        return handle;
    }
    private Allocation Owned(SystemRepairDirectAllocation value, SystemRepairDirectAllocationRole role)
    {
        Guard();
        Context(value.Context);
        if (value is not Allocation allocation || !allocations.Contains(allocation) || allocation.Role != role ||
            allocation.Pointer == 0 || allocation.FreeAttempted) throw new InvalidOperationException("Allocation ownership is unavailable.");
        return allocation;
    }
    private Handle Slot(SystemRepairDirectHandleRole role) => handles[(int)role];
    private void Adopt(Handle handle, nint raw)
    {
        // Positive-only conservative admission also refuses all negative pseudo-handle
        // values. A refused actual raw output stays in its rooted slot, never closed.
        handle.Raw = raw; handle.Owned = true;
        if (raw <= 0 || handles.Any(other => !ReferenceEquals(other, handle) && other.Owned && other.Raw == raw))
            throw new InvalidOperationException("Native handle shape or alias is uncertain.");
    }
    public SystemRepairDirectPipeResult CreatePipe(SystemRepairExecutionContext value, SystemRepairDirectPipe pipe)
    {
        Context(value);
        var (read, write) = pipe switch
        {
            SystemRepairDirectPipe.StandardInput => (Slot(SystemRepairDirectHandleRole.StdinChildRead), Slot(SystemRepairDirectHandleRole.StdinParentWrite)),
            SystemRepairDirectPipe.StandardOutput => (Slot(SystemRepairDirectHandleRole.StdoutParentRead), Slot(SystemRepairDirectHandleRole.StdoutChildWrite)),
            SystemRepairDirectPipe.StandardError => (Slot(SystemRepairDirectHandleRole.StderrParentRead), Slot(SystemRepairDirectHandleRole.StderrChildWrite)),
            _ => throw new InvalidOperationException()
        };
        if (read.EverAdopted || write.EverAdopted) throw new InvalidOperationException("Pipe slots are one shot.");
        int received = Native.CreatePipe(out nint rawRead, out nint rawWrite, 0, 0);
        int error = Marshal.GetLastPInvokeError();
        if (received == 0) return new(false, null, null, error); // Failed outputs are indeterminate.
        // Record BOTH success outputs before validation/result allocation can throw.
        read.Raw = rawRead; read.Owned = read.EverAdopted = true;
        write.Raw = rawWrite; write.Owned = write.EverAdopted = true;
        Adopt(read, rawRead); Adopt(write, rawWrite);
        return new(true, read, write);
    }
    private SystemRepairDirectOperationResult SetInheritance(SystemRepairDirectHandle value, bool inherit)
    {
        var handle = Owned(value);
        bool child = handle.Role is SystemRepairDirectHandleRole.StdinChildRead or SystemRepairDirectHandleRole.StdoutChildWrite or SystemRepairDirectHandleRole.StderrChildWrite;
        if (child != inherit) throw new InvalidOperationException("Unexpected handle-list role.");
        if (Native.SetHandleInformation(handle.Raw, 1, inherit ? 1u : 0u) == 0) return new(false, Marshal.GetLastPInvokeError());
        if (Native.GetHandleInformation(handle.Raw, out uint flags) == 0) return new(false, Marshal.GetLastPInvokeError());
        return new((flags & 1) == (inherit ? 1u : 0u) && (flags & 2) == 0);
    }
    public SystemRepairDirectOperationResult MakeParentNonInheritable(SystemRepairDirectHandle handle) => SetInheritance(handle, false);
    public SystemRepairDirectOperationResult MakeChildInheritable(SystemRepairDirectHandle handle) => SetInheritance(handle, true);
    public SystemRepairDirectSizeResult QueryAttributeSize(SystemRepairExecutionContext value)
    {
        Context(value);
        nuint size = 0;
        int received = Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        int error = Marshal.GetLastPInvokeError();
        return received == 0 && error == 122 && size is > 0 and <= 65536
            ? new(SystemRepairDirectSizeState.RequiredSize, size, error)
            : new(SystemRepairDirectSizeState.Failed, 0, received == 0 ? error : null);
    }
    private Allocation Allocate(SystemRepairDirectAllocationRole role, nuint bytes)
    {
        if (bytes is 0 or > 65536 || allocations.Any(a => a.Role == role)) throw new InvalidOperationException("Allocation bounds or reuse.");
        var allocation = new Allocation(context, role, bytes);
        allocations.Add(allocation); // Root the mutable output slot BEFORE allocation.
        allocation.Pointer = Marshal.AllocHGlobal(checked((int)bytes));
        for (int i = 0; i < (int)bytes; i++) Marshal.WriteByte(allocation.Pointer, i, 0);
        return allocation;
    }
    public SystemRepairDirectAllocation AllocateAttributes(SystemRepairExecutionContext value, nuint byteCount)
    { Context(value); return Allocate(SystemRepairDirectAllocationRole.AttributeList, byteCount); }
    private void Children(IReadOnlyList<SystemRepairDirectHandle> exact)
    {
        if (exact.Count != 3 || !ReferenceEquals(exact[0], Slot(SystemRepairDirectHandleRole.StdinChildRead)) ||
            !ReferenceEquals(exact[1], Slot(SystemRepairDirectHandleRole.StdoutChildWrite)) ||
            !ReferenceEquals(exact[2], Slot(SystemRepairDirectHandleRole.StderrChildWrite))) throw new InvalidOperationException("The exact child handle list is required.");
        foreach (var handle in exact) _ = Owned(handle);
        if (children != null && !ReferenceEquals(children, exact)) throw new InvalidOperationException("Changed child handle list.");
        children = exact;
    }
    public SystemRepairDirectAllocation AllocateHandleValues(SystemRepairExecutionContext value, IReadOnlyList<SystemRepairDirectHandle> exact)
    {
        Context(value); Children(exact);
        var allocation = Allocate(SystemRepairDirectAllocationRole.HandleListValues, checked((nuint)(3 * IntPtr.Size)));
        for (int i = 0; i < 3; i++) Marshal.WriteIntPtr(allocation.Pointer, i * IntPtr.Size, Owned(exact[i]).Raw);
        return allocation;
    }
    public SystemRepairDirectOperationResult InitializeAttributes(SystemRepairDirectAllocation value)
    {
        var allocation = Owned(value, SystemRepairDirectAllocationRole.AttributeList);
        if (allocation.Initialized) throw new InvalidOperationException("Attribute list already initialized.");
        nuint size = allocation.ByteCount;
        int received = Native.InitializeProcThreadAttributeList(allocation.Pointer, 1, 0, ref size);
        int error = Marshal.GetLastPInvokeError();
        if (received != 0) allocation.Initialized = true;
        return new(received != 0, received == 0 ? error : null);
    }
    public SystemRepairDirectOperationResult UpdateHandleList(SystemRepairDirectAllocation attributes,
        SystemRepairDirectAllocation values, IReadOnlyList<SystemRepairDirectHandle> exact)
    {
        var list = Owned(attributes, SystemRepairDirectAllocationRole.AttributeList);
        var block = Owned(values, SystemRepairDirectAllocationRole.HandleListValues);
        Children(exact);
        if (!list.Initialized || list.Updated) throw new InvalidOperationException();
        int received = Native.UpdateProcThreadAttribute(list.Pointer, 0, 0x00020002, block.Pointer, block.ByteCount, 0, 0);
        int error = Marshal.GetLastPInvokeError();
        if (received != 0) list.Updated = true;
        return new(received != 0, received == 0 ? error : null);
    }
    public SystemRepairDirectAllocation AllocateCreateState(SystemRepairExecutionContext value, SystemRepairDirectLaunchTarget selected)
    {
        Context(value);
        if (!ReferenceEquals(selected, target)) throw new InvalidOperationException("Foreign fixed target.");
        ValidateHeldTarget();
        string app = target.Identity.ToolPath;
        string system = target.Identity.SystemDirectory;
        string windows = Path.GetDirectoryName(system) ?? throw new InvalidOperationException();
        string args = context.Operation switch
        {
            SystemToolOperation.DismRestoreHealth => "/Online /Cleanup-Image /RestoreHealth /NoRestart",
            SystemToolOperation.SfcScanNow => "/scannow",
            _ => throw new InvalidOperationException()
        };
        if (app.Contains('"') || app.Contains('\0') || system.Contains('\0') || windows.Contains('\0')) throw new InvalidOperationException();
        string command = "\"" + app + "\" " + args;
        // Case-insensitive ordinal sorting: PATH, SystemRoot, windir.
        string env = "PATH=" + system + "\0SystemRoot=" + windows + "\0windir=" + windows + "\0\0";
        int appBytes = checked((app.Length + 1) * 2), commandBytes = checked((command.Length + 1) * 2);
        int cwdBytes = checked((system.Length + 1) * 2), envBytes = checked(env.Length * 2);
        int startupOffset = Align(checked(appBytes + commandBytes + cwdBytes + envBytes));
        int processOffset = Align(checked(startupOffset + Marshal.SizeOf<StartupInfoEx>()));
        int total = checked(processOffset + Marshal.SizeOf<ProcessInformation>());
        var frame = Allocate(SystemRepairDirectAllocationRole.CreateCallState, checked((nuint)total));
        frame.App = frame.Pointer;
        frame.Command = frame.App + appBytes;
        frame.Directory = frame.Command + commandBytes;
        frame.Environment = frame.Directory + cwdBytes;
        frame.Startup = frame.Pointer + startupOffset;
        frame.ProcessInformation = frame.Pointer + processOffset;
        WriteUtf16(frame.App, app); WriteUtf16(frame.Command, command); WriteUtf16(frame.Directory, system); WriteUtf16(frame.Environment, env);
        return frame;
    }
    private static int Align(int value) => checked((value + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size);
    private void ValidateHeldTarget()
    {
        string leaf = context.Operation switch
        {
            SystemToolOperation.DismRestoreHealth => "Dism.exe",
            SystemToolOperation.SfcScanNow => "sfc.exe",
            _ => throw new InvalidOperationException("Unsupported fixed repair target.")
        };
        var held = target.Identity;
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Path.IsPathFullyQualified(system) || !Path.IsPathFullyQualified(held.SystemDirectory) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(system)), held.SystemDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(held.ToolPath, Path.Combine(held.SystemDirectory, leaf), StringComparison.OrdinalIgnoreCase) ||
            held.ToolVersion == "Unknown" || string.IsNullOrWhiteSpace(held.ToolVersion) ||
            held.EmbeddedVersionObservation.State != SystemToolEmbeddedVersionState.Matched ||
            held.EmbeddedVersionObservation.FileVersion != held.ToolVersion || !SystemToolActionsPolicy.ValidHash(held.Sha256) ||
            SystemToolTrustNative.HasCleanupUncertainty || SystemToolCatalogObservation.HasCleanupUncertainty)
            throw new InvalidOperationException("The held identity does not qualify the exact fixed repair target.");
    }
    private static void WriteUtf16(nint destination, string text)
    { for (int i = 0; i < text.Length; i++) Marshal.WriteInt16(destination, checked(i * 2), unchecked((short)text[i])); }
    public SystemRepairDirectDrain StartDiscardDrain(SystemRepairDirectHandle parentRead)
    {
        var handle = Owned(parentRead);
        if (handle.Role is not (SystemRepairDirectHandleRole.StdoutParentRead or SystemRepairDirectHandleRole.StderrParentRead) || readers.Any(r => ReferenceEquals(r.ParentRead, handle)))
            throw new InvalidOperationException();
        var reader = new Reader(context, handle);
        readers.Add(reader); // Own buffer/pin/task frame before scheduling any read.
        reader.Start();
        return reader;
    }
    public SystemRepairDirectCreateResult CreateProcess(SystemRepairDirectLaunchTarget selected,
        SystemRepairDirectAllocation attributes, SystemRepairDirectAllocation createState, IReadOnlyList<SystemRepairDirectHandle> exact)
    {
        Guard();
        if (!ReferenceEquals(selected, target) || createAttempted) throw new InvalidOperationException();
        var list = Owned(attributes, SystemRepairDirectAllocationRole.AttributeList);
        var frame = Owned(createState, SystemRepairDirectAllocationRole.CreateCallState);
        Children(exact);
        ValidateHeldTarget();
        if (!list.Initialized || !list.Updated || readers.Count != 2 || readers.Any(r => !r.Ready || r.Completion.IsCompleted))
            return new(false, null, null, null);
        if (!AlreadyAdministrator()) return new(false, null, null, null);
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { Cb = checked((uint)Marshal.SizeOf<StartupInfoEx>()), Flags = 0x100,
                Stdin = Owned(exact[0]).Raw, Stdout = Owned(exact[1]).Raw, Stderr = Owned(exact[2]).Raw },
            Attributes = list.Pointer
        };
        Marshal.StructureToPtr(startup, frame.Startup, false);
        // Token/layout/privilege work above may have outlived a reader. Recheck
        // immediately before the native boundary; future IO failure remains possible.
        if (readers.Any(reader => reader.Completion.IsCompleted)) return new(false, null, null, null);
        createAttempted = true;
        int received = Native.CreateProcessW(frame.App, frame.Command, 0, 0, 1,
            0x400 | 0x80000 | 0x08000000, frame.Environment, frame.Directory, frame.Startup, frame.ProcessInformation);
        int error = Marshal.GetLastPInvokeError();
        if (received == 0) return new(false, null, null, null, error);
        // PI is explicit rooted memory. Save BOTH raw handles before result allocation
        // or shape validation. Native success outputs alone grant ownership.
        var process = Slot(SystemRepairDirectHandleRole.Process);
        var thread = Slot(SystemRepairDirectHandleRole.PrimaryThread);
        process.Raw = Marshal.ReadIntPtr(frame.ProcessInformation); process.Owned = process.EverAdopted = true;
        thread.Raw = Marshal.ReadIntPtr(frame.ProcessInformation, IntPtr.Size); thread.Owned = thread.EverAdopted = true;
        Adopt(process, process.Raw); Adopt(thread, thread.Raw);
        uint pid = unchecked((uint)Marshal.ReadInt32(frame.ProcessInformation, checked(2 * IntPtr.Size)));
        return new(true, process, thread, pid is > 0 and <= int.MaxValue ? (int)pid : null);
    }
    public async Task<SystemRepairDirectWaitResult> WaitForExitAsync(SystemRepairDirectHandle process)
    {
        var handle = Owned(process);
        if (handle.Role != SystemRepairDirectHandleRole.Process) throw new InvalidOperationException();
        while (true)
        {
            uint received = Native.WaitForSingleObject(handle.Raw, 0);
            int error = Marshal.GetLastPInvokeError();
            if (received == 0)
            {
                int success = Native.GetExitCodeProcess(handle.Raw, out uint code);
                int codeError = Marshal.GetLastPInvokeError();
                return new(true, success != 0 ? unchecked((int)code) : null, success == 0 ? codeError : null);
            }
            if (received != 0x102) return new(false, null, received == uint.MaxValue ? error : null);
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
    public SystemRepairDirectOperationResult Close(SystemRepairDirectHandle value)
    {
        var handle = Owned(value);
        handle.CloseAttempted = true;
        int received = Native.CloseHandle(handle.Raw);
        int error = Marshal.GetLastPInvokeError();
        if (received != 0) handle.Owned = false;
        return new(received != 0, received == 0 ? error : null);
    }
    public void DeleteAttributes(SystemRepairDirectAllocation value)
    {
        var allocation = Owned(value, SystemRepairDirectAllocationRole.AttributeList);
        if (!allocation.Initialized || allocation.DeleteAttempted) throw new InvalidOperationException();
        allocation.DeleteAttempted = true;
        Native.DeleteProcThreadAttributeList(allocation.Pointer);
        allocation.Deleted = true;
    }
    public SystemRepairDirectOperationResult Free(SystemRepairDirectAllocation value)
    {
        Guard();
        var allocation = Owned(value, value.Role);
        if (allocation.Initialized && !allocation.Deleted) throw new InvalidOperationException();
        allocation.FreeAttempted = true;
        for (int i = 0; i < (int)allocation.ByteCount; i++) Marshal.WriteByte(allocation.Pointer, i, 0);
        Marshal.FreeHGlobal(allocation.Pointer);
        allocation.Pointer = 0;
        return new(true);
    }
    private static bool AlreadyAdministrator()
    {
        using (var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true)) if (impersonated != null) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private sealed class Target(SystemRepairExecutionContext context, SystemToolTrustedLease identity) : SystemRepairDirectLaunchTarget(context)
    { internal SystemToolTrustedLease Identity { get; } = identity; }
    private sealed class Handle(SystemRepairExecutionContext context, SystemRepairDirectHandleRole role) : SystemRepairDirectHandle(context, role)
    { internal nint Raw; internal bool Owned, EverAdopted, CloseAttempted; }
    private sealed class Allocation(SystemRepairExecutionContext context, SystemRepairDirectAllocationRole role, nuint bytes) : SystemRepairDirectAllocation(context, role, bytes)
    {
        internal nint Pointer, App, Command, Directory, Environment, Startup, ProcessInformation;
        internal bool Initialized, Updated, DeleteAttempted, Deleted, FreeAttempted;
    }
    private sealed class Reader : SystemRepairDirectDrain
    {
        private readonly ReadWorker owner;
        internal Reader(SystemRepairExecutionContext context, Handle handle) : this(context, handle, new ReadWorker(handle)) { }
        private Reader(SystemRepairExecutionContext context, Handle handle, ReadWorker owner)
            : base(context, handle, true, owner.Work) => this.owner = owner;
        internal void Start()
        {
            owner.Prepare();
            owner.Work.Start(TaskScheduler.Default);
        }
    }
    private sealed class ReadWorker
    {
        private readonly Handle handle;
        private readonly byte[] buffer = new byte[SystemRepairDirectPipeReader.BufferSize];
        private GCHandle pin;
        private Func<SystemRepairDirectPipeRead>? read;
        internal readonly Task<SystemRepairDirectDrainResult> Work;
        internal ReadWorker(Handle handle)
        {
            this.handle = handle;
            Work = new Task<SystemRepairDirectDrainResult>(Run, CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach);
        }
        internal void Prepare()
        {
            pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            nint address = pin.AddrOfPinnedObject();
            read = () =>
            {
                int received = Native.ReadFile(handle.Raw, address, checked((uint)buffer.Length), out uint count, 0);
                int error = Marshal.GetLastPInvokeError();
                return new(received != 0, count, received == 0 ? error : null);
            };
        }
        private SystemRepairDirectDrainResult Run()
        {
            try
            {
                var outcome = SystemRepairDirectPipeReader.Discard(buffer, read!);
                // Unknown completion never permits mutation/unpinning of a live buffer.
                bool released = false;
                if (outcome.AllCallsReturned && outcome.EndOfStreamObserved) { pin.Free(); released = true; }
                return new(outcome.EndOfStreamObserved, released, outcome.NativeError);
            }
            catch { return new(false, false, null); }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Cb;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, Reserved2Count;
        internal nint Reserved2, Stdin, Stdout, Stderr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo StartupInfo; internal nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
    private static class Native
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CreatePipe(out nint read, out nint write, nint security, uint size);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int SetHandleInformation(nint handle, uint mask, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int GetHandleInformation(nint handle, out uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int InitializeProcThreadAttributeList(nint list, uint count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CreateProcessW(nint app, nint command, nint processSecurity, nint threadSecurity, int inherit, uint flags, nint environment, nint directory, nint startup, nint information);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint WaitForSingleObject(nint handle, uint timeout);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int GetExitCodeProcess(nint handle, out uint code);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int ReadFile(nint handle, nint buffer, uint size, out uint count, nint overlapped);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int CloseHandle(nint handle);
    }
}
