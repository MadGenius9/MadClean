using System.Text;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class RegistrationMetadataTests
{
    private static RegistrationTextMetadata Text(string text, uint type = 1, int maximum = RegistrationMetadataDecoder.NameCharacters)
    {
        byte[] data = Encoding.Unicode.GetBytes(text);
        return RegistrationMetadataDecoder.DecodeText(type, (uint)data.Length, data, maximum);
    }

    [Fact]
    public void WMetadataIsLiteralUtf16WithoutExpansionTrimmingOrCommandParsing()
    {
        const string literal = "  %LOCALAPPDATA%\\Example /not-a-command  ";
        Assert.Equal(literal, Text(literal + "\0", 2).Value);
        Assert.Equal("𐐀 Example", Text("𐐀 Example\0").Value);
        Assert.Equal(RegistrationMetadataState.Valid, Text("\0").State);
        Assert.Equal("", Text("\0").Value);
    }

    [Theory]
    [InlineData(0u)] [InlineData(3u)] [InlineData(4u)] [InlineData(6u)] [InlineData(7u)] [InlineData(11u)]
    public void OtherTypesCannotBeReinterpretedAsText(uint type)
    {
        var result = Text("Example\0", type);
        Assert.Equal(RegistrationMetadataState.Invalid, result.State); Assert.Null(result.Value);
    }

    [Fact]
    public void DeclaredCountAndActualBytesMustAgreeWithoutReadingPaddingOrTruncation()
    {
        byte[] data = Encoding.Unicode.GetBytes("Name\0");
        foreach (uint count in new[] { 0u, 1u, 8u, 12u, uint.MaxValue })
            Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, count, data, 4).State);
        Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, 3, [65, 0, 0], 4).State);
        Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, 0, [], 4).State);
    }

    [Theory]
    [InlineData("No terminator")]
    [InlineData("Before\0After\0")]
    [InlineData("Name\0\0")]
    [InlineData("Name\n\0")]
    [InlineData("hidden\u202ename\0")]
    [InlineData("hidden\u200bname\0")]
    [InlineData("hidden\U000E0001name\0")]
    public void UnterminatedEmbeddedNulAndControlOrBidiFormattingRemainInvalid(string text)
    {
        Assert.Equal(RegistrationMetadataState.Invalid, Text(text).State);
    }

    [Fact]
    public void RawUnpairedSurrogatesAndTruncatedPairsAreRejectedRatherThanReplaced()
    {
        foreach (byte[] data in new[]
        {
            new byte[] { 0, 0xD8, 0, 0 }, new byte[] { 0, 0xDC, 0, 0 },
            new byte[] { 0, 0xD8, 65, 0, 0, 0 }, new byte[] { 0x3D, 0xD8, 0, 0xDE }
        }) Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, (uint)data.Length, data, 32).State);
    }

    [Fact]
    public void FieldCapsIncludeOnlyOneTerminatorAndNeverScaleToProviderDeclaredGigabytes()
    {
        foreach (int maximum in new[] { 256, 512, 1_024, 8_192 })
        {
            Assert.Equal(RegistrationMetadataState.Valid, Text(new string('a', maximum) + "\0", maximum: maximum).State);
            Assert.Equal(RegistrationMetadataState.Invalid, Text(new string('a', maximum + 1) + "\0", maximum: maximum).State);
        }
        foreach (int maximum in new[] { -1, 0, 8_193, int.MaxValue })
            Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, 2, [0, 0], maximum).State);
        Assert.Equal(RegistrationMetadataState.Invalid, RegistrationMetadataDecoder.DecodeText(1, uint.MaxValue, [0, 0], 8_192).State);
    }

    [Fact]
    public void FlagsAreExactLittleEndianDwordsAndUnknownDoesNotBecomeFalse()
    {
        Assert.Equal(false, RegistrationMetadataDecoder.DecodeFlag(4, 4, [0, 0, 0, 0]).Value);
        Assert.Equal(true, RegistrationMetadataDecoder.DecodeFlag(4, 4, [1, 0, 0, 0]).Value);
        foreach (var result in new[]
        {
            RegistrationMetadataDecoder.DecodeFlag(4, 4, [0, 0, 0, 1]),
            RegistrationMetadataDecoder.DecodeFlag(5, 4, [1, 0, 0, 0]),
            RegistrationMetadataDecoder.DecodeFlag(4, 4, [2, 0, 0, 0]),
            RegistrationMetadataDecoder.DecodeFlag(4, 3, [1, 0, 0]),
            RegistrationMetadataDecoder.DecodeFlag(4, 4, [1, 0, 0, 0, 0])
        }) { Assert.Equal(RegistrationMetadataState.Invalid, result.State); Assert.Null(result.Value); }
    }

    [Fact]
    public void MissingIsDistinctFromEmptyValidTextAndMalformedData()
    {
        Assert.Equal(RegistrationMetadataState.Missing, RegistrationMetadataDecoder.MissingText.State);
        Assert.Null(RegistrationMetadataDecoder.MissingText.Value);
        Assert.Equal(RegistrationMetadataState.Missing, RegistrationMetadataDecoder.MissingFlag.State);
        Assert.Null(RegistrationMetadataDecoder.MissingFlag.Value);
        Assert.Equal(RegistrationMetadataState.Valid, Text("\0").State);
        Assert.Equal(RegistrationMetadataState.Invalid, Text("unterminated").State);
    }

    [Fact]
    public void CancellationWinsEvenForMalformedInputsBeforeAnyDecode()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RegistrationMetadataDecoder.DecodeText(3, uint.MaxValue, [], int.MaxValue, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => RegistrationMetadataDecoder.DecodeFlag(4, 4, [1, 0, 0, 0], stop.Token));
    }
}
