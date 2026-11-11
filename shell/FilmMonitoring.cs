using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed class MonitorSettings
{
    public bool Enabled { get; set; }
    public bool AutoExport { get; set; }
    public int MinimumKills { get; set; } = 2;
    // Applies to Halo 3 clips; other titles always export at 60 fps.
    public int FrameRate { get; set; } = 60;
    public int OutputResolution { get; set; } = 1080;
    public string OutputQuality { get; set; } = "high";
    public DateTime FirstEnabledUtc { get; set; }
}

internal sealed class MonitoredFilm
{
    public string Hash { get; set; } = "";
    public string Name { get; set; } = "";
    // FilmTitle.Id; records written before multi-title monitoring are Halo 3.
    public string Title { get; set; } = "halo3";
    public string Film { get; set; } = "";
    public DateTime FoundUtc { get; set; }
    public string State { get; set; } = "archived";
    public string Message { get; set; } = "Waiting to scan";
    public HighlightOptions Options { get; set; } = new() { MinimumKills = 2 };
    public List<HighlightClip> Clips { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public string JobPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public bool ExportRequested { get; set; }
}

internal sealed class MonitorData
{
    public string Schema { get; set; } = "mcc-monitor/v1";
    public MonitorSettings Settings { get; set; } = new();
    public List<MonitoredFilm> Films { get; set; } = new();
    public Dictionary<string, string> Receipts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

// Polling is intentional: reconciliation also handles missed notifications, sleep,
// source renames and folders that do not exist until MCC first saves a film.
// The caller serializes all access. Only Poll runs on a worker thread.
internal sealed class FilmMonitoring : IDisposable
{
    internal const long ArchiveLimit = 20L * 1024 * 1024 * 1024;
    private readonly string root;
    // Every Theater title the renderer supports, watched in MCC's per-title folders.
    private readonly (FilmTitle Title, string Autosave, string Movies)[] sources;
    private readonly FileStream owner;
    private readonly Dictionary<string, (string Stamp, DateTime Since)> observations = new(StringComparer.OrdinalIgnoreCase);
    public MonitorData Data { get; }
    public string Message { get; private set; } = "Monitoring is off.";
    public string Archive => Path.Combine(root, "Films");
    // Set by the UI before each poll; non-Halo 3 scans need map string tables.
    public string? MccRoot { get; set; }
    private string StatePath => Path.Combine(root, "monitor.json");
    internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(10);

    public FilmMonitoring(string root, string temporaryRoot)
    {
        this.root = Path.GetFullPath(root);
        sources = FilmTitle.All.Select(t => (t, Path.Combine(temporaryRoot, t.MovieFolderName, "autosave"),
            Path.Combine(temporaryRoot, "UserContent", t.MovieFolderName, "Movie"))).ToArray();
        Directory.CreateDirectory(root);
        owner = new FileStream(Path.Combine(root, "monitor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Data = File.Exists(StatePath) ? JsonSerializer.Deserialize<MonitorData>(File.ReadAllText(StatePath), BatchRecording.Json)
                ?? throw new IOException("The monitoring queue is empty or damaged.") : new();
            if (Data.Schema != "mcc-monitor/v1" || Data.Settings == null || Data.Films == null || Data.Receipts == null || Data.Films.Any(f => f == null))
                throw new IOException("The monitoring queue is unsupported or damaged. Its files have been preserved.");
            new HighlightOptions { MinimumKills = Data.Settings.MinimumKills }.Validate();
            if (Data.Settings.FrameRate is not (60 or 120)) Data.Settings.FrameRate = 60;
            // Custom CRF is not offered for automatic clips; fall back to the original default.
            try { BatchRecording.ValidateVideoProfile(Data.Settings.OutputResolution, Data.Settings.OutputQuality); if (Data.Settings.OutputQuality == "custom") throw new IOException(); }
            catch (IOException) { Data.Settings.OutputResolution = 1080; Data.Settings.OutputQuality = "high"; }
            Data.Receipts = new(Data.Receipts, StringComparer.OrdinalIgnoreCase);
            foreach (var film in Data.Films.Where(f => f.State == "exporting")) film.State = "ready";
        }
        catch { owner.Dispose(); throw; }
    }

    public void Save()
    {
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Data, BatchRecording.Json));
        File.Move(temp, StatePath, true);
    }

    public MonitorData Snapshot() => JsonSerializer.Deserialize<MonitorData>(JsonSerializer.Serialize(Data, BatchRecording.Json), BatchRecording.Json)!;

    public void Configure(bool enabled, bool autoExport, int minimumKills, DateTime now, int? frameRate = null, int? outputResolution = null, string? outputQuality = null)
    {
        new HighlightOptions { MinimumKills = minimumKills }.Validate();
        if (frameRate is not (null or 60 or 120)) throw new IOException("Choose 60 or 120 fps.");
        int resolution = outputResolution ?? Data.Settings.OutputResolution; string quality = outputQuality ?? Data.Settings.OutputQuality;
        BatchRecording.ValidateVideoProfile(resolution, quality);
        if (quality == "custom") throw new IOException("Automatic clips use Standard, High or Maximum quality.");
        Data.Settings.Enabled = enabled; Data.Settings.AutoExport = autoExport; Data.Settings.MinimumKills = minimumKills;
        if (frameRate != null) Data.Settings.FrameRate = frameRate.Value;
        Data.Settings.OutputResolution = resolution; Data.Settings.OutputQuality = quality;
        if (enabled && Data.Settings.FirstEnabledUtc == default) Data.Settings.FirstEnabledUtc = now;
        Save();
    }

    private static string Stamp(FileInfo file) => file.Length + ":" + file.LastWriteTimeUtc.Ticks;

    // A quiet file alone is not complete. Walk BLF chunks and verify the actual
    // EOF record, its absolute offset and permitted alignment bytes.
    internal static FilmTitle ValidateComplete(Stream input)
    {
        if (input.Length < 12 || input.Length > 4L * 1024 * 1024 * 1024) throw new IOException("Waiting for a complete Theater film.");
        var title = FilmTitle.ReadHeader(input).Title;
        input.Position = 0;
        var chunk = new byte[12]; bool header = false, data = false;
        for (int count = 0; count < 64 && input.Position + 12 <= input.Length; count++)
        {
            long offset = input.Position; input.ReadExactly(chunk);
            var tag = Encoding.ASCII.GetString(chunk, 0, 4);
            uint size = tag == "flmd" ? BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
            // Reach, Halo 4 and H2A store the update stream 4 bytes past flmd's size field.
            long extent = size + (tag == "flmd" && title != FilmTitle.Halo3 ? 4L : 0L);
            if (size < 12 || extent > input.Length - offset) throw new IOException("Waiting for complete film chunks.");
            if (tag == "flmh") { if (header) throw new IOException("Duplicate film header."); header = true; }
            if (tag == "flmd") { if (data || !header) throw new IOException("Invalid film data layout."); data = true; }
            if (tag == "_eof")
            {
                if (!header || !data) throw new IOException("Waiting for film data.");
                OtherFilmEventIndex.ValidateEnd(input, offset);
                return title;
            }
            input.Position = offset + extent;
        }
        throw new IOException("Waiting for the film's end marker.");
    }

    public void Poll(DateTime now, CancellationToken token)
    {
        if (!Data.Settings.Enabled) { Message = "Monitoring is off. Saved films and clips are kept."; return; }
        int waiting = 0, ingested = 0; string? issue = null;
        var paths = new List<string>();
        foreach (var (folder, pattern) in sources.SelectMany(s => new[] { (s.Autosave, "*.film"), (s.Movies, "*.mov") }))
        {
            try { if (Directory.Exists(folder)) paths.AddRange(Directory.EnumerateFiles(folder, pattern)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { issue = e.Message; }
        }
        var present = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in observations.Keys.Where(p => !present.Contains(p)).ToArray()) observations.Remove(path);
        foreach (var path in Data.Receipts.Keys.Where(p => !present.Contains(p)).ToArray()) Data.Receipts.Remove(path);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path); if (!info.Exists) continue;
                // Preserve current autosaves on first enable, but do not export a
                // user's entire historical Movie library unexpectedly.
                if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) && info.LastWriteTimeUtc < Data.Settings.FirstEnabledUtc) continue;
                string stamp = Stamp(info);
                if (Data.Receipts.TryGetValue(path, out var received) && received == stamp) continue;
                if (!observations.TryGetValue(path, out var seen) || seen.Stamp != stamp)
                { observations[path] = (stamp, now); waiting++; continue; }
                if (now - seen.Since < SettleTime || ingested >= 4) { waiting++; continue; }
                // This handle prevents writes/deletion for validation and copying,
                // never for the expensive event scan or rendering.
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    info.Refresh(); if (Stamp(info) != stamp) { observations.Remove(path); waiting++; continue; }
                    var title = ValidateComplete(source);
                    string hash = FilmEventIndex.ContentHash(source, token);
                    if (!Data.Films.Any(f => f.Hash == hash))
                    {
                        Directory.CreateDirectory(Archive);
                        long used = Directory.EnumerateFiles(Archive, "*.mov").Sum(f => new FileInfo(f).Length);
                        if (used + source.Length > ArchiveLimit || new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < source.Length + 1024L * 1024 * 1024)
                            throw new IOException("Film storage is full or less than 1 GB is free. Free space in the saved films folder to continue.");
                        string destination = Path.Combine(Archive, hash + ".mov");
                        string temp = destination + ".partial";
                        try
                        {
                            source.Position = 0;
                            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var buffer = new byte[65536]; int bytes;
                                while ((bytes = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, bytes); }
                                output.Flush(true);
                            }
                            using (var copied = File.OpenRead(temp))
                            {
                                ValidateComplete(copied);
                                if (FilmEventIndex.ContentHash(copied, token) != hash) throw new IOException("Film copy did not match its source.");
                            }
                            File.Move(temp, destination, true);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                        Data.Films.Add(new() { Hash = hash, Name = info.Name, Title = title.Id, Film = destination, FoundUtc = now,
                            Options = new() { MinimumKills = Data.Settings.MinimumKills } });
                        ingested++;
                    }
                    Data.Receipts[path] = stamp;
                    Save(); // Archive and identity survive a crash before indexing.
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { waiting++; issue = Path.GetFileName(path) + ": " + e.Message; }
        }
        foreach (var film in Data.Films.Where(f => f.State == "archived").Take(4))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var index = FilmEventIndex.Read(film.Film, Path.Combine(root, "FilmIndex"), token, null, MccRoot);
                film.Warnings.Clear(); film.Clips = HighlightPlanner.Plan(index, film.Options, film.Warnings);
                film.State = film.Clips.Count == 0 ? "no-matches" : "ready";
                film.Message = film.Clips.Count == 0 ? "No matching multikills" : $"Found {film.Clips.Count} clips";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException)
            { film.State = "scan-failed"; film.Message = e.Message; }
            Save();
        }
        Message = issue ?? (waiting > 0 ? $"Waiting for {waiting} film(s) to finish writing." :
            !sources.Any(s => Directory.Exists(s.Autosave)) ? "Waiting for MCC to create an autosave folder." :
            "Watching Theater autosaves for " + string.Join(", ", sources.Where(s => Directory.Exists(s.Autosave)).Select(s => s.Title.DisplayName)) + ". Keep this app open.");
    }

    public void Dispose() => owner.Dispose();
}
