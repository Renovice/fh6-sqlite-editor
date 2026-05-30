using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FH6SQLiteEditorNative;

public partial class LiveTuningPatcherWindow : Window
{
    private readonly ObservableCollection<LiveTuningLimitEdit> _rows = LiveTuningLimitsPatcher.CreateDefaultRows();

    public LiveTuningPatcherWindow(bool darkMode)
    {
        InitializeComponent();
        LimitsGrid.ItemsSource = _rows;
        ApplyTheme(darkMode);
        StatusBox.Text = "Press Scan Current to read the running game's live tuning clamp values.";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync("Scanning live tuning clamps...", token =>
        {
            var report = LiveTuningLimitsPatcher.ScanIntoRows(_rows, token);
            return report + Environment.NewLine + Environment.NewLine + LiveTuningLimitsPatcher.FormatRows(_rows);
        });
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        LiveTuningLimitsPatcher.RestoreDefaults(_rows);
        StatusBox.Text = "Default target values restored. Press Apply to Game to write them into the running game.";
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        LimitsGrid.CommitEdit();
        LimitsGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        await RunAsync("Applying live tuning clamps...", token =>
        {
            var report = LiveTuningLimitsPatcher.Apply(_rows, token);
            return report + Environment.NewLine + Environment.NewLine + LiveTuningLimitsPatcher.FormatRows(_rows);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(string status, Func<CancellationToken, string> action)
    {
        SetBusy(true);
        StatusBox.Text = status;
        try
        {
            using var cts = new CancellationTokenSource();
            var result = await Task.Run(() => action(cts.Token));
            StatusBox.Text = result;
        }
        catch (Exception ex)
        {
            StatusBox.Text = "Failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        RestoreDefaultsButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy;
    }

    private void ApplyTheme(bool darkMode)
    {
        var bg = Brush(darkMode ? "#101719" : "#eef2f4");
        var surface = Brush(darkMode ? "#172124" : "#ffffff");
        var soft = Brush(darkMode ? "#1d2a2f" : "#f6f8f9");
        var line = Brush(darkMode ? "#2d4047" : "#d6e0e4");
        var text = Brush(darkMode ? "#e5eff2" : "#142025");
        var muted = Brush(darkMode ? "#98a9ae" : "#64747b");
        var accent = Brush(darkMode ? "#22a5ba" : "#086f83");
        var selection = Brush(darkMode ? "#264852" : "#c8eaf0");

        Resources["DialogBgBrush"] = bg;
        Resources["DialogSurfaceBrush"] = surface;
        Resources["DialogSoftBrush"] = soft;
        Resources["DialogLineBrush"] = line;
        Resources["DialogTextBrush"] = text;
        Resources["DialogMutedBrush"] = muted;
        Resources["DialogAccentBrush"] = accent;
        Resources["DialogSelectionBrush"] = selection;

        Background = bg;
        RootGrid.Background = bg;
        Foreground = text;
        TitleText.Foreground = text;
        IntroText.Foreground = muted;
        StatusBox.Background = darkMode ? Brush("#0b1214") : surface;
        StatusBox.Foreground = darkMode ? Brush("#bfeee4") : text;
        StatusBox.BorderBrush = line;

        LimitsGrid.Background = surface;
        LimitsGrid.RowBackground = surface;
        LimitsGrid.AlternatingRowBackground = soft;
        LimitsGrid.Foreground = text;
        LimitsGrid.BorderBrush = line;
        LimitsGrid.HorizontalGridLinesBrush = line;
        LimitsGrid.VerticalGridLinesBrush = line;

        ApplySystemBrushOverrides(LimitsGrid.Resources, surface, text, accent, selection);
        ApplySystemBrushOverrides(StatusBox.Resources, surface, text, accent, selection);

        foreach (var button in new[] { ScanButton, RestoreDefaultsButton, ApplyButton })
        {
            button.Background = surface;
            button.Foreground = text;
            button.BorderBrush = line;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private static void ApplySystemBrushOverrides(ResourceDictionary resources, Brush surface, Brush text, Brush accent, Brush selection)
    {
        resources[SystemColors.ControlBrushKey] = surface;
        resources[SystemColors.ControlTextBrushKey] = text;
        resources[SystemColors.WindowBrushKey] = surface;
        resources[SystemColors.WindowTextBrushKey] = text;
        resources[SystemColors.HighlightBrushKey] = accent;
        resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        resources[SystemColors.InactiveSelectionHighlightBrushKey] = selection;
        resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = text;
    }
}
