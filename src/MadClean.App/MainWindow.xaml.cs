using System.Windows;
namespace MadClean.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        Closed += (_, _) => model.Cancel();
    }
}
