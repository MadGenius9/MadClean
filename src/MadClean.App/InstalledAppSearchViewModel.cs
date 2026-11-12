using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using MadClean.Core;

namespace MadClean.App;

public sealed partial class MainViewModel
{
    private string installedAppSearchText = "";
    private InstalledApplicationSearch installedAppSearch = InstalledApplicationSearch.Create("");
    private bool installedAppRefreshPending;
    public ICollectionView InstalledApplicationsView { get; private set; } = null!;
    public ICommand ClearInstalledAppSearchCommand { get; private set; } = null!;
    public string InstalledAppSearchText
    {
        get => installedAppSearchText;
        set
        {
            if (MsiUninstallDispatchReserved) return;
            string text = value ?? "";
            if (installedAppSearchText == text) return;
            InvalidateMsiUninstallRequest();
            if (MsiUninstallDispatchReserved) return;
            installedAppSearchText = text;
            installedAppSearch = InstalledApplicationSearch.Create(text);
            // A hidden selection must not retain a review plan or receive pending MSI results.
            if (selectedApplication != null && !installedAppSearch.Matches(selectedApplication))
                SelectedInstalledApplication = null;
            InstalledApplicationsView.Refresh();
            RefreshPackagedApplicationSearch();
            // Publish the text only after the new literal filter and selection invalidation
            // are in force, so a binding callback cannot reuse the previous visibility rule.
            PropertyChanged?.Invoke(this, new(nameof(InstalledAppSearchText)));
            NotifyInstalledAppSearchSummary();
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string InstalledAppSearchSummary => !installedAppSearch.IsValid
        ? "Search accepts up to 256 characters of literal text without control or hidden formatting characters. Clear it to show loaded registrations; no new inventory was started."
        : installedAppRefreshPending
            ? "Registration refresh in progress; the filter applies as rows arrive. Counts will be shown after publication completes."
            : $"{InstalledApplicationsView.Cast<InstalledApplication>().Count():N0} of {InstalledApplications.Count:N0} loaded registrations shown. Filtering is local and does not change inventory coverage or captured baselines. No matching rows is not proof an app is absent.";
    private void InitializeInstalledAppSearch()
    {
        InstalledApplicationsView = CollectionViewSource.GetDefaultView(InstalledApplications);
        InstalledApplicationsView.Filter = item => item is InstalledApplication app && installedAppSearch.Matches(app);
        ClearInstalledAppSearchCommand = new ActionCommand(() => InstalledAppSearchText = "", () => InstalledAppSearchText.Length != 0);
    }
    private void SetInstalledAppSearchRefreshPending(bool pending)
    {
        installedAppRefreshPending = pending;
        NotifyInstalledAppSearchSummary();
    }
    private void NotifyInstalledAppSearchSummary() => PropertyChanged?.Invoke(this, new(nameof(InstalledAppSearchSummary)));
}
