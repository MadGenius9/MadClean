using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class SpaceMapLayoutTests
{
    [Fact]
    public void RectangleAreaPreservesMeasuredByteSharesAndOriginalItemIdentity()
    {
        long[] weights = [30, 0, 20, 50];
        var rectangles = SpaceMapLayout.Create(weights, 800, 300);
        Assert.Equal(new[] { 0, 2, 3 }, rectangles.Select(r => r.Index));
        foreach (var rectangle in rectangles)
            Assert.Equal(weights[rectangle.Index] / 100d, rectangle.Width * rectangle.Height / 240000d, 10);
        Assert.Equal(240000d, rectangles.Sum(r => r.Width * r.Height), 8);
        foreach (var a in rectangles)
            foreach (var b in rectangles.Where(b => b.Index > a.Index))
                Assert.True(a.X + a.Width <= b.X + 1e-8 || b.X + b.Width <= a.X + 1e-8 ||
                    a.Y + a.Height <= b.Y + 1e-8 || b.Y + b.Height <= a.Y + 1e-8);
    }
    [Fact]
    public void EmptyZeroAndUnmeasuredViewsDoNotInventVisibleBytes()
    {
        Assert.Empty(SpaceMapLayout.Create([], 800, 300));
        Assert.Empty(SpaceMapLayout.Create([0, 0], 800, 300));
        Assert.Empty(SpaceMapLayout.Create([10], 0, 300));
    }
    [Fact]
    public void VeryLargeLogicalWeightsDoNotOverflowIntegerTotalsOrGeometry()
    {
        var rectangles = SpaceMapLayout.Create([long.MaxValue, long.MaxValue], 200, 100);
        Assert.Equal(2, rectangles.Count);
        Assert.All(rectangles, r => Assert.Equal(10000d, r.Width * r.Height, 8));
    }
    [Fact]
    public void InvalidWeightsAndViewportCannotBecomePlausibleGeometry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceMapLayout.Create([-1], 20, 20));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpaceMapLayout.Create([1], double.NaN, 20));
    }
}
