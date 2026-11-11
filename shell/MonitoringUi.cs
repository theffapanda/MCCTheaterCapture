using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace MCCTheaterCapture;

internal sealed partial class ViewerWindow
{
    private FilmMonitoring? filmMonitor;
    private MonitorData monitorSnapshot = new();
    private readonly SemaphoreSlim monitorGate = new(1, 1);
    private readonly CancellationTokenSource monitorCancellation = new();
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private HighlightRecording? monitorRecording;
    private string monitorActiveHash = "", monitorMessage = "Monitoring is off.", monitorExportMessage = "";
    private bool monitorTickActive;
    private bool MonitorExportRunning => monitorRecording != null;
    private string MonitorRoot => Path.Combine(dataRoot, "Monitoring");

    private void InitializeMonitoring()
    {
        try
        {
            filmMonitor ??= new FilmMonitoring(MonitorRoot,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "MCC", "Temporary"));
            RepairMonitorClipFiles();
            monitorSnapshot = filmMonitor.Snapshot();
            monitorMessage = monitorSnapshot.Settings.Enabled ? "Checking Theater autosaves…" : "Monitoring is off.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { monitorMessage = "Monitoring unavailable: " + e.Message; }
    }

    private void StartMonitoring()
    {
        monitorTimer.Tick += async (_, _) => await MonitorTick();
        monitorTimer.Start();
        _ = MonitorTick();
    }

    private object MonitoringState() => new
    {
        settings = monitorSnapshot.Settings, message = monitorMessage, running = MonitorExportRunning,
        exportMessage = monitorExportMessage, activeHash = monitorActiveHash,
        outputDirectory = Path.Combine(preferences.BatchOutputRoot, "Automatic clips"),
        films = monitorSnapshot.Films.Where(f => f.State != "dismissed").OrderByDescending(f => f.FoundUtc).Select(f => new
        {
            f.Hash, f.Name, f.Title, titleName = FilmTitle.All.FirstOrDefault(t => t.Id == f.Title)?.DisplayName ?? "Halo 3", f.State, f.Message, f.FoundUtc, f.Warnings, f.OutputDirectory, f.ExportRequested,
            clips = f.Clips.Select(c => new { c.Id, c.Medal, c.StartTick, c.EndTick, c.DurationSeconds, c.Map, c.Recorder, c.State, c.File })
        })
    };

    private void PublishMonitoring()
    {
        if (!ready || closing) return;
#if NATIVE_UI_BETA
        RefreshNativeMonitoring();
        RefreshNativeDesk();
#else
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "monitor-status", data = MonitoringState() }, Json));
#endif
    }

    private async Task MonitorTick()
    {
        if (closing || !ready || monitorTickActive) return;
        monitorTickActive = true;
        await monitorGate.WaitAsync();
        try
        {
            if (closing || filmMonitor == null) return;
            if (monitorRecording != null)
            {
                if (monitorRecording.Completion.IsCompleted)
                    await FinishMonitorExport();
            }
            filmMonitor.MccRoot = string.IsNullOrWhiteSpace(preferences.Maps) ? null : FilmTitle.InstallationRoot(preferences.Maps);
            await Task.Run(() => filmMonitor.Poll(DateTime.UtcNow, monitorCancellation.Token), monitorCancellation.Token);
            monitorMessage = filmMonitor.Message;
            int failed = filmMonitor.Data.Films.Count(f => f.State is "scan-failed" or "export-failed");
            if (failed > 0) monitorMessage += $" {failed} film(s) need attention — open Activity & settings.";
            if (filmMonitor.Data.Settings.Enabled && !filmMonitor.Data.Settings.AutoExport)
            {
                int clips = filmMonitor.Data.Films.Where(f => f.State == "ready").Sum(f => f.Clips.Count);
                if (clips > 0) monitorMessage += $" {clips} clips ready — review Activity & settings to export.";
            }
            if (filmMonitor.Data.Settings.Enabled && monitorRecording == null)
            {
                var next = filmMonitor.Data.Films.FirstOrDefault(f => f.State == "ready" && (f.ExportRequested || filmMonitor.Data.Settings.AutoExport));
                if (next != null)
                {
                    // Exports run in the headless native renderer, so they start right away even
                    // while MCC is open; only this app's own playback/jobs share that renderer.
                    if (switching || surface != null || highlightScanning || highlightRecording?.IsRunning == true ||
                        batchRecording?.IsRunning == true || backgroundRecording?.IsRunning == true || IsRecordingActive)
                        monitorMessage = "Clips are ready. Waiting for playback or the current job to finish.";
                    else if (string.IsNullOrWhiteSpace(preferences.Maps)) monitorMessage = "Clips are ready. Choose the MCC map folder to enable exports.";
                    else
                    {
                        string mcc = FilmTitle.InstallationRoot(preferences.Maps);
                        string? issue = FilmTitle.FromId(next.Title).InstallationIssue(mcc);
                        if (issue != null) monitorMessage = issue;
                        else StartMonitorExport(next, mcc);
                    }
                }
            }
            monitorSnapshot = filmMonitor.Snapshot();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { monitorMessage = "Monitoring needs attention: " + e.Message; }
        finally { monitorGate.Release(); monitorTickActive = false; PublishMonitoring(); }
    }

    private void StartMonitorExport(MonitoredFilm film, string mcc)
    {
        // This runs on the UI dispatcher with no await between the idle check and
        // reserving the renderer. Every manual entry point checks HighlightRunning.
        HighlightJobPlan plan;
        if (film.JobPath.Length > 0)
        {
            if (!File.Exists(film.JobPath)) { film.State = "export-failed"; film.Message = "Export checkpoint is missing. Saved clips have been kept."; filmMonitor!.Save(); return; }
            plan = HighlightRecording.Read(film.JobPath);
        }
        else
        {
            plan = new() { Name = FilmTitle.FromId(film.Title).DisplayName + " automatic highlights", MccRoot = mcc, Fov = preferences.FovDegrees,
                OutputResolution = filmMonitor!.Data.Settings.OutputResolution, OutputQuality = filmMonitor.Data.Settings.OutputQuality, Mode = "separate", Options = film.Options,
                FrameRate = film.Title == FilmTitle.Halo3.Id ? filmMonitor!.Data.Settings.FrameRate : 60,
                SettingsIni = Program.SettingsIniPath(args), Clips = film.Clips, ContinueOnError = false };
            plan.OutputDirectory = Path.Combine(preferences.BatchOutputRoot, "Automatic clips", film.FoundUtc.ToLocalTime().ToString("yyyy-MM-dd-HHmmss") + "-" + film.Hash[..12]);
            string checkpoint = Path.Combine(MonitorRoot, "Jobs", plan.Id, "job.json");
            HighlightRecording.Save(checkpoint, plan);
            film.JobPath = checkpoint; film.OutputDirectory = plan.OutputDirectory;
        }
        var owned = new HighlightRecording(plan, nativeHost, film.JobPath, Path.Combine(dataRoot, "highlights", "CaptureCache"));
        film.State = "exporting"; film.Message = "Exporting clips";
        try { filmMonitor!.Save(); } catch { owned.Dispose(); throw; }
        monitorActiveHash = film.Hash; monitorRecording = owned; monitorExportMessage = "Preparing automatic clips…";
        owned.Changed += _ =>
        {
            if (!closing) Dispatcher.BeginInvoke(() =>
            {
                if (monitorRecording != owned) return;
                monitorExportMessage = owned.Plan.Message.Replace(Path.GetFileName(film.Film), film.Name, StringComparison.OrdinalIgnoreCase); PublishMonitoring();
            });
        };
        owned.Start();
    }

    private async Task FinishMonitorExport()
    {
        var owned = monitorRecording!; await owned.Completion;
        var film = filmMonitor!.Data.Films.Single(f => f.Hash == monitorActiveHash);
        SyncMonitorClips(film, owned.Plan.Clips);
        film.State = owned.Plan.State == "completed" ? "exported" : "export-failed";
        film.Message = owned.Plan.Message.Replace(Path.GetFileName(film.Film), film.Name, StringComparison.OrdinalIgnoreCase); film.ExportRequested = false;
        // Keep ownership if saving fails: the next tick retries persistence without
        // starting a second renderer or losing the completed checkpoint.
        filmMonitor.Save(); owned.Dispose(); monitorRecording = null; monitorActiveHash = ""; monitorExportMessage = "";
    }

    // HighlightRecording works on its own copy of the plan, so finished clip
    // files only exist in the job checkpoint until they're copied back here.
    private static bool SyncMonitorClips(MonitoredFilm film, IEnumerable<HighlightClip> exported)
    {
        bool changed = false;
        foreach (var done in exported)
        {
            var clip = film.Clips.FirstOrDefault(c => c.Id == done.Id);
            if (clip == null || (clip.File == done.File && clip.State == done.State)) continue;
            clip.File = done.File; clip.State = done.State; clip.Sha256 = done.Sha256; clip.Error = done.Error; changed = true;
        }
        return changed;
    }

    // One-time repair for films exported before clip files were synced back.
    private void RepairMonitorClipFiles()
    {
        bool changed = false;
        foreach (var film in filmMonitor!.Data.Films)
        {
            if (film.State == "exporting" || film.JobPath.Length == 0 || !File.Exists(film.JobPath) || film.Clips.All(c => c.File.Length > 0)) continue;
            try { changed |= SyncMonitorClips(film, HighlightRecording.Read(film.JobPath).Clips.Where(c => c.File.Length > 0 && File.Exists(c.File))); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
        if (changed) filmMonitor.Save();
    }

    private async Task StopMonitorExport(string state, string message)
    {
        if (monitorRecording == null) return;
        var owned = monitorRecording;
        await owned.CancelAsync();
        if (owned.Plan.State == "completed") { await FinishMonitorExport(); return; }
        var film = filmMonitor!.Data.Films.Single(f => f.Hash == monitorActiveHash);
        SyncMonitorClips(film, owned.Plan.Clips);
        film.State = state; film.Message = message;
        if (state == "paused") film.ExportRequested = false;
        filmMonitor.Save(); owned.Dispose(); monitorRecording = null; monitorActiveHash = ""; monitorExportMessage = "";
    }

    private async Task<object> MonitoringMessage(JsonElement request)
    {
        await monitorGate.WaitAsync();
        try
        {
            if (closing) throw new IOException("The app is closing.");
            if (filmMonitor == null) InitializeMonitoring();
            if (filmMonitor == null) throw new IOException(monitorMessage);
            string? type = request.GetProperty("type").GetString();
            if (type == "monitor-settings")
            {
                bool enabled = request.GetProperty("enabled").GetBoolean(), auto = request.GetProperty("autoExport").GetBoolean();
                filmMonitor.Configure(enabled, auto, request.GetProperty("minimumKills").GetInt32(), DateTime.UtcNow,
                    request.TryGetProperty("frameRate", out var frameRate) && frameRate.ValueKind == JsonValueKind.Number ? frameRate.GetInt32() : null,
                    request.TryGetProperty("outputResolution", out var resolution) && resolution.ValueKind == JsonValueKind.Number ? resolution.GetInt32() : null,
                    request.TryGetProperty("outputQuality", out var quality) && quality.ValueKind == JsonValueKind.String ? quality.GetString() : null);
                if (!enabled || !auto && monitorRecording != null && !filmMonitor.Data.Films.Single(f => f.Hash == monitorActiveHash).ExportRequested)
                    await StopMonitorExport("paused", "Export paused. Use Export clips to resume.");
                monitorMessage = enabled ? "Checking Theater autosaves…" : "Monitoring is off. Saved films and clips are kept.";
            }
            else if (type == "monitor-cancel") await StopMonitorExport("paused", "Export paused. Use Export clips to resume.");
            else if (type == "monitor-archive")
            {
                Directory.CreateDirectory(filmMonitor.Archive);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { filmMonitor.Archive } });
            }
            else
            {
                string hash = request.GetProperty("hash").GetString() ?? "";
                var film = filmMonitor.Data.Films.SingleOrDefault(f => f.Hash == hash) ?? throw new IOException("This monitored film is no longer available.");
                if (type == "monitor-folder")
                {
                    if (Directory.Exists(film.OutputDirectory)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { film.OutputDirectory } });
                    else throw new IOException("This film has no exported clips yet.");
                }
                else if (type == "monitor-open-clip")
                {
                    string clipId = request.GetProperty("clipId").GetString() ?? "";
                    var clip = film.Clips.SingleOrDefault(c => c.Id == clipId && c.State == "completed");
                    if (clip != null && File.Exists(clip.File)) Process.Start(new ProcessStartInfo(clip.File) { UseShellExecute = true });
                    else throw new IOException("This saved clip is no longer available.");
                }
                else if (type == "monitor-export")
                {
                    if (!filmMonitor.Data.Settings.Enabled) throw new IOException("Turn on monitoring to process queued clips.");
                    if (film.State is "ready" or "paused" or "export-failed")
                    { film.State = "ready"; film.ExportRequested = true; film.Message = "Queued for export"; }
                }
                else if (type == "monitor-retry-scan" && film.State == "scan-failed")
                { film.State = "archived"; film.Message = "Waiting to scan"; }
                else if (type == "monitor-dismiss" && film.State != "exporting")
                { film.State = "dismissed"; film.ExportRequested = false; film.Message = "Dismissed; original film preserved"; }
            }
            filmMonitor.Save(); monitorSnapshot = filmMonitor.Snapshot(); PublishMonitoring();
            return MonitoringState();
        }
        finally { monitorGate.Release(); }
    }

    private async Task StopMonitoring()
    {
        monitorTimer.Stop(); monitorCancellation.Cancel();
        await monitorGate.WaitAsync();
        try
        {
            try { await StopMonitorExport("ready", "Waiting to resume after restart"); }
            finally { filmMonitor?.Dispose(); }
        }
        finally { monitorGate.Release(); }
    }
}
