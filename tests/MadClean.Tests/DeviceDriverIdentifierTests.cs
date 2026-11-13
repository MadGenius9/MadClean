using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DeviceDriverIdentifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static byte[] Bytes(string text) => Encoding.Unicode.GetBytes(text);
    private static DeviceDriverIdentifiers Decode(string text) => DeviceDriverPropertyDecoder.IdentifierList(0x2012, Bytes(text));
    private static DeviceDriverObservation Row(DeviceDriverIdentifiers identifiers) => new(1,
        new(DeviceDriverFieldState.Unknown, null, null), new(DeviceDriverFieldState.Unknown, null, null),
        new(DeviceDriverFieldState.Unknown, null, null), new(DeviceDriverFieldState.Unknown, null, null),
        new(DeviceDriverFieldState.Unknown, null, null), Now) { HardwareIds = identifiers };

    [Fact]
    public void UnicodeListPreservesOrderSpellingAndDuplicateDeclarationsWithoutNormalization()
    {
        var list = Decode("PCI\\VEN_ABCD\0pci\\ven_abcd\0PCI\\VEN_ABCD\0Reported设备😀\0\0");
        Assert.Equal(DeviceDriverFieldState.Available, list.State);
        Assert.Equal(new[] { @"PCI\VEN_ABCD", @"pci\ven_abcd", @"PCI\VEN_ABCD", "Reported设备😀" }, list.Values);
        Assert.Null(list.NativeError); Assert.True(DeviceDriverPropertyDecoder.IsConsistent(Row(list), Now));
    }

    [Fact]
    public void OfficialEmptySequenceIsOneNulAndNeverAnInventedIdentifier()
    {
        var empty = Decode("\0");
        Assert.Equal(DeviceDriverFieldState.Available, empty.State); Assert.Empty(empty.Values);
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(Row(empty), Now));
        Assert.Equal(DeviceDriverFieldState.Invalid, Decode("\0\0").State);
    }

    [Theory]
    [InlineData(0x12u)] [InlineData(0x1012u)] [InlineData(0x2010u)] [InlineData(0x12012u)] [InlineData(0u)]
    public void WrongTypesAndModifiersCannotBeMaskedIntoAList(uint type)
    {
        var result = DeviceDriverPropertyDecoder.IdentifierList(type, Bytes("ID\0\0"));
        Assert.Equal(DeviceDriverFieldState.Invalid, result.State); Assert.Empty(result.Values);
    }

    [Theory]
    [InlineData("")] [InlineData("ID")] [InlineData("ID\0")] [InlineData("ID\0\0tail")]
    [InlineData("ID\0\0tail\0\0")] [InlineData("ID\0\0\0")] [InlineData("\0ID\0\0")]
    [InlineData("ID\0 \0\0")] [InlineData(" ID\0\0")] [InlineData("ID \0\0")]
    [InlineData("ID\tvalue\0\0")] [InlineData("ID\u202Evalue\0\0")]
    public void MissingTerminatorsEmptyInteriorItemsOrUnsafeTextWithholdsTheWholeList(string text)
    {
        var result = Decode(text);
        Assert.Equal(DeviceDriverFieldState.Invalid, result.State); Assert.Empty(result.Values);
        Assert.Null(result.NativeError);
    }

    [Fact]
    public void OddByteCountAndUnpairedSurrogateDoNotProduceReplacementCharacterIdentifiers()
    {
        foreach (byte[] bytes in new[] { new byte[] { 65, 0, 0 }, new byte[] { 0, 0xD8, 0, 0, 0, 0 } })
        {
            var result = DeviceDriverPropertyDecoder.IdentifierList(0x2012, bytes);
            Assert.Equal(DeviceDriverFieldState.Invalid, result.State); Assert.Empty(result.Values);
        }
    }

    [Fact]
    public void NativeReturnedByteSliceCannotBorrowATerminatorFromUnusedBufferCapacity()
    {
        byte[] buffer = new byte[4096]; Bytes("ID").CopyTo(buffer, 0);
        var result = DeviceDriverPropertyDecoder.IdentifierList(0x2012, buffer.AsSpan(0, 4));
        Assert.Equal(DeviceDriverFieldState.Invalid, result.State); Assert.Empty(result.Values);
    }

    [Fact]
    public void BoundsWithholdOversizedListsRatherThanReturnAnAcceptedPrefix()
    {
        foreach (var result in new[]
        {
            Decode(string.Join('\0', Enumerable.Repeat("ID", 33)) + "\0\0"),
            Decode(new string('a', 257) + "\0\0"),
            DeviceDriverPropertyDecoder.IdentifierList(0x2012, new byte[4098])
        })
        {
            Assert.Equal(DeviceDriverFieldState.Oversized, result.State); Assert.Empty(result.Values);
        }
        var exactCount = Decode(string.Join('\0', Enumerable.Repeat("ID", 32)) + "\0\0");
        Assert.Equal(32, exactCount.Values.Count); Assert.Equal(DeviceDriverFieldState.Available, exactCount.State);
        Assert.Equal(DeviceDriverFieldState.Available, Decode(new string('a', 254) + "😀\0\0").State);
        Assert.Equal(DeviceDriverFieldState.Oversized, Decode(new string('a', 255) + "😀\0\0").State);
    }

    [Fact]
    public void ByteLimitAndPerItemLimitAreIndependentAndRevalidatedForConstructedObservations()
    {
        string text = string.Join('\0', Enumerable.Repeat(new string('a', 126), 15).Append(new string('b', 141))) + "\0\0";
        Assert.Equal(4096, Bytes(text).Length);
        var atLimit = Decode(text);
        Assert.Equal(DeviceDriverFieldState.Available, atLimit.State);
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(Row(atLimit), Now));
        var constructedTooLarge = new DeviceDriverIdentifiers(DeviceDriverFieldState.Available,
            Enumerable.Repeat(new string('a', 256), 32), null);
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(constructedTooLarge), Now));
    }

    [Fact]
    public void AcceptedIdentifiersAreCopiedAndCannotBeMutatedAfterPublication()
    {
        var source = new List<string> { @"PCI\VEN_ABCD" };
        var list = new DeviceDriverIdentifiers(DeviceDriverFieldState.Available, source, null);
        source[0] = "Changed"; source.Clear();
        Assert.Equal(@"PCI\VEN_ABCD", Assert.Single(list.Values));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)list.Values).Clear());
        Assert.Null(typeof(DeviceDriverIdentifiers).GetProperty(nameof(DeviceDriverIdentifiers.Values))!.SetMethod);
    }

    [Fact]
    public void UnknownOrFailedFieldsCannotHideValuesAndAvailableCannotHideANativeError()
    {
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(new(DeviceDriverFieldState.Unknown, ["hidden"], null)), Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(new(DeviceDriverFieldState.Available, ["ID"], 0)), Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(new((DeviceDriverFieldState)99, [], null)), Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(new(DeviceDriverFieldState.Available, Enumerable.Repeat("ID", 100), null)), Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(Row(new(DeviceDriverFieldState.Available, [null!], null)), Now));
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(Row(new(DeviceDriverFieldState.AccessDenied, [], 5)), Now));
    }

    [Theory]
    [InlineData(1168, DeviceDriverFieldState.Missing)] [InlineData(5, DeviceDriverFieldState.AccessDenied)]
    [InlineData(122, DeviceDriverFieldState.InsufficientBuffer)] [InlineData(13, DeviceDriverFieldState.Invalid)]
    [InlineData(0, DeviceDriverFieldState.Failed)] [InlineData(int.MinValue, DeviceDriverFieldState.Failed)]
    public void FailedNativeCallsPreserveTheirActualFailureRoleRatherThanClaimingPropertySize(int error, DeviceDriverFieldState expected)
    {
        var state = DeviceDriverPropertyDecoder.FailureState(error);
        Assert.Equal(expected, state);
        var field = new DeviceDriverIdentifiers(state, [], error);
        Assert.Equal(error, field.NativeError);
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(Row(field), Now));
        if (error == 122) Assert.NotEqual(DeviceDriverFieldState.Oversized, field.State);
    }
}
