using System.Collections;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class JournalCreationParentSecurityTests
{
    private const string User = "S-1-5-21-123-456-789-1001";
    private static JournalParentAce Allow(string? sid = User, int mask = 0x001f01ff, int flags = 0) =>
        new(JournalParentAceKind.OrdinaryAllow, sid, mask, flags);
    private static bool Supported(params JournalParentAce[] aces) =>
        JournalCreationParentSecurity.IsSupported(User, User, true, aces);

    [Fact]
    public void ExplicitTrustIncludesCurrentUserSystemAndAdministratorsWithInheritedEntries()
    {
        Assert.True(Supported(Allow(), Allow("S-1-5-18", flags: 0x13), Allow("S-1-5-32-544", flags: 0x1f)));
        Assert.True(Supported(Allow("S-1-5-18", mask: 1)));
        Assert.True(Supported(Allow("S-1-5-32-544", mask: 0x10000)));
        Assert.True(Supported(Allow(), Allow()));
    }

    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-5-21-123-456-789-1002")]
    public void AnyForeignAllowIncludingReadOnlyOrInheritOnlyIsUnsupported(string sid)
    {
        Assert.False(Supported(Allow(), Allow(sid, mask: 1)));
        Assert.False(Supported(Allow(), Allow(sid, mask: 1, flags: 8)));
    }

    [Fact]
    public void MissingOwnerUserOrDaclAndEmptyDaclAreUnsupported()
    {
        Assert.False(JournalCreationParentSecurity.IsSupported(null, null, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(null, User, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(User, null, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported("S-1-5-18", User, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(User.ToLowerInvariant(), User, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(User + " ", User, true, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, false, [Allow()]));
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, true, null));
        Assert.False(Supported());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" S-1-5-18")]
    [InlineData("S-1-5-18 ")]
    [InlineData("s-1-5-18")]
    [InlineData("S-1-05-18")]
    [InlineData("S-1-5-018")]
    [InlineData("S-1-5--18")]
    [InlineData("S-1-5-18-")]
    [InlineData("S-1-281474976710656-18")]
    [InlineData("S-1-5-4294967296")]
    [InlineData("S-1-5-١٨")]
    public void MalformedIdentityCannotBecomeTrustedEvenWhenOwnerAndUserMatch(string? sid)
    {
        Assert.False(JournalCreationParentSecurity.IsSupported(sid, sid, true, [Allow(sid)]));
        Assert.False(Supported(Allow(sid)));
    }

    [Fact]
    public void SidComponentAndLengthBoundsRejectUnboundedIdentity()
    {
        var longest = "S-1-281474976710655" + string.Concat(Enumerable.Repeat("-4294967295", 15));
        Assert.True(JournalCreationParentSecurity.IsSupported(longest, longest, true, [Allow(longest)]));
        var tooMany = "S-1-5" + string.Concat(Enumerable.Repeat("-1", 16));
        Assert.False(JournalCreationParentSecurity.IsSupported(tooMany, tooMany, true, [Allow(tooMany)]));
        var tooLong = "S-1-5-" + new string('1', 185);
        Assert.False(JournalCreationParentSecurity.IsSupported(tooLong, tooLong, true, [Allow(tooLong)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x200)]
    [InlineData(0x200000)]
    [InlineData(0x01000000)]
    [InlineData(0x02000000)]
    [InlineData(0x10000000)]
    [InlineData(unchecked((int)0x80000000))]
    [InlineData(-1)]
    public void ZeroReservedGenericAndAdditionalRightsAreUnsupported(int mask) =>
        Assert.False(Supported(Allow(mask: mask)));

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0x100)]
    [InlineData(-1)]
    public void UnsupportedAceFlagsAreRejected(int flags) => Assert.False(Supported(Allow(flags: flags)));

    [Fact]
    public void EverySupportedInheritanceFlagAndConcreteRightIsAccepted()
    {
        for (var flags = 0; flags <= 0x1f; flags++) Assert.True(Supported(Allow(mask: 1, flags: flags)));
        for (var bit = 0; bit < 31; bit++)
            if ((0x001f01ff & (1 << bit)) != 0) Assert.True(Supported(Allow(mask: 1 << bit)));
    }

    [Fact]
    public void UnsupportedProjectionKindsAndNullEntriesFailClosed()
    {
        // The adapter projects deny/object/callback/compound/unknown ACEs as Unsupported.
        Assert.False(Supported(Allow() with { Kind = JournalParentAceKind.Unsupported }));
        Assert.False(Supported(Allow() with { Kind = (JournalParentAceKind)2 }));
        Assert.False(Supported([null!]));
    }

    [Fact]
    public void AceCountIsBoundedWithoutCollapsingDuplicateLegitimateEntries()
    {
        Assert.True(Supported(Enumerable.Repeat(Allow(), JournalCreationParentSecurity.MaximumAces).ToArray()));
        Assert.False(Supported(Enumerable.Repeat(Allow(), JournalCreationParentSecurity.MaximumAces + 1).ToArray()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public void InvalidCountNeverReadsEntries(int count)
    {
        var list = new HostileList(() => count, _ => throw new InvalidOperationException());
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, true, list));
        Assert.Equal(0, list.Reads);
    }

    [Fact]
    public void ThrowingCountIndexerOrChangingCountFailsClosedWithoutEnumeration()
    {
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, true,
            new HostileList(() => throw new InvalidOperationException(), _ => Allow())));
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, true,
            new HostileList(() => 1, _ => throw new InvalidOperationException())));
        var calls = 0;
        var changing = new HostileList(() => ++calls == 1 ? 1 : int.MaxValue, _ => Allow());
        Assert.False(JournalCreationParentSecurity.IsSupported(User, User, true, changing));
        Assert.Equal(1, changing.Reads);
        var bounded = new HostileList(() => 32, _ => Allow());
        Assert.True(JournalCreationParentSecurity.IsSupported(User, User, true, bounded));
        Assert.Equal(32, bounded.Reads);
    }

    private sealed class HostileList(Func<int> count, Func<int, JournalParentAce> item) : IReadOnlyList<JournalParentAce>
    {
        public int Reads { get; private set; }
        public int Count => count();
        public JournalParentAce this[int index] { get { Reads++; return item(index); } }
        public IEnumerator<JournalParentAce> GetEnumerator() => throw new InvalidOperationException("Must not enumerate.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
