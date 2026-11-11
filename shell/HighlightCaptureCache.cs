using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

// Verified, unformatted native H.264/AAC prefixes. Shared by preview/export
// jobs; a lease prevents eviction or replacement while FFmpeg reads a hit.
internal static class HighlightCaptureCache
{
    internal const long DefaultLimitBytes = 8L * 1024 * 1024 * 1024;
    private sealed record Entry(string Schema, string Key, long Frames, long Bytes, string Sha256, int StartTick = 0);
    internal sealed class Lease(FileStream stream, long frames, int startTick = 0) : IDisposable
    {
        public string File => stream.Name;
        public long Frames => frames;
        public int StartTick => startTick;
        public void Dispose() => stream.Dispose();
    }

    internal static void Invalidate(Lease lease)
    {
        string file = lease.File; lease.Dispose();
        Remove(file);
    }

    // Entry.Frames counts film ticks; a 120 fps prefix holds two encoded frames per tick.
    internal static string KeyFor(string film, string filmHash, string mccRoot, string settings, int fov, string backend, CancellationToken token, int frameRate = 60, string captureKey = "")
    {
        var engines = new[] { "halo3", "haloreach", "halo4", "groundhog" }
            .Select(title => Path.Combine(mccRoot, title, title + ".dll"))
            .Where(System.IO.File.Exists).ToDictionary(file => Path.GetFileName(file)!, file => Hash(file, token));
        var identity = JsonSerializer.Serialize(new
        {
            version = "native-ranged-1440p60-aac-v2", renderer = "native-medals-v1", capturePolicy = "isolated-legacy-audio-qpc-v2",
            film = Path.GetFullPath(film).ToUpperInvariant(), filmHash,
            mccRoot = Path.GetFullPath(mccRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(),
            settings = Hash(settings, token), fov, backend = Hash(backend, token),
            engines
        }) + (frameRate == 60 ? "" : "|fps:" + frameRate) + captureKey; // Default 60 fps / 50 Mbps keys stay identical to existing caches.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string Hash(string file, CancellationToken token)
    {
        using var input = System.IO.File.OpenRead(file);
        return Digest(input, token);
    }
    private static string Digest(Stream input, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024]; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        token.ThrowIfCancellationRequested(); return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static string Folder(string root, string key)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new IOException("Invalid footage cache identity.");
        return Path.Combine(Path.GetFullPath(root), key);
    }
    private static bool Link(string path) => (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static Lease? TryAcquire(string root, string key, long requiredFrames, CancellationToken token, int startTick = 0)
    {
        token.ThrowIfCancellationRequested();
        string folder = Folder(root, key);
        if (!Directory.Exists(folder) || Link(folder) || Link(Path.GetFullPath(root))) return null;
        // Prefer the smallest complete prefix covering the requested range.
        var candidates = new List<(string Path, Entry Data)>();
        foreach (string metadata in Directory.EnumerateFiles(folder, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(metadata), "N", out _)) continue;
            try
            {
                if (Link(metadata) || new FileInfo(metadata).Length > 4096) continue;
                var entry = JsonSerializer.Deserialize<Entry>(System.IO.File.ReadAllText(metadata));
                if (entry is { Schema: "mcc-footage-cache/v1", Sha256.Length: 64 } && entry.Key == key && entry.StartTick >= 0 && entry.StartTick <= startTick && entry.Frames >= requiredFrames - entry.StartTick && entry.Bytes > 0)
                    candidates.Add((metadata, entry));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        foreach (var candidate in candidates.OrderBy(c => c.Data.Frames))
        {
            FileStream? stream = null;
            try
            {
                string file = Path.ChangeExtension(candidate.Path, ".mp4");
                if (Link(file)) continue;
                stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != candidate.Data.Bytes || Digest(stream, token) != candidate.Data.Sha256) continue;
                try { System.IO.File.SetLastWriteTimeUtc(candidate.Path, DateTime.UtcNow); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                var lease = new Lease(stream, candidate.Data.Frames, candidate.Data.StartTick); stream = null; return lease;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            finally { stream?.Dispose(); }
        }
        return null;
    }

    // Only finalized prefixes that passed BackgroundRecording validation enter
    // this cache. Copy before committing, so a cache failure cannot lose export input.
    internal static bool Store(string root, string key, string source, long frames, CancellationToken token, long limitBytes = DefaultLimitBytes, int startTick = 0)
    {
        token.ThrowIfCancellationRequested();
        if (frames <= 0 || startTick < 0 || frames > long.MaxValue-startTick) throw new IOException("Cannot cache an invalid capture range.");
        string folder = Folder(root, key);
        long bytes = new FileInfo(source).Length;
        if (bytes <= 0 || bytes > limitBytes) return false;
        if (Directory.Exists(root) && Link(root)) throw new IOException("Footage cache folders cannot be links.");
        Directory.CreateDirectory(folder);
        if (Link(root) || Link(folder)) throw new IOException("Footage cache folders cannot be links.");
        // All participating stores/evictions use one short-lived lock. Readers
        // hold their media leases instead, allowing concurrent formatted exports.
        using var gate = new FileStream(Path.Combine(root, ".store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!Prune(root, limitBytes - bytes)) return false;
        string file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp4");
        string temporary = file + ".tmp", metadata = Path.ChangeExtension(file, ".json"), metadataTemp = metadata + ".tmp";
        bool committed = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            var entry = new Entry("mcc-footage-cache/v1", key, frames, bytes, Convert.ToHexString(hash.GetHashAndReset()), startTick);
            System.IO.File.WriteAllText(metadataTemp, JsonSerializer.Serialize(entry));
            System.IO.File.Move(temporary, file);
            System.IO.File.Move(metadataTemp, metadata); committed = true; return true;
        }
        finally
        {
            foreach (string path in new[] { temporary, metadataTemp, committed ? "" : file })
                if (path.Length > 0 && System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    private static bool Prune(string root, long keepBytes)
    {
        var files = new List<FileInfo>();
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (name.Length != 64 || !name.All(char.IsAsciiHexDigit) || Link(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*.mp4"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) && !Link(path)) files.Add(new(path));
        }
        long total = files.Sum(f => f.Length);
        foreach (var file in files.OrderBy(f => System.IO.File.Exists(Path.ChangeExtension(f.FullName, ".json")) ? System.IO.File.GetLastWriteTimeUtc(Path.ChangeExtension(f.FullName, ".json")) : f.LastWriteTimeUtc))
        {
            if (total <= keepBytes) return true;
            try
            {
                long length = file.Length;
                if (Remove(file.FullName)) total -= length;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return total <= keepBytes;
    }

    private static bool Remove(string file)
    {
        try
        {
            // Delete-on-close atomically excludes any current/future reader.
            using (var unused = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            System.IO.File.Delete(Path.ChangeExtension(file, ".json")); return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
