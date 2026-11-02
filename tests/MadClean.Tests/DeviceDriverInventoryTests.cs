using System.Buffers.Binary;
using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class DeviceDriverInventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static byte[] Encoded(string value) => Encoding.Unicode.GetBytes(value + "\0");
    private static DeviceDriverText Text(string value) => DeviceDriverPropertyDecoder.Text(0x12, Encoded(value));
    private static DeviceDriverDate Date(DateTime value)
    {
        byte[] bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value.ToFileTimeUtc());
        return DeviceDriverPropertyDecoder.Date(0x10, bytes);
    }
    private static DeviceDriverObservation Row() => new(1, Text("Owned fixture device"), Text("Reported device description"),
        Text("Example Vendor"), Text("031.0007-beta (OEM)"), Date(new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc)), Now);

    [Fact]
    public void ProviderVersionRemainsLiteralTextAndFutureReportedDateIsNotAnInstallationOrOrderingClaim()
    {
        var row = Row();
        Assert.Equal("031.0007-beta (OEM)", row.InstalledVersion.Value);
        Assert.Equal(new DateOnly(2030, 1, 2), row.DriverDate.Value);
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(row, Now));
        Assert.Null(row.InstalledVersion.NativeError); Assert.Null(row.DriverDate.NativeError);
        Assert.Equal("Vendor Name", Text("  Vendor Name  ").Value);
        Assert.Equal(DeviceDriverFieldState.Available, Text("0.0.0.0").State); // No invented placeholder/version policy.
    }

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x10u)]
    [InlineData(0x2012u)]
    [InlineData(0x1012u)]
    [InlineData(0xFFFF0012u)]
    public void TypeModifiersAndNonStringPropertiesCannotBeSilentlyCoerced(uint type)
    {
        var value = DeviceDriverPropertyDecoder.Text(type, Encoded("Example"));
        Assert.Equal(DeviceDriverFieldState.Invalid, value.State); Assert.Null(value.Value); Assert.Null(value.NativeError);
    }

    [Fact]
    public void StringRequiresOneCountedTerminalNullAndNeverRepairsMalformedUtf16()
    {
        var malformed = new[] { Array.Empty<byte>(), new byte[] { 0 }, Encoding.Unicode.GetBytes("Unterminated"), Encoded("Two\0"),
            Encoded("Embedded\0Value"), new byte[] { 0x00, 0xD8, 0, 0 }, new byte[] { 0x00, 0xDC, 0, 0 } };
        foreach (byte[] bytes in malformed)
        {
            var value = DeviceDriverPropertyDecoder.Text(0x12, bytes);
            Assert.Equal(DeviceDriverFieldState.Invalid, value.State); Assert.Null(value.Value); Assert.Null(value.NativeError);
        }
    }

    [Theory]
    [InlineData("Name\nLine")]
    [InlineData("\tName")]
    [InlineData("Name\u200B")]
    [InlineData("Name\u202E")]
    [InlineData("Name\u2028Line")]
    [InlineData("Name\u2029Line")]
    [InlineData("Name\U000E0001")]
    public void DisplayTrimmingDoesNotHideUnsafeUnicode(string value)
    {
        var decoded = Text(value);
        Assert.Equal(DeviceDriverFieldState.Invalid, decoded.State); Assert.Null(decoded.Value); Assert.Null(decoded.NativeError);
    }

    [Fact]
    public void CodeUnitCapAcceptsPairsWithinBoundAndNeverTruncatesAnOversizedFact()
    {
        Assert.Equal(DeviceDriverFieldState.Available, Text(new string('x', 256)).State);
        Assert.Equal(DeviceDriverFieldState.Oversized, Text(new string('x', 257)).State);
        string paired = string.Concat(Enumerable.Repeat("\U0001F4BB", 128));
        Assert.Equal(256, paired.Length); Assert.Equal(paired, Text(paired).Value);
        Assert.Equal(DeviceDriverFieldState.Oversized, Text(paired + "x").State);
        var huge = DeviceDriverPropertyDecoder.Text(0x12, new byte[4096]);
        Assert.Equal(DeviceDriverFieldState.Oversized, huge.State); Assert.Null(huge.Value); Assert.Null(huge.NativeError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Unknown ")]
    [InlineData("n/A")]
    [InlineData("Not available")]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("-")]
    public void MissingDisplayFactsRemainUnknownWithoutInventedNativeError(string value)
    {
        var decoded = Text(value);
        Assert.Equal(DeviceDriverFieldState.Unknown, decoded.State); Assert.Null(decoded.Value); Assert.Null(decoded.NativeError);
    }

    [Fact]
    public void DateRequiresExactFileTimeTypeWidthAndARealNonzeroRepresentableValue()
    {
        Assert.Equal(DeviceDriverFieldState.Unknown, DeviceDriverPropertyDecoder.Date(0x10, new byte[8]).State);
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[7], new byte[9], Enumerable.Repeat((byte)0xFF, 8).ToArray() })
        {
            var result = DeviceDriverPropertyDecoder.Date(0x10, bytes);
            Assert.Equal(DeviceDriverFieldState.Invalid, result.State); Assert.Null(result.Value); Assert.Null(result.NativeError);
        }
        byte[] one = new byte[8]; one[0] = 1;
        Assert.Equal(new DateOnly(1601, 1, 1), DeviceDriverPropertyDecoder.Date(0x10, one).Value);
        foreach (uint type in new[] { 0x12u, 0x1010u, 0x2010u })
            Assert.Equal(DeviceDriverFieldState.Invalid, DeviceDriverPropertyDecoder.Date(type, one).State);
        byte[] outOfRange = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(outOfRange, long.MaxValue);
        Assert.Equal(DeviceDriverFieldState.Invalid, DeviceDriverPropertyDecoder.Date(0x10, outOfRange).State);
        Assert.Equal(DateOnly.MaxValue, Date(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)).Value);
    }

    [Fact]
    public void RowConsistencyPreservesUnknownAndFailureCodesButRejectsFactsOnFailedFields()
    {
        foreach (var state in Enum.GetValues<DeviceDriverFieldState>().Where(state => state != DeviceDriverFieldState.Available))
        {
            var row = Row() with { Name = new(state, null, 0), DriverDate = new(state, null, 5) };
            Assert.True(DeviceDriverPropertyDecoder.IsConsistent(row, Now)); // Actual failed BOOL can report numeric zero.
            Assert.False(DeviceDriverPropertyDecoder.IsConsistent(row with { Name = row.Name with { Value = "Invented" } }, Now));
            Assert.False(DeviceDriverPropertyDecoder.IsConsistent(row with { DriverDate = row.DriverDate with { Value = new DateOnly(2020, 1, 1) } }, Now));
        }
        var valid = Row();
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { Name = valid.Name with { NativeError = 0 } }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { DriverDate = valid.DriverDate with { NativeError = 5 } }, Now));
        int setupApiError = unchecked((int)0xE000020B);
        var failedSetupApi = valid with { Name = new(DeviceDriverFieldState.Failed, null, setupApiError), DriverDate = new(DeviceDriverFieldState.Failed, null, setupApiError) };
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(failedSetupApi, Now));
        Assert.Equal("E000020B", failedSetupApi.Name.NativeError!.Value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { Name = valid.Name with { NativeError = setupApiError } }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { Name = new((DeviceDriverFieldState)99, null, null) }, Now));
    }

    [Fact]
    public void ObservationNumbersAndCheckTimeAreBoundedMetadataNotStableIdentities()
    {
        var valid = Row();
        Assert.True(DeviceDriverPropertyDecoder.IsConsistent(valid with { ObservationNumber = 512 }, Now));
        foreach (int number in new[] { 0, -1, 513 }) Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { ObservationNumber = number }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { CheckedUtc = default }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { CheckedUtc = Now.AddTicks(1) }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { CheckedUtc = Now.ToOffset(TimeSpan.FromHours(1)) }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid, default(DateTimeOffset)));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid, Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(null, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { Name = null! }, Now));
        Assert.False(DeviceDriverPropertyDecoder.IsConsistent(valid with { DriverDate = new(DeviceDriverFieldState.Available, DateOnly.MinValue, null) }, Now));
    }
}
