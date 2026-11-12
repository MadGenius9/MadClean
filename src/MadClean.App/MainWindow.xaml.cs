using System.Windows;
using System.Windows.Interop;
using MadClean.Windows;
namespace MadClean.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        SourceInitialized += (_, _) => WindowAppearance.TryUseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Closing += (_, args) =>
        {
            if (model.HasPendingRepairReview)
            {
                args.Cancel = true;
                model.ExplainRepairReviewCloseBlock();
                return;
            }
            if (model.HasPendingWindowsUpdateHistoryRead)
            {
                args.Cancel = true;
                model.ExplainWindowsUpdateHistoryCloseBlock();
                return;
            }
            if (model.HasPendingDefenderLifecycleRead)
            {
                args.Cancel = true;
                model.ExplainDefenderLifecycleCloseBlock();
                return;
            }
            if (model.HasPendingSystemToolIdentity)
            {
                args.Cancel = true;
                model.ExplainSystemToolIdentityCloseBlock();
                return;
            }
            if (model.HasPendingHardwareQuery)
            {
                args.Cancel = true;
                model.ExplainHardwareQueryCloseBlock();
                return;
            }
            if (model.HasPendingAppUpdateDiscovery)
            {
                args.Cancel = true;
                model.ExplainAppUpdateCloseBlock();
                return;
            }
            if (model.HasPendingDiskIdentityRead)
            {
                args.Cancel = true;
                model.ExplainDiskIdentityCloseBlock();
                return;
            }
            if (model.HasPendingMsiRegistrationWatchWork)
            {
                args.Cancel = true;
                model.ExplainMsiWatchCloseBlock();
                return;
            }
            if (model.HasPendingSfcVerification)
            {
                args.Cancel = true;
                model.ExplainSfcVerificationCloseBlock();
                return;
            }
            if (model.HasPendingComponentStoreStatus)
            {
                args.Cancel = true;
                model.ExplainComponentStoreCloseBlock();
                return;
            }
            if (!model.HasPendingMsiUninstallRequest) return;
            args.Cancel = true;
            model.ExplainMsiUninstallCloseBlock();
        };
        Closed += (_, _) =>
        {
            model.ShutdownSfcVerification();
            model.Shutdown();
        };
    }
}
