using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed class HighlightJobPlan
{
    public string Schema { get; set; } = "mcc-highlights/v1";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Multikill highlights";
    public string Mode { get; set; } = "separate";
    public string OutputFormat { get; set; } = "mp4";
    public int OutputResolution { get; set; } = 1440;
    public string OutputQuality { get; set; } = "high";
    public ExportEncoding Encoding { get; set; } = new();
    public string MccRoot { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string? SettingsIni { get; set; }
    public string SettingsSnapshot { get; set; } = "";
    public int Fov { get; set; } = 91;
    // 60 or 120 (Halo 3 only). Clip ranges stay in 60 Hz film ticks.
    public int FrameRate { get; set; } = 60;
    public bool ContinueOnError { get; set; } = true;
    public HighlightOptions Options { get; set; } = new();
    public HighlightFormat Format { get; set; } = new();
    public List<HighlightClip> Clips { get; set; } = new();
    public string Signature { get; set; } = "";
    public string Checkpoint { get; set; } = "";
    public string State { get; set; } = "draft";
    public string Message { get; set; } = "Ready to export highlights.";
    public double Progress { get; set; }
    public double? EstimatedSecondsRemaining { get; set; }
    public double? PrefixSeconds { get; set; }
    public int CachedSources { get; set; }
    public int CapturedSources { get; set; }
    public string FootageCacheWarning { get; set; } = "";
    public int CaptureStartTick { get; set; }
    public string CaptureWarning { get; set; } = "";
    public bool Finished { get; set; }
    public string CombinedFile { get; set; } = "";
    public string CombinedSha256 { get; set; } = "";
    public string CombinedInputsHash { get; set; } = "";
}

// One native capture window per source, then frame-accurate trims. Every clip
// has its own checkpoint/hash so failed or cancelled attempts can be resumed.
internal sealed class HighlightRecording : IDisposable
{
    private const string AudioDeliveryRevision = "same-capture-48k-4k-v1";
    private readonly string backend, checkpoint, captureCacheRoot;
    private readonly CancellationTokenSource cancellation = new();
    private BackgroundRecording? active;
    private Task? running;
    private long lastPublish;
    public HighlightJobPlan Plan { get; }
    public event Action<HighlightJobPlan>? Changed;
    public bool IsRunning => running is { IsCompleted: false };
    public Task Completion => running ?? Task.CompletedTask;
    public HighlightRecording(HighlightJobPlan plan, string backend, string checkpoint, string? captureCacheRoot = null)
    {
        Plan = JsonSerializer.Deserialize<HighlightJobPlan>(JsonSerializer.Serialize(plan, BatchRecording.Json), BatchRecording.Json)!;
        this.backend = backend; this.checkpoint = Path.GetFullPath(checkpoint); Plan.Checkpoint = this.checkpoint;
        this.captureCacheRoot = Path.GetFullPath(captureCacheRoot ?? Path.Combine(Path.GetDirectoryName(this.checkpoint)!, "CaptureCache"));
    }
    public static HighlightJobPlan Read(string path) => JsonSerializer.Deserialize<HighlightJobPlan>(File.ReadAllText(path), BatchRecording.Json) ?? throw new IOException("Empty highlight job.");
    public static void Save(string path, HighlightJobPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(plan, BatchRecording.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Start() { if (running != null) throw new InvalidOperationException("Job already started."); running = Task.Run(Run); }
    private void Publish(string state, string message, bool save = true)
    {
        Plan.State = state; Plan.Message = message;
        if (state != "capturing") Plan.EstimatedSecondsRemaining = null;
        if (save) Save(checkpoint, Plan); Changed?.Invoke(Plan);
    }
    private void ValidateShape()
    {
        BatchRecording.ValidateOutputFormat(Plan.OutputFormat);
        BatchRecording.ValidateVideoProfile(Plan.OutputResolution,Plan.OutputQuality);
        Plan.Encoding ??= new(); Plan.Encoding.Validate();
        if (Plan.Format == null || Plan.Options == null || Plan.Clips == null || Plan.Clips.Any(c => c == null || c.Caption == null))
            throw new IOException("Highlight jobs need format settings, scan options and a caption object for every clip.");
        Plan.Options.Validate();
        Plan.Format.Validate();
        foreach (var clip in Plan.Clips) clip.Caption.Validate();
        if (Plan.Schema != "mcc-highlights/v1" || !Guid.TryParseExact(Plan.Id, "N", out _) || Plan.Clips.Count is < 1 or > 5000 || Plan.Clips.Select(c => c.Id).Distinct().Count() != Plan.Clips.Count)
            throw new IOException("Unsupported highlight job, duplicate clips or empty selection.");
        if (Plan.Mode is not ("separate" or "combined" or "both") || Plan.Fov is < 55 or > 120) throw new IOException("Invalid highlight delivery mode or FOV.");
        if (Plan.FrameRate is not (60 or 120)) throw new IOException("Choose 60 or 120 fps.");
        if (Plan.FrameRate == 120 && Plan.Clips.Any(c => FilmTitle.Detect(c.Film) != FilmTitle.Halo3)) throw new IOException("120 fps clips are available for Halo 3 films only.");
        Plan.MccRoot=FilmTitle.InstallationRoot(Plan.MccRoot);
        if (!Directory.Exists(Plan.MccRoot) || !File.Exists(backend)) throw new IOException("Choose the installed MCC content folder and native backend.");
        foreach(var source in Plan.Clips.Select(clip=>clip.Film).Distinct(StringComparer.OrdinalIgnoreCase))
        {var title=FilmTitle.Detect(source);if(title.InstallationIssue(Plan.MccRoot) is string issue)throw new IOException(issue);}
        if (string.IsNullOrWhiteSpace(Plan.OutputDirectory)) throw new IOException("Choose an export folder.");
        foreach (string tool in new[] { "ffmpeg", "ffprobe" }) if (BatchRecording.FindTool(tool) == null) throw new IOException($"Highlights need {tool}.exe on PATH or in the package tools folder.");
    }
    private void FreezeSettings()
    {
        if (Plan.SettingsSnapshot.Length == 0)
        {
            if (Plan.Signature.Length > 0) throw new IOException("Saved highlight job is missing its frozen settings.");
            var original = Plan.SettingsIni ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "MCC", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
            if (Plan.SettingsIni != null && !File.Exists(original)) throw new IOException("Selected MCC settings file is missing.");
            Plan.SettingsSnapshot = Path.Combine(Path.GetDirectoryName(checkpoint)!, "settings-" + Guid.NewGuid().ToString("N") + ".ini");
            if (File.Exists(original)) File.Copy(original, Plan.SettingsSnapshot, false); else File.WriteAllText(Plan.SettingsSnapshot, "");
        }
        string signatureData = JsonSerializer.Serialize(new
        {
            renderer = "native-medals-v1", parser = "mcc-film-events/v1", encoder = "highlight-x264-crf16-v1",
            Plan.Mode, Plan.Fov, Plan.Options, Plan.MccRoot, Plan.OutputDirectory,
            settings = BatchRecording.Hash(Plan.SettingsSnapshot), clips = Plan.Clips.Select(c => new { c.Id, c.Film, c.SourceHash, c.StartTick, c.EndTick })
        });
        // Retain signatures of existing Original jobs. Portrait checkpoints also
        // freeze composition and captions, so Retry cannot reuse a stale style.
        if (Plan.Format.IsPortrait) signatureData += "|soft-focus-v1|" + JsonSerializer.Serialize(new { Plan.Format, captions = Plan.Clips.Select(c => new { c.Id, c.Caption }) });
        if (Plan.Clips.Select(c => c.Film).Distinct(StringComparer.OrdinalIgnoreCase).Any(film => { var title = FilmTitle.Detect(film); return title == FilmTitle.Halo4 || title == FilmTitle.Halo2A; }))
            signatureData += "|isolated-legacy-audio-qpc-v2";
        if (Plan.Clips.Select(c => c.Film).Distinct(StringComparer.OrdinalIgnoreCase).Any(film => FilmTitle.Detect(film) == FilmTitle.Halo2A))
            signatureData += "|h2a-full-clip-audio-v1";
        if (Plan.Clips.Select(c => c.Film).Distinct(StringComparer.OrdinalIgnoreCase).Any(film => { var title = FilmTitle.Detect(film); return title == FilmTitle.Halo4 || title == FilmTitle.Halo2A; }))
            signatureData += "|native-h4-h2a-hud-fonts-v1";
        if(Plan.OutputFormat!="mp4")signatureData+="|container:"+Plan.OutputFormat;
        if(Plan.OutputResolution!=1440||Plan.OutputQuality!="high")signatureData+=$"|video:{Plan.OutputResolution}:{Plan.OutputQuality}";
        if(Plan.FrameRate!=60)signatureData+="|fps:"+Plan.FrameRate;
        signatureData+=Plan.Encoding.ExportKey(Plan.OutputQuality,audio:true)+Plan.Encoding.CaptureKey;
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureData)));
        if (Plan.Signature.Length > 0 && Plan.Signature != signature) throw new IOException("Highlight sources, ranges, settings, format, captions or renderer changed. Start a new highlight job.");
        Plan.Signature = signature;
    }
    private async Task Run()
    {
        FileStream? jobLock = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
            jobLock = new(checkpoint + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            ValidateShape(); Plan.Finished = false; Plan.Progress = 0; Plan.CachedSources = 0; Plan.CapturedSources = 0; Plan.FootageCacheWarning = ""; Plan.CaptureWarning = ""; FreezeSettings();
            Directory.CreateDirectory(Plan.OutputDirectory); Publish("preparing", "Verifying multikill evidence and saved clips…");
            var groups = Plan.Clips.GroupBy(c => Path.GetFullPath(c.Film), StringComparer.OrdinalIgnoreCase).ToArray(); int completedGroups = 0; bool failed = false; string failureReason = "";
            foreach (var group in groups)
            {
                cancellation.Token.ThrowIfCancellationRequested(); string? prefix = null; HighlightCaptureCache.Lease? cached = null;
                try
                {
                    // Keep source read-locked until native replay and trimming end.
                    using var sourceLock = new FileStream(group.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var index = FilmEventIndex.Read(group.Key, Path.Combine(Path.GetDirectoryName(checkpoint)!, "index"), cancellation.Token,mccRoot:Plan.MccRoot);
                    var canonical = HighlightPlanner.Plan(index, Plan.Options).ToDictionary(c => c.Id);
                    foreach (var clip in group)
                    {
                        if (clip.SourceHash != index.Hash || !canonical.TryGetValue(clip.Id, out var expected) || clip.StartTick != expected.StartTick || clip.EndTick != expected.EndTick || clip.PlayerId != expected.PlayerId)
                            throw new IOException("Film contents or highlight evidence changed. Find highlights again.");
                        clip.Kills = expected.Kills; clip.Award = expected.Award; clip.Medal = expected.Medal;
                        clip.Recorder = expected.Recorder; clip.Map = expected.Map; clip.KillCount = expected.KillCount; clip.Title = expected.Title;
                        clip.TotalTicks = expected.TotalTicks; clip.ShortLeadIn = expected.ShortLeadIn;
                        if (clip.State == "completed" && File.Exists(clip.File) && clip.Sha256.Length > 0 && BatchRecording.Hash(clip.File) == clip.Sha256 && (clip.Title != "groundhog" || HasVerifiedAudioDelivery(clip)))
                        { WriteManifest(clip); continue; }
                        clip.State = "queued"; clip.Error = ""; clip.AudioDelivery = null;
                    }
                    var pending = group.Where(c => c.State != "completed").ToArray();
                    if (pending.Length == 0) { completedGroups++; continue; }
                    int captureEnd = Math.Min(index.TotalTicks, pending.Max(c => c.EndTick) + 30);
                    double through = captureEnd / 60.0;
                    Plan.PrefixSeconds = through;
                    // Forward warm-up is verified only for Halo 3. Other engines
                    // record from tick zero and trim against that same timeline.
                    int captureStart = index.Title == "halo3" ? Math.Max(0, pending.Min(c => c.StartTick) - 120) : 0;
                    if (captureStart < 3) captureStart = 0;
                    Plan.CaptureStartTick = captureStart;
                    string? cacheKey = null;
                    try
                    {
                        cacheKey = HighlightCaptureCache.KeyFor(group.Key, index.Hash, Plan.MccRoot, Plan.SettingsSnapshot, Plan.Fov, backend, cancellation.Token, Plan.FrameRate, Plan.Encoding.CaptureKey);
                        Publish("preparing", "Checking previously captured footage…");
                        cached = HighlightCaptureCache.TryAcquire(captureCacheRoot, cacheKey, pending.Max(c => c.EndTick), cancellation.Token, pending.Min(c => c.StartTick));
                        if (cached != null)
                        {
                            var media = await BatchRecording.Probe(cached.File, cancellation.Token);
                            await VerifyFormat(cached.File, cancellation.Token, true);
                            if (media.Frames != cached.Frames * FramesPerTick) throw new IOException("Cached footage frame count changed.");
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                    {
                        if (cached != null) HighlightCaptureCache.Invalidate(cached);
                        cached = null;
                        Plan.FootageCacheWarning = "Footage cache unavailable or invalid; rendering from the film. " + error.Message;
                    }
                    if (cached != null)
                    {
                        Plan.CaptureStartTick = cached.StartTick;
                        Plan.CachedSources++;
                        Plan.Progress = Math.Min(.99, (completedGroups + .8) / groups.Length);
                        Publish("preparing", $"Reusing captured footage for {Path.GetFileName(group.Key)} — no Theater replay.");
                    }
                    else
                    {
                        Plan.CapturedSources++;
                        prefix = Path.Combine(Plan.OutputDirectory, ".parts", "source-" + Guid.NewGuid().ToString("N") + ".mp4");
                        async Task<BackgroundRecordingStatus> CaptureRange()
                        {
                            var session = Path.Combine(Path.GetDirectoryName(checkpoint)!, "sessions", Guid.NewGuid().ToString("N"));
                            Publish("capturing", captureStart > 0 ? $"Advancing to {captureStart / 60.0:F1}s, then recording {through - captureStart / 60.0:F1}s for {pending.Length} clips." : $"Replaying {Path.GetFileName(group.Key)} through {through:F1}s for {pending.Length} selected clips.");
                            using var capture = new BackgroundRecording(backend, group.Key, Plan.MccRoot, session, (captureEnd-captureStart)/60.0, prefix, Plan.SettingsSnapshot, Plan.Fov, captureStart, Plan.FrameRate, Plan.Encoding);
                            active = capture;
                            capture.Changed += status =>
                            {
                                Plan.Progress = Math.Min(.99, (completedGroups + (status.Progress ?? 0) * .8) / groups.Length);
                                Plan.EstimatedSecondsRemaining = status.EstimatedSecondsRemaining;
                                if (Environment.TickCount64 - lastPublish > 500) { lastPublish = Environment.TickCount64; Publish("capturing", $"{Path.GetFileName(group.Key)} · {status.Message} · capture {captureStart / 60.0:F1}–{through:F1}s", false); }
                            };
                            capture.Start(); if (cancellation.IsCancellationRequested) await capture.CancelAsync();
                            var result = await capture.Completion; active = null; cancellation.Token.ThrowIfCancellationRequested();
                            return result;
                        }
                        var captureResult = await CaptureRange();
                        if (!captureResult.Complete && captureStart > 0)
                        {
                            Plan.CaptureWarning = "Fast warm-up was unavailable; used a verified replay from the beginning. " + captureResult.Message;
                            captureStart = 0; Plan.CaptureStartTick = 0;
                            if (File.Exists(prefix)) File.Delete(prefix);
                            captureResult = await CaptureRange();
                        }
                        if (!captureResult.Complete) throw new IOException(captureResult.Message);
                        if (cacheKey != null)
                        {
                            Publish("preparing", "Keeping verified footage for faster edits…");
                            try
                            {
                                var media = await BatchRecording.Probe(prefix, cancellation.Token);
                                if (media.Frames % FramesPerTick != 0) throw new IOException("Captured footage does not hold whole film ticks.");
                                if (!HighlightCaptureCache.Store(captureCacheRoot, cacheKey, prefix, media.Frames / FramesPerTick, cancellation.Token, startTick: captureStart))
                                    Plan.FootageCacheWarning = "Export continues without retaining footage: the 8 GB cache has no room for this capture.";
                            }
                            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                            { Plan.FootageCacheWarning = "Export continues without retaining footage: " + error.Message; }
                        }
                    }
                    for (int i = 0; i < pending.Length; i++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested(); var clip = pending[i]; clip.State = "trimming";
                        Publish("trimming", $"Saving {clip.Medal} · {clip.Recorder} · {clip.DurationSeconds:F2}s");
                        string folder = Plan.Mode == "combined" ? Path.Combine(Plan.OutputDirectory, ".parts") : Plan.OutputDirectory;
                        Directory.CreateDirectory(folder);
                        string name = $"{Plan.Clips.IndexOf(clip) + 1:000}-{BatchRecording.SafeName(clip.Recorder)}-{BatchRecording.SafeName(clip.Medal)}-{clip.Id[..8]}-{Guid.NewGuid().ToString("N")[..6]}";
                        string final = Path.Combine(folder, name + ".mp4"), temp = Path.Combine(folder, name + ".partial.mp4"), captions = Path.Combine(folder, name + ".captions.png");
                        try
                        {
                            var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-n", "-ss", Seconds(clip.StartTick - (cached?.StartTick ?? captureStart)), "-i", cached?.File ?? prefix! };
                            if (Plan.Format.IsPortrait)
                            {
                                await HighlightComposition.WriteCaptions(captions, Plan.Format, clip.Caption, cancellation.Token);
                                arguments.AddRange(new[] { "-i", captions, "-filter_complex", Plan.Format.FilterGraph(), "-map", "[out]" });
                            }
                            else arguments.AddRange(new[] { "-map", "0:v:0", "-vf", $"scale={BatchRecording.VideoWidth(Plan.OutputResolution)}:{Plan.OutputResolution}:flags=lanczos,setsar=1" });
                            arguments.AddRange(new[] { "-t", Seconds(clip.EndTick - clip.StartTick), "-map", "0:a:0" });
                            arguments.AddRange(Plan.Encoding.X264Arguments(Plan.OutputQuality));
                            arguments.AddRange(new[] { "-video_track_timescale", "15360",
                                "-c:a", "aac", "-b:a", Plan.Encoding.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) + "k", "-movie_timescale", "48000", "-movflags", "+faststart", temp });
                            await BatchRecording.Execute(BatchRecording.FindTool("ffmpeg")!, arguments.ToArray(), cancellation.Token);
                            var media = await BatchRecording.Probe(temp, cancellation.Token);
                            if (media.Frames != (long)(clip.EndTick - clip.StartTick) * FramesPerTick || Math.Abs(media.Seconds - clip.DurationSeconds) > .05)
                                throw new IOException("Highlight frame count or duration did not match its selected range.");
                            await VerifyFormat(temp, cancellation.Token);
                            long sourceStartSample = checked((long)(clip.StartTick - (cached?.StartTick ?? captureStart)) * 800);
                            long sampleCount = checked((long)(clip.EndTick - clip.StartTick) * 800);
                            AudioDeliveryVerification delivery;
                            try { delivery = await AudioDeliveryVerifier.VerifyAsync(cached?.File ?? prefix!, temp, sourceStartSample, sampleCount, cancellation.Token); }
                            catch (IOException)
                            {
                                // Some dense game audio loses too much detail at
                                // 192 kbps to pass the existing sample verification.
                                // Retry once at higher fidelity, with identical trim
                                // arguments and unchanged verification thresholds.
                                Publish("trimming", "Retrying clip with higher-quality game audio…");
                                arguments[arguments.IndexOf("-b:a") + 1] = "320k";
                                File.Delete(temp);
                                await BatchRecording.Execute(BatchRecording.FindTool("ffmpeg")!, arguments.ToArray(), cancellation.Token);
                                var retried = await BatchRecording.Probe(temp, cancellation.Token);
                                if (retried.Frames != (long)(clip.EndTick - clip.StartTick) * FramesPerTick ||Math.Abs(retried.Seconds - clip.DurationSeconds) > .05)
                                    throw new IOException("Retried highlight duration does not match its selected range.");
                                await VerifyFormat(temp, cancellation.Token);
                                delivery = await AudioDeliveryVerifier.VerifyAsync(cached?.File ?? prefix!, temp, sourceStartSample, sampleCount, cancellation.Token);
                            }
                            clip.AudioDelivery = JsonSerializer.SerializeToElement(new { schema = "mcc-audio-delivery/v1", verificationRevision = AudioDeliveryRevision, verified = true, source = "same native capture", sourceStartSample, sampleCount, measurement = delivery }, BatchRecording.Json);
                            await BatchRecording.Execute(BatchRecording.FindTool("ffmpeg")!, new[] { "-v", "error", "-xerror", "-nostdin", "-i", temp, "-f", "null", "-" }, cancellation.Token);
                            if(Plan.OutputFormat!="mp4")
                                final=await BatchRecording.DeliverContainer(temp,Plan.OutputFormat,folder,cancellation.Token);
                            else File.Move(temp, final, false);
                            clip.File = final; clip.Sha256 = BatchRecording.Hash(final); clip.State = "completed";
                            WriteManifest(clip); Publish("trimming", $"Saved {Path.GetFileName(final)}");
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(captions)) File.Delete(captions); }
                        Plan.Progress = Math.Min(.99, (completedGroups + .8 + .2 * (i + 1) / pending.Length) / groups.Length);
                    }
                    completedGroups++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    failed = true;
                    failureReason = $"{Path.GetFileName(group.Key)}: {error.Message}";
                    foreach (var clip in group.Where(c => c.State != "completed")) { clip.State = "failed"; clip.Error = error.Message; }
                    Publish("capturing", $"Failed {Path.GetFileName(group.Key)}: {error.Message}");
                    if (!Plan.ContinueOnError) break;
                    completedGroups++;
                }
                finally { active = null; cached?.Dispose(); if (prefix != null && File.Exists(prefix)) File.Delete(prefix); }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (failed || Plan.Clips.Any(c => c.State != "completed")) { Plan.Finished = true; Publish("failed", "Some source checks or clips did not finish. Completed clips are preserved; retry to resume. " + failureReason); return; }
            var combinedInputs = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", Plan.Clips.Select(c => c.Id + ":" + c.Sha256)))));
            if (Plan.Mode != "separate" && (Plan.CombinedInputsHash != combinedInputs || !await BatchRecording.ReusableCombined(Plan.CombinedFile, Plan.CombinedSha256, cancellation.Token)))
            {
                Publish("joining", "Joining selected highlights with chapters…");
                var joinPlan = new BatchPlan { Name = Plan.Name, OutputFormat = Plan.OutputFormat, OutputDirectory = Plan.OutputDirectory, Items = Plan.Clips.Select(c => new BatchItem { File = c.File, Name = c.Medal + " - " + c.Recorder, Frames = (c.EndTick - c.StartTick) * FramesPerTick, DurationSeconds = c.DurationSeconds }).ToList() };
                Plan.CombinedFile = await BatchRecording.Join(joinPlan, cancellation.Token); Plan.CombinedSha256 = BatchRecording.Hash(Plan.CombinedFile); Plan.CombinedInputsHash = combinedInputs;
            }
            Plan.Finished = true; Plan.Progress = 1; Publish("completed", $"Saved {Plan.Clips.Count} multikill clips" + (Plan.Mode == "separate" ? "." : " and the combined reel."));
        }
        catch (OperationCanceledException)
        {
            foreach (var c in Plan.Clips.Where(c => c.State != "completed" && c.State != "failed")) c.State = "cancelled";
            Plan.Finished = true; Publish("cancelled", "Cancelled. Completed clips are preserved; retry to continue.");
        }
        catch (Exception error) { Plan.Finished = true; Plan.State = "failed"; Plan.Message = error.Message; if (jobLock != null) Publish("failed", error.Message); else Changed?.Invoke(Plan); }
        finally { active = null; jobLock?.Dispose(); }
    }
    private int FramesPerTick => Plan.FrameRate / 60;
    private static string Seconds(int ticks) => (ticks / 60.0).ToString("R", CultureInfo.InvariantCulture);
    private static bool HasVerifiedAudioDelivery(HighlightClip clip) =>
        clip.AudioDelivery is JsonElement proof && proof.ValueKind == JsonValueKind.Object &&
        proof.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String && schema.GetString() == "mcc-audio-delivery/v1" &&
        proof.TryGetProperty("verificationRevision", out var revision) && revision.ValueKind == JsonValueKind.String && revision.GetString() == AudioDeliveryRevision &&
        proof.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True &&
        proof.TryGetProperty("sampleCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out long samples) && samples == (long)(clip.EndTick - clip.StartTick) * 800 &&
        proof.TryGetProperty("sourceStartSample", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out long first) && first == (long)clip.StartTick * 800 &&
        proof.TryGetProperty("measurement", out var measurement) && measurement.ValueKind == JsonValueKind.Object &&
        measurement.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "same-capture-pcm" &&
        measurement.TryGetProperty("analysisLowpassHz", out var band) && band.ValueKind == JsonValueKind.Number && band.TryGetInt32(out int cutoff) && cutoff == 4000 &&
        measurement.TryGetProperty("sampleRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetInt32(out int hz) && hz == 48000 &&
        measurement.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Number && channels.TryGetInt32(out int channelCount) && channelCount == 2 &&
        measurement.TryGetProperty("samplesVerified", out var covered) && covered.ValueKind == JsonValueKind.Number && covered.TryGetInt64(out long checkedSamples) && checkedSamples == samples &&
        measurement.TryGetProperty("windowsVerified", out var windows) && windows.ValueKind == JsonValueKind.Number && windows.TryGetInt32(out int windowCount) && windowCount > 0;
    private void WriteManifest(HighlightClip clip) => File.WriteAllText(clip.File + ".json", JsonSerializer.Serialize(new { schema = "mcc-highlight-clip/v1", renderer = "native-medals-v1", indexSchema = "mcc-film-events/v1", Plan.Fov, Plan.Format, Plan.OutputResolution, Plan.OutputQuality, Plan.Encoding, settingsHash = BatchRecording.Hash(Plan.SettingsSnapshot), clip }, BatchRecording.Json));
    private async Task VerifyFormat(string file, CancellationToken token, bool nativeCapture = false)
    {
        using var doc = JsonDocument.Parse(await BatchRecording.Execute(BatchRecording.FindTool("ffprobe")!, new[] { "-v", "error", "-show_streams", "-of", "json", file }, token));
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var v = streams.Single(s => s.GetProperty("codec_type").GetString() == "video"); var a = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
        int width = nativeCapture ? 2560 : Plan.Format.IsPortrait ? Plan.Format.Width : BatchRecording.VideoWidth(Plan.OutputResolution), height = nativeCapture ? 1440 : Plan.Format.IsPortrait ? Plan.Format.Height : Plan.OutputResolution;
        if (v.GetProperty("width").GetInt32() != width || v.GetProperty("height").GetInt32() != height || v.GetProperty("r_frame_rate").GetString() != $"{Plan.FrameRate}/1" || a.GetProperty("sample_rate").GetString() != "48000" || a.GetProperty("channels").GetInt32() != 2)
            throw new IOException($"Highlight media did not pass {width}×{height} / {Plan.FrameRate} fps / stereo game-audio validation.");
    }
    public async Task CancelAsync() { cancellation.Cancel(); if (active != null) await active.CancelAsync(); if (running != null) await running; }
    public void Dispose() { if (IsRunning) throw new InvalidOperationException("Cancel before disposing the highlight job."); cancellation.Dispose(); }
}
