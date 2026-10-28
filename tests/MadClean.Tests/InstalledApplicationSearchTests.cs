using System.Globalization;
using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class InstalledApplicationSearchTests
{
    private static InstalledApplication App(string? name = "Example application", string? publisher = "Example publisher") =>
        new("Identity-only marker", name!, "Version-only marker", publisher!, "Source-only marker", @"C:\Path-only marker", "Support-only marker");

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")] [InlineData("\u00a0\u3000")]
    public void ValidEmptyQueryIncludesEveryNonNullRegistrationWithoutInspectingFields(string? text)
    {
        var search = InstalledApplicationSearch.Create(text);
        Assert.True(search.IsValid); Assert.True(search.IsEmpty);
        Assert.True(search.Matches(App(null, null))); Assert.False(search.Matches(null));
    }

    [Fact]
    public void LiteralSubstringMatchesNameOrPublisherWithoutChangingTheRegistration()
    {
        var app = App("Example application", "Contoso Labs");
        Assert.True(InstalledApplicationSearch.Create(" APPLICATION ").Matches(app));
        Assert.True(InstalledApplicationSearch.Create("toso la").Matches(app));
        Assert.False(InstalledApplicationSearch.Create("application contoso").Matches(app));
        Assert.False(InstalledApplicationSearch.Create("missing").Matches(app));
        Assert.Equal("Example application", app.Name);
    }

    [Theory]
    [InlineData("Identity-only")] [InlineData("Version-only")] [InlineData("Source-only")]
    [InlineData("Path-only")] [InlineData("Support-only")]
    public void MetadataOutsideNameAndPublisherCannotAffectFiltering(string query) =>
        Assert.False(InstalledApplicationSearch.Create(query).Matches(App()));

    [Fact]
    public void MetacharactersRemainLiteralAndCannotBecomePatterns()
    {
        var app = App("Example [v1].*? (x)+ $test^ \\name", "Publisher");
        foreach (string literal in new[] { "[v1]", ".*?", "(x)+", "$test^", "\\name" })
            Assert.True(InstalledApplicationSearch.Create(literal).Matches(app));
        Assert.False(InstalledApplicationSearch.Create("Example.*").Matches(App()));
        Assert.False(InstalledApplicationSearch.Create("*").Matches(App()));
        Assert.False(InstalledApplicationSearch.Create("^Example").Matches(App()));
    }

    [Theory]
    [InlineData("\n")] [InlineData("\t")] [InlineData(" \r ")]
    [InlineData("hidden\u202ename")] [InlineData("\u200b")] [InlineData("\u2028")] [InlineData("\u2029")]
    [InlineData("\U000E0001")]
    public void InvalidUnicodeControlsAndSeparatorsNeverBecomeAnEmptyMatchAll(string input)
    {
        var search = InstalledApplicationSearch.Create(input);
        Assert.False(search.IsValid); Assert.False(search.IsEmpty);
        Assert.False(search.Matches(App())); Assert.False(search.Matches(App(null, null))); Assert.False(search.Matches(null));
    }

    [Fact]
    public void UnpairedUtf16SurrogatesCannotBeReplacedIntoValidSearchText()
    {
        foreach (string input in new[] { new string((char)0xD800, 1), new string((char)0xDC00, 1), "a" + (char)0xD800 + "b" })
        {
            var search = InstalledApplicationSearch.Create(input);
            Assert.False(search.IsValid); Assert.False(search.IsEmpty); Assert.False(search.Matches(App()));
        }
    }

    [Fact]
    public void BoundUsesOriginalUtf16UnitsBeforeTrimmingRatherThanUnicodeScalarCount()
    {
        var exact = InstalledApplicationSearch.Create(new string('a', 256));
        Assert.True(exact.IsValid); Assert.True(exact.Matches(App(new string('a', 256))));
        var tooLong = InstalledApplicationSearch.Create(new string(' ', 257));
        Assert.False(tooLong.IsValid); Assert.False(tooLong.IsEmpty); Assert.False(tooLong.Matches(App()));
        string scalar = char.ConvertFromUtf32(0x1F600);
        string boundary = string.Concat(Enumerable.Repeat(scalar, 128));
        Assert.True(InstalledApplicationSearch.Create(boundary).IsValid);
        Assert.False(InstalledApplicationSearch.Create(boundary + scalar).IsValid);
    }

    [Fact]
    public void MatchingIsOrdinalIgnoreCaseAcrossUserCulturesWithoutFuzzyNormalization()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(InstalledApplicationSearch.Create("file").Matches(App("FILE Utility")));
            Assert.False(InstalledApplicationSearch.Create("Café").Matches(App("Cafe\u0301")));
            Assert.True(InstalledApplicationSearch.Create("Cafe\u0301").Matches(App("Cafe\u0301")));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void NullFieldsAreNotMatchedByNonemptyTextWhileTheOtherFieldStillWorks()
    {
        Assert.False(InstalledApplicationSearch.Create("Unknown").Matches(App(null, null)));
        Assert.True(InstalledApplicationSearch.Create("publisher").Matches(App(null, "Publisher")));
        Assert.True(InstalledApplicationSearch.Create("application").Matches(App("Application", null)));
    }
}
