using System.ComponentModel;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed record NavigationLink(string Title, int Page);

public sealed partial class MainViewModel
{
    private static readonly int[] PrimaryPages = [0, 1, 3, 2, 4];
    private CheckState? overviewStorageState;
    private bool overviewStorageChecking;
    private DiskRow? selectedOverviewDisk;
    private string storageAttemptTime = "Not checked";
    public IReadOnlyList<NavigationLink> ToolNavigationItems { get; } =
    [
        new("MadClean updates", 5), new("Installed apps", 6), new("Disk space map", 7),
        new("Leftover review", 8), new("Capability queue", 9), new("Activity log", 10), new("Hardware Upgrades", 11)
    ];
    public int PrimaryNavigationIndex
    {
        get => Array.IndexOf(PrimaryPages, SelectedPage);
        set { if (value >= 0 && value < PrimaryPages.Length) SelectedPage = PrimaryPages[value]; }
    }
    public NavigationLink? SelectedToolNavigation
    {
        get => ToolNavigationItems.FirstOrDefault(link => link.Page == SelectedPage);
        set { if (value != null && ToolNavigationItems.Contains(value)) SelectedPage = value.Page; }
    }
    public ICommand OpenCleanupPageCommand { get; private set; } = null!;
    public ICommand OpenStartupPageCommand { get; private set; } = null!;
    public ICommand OpenSecurityPageCommand { get; private set; } = null!;
    public ICommand OpenUpdatesPageCommand { get; private set; } = null!;
    public ICommand OpenActivityPageCommand { get; private set; } = null!;
    public DiskRow? SelectedOverviewDisk
    {
        get => selectedOverviewDisk;
        set { if (value != null && !Disks.Contains(value)) return; Set(ref selectedOverviewDisk, value); NotifyStorageOverview(); }
    }
    private StorageGaugeReading? OverviewReading => StorageGaugeReading.From(overviewStorageState,
        selectedOverviewDisk == null ? null : new DiskInfo(selectedOverviewDisk.Name, selectedOverviewDisk.TotalBytes, selectedOverviewDisk.FreeBytes));
    public double StorageFreeFraction => OverviewReading?.FreeFraction ?? double.NaN;
    public string StorageFreeText => OverviewReading is { } reading ? Display.Bytes(reading.FreeBytes) : overviewStorageChecking ? "Checking…" :
        overviewStorageState switch { null => "Not checked", CheckState.Cancelled => "Cancelled", CheckState.Failed => "Failed", CheckState.Unsupported => "Unsupported", _ => "Unknown" };
    public string StorageCapacityText => OverviewReading is { } reading ? "free of " + Display.Bytes(reading.TotalBytes) : "No confirmed capacity reading";
    public string StorageScopeText => overviewStorageChecking ? "Reading local fixed-drive capacity…" : selectedOverviewDisk is { } disk ? "Reported drive " + disk.Name : "Choose Inspect this PC to read disk capacity.";
    public string StorageCoverageText => overviewStorageChecking ? "Local inventory in progress; no completed reading is claimed." : overviewStorageState switch
    {
        null => "Not checked · no disk has been queried.",
        CheckState.Partial => "Partial inventory · this drive's returned values do not establish coverage of every drive.",
        CheckState.Available => "Reported capacity · free space is available to this user and may reflect quotas.",
        _ => overviewStorageState + " · no completed drive reading is claimed."
    };
    public string StorageAttemptTime { get => storageAttemptTime; private set => Set(ref storageAttemptTime, value); }
    public string StorageFactDetails => selectedOverviewDisk is { } disk
        ? $"{StorageScopeText}\nSource: System.IO.DriveInfo.TotalSize / AvailableFreeSpace; fixed disks only.\nReported total bytes: {disk.TotalBytes}; available bytes: {disk.FreeBytes}.\n{StorageCoverageText}\nQuery attempt: {StorageAttemptTime}. Display sizes use 1024-byte steps; blue means free, not used or safe to delete.\n{DiskMessage}"
        : $"Source: local Windows fixed-drive capacity inventory. No drive reading selected.\nQuery attempt: {StorageAttemptTime}.\n{DiskMessage}";
    private void InitializeOverview()
    {
        OpenCleanupPageCommand = new ActionCommand(() => SelectedPage = 1, () => true);
        OpenStartupPageCommand = new ActionCommand(() => SelectedPage = 2, () => true);
        OpenSecurityPageCommand = new ActionCommand(() => SelectedPage = 3, () => true);
        OpenUpdatesPageCommand = new ActionCommand(() => SelectedPage = 4, () => true);
        OpenActivityPageCommand = new ActionCommand(() => SelectedPage = 10, () => true);
        Disks.CollectionChanged += (_, _) =>
        {
            if (selectedOverviewDisk == null || !Disks.Contains(selectedOverviewDisk)) SelectedOverviewDisk = Disks.FirstOrDefault();
        };
    }
    private void NotifyShellNavigation()
    {
        PropertyChanged?.Invoke(this, new(nameof(PrimaryNavigationIndex)));
        PropertyChanged?.Invoke(this, new(nameof(SelectedToolNavigation)));
    }
    private void BeginStorageOverview()
    {
        overviewStorageState = CheckState.Unknown;
        overviewStorageChecking = true;
        StorageAttemptTime = DateTimeOffset.Now.ToString("g");
        NotifyStorageOverview();
    }
    private void CompleteStorageOverview(CheckState state)
    {
        overviewStorageState = state;
        overviewStorageChecking = false;
        NotifyStorageOverview();
    }
    private void NotifyStorageOverview()
    {
        foreach (var property in new[] { nameof(StorageFreeFraction), nameof(StorageFreeText), nameof(StorageCapacityText), nameof(StorageScopeText), nameof(StorageCoverageText), nameof(StorageFactDetails) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
