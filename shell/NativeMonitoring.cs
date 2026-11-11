#if NATIVE_UI_BETA
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCCTheaterCapture;

internal sealed partial class ViewerWindow
{
    private readonly CheckBox nativeMonitorEnabled = new() { Content = "Monitor MCC Theater films", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 20, 8) };
    private readonly CheckBox nativeMonitorAuto = new() { Content = "Automatically export clips", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 20, 8) };
    private readonly ComboBox nativeMonitorMinimum = new() { Width = 150, Margin = new Thickness(0, 8, 20, 8) };
    private readonly ComboBox nativeMonitorResolution = new() { Width = 130, Margin = new Thickness(0, 8, 20, 8), ToolTip = "Clip resolution" };
    private readonly ComboBox nativeMonitorQuality = new() { Width = 130, Margin = new Thickness(0, 8, 20, 8), ToolTip = "Clip quality" };
    private static readonly int[] NativeMonitorResolutions = { 720, 1080, 1440, 2160 };
    private static readonly string[] NativeMonitorQualities = { "standard", "high", "maximum" };
    private readonly TextBlock nativeMonitorStatus = Text("Monitoring is off.");
    private readonly DataGrid nativeMonitorFilms = NativeGrid();
    private bool updatingNativeMonitor;

    private void BuildNativeMonitoring()
    {
        foreach (var name in new[] { "Double Kill", "Triple Kill", "Overkill", "Killtacular", "Killtrocity", "Killimanjaro", "Killtastrophe", "Killpocalypse", "Killionaire" }) nativeMonitorMinimum.Items.Add(name);
        foreach (var name in new[] { "720p", "1080p", "1440p", "4K (upscaled)" }) nativeMonitorResolution.Items.Add(name);
        foreach (var name in new[] { "Standard (CRF 20)", "High (CRF 16)", "Maximum (CRF 12)" }) nativeMonitorQuality.Items.Add(name);
        async Task Configure()
        {
            if (updatingNativeMonitor) return;
            try { await MonitoringMessage(JsonSerializer.SerializeToElement(new { type = "monitor-settings", enabled = nativeMonitorEnabled.IsChecked == true,
                autoExport = nativeMonitorAuto.IsChecked == true, minimumKills = nativeMonitorMinimum.SelectedIndex + 2,
                outputResolution = NativeMonitorResolutions[Math.Max(0, nativeMonitorResolution.SelectedIndex)], outputQuality = NativeMonitorQualities[Math.Max(0, nativeMonitorQuality.SelectedIndex)] })); }
            catch (Exception e) { NativeError(e); }
        }
        nativeMonitorEnabled.Click += async (_, _) => await Configure();
        nativeMonitorAuto.Click += async (_, _) => await Configure();
        nativeMonitorMinimum.SelectionChanged += async (_, _) => await Configure();
        nativeMonitorResolution.SelectionChanged += async (_, _) => await Configure();
        nativeMonitorQuality.SelectionChanged += async (_, _) => await Configure();
        async Task Selected(string type)
        {
            if (nativeMonitorFilms.SelectedItem is MonitoredFilm film)
                await MonitoringMessage(JsonSerializer.SerializeToElement(new { type, hash = film.Hash }));
        }
        NativeColumn(nativeMonitorFilms, "Film", "Name", 2);
        NativeColumn(nativeMonitorFilms, "Status", "Message", 3);
        NativeColumn(nativeMonitorFilms, "Clips", "Clips.Count", .5);
        var top = new StackPanel();
        top.Children.Add(Text("AUTOMATIC HIGHLIGHTS · HALO 3", 18));
        top.Children.Add(NativeBar(nativeMonitorEnabled, nativeMonitorAuto, nativeMonitorMinimum, nativeMonitorResolution, nativeMonitorQuality));
        top.Children.Add(Text("Find multikills in completed Halo 3, Reach, Halo 4 and H2A autosaves without opening Theater. Keep this app open. Clips export in the background right away, even while MCC is open.", 12));
        top.Children.Add(Text("Chosen resolution and quality · 60 fps · game audio · 3 seconds before and after. Minimum applies to new films. Saved films are limited to 20 GB.", 12));
        top.Children.Add(nativeMonitorStatus);
        var actions = NativeBar(NativeAction("Export clips / retry", () => Selected("monitor-export")),
            NativeAction("Retry scan", () => Selected("monitor-retry-scan")), NativeAction("Dismiss", () => Selected("monitor-dismiss")),
            NativeAction("Open output", () => Selected("monitor-folder")),
            NativeAction("Pause export", async () => { await MonitoringMessage(JsonSerializer.SerializeToElement(new { type = "monitor-cancel" })); }),
            NativeAction("Saved films", async () => { await MonitoringMessage(JsonSerializer.SerializeToElement(new { type = "monitor-archive" })); }));
        nativeTabs.Items.Add(new TabItem { Header = "Monitoring", Content = NativePage(top, nativeMonitorFilms, actions) });
        RefreshNativeMonitoring();
    }

    private void RefreshNativeMonitoring()
    {
        updatingNativeMonitor = true;
        try
        {
            nativeMonitorEnabled.IsChecked = monitorSnapshot.Settings.Enabled;
            nativeMonitorAuto.IsChecked = monitorSnapshot.Settings.AutoExport;
            nativeMonitorAuto.IsEnabled = monitorSnapshot.Settings.Enabled;
            nativeMonitorMinimum.SelectedIndex = monitorSnapshot.Settings.MinimumKills - 2;
            nativeMonitorResolution.SelectedIndex = Array.IndexOf(NativeMonitorResolutions, monitorSnapshot.Settings.OutputResolution);
            nativeMonitorQuality.SelectedIndex = Array.IndexOf(NativeMonitorQualities, monitorSnapshot.Settings.OutputQuality);
            nativeMonitorStatus.Text = monitorMessage + "\n" + monitorExportMessage + "\nSave to: " + System.IO.Path.Combine(preferences.BatchOutputRoot, "Automatic clips");
            string? selected = (nativeMonitorFilms.SelectedItem as MonitoredFilm)?.Hash;
            var films = monitorSnapshot.Films.Where(f => f.State != "dismissed").OrderByDescending(f => f.FoundUtc).Take(100).ToArray();
            nativeMonitorFilms.ItemsSource = films; nativeMonitorFilms.SelectedItem = films.FirstOrDefault(f => f.Hash == selected);
        }
        finally { updatingNativeMonitor = false; }
    }
}
#endif
