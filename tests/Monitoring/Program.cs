using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MCCTheaterCapture;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (IOException) { Check(true, message); return; } throw new Exception("Expected rejection: " + message); }
    private static byte[] Fixture() => Fixture(FilmTitle.Halo3);
    private static byte[] Fixture(FilmTitle title)
    {
        // Structurally finalized but deliberately lacks valid event records.
        // Reach/H4/H2A update streams extend 4 bytes past flmd's size field.
        int data = 48 + title.HeaderBytes, end = data + 12 + (title == FilmTitle.Halo3 ? 0 : 4);
        byte[] bytes = new byte[end + 17 + 11];
        void Chunk(int at, string tag, int size, ushort major = 1, ushort minor = 1)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(bytes, at);
            if (tag == "flmd") BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), size);
            else BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 4), size);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at + 8), major);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at + 10), minor);
        }
        Chunk(0, "_blf", 48); Chunk(48, "flmh", title.HeaderBytes, title.Major, title.Minor);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48 + title.TickOffset), 1);
        Chunk(data, "flmd", 12); Chunk(end, "_eof", 17);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(end + 12), end);
        return bytes;
    }
    public static void Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--validate")
        {
            // Check real MCC films: prints the detected title or why it isn't complete.
            foreach (var file in args.Skip(1))
                try { using var s = File.OpenRead(file); Console.WriteLine($"OK   {FilmMonitoring.ValidateComplete(s).DisplayName,-20} {file}"); }
                catch (Exception e) { Console.WriteLine($"FAIL {e.Message,-20} {file}"); }
            return;
        }
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "monitor-tests-" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(root, "Temporary"), store = Path.Combine(root, "Store");
        Directory.CreateDirectory(Path.Combine(temp, "Halo3", "autosave"));
        Directory.CreateDirectory(Path.Combine(temp, "UserContent", "Halo3", "Movie"));
        DateTime now = DateTime.UtcNow.AddMinutes(-1);
        var bytes = args.Length == 1 ? File.ReadAllBytes(args[0]) : Fixture();
        string source = Path.Combine(temp, "Halo3", "autosave", "match.film");
        File.WriteAllBytes(source, bytes);
        using (var stream = new MemoryStream(bytes)) FilmMonitoring.ValidateComplete(stream);
        Check(true, "Finalized Halo 3 BLF and alignment accepted");
        foreach (var other in new[] { FilmTitle.Reach, FilmTitle.Halo4, FilmTitle.Halo2A })
        {
            var film = Fixture(other);
            using (var stream = new MemoryStream(film)) Check(FilmMonitoring.ValidateComplete(stream) == other, $"Finalized {other.DisplayName} BLF accepted and identified");
            int otherEnd = film.AsSpan().LastIndexOf(Encoding.ASCII.GetBytes("_eof"));
            Reject(() => FilmMonitoring.ValidateComplete(new MemoryStream(film[..otherEnd])), $"Unfinished {other.DisplayName} film is rejected");
        }
        var eof = Encoding.ASCII.GetBytes("_eof");
        int end = bytes.AsSpan().LastIndexOf(eof);
        Reject(() => FilmMonitoring.ValidateComplete(new MemoryStream(bytes[..end])), "Quiet film without EOF is rejected");
        var invalid = bytes.ToArray(); invalid[end + 12] ^= 1;
        Reject(() => FilmMonitoring.ValidateComplete(new MemoryStream(invalid)), "Incorrect EOF offset is rejected");
        invalid = bytes.ToArray(); invalid[^1] = 42;
        Reject(() => FilmMonitoring.ValidateComplete(new MemoryStream(invalid)), "Nonzero trailing padding is rejected");
        string hash;
        using (var monitor = new FilmMonitoring(store, temp))
        {
            Reject(() => { using var other = new FilmMonitoring(store, temp); }, "Only one monitor owns a queue");
            monitor.Poll(now, default); Check(monitor.Data.Films.Count == 0, "Disabled monitoring does not ingest");
            monitor.Configure(true, false, 2, now);
            monitor.Poll(now, default); Check(monitor.Data.Films.Count == 0, "First observation waits for stability");
            using (var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { monitor.Poll(now.AddSeconds(11), default); Check(monitor.Data.Films.Count == 0, "Writer lock prevents ingestion"); }
            File.WriteAllBytes(source, bytes[..end]);
            monitor.Poll(now.AddSeconds(12), default); monitor.Poll(now.AddSeconds(24), default);
            Check(monitor.Data.Films.Count == 0, "Stable unfinished autosave remains pending");
            File.WriteAllBytes(source, bytes);
            monitor.Poll(now.AddSeconds(25), default); monitor.Poll(now.AddSeconds(36), default);
            Check(monitor.Data.Films.Count == 1, "Finalized autosave preserved once");
            var film = monitor.Data.Films.Single(); hash = film.Hash;
            Check(File.ReadAllBytes(film.Film).SequenceEqual(bytes), "Archive exactly matches original bytes");
            Check(File.ReadAllBytes(source).SequenceEqual(bytes), "Original autosave untouched");
            if (args.Length == 1) Check(film.State == "ready" && film.Clips.Count == 2, "Real autosave produces two Double Kill clips");
            else Check(film.State == "scan-failed", "Invalid event payload is visible and not exported");
            string movie = Path.Combine(temp, "UserContent", "Halo3", "Movie", "different-name.mov");
            File.WriteAllBytes(movie, bytes);
            monitor.Poll(now.AddSeconds(37), default); monitor.Poll(now.AddSeconds(48), default);
            Check(monitor.Data.Films.Count == 1 && monitor.Data.Receipts.Count == 2, "Renamed Movie copy deduplicates by content");
            monitor.Configure(true, true, 4, now);
            Check(film.Options.MinimumKills == 2, "Rule changes do not alter existing queued clips");
            Check(film.Title == "halo3", "Halo 3 autosave is tagged with its title");
            Directory.CreateDirectory(Path.Combine(temp, "HaloReach", "autosave"));
            File.WriteAllBytes(Path.Combine(temp, "HaloReach", "autosave", "reach.film"), Fixture(FilmTitle.Reach));
            monitor.Poll(now.AddSeconds(49), default); monitor.Poll(now.AddSeconds(60), default);
            var reach = monitor.Data.Films.SingleOrDefault(f => f.Title == "haloreach");
            Check(reach != null && monitor.Data.Films.Count == 2, "Reach autosave is preserved from its own folder");
            Check(reach!.State == "scan-failed", "Reach film without a map folder reports a visible scan error");
            monitor.Data.Films.Remove(reach); monitor.Save();
            film.State = "exporting"; film.JobPath = "checkpoint.json"; monitor.Save();
        }
        using (var monitor = new FilmMonitoring(store, temp))
        {
            Check(monitor.Data.Settings.AutoExport && monitor.Data.Settings.MinimumKills == 4, "Settings survive restart");
            var film = monitor.Data.Films.Single();
            Check(film.Hash == hash && film.State == "ready" && film.JobPath == "checkpoint.json", "Interrupted job resumes same checkpoint");
            monitor.Poll(now.AddSeconds(60), default);
            Check(monitor.Data.Films.Count == 1, "Restart does not rescan or duplicate sources");
            film.State = "exported"; monitor.Save();
        }
        using (var monitor = new FilmMonitoring(store, temp))
            Check(monitor.Data.Films.Single().State == "exported", "Completed job stays completed after restart");
        File.WriteAllText(Path.Combine(store, "monitor.json"), "{}");
        // A valid empty JSON object uses defaults; malformed JSON must be preserved.
        File.WriteAllText(Path.Combine(store, "monitor.json"), "{broken");
        try { using var bad = new FilmMonitoring(store, temp); throw new Exception("Expected invalid JSON rejection"); }
        catch (JsonException) { Check(File.ReadAllText(Path.Combine(store, "monitor.json")) == "{broken", "Damaged queue is not overwritten"); }
        Console.WriteLine($"{checks} checks passed. Evidence: {root}");
    }
}

namespace MCCTheaterCapture
{
    internal static class BatchRecording
    {
        internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
        // Mirrors shell/BatchRecording.cs so monitoring settings validate the same way.
        internal static void ValidateVideoProfile(int resolution, string quality)
        {
            if (resolution is not (720 or 1080 or 1440 or 2160) || quality is not ("standard" or "high" or "maximum" or "custom"))
                throw new IOException("Choose 720p, 1080p, 1440p or 4K, and Standard, High, Maximum or Custom quality.");
        }
    }
}
