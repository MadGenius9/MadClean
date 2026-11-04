using System.Collections.Concurrent;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SystemToolAdmissionTests
{
    [Theory]
    [InlineData(SystemToolOperation.DismCheckHealth)]
    [InlineData(SystemToolOperation.DismScanHealth)]
    [InlineData(SystemToolOperation.SfcVerifyOnly)]
    public void SupportedReadOnlyOwnerBlocksOtherRoutesUntilExplicitRelease(SystemToolOperation operation)
    {
        var gate = new SystemToolAdmissionGate();
        var request = Guid.NewGuid();
        var lease = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(operation, request));
        Assert.Equal(operation, lease.Operation);
        Assert.Equal(request, lease.RequestId);
        Assert.True(gate.HasPendingWork);
        Assert.False(gate.RequiresRetention);
        Assert.Null(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.NewGuid()));
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismScanHealth, request));

        lease.Complete(ownershipReleased: true);

        Assert.False(gate.HasPendingWork);
        Assert.False(gate.RequiresRetention);
        var successor = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()));
        successor.Complete(ownershipReleased: true);
    }

    [Theory]
    [InlineData(SystemToolOperation.DismRestoreHealth)]
    [InlineData(SystemToolOperation.SfcScanNow)]
    [InlineData((SystemToolOperation)(-1))]
    [InlineData((SystemToolOperation)int.MaxValue)]
    public void RepairAndUnknownOperationsDoNotConsumeAdmission(SystemToolOperation operation)
    {
        var gate = new SystemToolAdmissionGate();
        Assert.Null(gate.TryAcquire(operation, Guid.NewGuid()));
        Assert.False(gate.HasPendingWork);
        Assert.False(gate.RequiresRetention);
        Assert.NotNull(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.NewGuid()));
    }

    [Fact]
    public void EmptyRequestIdentityCannotConsumeOrStealAdmission()
    {
        var gate = new SystemToolAdmissionGate();
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.Empty));
        var lease = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.NewGuid()));
        Assert.Null(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.Empty));
        Assert.True(gate.HasPendingWork);
        lease.Complete(ownershipReleased: true);
        Assert.False(gate.HasPendingWork);
    }

    [Fact]
    public void ConcurrentDifferentRoutesAdmitExactlyOneHeldOwner()
    {
        var gate = new SystemToolAdmissionGate();
        SystemToolOperation[] operations = [SystemToolOperation.DismCheckHealth,
            SystemToolOperation.DismScanHealth, SystemToolOperation.SfcVerifyOnly];
        var acquired = new ConcurrentBag<SystemToolAdmissionLease>();

        Parallel.For(0, 128, index =>
        {
            var lease = gate.TryAcquire(operations[index % operations.Length], Guid.NewGuid());
            if (lease != null) acquired.Add(lease);
        });

        var soleOwner = Assert.Single(acquired);
        Assert.True(gate.HasPendingWork);
        soleOwner.Complete(ownershipReleased: true);
        Assert.False(gate.HasPendingWork);
        Assert.NotNull(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, Guid.NewGuid()));
    }

    [Fact]
    public void UncertainReleaseIsTerminalRetainedAndCannotBeClearedByLaterCallbacks()
    {
        var gate = new SystemToolAdmissionGate();
        var request = Guid.NewGuid();
        var lease = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, request));

        lease.Complete(ownershipReleased: false);
        Assert.False(gate.HasPendingWork);
        Assert.True(gate.RequiresRetention);

        Parallel.For(0, 128, index => lease.Complete(ownershipReleased: index % 2 == 0));

        Assert.False(gate.HasPendingWork);
        Assert.True(gate.RequiresRetention);
        Assert.Null(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, request));
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismCheckHealth, Guid.NewGuid()));
    }

    [Fact]
    public void CompletedOwnerCannotReleaseOrRetainSuccessorWithReusedRequestIdentity()
    {
        var gate = new SystemToolAdmissionGate();
        var request = Guid.NewGuid();
        var old = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.DismCheckHealth, request));
        old.Complete(ownershipReleased: true);
        var current = Assert.IsType<SystemToolAdmissionLease>(gate.TryAcquire(SystemToolOperation.SfcVerifyOnly, request));

        Parallel.For(0, 128, index => old.Complete(ownershipReleased: index % 2 == 0));

        Assert.True(gate.HasPendingWork);
        Assert.False(gate.RequiresRetention);
        Assert.Null(gate.TryAcquire(SystemToolOperation.DismScanHealth, Guid.NewGuid()));
        current.Complete(ownershipReleased: true);
        old.Complete(ownershipReleased: false);
        Assert.False(gate.HasPendingWork);
        Assert.False(gate.RequiresRetention);
        Assert.NotNull(gate.TryAcquire(SystemToolOperation.DismScanHealth, request));
    }

    [Fact]
    public void ScopeIsOnlyTheSharedInstanceNotGlobalOrCrossProcessExclusion()
    {
        var first = new SystemToolAdmissionGate();
        var separate = new SystemToolAdmissionGate();
        var request = Guid.NewGuid();
        var firstLease = Assert.IsType<SystemToolAdmissionLease>(first.TryAcquire(SystemToolOperation.SfcVerifyOnly, request));
        var separateLease = Assert.IsType<SystemToolAdmissionLease>(separate.TryAcquire(SystemToolOperation.DismCheckHealth, request));

        firstLease.Complete(ownershipReleased: false);
        separateLease.Complete(ownershipReleased: true);

        Assert.True(first.RequiresRetention);
        Assert.False(separate.RequiresRetention);
        Assert.False(separate.HasPendingWork);
        Assert.NotNull(separate.TryAcquire(SystemToolOperation.DismScanHealth, Guid.NewGuid()));
    }
}
