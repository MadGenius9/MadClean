using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class ApplicationSelectionPolicyTests
{
    private static InstalledApplication Desktop(string id = "Registration", string name = "Example [tool]", string publisher = "Contoso") =>
        new(id, name, "Unknown", publisher, "Unknown", null, "Removal restricted by registration; action unavailable");
    private static PackagedApplication Package(string id = "Example_1_x64__publisher", string name = "Example [tool]", string publisher = "Contoso") =>
        new(name, id, "Unknown", "Unknown", publisher, "Unknown", "Unknown", true, true, true, "Unknown");
    private static CheckResult<T> Snapshot<T>(params T[] rows) => new(CheckState.Available, "Coverage unknown", rows);
    private static InstalledApplicationSearch Search(string? query = null) => InstalledApplicationSearch.Create(query);

    [Fact]
    public void VisiblePartialRowsRemainReviewableWithoutActionEvidenceAndOrderIsIrrelevant()
    {
        var desktop = Desktop();
        var other = Desktop("Other");
        var package = Package();
        var another = Package("Other_1_x64__publisher");
        Assert.True(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, other) with { State = CheckState.Partial },
            [other, desktop], desktop, Search("[tool]")));
        Assert.True(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, another) with { State = CheckState.Partial },
            [another, package], package, Search("CONTOSO")));
    }

    [Fact]
    public void EqualClonesAndPreviousSnapshotReferencesCannotSelectCurrentRows()
    {
        var oldDesktop = Desktop();
        var currentDesktop = oldDesktop with { };
        var oldPackage = Package();
        var currentPackage = oldPackage with { };
        Assert.Equal(oldDesktop, currentDesktop);
        Assert.Equal(oldPackage, currentPackage);
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(currentDesktop), [currentDesktop], oldDesktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(currentPackage), [currentPackage], oldPackage, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(currentDesktop), [oldDesktop], currentDesktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(currentPackage), [oldPackage], currentPackage, Search()));
    }

    [Theory]
    [InlineData(CheckState.Unknown)] [InlineData(CheckState.Failed)] [InlineData(CheckState.Cancelled)]
    [InlineData(CheckState.Unavailable)] [InlineData(CheckState.Unsupported)] [InlineData((CheckState)999)]
    public void NonpositiveSnapshotsCannotPromoteAttachedRows(CheckState state)
    {
        var desktop = Desktop(); var package = Package();
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop) with { State = state }, [desktop], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package) with { State = state }, [package], package, Search()));
    }

    [Theory]
    [InlineData("missing")] [InlineData("Example.*")] [InlineData("\n")] [InlineData("\u202e")]
    public void HiddenRowsAndInvalidFiltersAreNotSelectableButClearingRestoresVisibility(string query)
    {
        var desktop = Desktop(); var package = Package();
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), [desktop], desktop, Search(query)));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), [package], package, Search(query)));
        Assert.True(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), [desktop], desktop, Search()));
        Assert.True(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), [package], package, Search(" ")));
    }

    [Fact]
    public void DuplicateOrCaseAmbiguousIdentitiesAreRejectedInEitherContainer()
    {
        var desktop = Desktop(); var package = Package();
        var otherDesktop = Desktop("Other"); var otherPackage = Package("Other");
        foreach (var duplicate in new[] { desktop, desktop with { Identity = "REGISTRATION" } })
        {
            Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, duplicate), [desktop, otherDesktop], desktop, Search()));
            Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, otherDesktop), [desktop, duplicate], desktop, Search()));
        }
        foreach (var duplicate in new[] { package, package with { FullName = "EXAMPLE_1_X64__PUBLISHER" } })
        {
            Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, duplicate), [package, otherPackage], package, Search()));
            Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, otherPackage), [package, duplicate], package, Search()));
        }
    }

    [Fact]
    public void MissingInputsOmittedSelectionAndIncompletePublicationAreRejected()
    {
        var desktop = Desktop(); var package = Package();
        var otherDesktop = Desktop("Other"); var otherPackage = Package("Other");
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(null, [desktop], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(null, [package], package, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), null, desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), null, package, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), [desktop], null, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), [package], null, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), [desktop], desktop, null));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), [package], package, null));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, otherDesktop), [desktop], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, otherPackage), [package], package, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), [otherDesktop], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package), [otherPackage], package, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(new(CheckState.Partial, "", null!), [desktop], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(new(CheckState.Partial, "", null!), [package], package, Search()));
    }

    [Fact]
    public void EveryRowNeedsABoundedSafeIdentityEvenWhenItIsNotSelected()
    {
        var desktop = Desktop(); var package = Package();
        foreach (var invalid in new[] { "", " ", "hidden\u202e", "line\n", "\u2028", new string('\ud800', 1), new string('x', 1025) })
        {
            var badDesktop = Desktop(invalid); var badPackage = Package(invalid);
            Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, badDesktop), [desktop, badDesktop], desktop, Search()));
            Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, badPackage), [package, badPackage], package, Search()));
            Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, Desktop("Other")), [desktop, badDesktop], desktop, Search()));
            Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, Package("Other")), [package, badPackage], package, Search()));
        }
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, null!), [desktop, Desktop("Other")], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, null!), [package, Package("Other")], package, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop, Desktop("Other")), [desktop, null!], desktop, Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(package, Package("Other")), [package, null!], package, Search()));
        var unicodeDesktop = Desktop(new string('x', 1022) + "😀");
        var unicodePackage = Package(new string('x', 1022) + "😀");
        Assert.True(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(unicodeDesktop), [unicodeDesktop], unicodeDesktop, Search()));
        Assert.True(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(unicodePackage), [unicodePackage], unicodePackage, Search()));
    }

    [Fact]
    public void InventoryBoundsApplyBeforeSelectionAndAtTheActualSupportedLimits()
    {
        var desktop = Enumerable.Range(0, 20_000).Select(index => Desktop("Desktop" + index)).ToArray();
        var packages = Enumerable.Range(0, PackagedApplicationParser.MaximumRecords).Select(index => Package("Package" + index)).ToArray();
        Assert.True(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(desktop), desktop, desktop[0], Search()));
        Assert.True(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(packages), packages, packages[0], Search()));
        var tooManyDesktop = desktop.Append(Desktop("Overflow")).ToArray();
        var tooManyPackages = packages.Append(Package("Overflow")).ToArray();
        Assert.False(ApplicationSelectionPolicy.IsCurrentDesktop(Snapshot(tooManyDesktop), tooManyDesktop, desktop[0], Search()));
        Assert.False(ApplicationSelectionPolicy.IsCurrentPackage(Snapshot(tooManyPackages), tooManyPackages, packages[0], Search()));
    }
}
