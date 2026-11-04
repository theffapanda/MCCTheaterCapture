using System.IO;
using System.Text.Json;

namespace MCCTheaterCapture;

// Editing a completed/checkpointed attempt must never reuse its capture signature.
internal static class NativeBetaDraft
{
    internal static BatchPlan Fork(BatchPlan source)
    {
        var copy = JsonSerializer.Deserialize<BatchPlan>(JsonSerializer.Serialize(source, BatchRecording.Json), BatchRecording.Json)!;
        copy.Id = Guid.NewGuid().ToString("N"); copy.Signature = ""; copy.Checkpoint = "";
        copy.SettingsSnapshot = ""; copy.OutputDirectory = ""; copy.State = "draft";
        copy.Message = "Ready to build your queue."; copy.ActiveIndex = -1;
        copy.Progress = copy.ActiveProgress = 0; copy.Finished = false;
        copy.EstimatedSecondsRemaining = copy.CurrentFilmSecondsRemaining = null;
        copy.CombinedFile = copy.CombinedSha256 = "";
        foreach (var item in copy.Items)
        {
            item.State = "queued"; item.File = item.Error = item.Sha256 = "";
            item.Frames = 0; item.DurationSeconds = 0;
        }
        return copy;
    }

    internal static BatchItem Item(Recording film) => new()
    {
        Film = film.Path, Name = film.Name, Map = film.Map, Title = film.Title,
        TotalTicks = film.TotalTicks, SourceBytes = film.Bytes, SourceModified = film.Modified
    };

    internal static void Move(BatchPlan plan, int from, int to)
    {
        if (from < 0 || from >= plan.Items.Count || to < 0 || to >= plan.Items.Count || from == to) return;
        var item = plan.Items[from]; plan.Items.RemoveAt(from); plan.Items.Insert(to, item);
    }

    internal static bool ScanMatches(HighlightScanResult? scan, BatchPlan plan)
    {
        if (scan == null || plan.HighlightOptions == null) return false;
        var inputs = plan.Items.Select(i => Path.GetFullPath(i.Film)).Distinct(StringComparer.OrdinalIgnoreCase);
        return scan.Inputs.Select(Path.GetFullPath).SequenceEqual(inputs, StringComparer.OrdinalIgnoreCase)
            && JsonSerializer.Serialize(scan.Options) == JsonSerializer.Serialize(plan.HighlightOptions);
    }
}
