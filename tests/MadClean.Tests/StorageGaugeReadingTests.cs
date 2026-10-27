using MadClean.Core;
using Xunit;

namespace MadClean.Tests;

public sealed class StorageGaugeReadingTests
{
    [Theory]
    [InlineData(CheckState.Unknown)]
    [InlineData(CheckState.Unavailable)]
    [InlineData(CheckState.Unsupported)]
    [InlineData(CheckState.Failed)]
    [InlineData(CheckState.Cancelled)]
    [InlineData((CheckState)99)]
    public void PlausibleOrRetainedDiskNumbersCannotOverrideAnUnverifiedSourceState(CheckState state)
    {
        var retainedDisk = new DiskInfo("Test drive", 1_000_000, 400_000);
        Assert.Null(StorageGaugeReading.From(state, retainedDisk));
    }

    [Fact]
    public void NoCheckStateCannotProduceAMeasurementFromPlausibleNumbers()
    {
        Assert.Null(StorageGaugeReading.From(null, new DiskInfo("Test drive", 1_000_000, 400_000)));
    }

    [Fact]
    public void ReadableDriveCanBeMeasuredWithinAnExplicitlyPartialInventory()
    {
        var disk = new DiskInfo("Readable test drive", 4096, 1234);
        var reading = Assert.IsType<StorageGaugeReading>(StorageGaugeReading.From(CheckState.Partial, disk));
        Assert.Equal(disk.TotalBytes, reading.TotalBytes);
        Assert.Equal(disk.FreeBytes, reading.FreeBytes);
        Assert.InRange(reading.FreeFraction, double.Epsilon, 1d);
    }

    [Theory]
    [InlineData(CheckState.Available)]
    [InlineData(CheckState.Partial)]
    public void SuccessfulOverallStateDoesNotInventAMissingDrive(CheckState state)
    {
        Assert.Null(StorageGaugeReading.From(state, null));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(0L, 1L)]
    [InlineData(-100L, 0L)]
    [InlineData(100L, -1L)]
    [InlineData(100L, 101L)]
    [InlineData(long.MinValue, long.MinValue)]
    public void ContradictoryOrMissingCapacityDataCannotBecomeAnEmptyOrFullGauge(long total, long free)
    {
        Assert.Null(StorageGaugeReading.From(CheckState.Available, new DiskInfo("Test drive", total, free)));
    }

    [Theory]
    [InlineData(0L, 0d)]
    [InlineData(1024L, 1d)]
    public void KnownZeroAndFullyFreeDrivesRemainRealMeasurements(long free, double expectedFraction)
    {
        var reading = Assert.IsType<StorageGaugeReading>(StorageGaugeReading.From(CheckState.Available,
            new DiskInfo("Test drive", 1024, free)));
        Assert.Equal(1024, reading.TotalBytes);
        Assert.Equal(free, reading.FreeBytes);
        Assert.Equal(expectedFraction, reading.FreeFraction);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue / 2)]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue)]
    public void LargeCapacitiesPreserveRawValuesAndAFiniteBoundedFraction(long free)
    {
        var reading = Assert.IsType<StorageGaugeReading>(StorageGaugeReading.From(CheckState.Available,
            new DiskInfo("Large test drive", long.MaxValue, free)));
        Assert.Equal(long.MaxValue, reading.TotalBytes);
        Assert.Equal(free, reading.FreeBytes);
        Assert.True(double.IsFinite(reading.FreeFraction));
        Assert.InRange(reading.FreeFraction, 0d, 1d);
        if (free > 0) Assert.True(reading.FreeFraction > 0d);
    }
}
