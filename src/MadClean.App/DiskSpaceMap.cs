using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MadClean.Core;

namespace MadClean.App;

public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long bytes ? Display.Bytes(bytes) : "Unknown";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Original WPF one-level treemap. A linked, keyboard-accessible list retains every child and exact value.
public sealed class DiskSpaceMap : Canvas
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(nameof(Node), typeof(DiskUsageNode), typeof(DiskSpaceMap), new FrameworkPropertyMetadata(null, Redraw));
    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode), typeof(DiskUsageNode), typeof(DiskSpaceMap), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, SelectionChanged));
    public static readonly DependencyProperty NavigateCommandProperty = DependencyProperty.Register(nameof(NavigateCommand), typeof(ICommand), typeof(DiskSpaceMap));
    public DiskUsageNode? Node { get => (DiskUsageNode?)GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    public DiskUsageNode? SelectedNode { get => (DiskUsageNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public ICommand? NavigateCommand { get => (ICommand?)GetValue(NavigateCommandProperty); set => SetValue(NavigateCommandProperty, value); }
    private static readonly Brush[] Palette = [new SolidColorBrush(Color.FromRgb(38, 103, 107)), new SolidColorBrush(Color.FromRgb(56, 82, 130)), new SolidColorBrush(Color.FromRgb(76, 101, 145)), new SolidColorBrush(Color.FromRgb(64, 113, 119)), new SolidColorBrush(Color.FromRgb(76, 88, 132))];
    public DiskSpaceMap()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(16, 20, 29));
        SizeChanged += (_, _) => Draw();
    }
    private static void Redraw(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((DiskSpaceMap)sender).Draw();
    private static void SelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var map = (DiskSpaceMap)sender;
        foreach (var button in map.Children.OfType<Button>())
        {
            bool selected = button.Tag is DiskUsageNode item && ReferenceEquals(item, map.SelectedNode);
            button.BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(40, 49, 65));
            button.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }
    private void Draw()
    {
        Children.Clear();
        if (Node == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var entries = Node.Children.Where(n => n.Bytes > 0).ToArray();
        if (entries.Length == 0)
        {
            Children.Add(new TextBlock { Text = "No measured non-zero child sizes. Review the status and issues; missing sizes are unknown.", Margin = new Thickness(16), Width = Math.Max(0, ActualWidth - 32) });
            return;
        }
        // Bound rendering, not source data: the remainder keeps its actual summed area in an explicit Other bucket.
        var nodes = entries.Take(119).Cast<DiskUsageNode?>().ToList();
        var weights = nodes.Select(n => n!.Bytes).ToList();
        long other = entries.Skip(119).Sum(n => n.Bytes);
        if (other > 0) { nodes.Add(null); weights.Add(other); }
        foreach (var rectangle in SpaceMapLayout.Create(weights, ActualWidth, ActualHeight))
        {
            var node = nodes[rectangle.Index];
            string label = node?.Name ?? $"Other entries ({entries.Length - 119})";
            long bytes = node?.Bytes ?? other;
            var tile = new Button
            {
                Tag = node,
                Content = new TextBlock { Text = label + "\n" + Display.Bytes(bytes), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, FontSize = 12 },
                Style = TryFindResource("SpaceMapTile") as Style,
                Width = Math.Max(0, rectangle.Width - 2),
                Height = Math.Max(0, rectangle.Height - 2),
                Background = node == null ? new SolidColorBrush(Color.FromRgb(68, 74, 87)) : Palette[rectangle.Index % Palette.Length],
                BorderBrush = node == SelectedNode ? Brushes.White : new SolidColorBrush(Color.FromRgb(40, 49, 65)),
                BorderThickness = new Thickness(node == SelectedNode ? 3 : 1),
                ToolTip = (node?.Path ?? "Aggregate of smaller measured entries; inspect individual entries in the list.") + "\n" + Display.Bytes(bytes) + " known logical bytes"
            };
            AutomationProperties.SetName(tile, label + ", " + Display.Bytes(bytes) + " known logical bytes");
            if (node != null)
            {
                tile.Click += (_, _) => SetCurrentValue(SelectedNodeProperty, node);
                tile.MouseDoubleClick += (_, _) =>
                {
                    SetCurrentValue(SelectedNodeProperty, node);
                    if (node.IsDirectory && NavigateCommand?.CanExecute(null) == true) NavigateCommand.Execute(null);
                };
            }
            else tile.IsEnabled = false;
            SetLeft(tile, rectangle.X + 1); SetTop(tile, rectangle.Y + 1); Children.Add(tile);
        }
    }
}
