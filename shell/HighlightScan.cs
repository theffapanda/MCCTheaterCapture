using System.IO;

namespace MCCTheaterCapture;

internal sealed class HighlightScanRequest
{
    public List<string> Films { get; set; } = new();
    public HighlightOptions Options { get; set; } = new();
    public string? MccRoot { get; set; }
}
internal sealed record HighlightFilmResult(string Film, string Recorder, string State, int Matches, string Error, List<string> Warnings);
internal sealed class HighlightScanResult
{
    public string Schema { get; set; } = "mcc-highlight-scan/v1";
    public List<HighlightFilmResult> Films { get; set; } = new();
    public List<HighlightClip> Clips { get; set; } = new();
    public HighlightOptions Options { get; set; } = new();
    public string State { get; set; } = "completed";
    public List<string> Inputs { get; set; } = new();
}
internal static class HighlightScan
{
    public static Task<HighlightScanResult> Run(HighlightScanRequest request, string cache, CancellationToken token, Action<string, double>? progress = null) => Task.Run(() =>
    {
        request.Options.Validate();
        if (request.Films.Count is < 1 or > 500) throw new IOException("Select between 1 and 500 Theater files.");
        var paths = request.Films.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = new HighlightScanResult { Options = request.Options, Inputs = paths.ToList() };
        var contents = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var path = paths[i];
            progress?.Invoke($"Scanning {i + 1} / {paths.Length}: {Path.GetFileName(path)}", i / (double)paths.Length);
            try
            {
                var index = FilmEventIndex.Read(path, cache, token, value => progress?.Invoke($"Scanning {i + 1} / {paths.Length}: {Path.GetFileName(path)}", (i + value) / paths.Length),request.MccRoot);
                if (!contents.Add(index.Hash)) { result.Films.Add(new(path, index.Recorder, "duplicate", 0, "", new() { "Same film content already scanned earlier in this sequence." })); continue; }
                var warnings = new List<string>(); var clips = HighlightPlanner.Plan(index, request.Options, warnings);
                result.Films.Add(new(path, index.Recorder, clips.Count == 0 ? "no-matches" : "completed", clips.Count, "", warnings));
                result.Clips.AddRange(clips);
                if (request.Options.Selection == "first" && clips.Count > 0) { result.Clips = result.Clips.Take(1).ToList(); break; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException)
            { result.Films.Add(new(path, "", "unsupported", 0, e.Message, new())); }
        }
        progress?.Invoke($"Found {result.Clips.Count} multikill {(result.Clips.Count == 1 ? "clip" : "clips")}.", 1); return result;
    }, token);
}
