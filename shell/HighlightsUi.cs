using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace MCCTheaterCapture;

internal sealed partial class ViewerWindow
{
    private HighlightRecording? highlightRecording;
    private HighlightJobPlan? savedHighlightJob;
    private HighlightScanResult? highlightScan;
    private CancellationTokenSource? highlightScanCancellation;
    private bool highlightScanning;
    private string highlightScanMessage = "Select films in the sequence, then find multikills.";
    private double highlightScanProgress;
    private string HighlightCurrentPath => Path.Combine(dataRoot, "highlight-current.json");
    private string HighlightScanPath => Path.Combine(dataRoot, "highlight-scan.json");
    private bool HighlightRunning => highlightRecording?.IsRunning == true || highlightScanning;
    private object HighlightsState() => new { scan = highlightScan, scanning = highlightScanning, scanMessage = highlightScanMessage, scanProgress = highlightScanProgress,
        job = highlightRecording?.Plan ?? savedHighlightJob, running = highlightRecording?.IsRunning == true };
    private void LoadHighlights()
    {
        try
        {
            if (File.Exists(HighlightCurrentPath))
            {
                savedHighlightJob = HighlightRecording.Read(HighlightCurrentPath);
                if (Guid.TryParseExact(savedHighlightJob.Id, "N", out _))
                {
                    var latest = Path.Combine(dataRoot, "highlights", savedHighlightJob.Id, "job.json");
                    if (File.Exists(latest)) savedHighlightJob = HighlightRecording.Read(latest);
                }
            }
            if (File.Exists(HighlightScanPath)) highlightScan = JsonSerializer.Deserialize<HighlightScanResult>(File.ReadAllText(HighlightScanPath), BatchRecording.Json);
        }
        catch (Exception e) when (e is IOException or JsonException) { savedHighlightJob = null; highlightScan = null; }
    }
    private void PublishHighlights()
    {
#if NATIVE_UI_BETA
        if (ready && !closing) RefreshNativeDesk();
#else
        if (ready && !closing) browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "highlights-status", data = HighlightsState() }, Json));
#endif
    }
    private async Task<object?> HighlightMessage(JsonElement request)
    {
        switch (request.GetProperty("type").GetString())
        {
            case "highlights-state": return HighlightsState();
            case "highlights-scan":
                if (HighlightRunning || batchRecording?.IsRunning == true || backgroundRecording?.IsRunning == true || IsRecordingActive)
                    throw new IOException("Finish the current scan or recording before finding highlights.");
                var scanRequest = request.GetProperty("request").Deserialize<HighlightScanRequest>(BatchRecording.Json) ?? throw new IOException("Missing scan request.");
                scanRequest.MccRoot=FilmTitle.InstallationRoot(preferences.Maps);
                highlightScanCancellation?.Dispose(); highlightScanCancellation = new(); highlightScanning = true; highlightScan = null;
                PublishHighlights();
                try
                {
                    long last = 0;
                    highlightScan = await HighlightScan.Run(scanRequest, Path.Combine(dataRoot, "FilmIndex"), highlightScanCancellation.Token, (message, progress) =>
                    {
                        if (Environment.TickCount64 - last < 150 && progress < 1) return; last = Environment.TickCount64;
                        Dispatcher.BeginInvoke(() => { if (highlightScanning) { highlightScanMessage = message; highlightScanProgress = progress; PublishHighlights(); } });
                    });
                    File.WriteAllText(HighlightScanPath, JsonSerializer.Serialize(highlightScan, BatchRecording.Json));
                    highlightScanMessage = $"Found {highlightScan.Clips.Count} multikill {(highlightScan.Clips.Count == 1 ? "clip" : "clips")}."; highlightScanProgress = 1;
                }
                catch (OperationCanceledException) { highlightScanMessage = "Scan cancelled. Find multikills to start again."; }
                finally { highlightScanning = false; PublishHighlights(); }
                return HighlightsState();
            case "highlights-cancel-scan": highlightScanCancellation?.Cancel(); return HighlightsState();
            case "highlights-start":
            case "highlights-save":
                if (highlightScan == null || highlightScanning) throw new IOException("Find multikills before exporting.");
                var ids = request.GetProperty("ids").EnumerateArray().Select(v => v.GetString()).ToHashSet();
                var format = request.TryGetProperty("format", out var formatJson) ? formatJson.Deserialize<HighlightFormat>(BatchRecording.Json) ?? throw new IOException("Missing clip format.") : new();
                format.Validate();
                var captions = request.TryGetProperty("captions", out var captionsJson) ? captionsJson.Deserialize<Dictionary<string, HighlightCaption>>(BatchRecording.Json) ?? throw new IOException("Missing clip captions.") : new();
                var plan = new HighlightJobPlan
                {
                    Name = request.GetProperty("name").GetString() ?? "Multikill highlights", Mode = request.GetProperty("mode").GetString() ?? "separate",
                    Fov = request.GetProperty("fov").GetInt32(), ContinueOnError = request.GetProperty("continueOnError").GetBoolean(),
                    Options = highlightScan.Options, Format = format,
                    OutputFormat = request.TryGetProperty("outputFormat",out var outputFormat) ? outputFormat.GetString() ?? "mp4" : "mp4",
                    OutputResolution = request.TryGetProperty("outputResolution",out var outputResolution) ? outputResolution.GetInt32() : 1440,
                    OutputQuality = request.TryGetProperty("outputQuality",out var outputQuality) ? outputQuality.GetString() ?? "high" : "high",
                    Clips = highlightScan.Clips.Where(c => ids.Contains(c.Id)).Select(c => JsonSerializer.Deserialize<HighlightClip>(JsonSerializer.Serialize(c, BatchRecording.Json), BatchRecording.Json)!).ToList()
                };
                if (plan.Clips.Count == 0) throw new IOException("Select at least one multikill clip.");
                foreach (var selected in plan.Clips)
                {
                    selected.Caption = captions.TryGetValue(selected.Id, out var caption) ? caption ?? throw new IOException("Missing clip caption.") : new();
                    selected.Caption.Validate();
                }
                plan.MccRoot = FilmTitle.InstallationRoot(preferences.Maps); plan.SettingsIni = Program.SettingsIniPath(args);
                plan.OutputDirectory = Path.Combine(preferences.BatchOutputRoot, BatchRecording.SafeName(plan.Name) + "-" + plan.Id[..8]);
                if (request.TryGetProperty("preview", out var preview) && preview.GetBoolean()) plan.OutputDirectory = Path.Combine(dataRoot, "HighlightPreviews", plan.Id);
                if (request.GetProperty("type").GetString() == "highlights-save")
                {
                    var save = new SaveFileDialog { Title = "Save a headless highlight job", Filter = "Highlight job (*.json)|*.json", FileName = BatchRecording.SafeName(plan.Name) + "-highlights.json", AddExtension = true, DefaultExt = ".json" };
                    if (save.ShowDialog(this) == true) { HighlightRecording.Save(save.FileName, plan); return save.FileName; }
                    return null;
                }
                await StartHighlights(plan); return HighlightsState();
            case "highlights-retry":
                await StartHighlights(highlightRecording?.Plan ?? savedHighlightJob ?? throw new IOException("Load a highlight job first.")); return HighlightsState();
            case "highlights-cancel": if (highlightRecording != null) await highlightRecording.CancelAsync(); return HighlightsState();
            case "highlights-load":
                if (HighlightRunning || batchRecording?.IsRunning == true) throw new IOException("Finish the current job before loading another.");
                var dialog = new OpenFileDialog { Title = "Load a highlight checkpoint", Filter = "Highlight jobs (*.json)|*.json", CheckFileExists = true };
                if (dialog.ShowDialog(this) == true)
                {
                    var loaded = HighlightRecording.Read(dialog.FileName);
                    if (loaded.Schema != "mcc-highlights/v1" || !Guid.TryParseExact(loaded.Id, "N", out _)) throw new IOException("Not a supported highlight job.");
                    highlightRecording?.Dispose(); highlightRecording = null; savedHighlightJob = loaded; HighlightRecording.Save(HighlightCurrentPath, loaded);
                }
                return HighlightsState();
            case "highlights-open-folder":
                var output = (highlightRecording?.Plan ?? savedHighlightJob)?.OutputDirectory;
                if (output != null && Directory.Exists(output)) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { output } });
                return null;
            case "highlights-open-clip":
                var id = request.GetProperty("clipId").GetString();
                var clip = (highlightRecording?.Plan ?? savedHighlightJob)?.Clips.FirstOrDefault(c => c.Id == id && c.State == "completed");
                if (clip != null && File.Exists(clip.File)) Process.Start(new ProcessStartInfo(clip.File) { UseShellExecute = true });
                return null;
            default: throw new IOException("Unknown highlight request.");
        }
    }
    private async Task StartHighlights(HighlightJobPlan plan)
    {
        if (HighlightRunning || batchRecording?.IsRunning == true || backgroundRecording?.IsRunning == true || IsRecordingActive)
            throw new IOException("Finish the current scan or recording before starting highlights.");
        if (surface != null) await ShowLibrary();
        highlightRecording?.Dispose();
        var owned = new HighlightRecording(plan, nativeHost, Path.Combine(dataRoot, "highlights", plan.Id, "job.json"), Path.Combine(dataRoot, "highlights", "CaptureCache"));
        highlightRecording = owned; savedHighlightJob = owned.Plan; HighlightRecording.Save(HighlightCurrentPath, owned.Plan);
        owned.Changed += _ => { if (!closing) Dispatcher.BeginInvoke(() => { if (highlightRecording == owned) PublishHighlights(); }); };
        owned.Start(); PublishHighlights(); _ = CompleteHighlights(owned);
    }
    private async Task CompleteHighlights(HighlightRecording owned)
    {
        await owned.Completion;
        if (highlightRecording == owned) { savedHighlightJob = owned.Plan; HighlightRecording.Save(HighlightCurrentPath, owned.Plan); PublishHighlights(); }
    }
}
