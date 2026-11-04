using System.IO;
using System.Text.Json;

namespace MCCTheaterCapture;

internal static class HighlightCli
{
    public static bool ShouldRun(string[] args) => args.Contains("--highlight-scan") || args.Contains("--highlight-job");
    public static async Task Run(string[] args)
    {
        string? Arg(string key) { int p = Array.IndexOf(args, key); return p >= 0 && p + 1 < args.Length ? args[p + 1] : null; }
        try
        {
            string data = Path.GetFullPath(Arg("--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "UserData"));
            using var stop = new CancellationTokenSource();
            var watcher = Task.Run(async () => { while (!stop.IsCancellationRequested) { if (Arg("--cancel-file") is string f && File.Exists(f)) { stop.Cancel(); break; } try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { break; } } });
            try
            {
                if (Arg("--highlight-scan") is string requestPath)
                {
                    var request = JsonSerializer.Deserialize<HighlightScanRequest>(File.ReadAllText(requestPath), BatchRecording.Json) ?? throw new IOException("Empty scan request.");
                    request.MccRoot=Arg("--mcc-root")??request.MccRoot;
                    var result = await HighlightScan.Run(request, Path.Combine(data, "FilmIndex"), stop.Token);
                    string json = JsonSerializer.Serialize(result, BatchRecording.Json);
                    if (Arg("--report") is string report) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllText(report, json); }
                    Environment.ExitCode = result.Films.Any(f => f.State == "unsupported") ? 1 : 0;
                }
                else
                {
                    string path = Path.GetFullPath(Arg("--highlight-job") ?? throw new IOException("Missing highlight job path."));
                    var plan = HighlightRecording.Read(path);
                    using var job = new HighlightRecording(plan, Path.GetFullPath(Arg("--native-host") ?? Path.Combine(AppContext.BaseDirectory, "native", "NativeReplayBackend.exe")), path, Path.Combine(data, "highlights", "CaptureCache"));
                    job.Start(); using var registration = stop.Token.Register(() => _ = job.CancelAsync()); await job.Completion;
                    if (Arg("--report") is string report) HighlightRecording.Save(Path.GetFullPath(report), job.Plan);
                    Environment.ExitCode = job.Plan.State == "completed" ? 0 : 1;
                }
            }
            finally { stop.Cancel(); await watcher; }
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            if (Arg("--report") is string report)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { state = error is OperationCanceledException ? "cancelled" : "failed", error = error.Message }, BatchRecording.Json));
            }
        }
    }
}
