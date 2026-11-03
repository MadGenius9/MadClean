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
        Closed += (_, _) => model.Shutdown();
    }
}
