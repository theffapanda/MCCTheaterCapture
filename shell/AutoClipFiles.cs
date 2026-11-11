using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MCCTheaterCapture;

// File manager for the Auto clipping tab: lists every clip under the
// "Automatic clips" folder and lets the user open, reveal, rename or recycle it.
internal sealed partial class ViewerWindow
{
    private static readonly string[] AutoClipExtensions = { ".mp4", ".mov" };
    private string AutoClipRoot => Path.Combine(preferences.BatchOutputRoot, "Automatic clips");

    private object AutoClipListing()
    {
        string root = AutoClipRoot;
        string? busy = monitorRecording?.Plan.OutputDirectory;
        var files = new List<(DateTime modified, object file)>();
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!AutoClipExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                // Files the monitor doesn't track (renamed outside the app, older exports) have no record.
                MonitoredFilm? owner = null; HighlightClip? record = null;
                foreach (var f in filmMonitor?.Data.Films ?? new())
                {
                    record = f.Clips?.FirstOrDefault(c => string.Equals(c.File, path, StringComparison.OrdinalIgnoreCase));
                    if (record != null) { owner = f; break; }
                }
                bool exporting = busy != null && string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), busy.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                files.Add((info.LastWriteTimeUtc, new
                {
                    path, name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path),
                    folder = Path.GetRelativePath(root, Path.GetDirectoryName(path) ?? root),
                    size = info.Length, modifiedUtc = info.LastWriteTimeUtc, exporting,
                    medal = NullIfEmpty(record?.Medal), map = NullIfEmpty(record?.Map), film = owner?.Name, titleName = owner == null ? null : FilmTitle.All.FirstOrDefault(t => t.Id == owner.Title)?.DisplayName, durationSeconds = record?.DurationSeconds
                }));
            }
        }
        return new { root, files = files.OrderByDescending(f => f.modified).Select(f => f.file).ToList() };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private string AutoClipPath(JsonElement request)
    {
        string root = Path.GetFullPath(AutoClipRoot).TrimEnd('\\') + "\\";
        string path = Path.GetFullPath(request.GetProperty("path").GetString() ?? "");
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !AutoClipExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("That file is not in the Automatic clips folder.");
        if (!File.Exists(path)) throw new IOException("This clip is no longer available. It may have been moved or deleted.");
        return path;
    }

    private void RequireAutoClipIdle(string path)
    {
        string? busy = monitorRecording?.Plan.OutputDirectory;
        if (busy != null && string.Equals(Path.GetDirectoryName(path)?.TrimEnd('\\'), busy.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("This film's clips are still exporting. Try again when the export finishes.");
    }

    private async Task<object?> AutoClipMessage(JsonElement request)
    {
        string type = request.GetProperty("type").GetString() ?? "";
        if (type == "autoclips-list") return AutoClipListing();
        if (type == "autoclips-folder")
        {
            Directory.CreateDirectory(AutoClipRoot);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { AutoClipRoot } });
            return null;
        }
        string path = AutoClipPath(request);
        switch (type)
        {
#if !NATIVE_UI_BETA
            case "autoclips-watch":
                return new { url = AutoClipStreamUrl(path) };
#endif
            case "autoclips-open":
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return null;
            case "autoclips-reveal":
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", path } }); return null;
            case "autoclips-rename":
            {
                RequireAutoClipIdle(path);
                string name = (request.GetProperty("name").GetString() ?? "").Trim();
                if (name.Length == 0 || name.Length > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name is "." or "..")
                    throw new IOException("Use a name of up to 120 characters without \\ / : * ? \" < > |.");
                string target = Path.Combine(Path.GetDirectoryName(path)!, name + Path.GetExtension(path));
                if (string.Equals(target, path, StringComparison.Ordinal)) return AutoClipListing();
                if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                    throw new IOException("A clip with that name already exists in this folder.");
                await monitorGate.WaitAsync();
                try
                {
                    File.Move(path, target);
                    // Keep the clip's metadata sidecar (clip.mp4.json) next to the video.
                    try { if (File.Exists(path + ".json") && !File.Exists(target + ".json")) File.Move(path + ".json", target + ".json"); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    UpdateMonitorClipFile(path, target);
                }
                finally { monitorGate.Release(); }
                PublishMonitoring();
                return AutoClipListing();
            }
            case "autoclips-delete":
            {
                RequireAutoClipIdle(path);
                await monitorGate.WaitAsync();
                try
                {
                    RecycleFile(path);
                    if (File.Exists(path + ".json")) { try { RecycleFile(path + ".json"); } catch (IOException) { } }
                    UpdateMonitorClipFile(path, "");
                }
                finally { monitorGate.Release(); }
                PublishMonitoring();
                return AutoClipListing();
            }
            default: throw new IOException("Unknown clip request.");
        }
    }

#if !NATIVE_UI_BETA
    // The in-app player streams clips from a private host that this window answers
    // itself (WebResourceRequested). Each request reads only the byte range asked
    // for and closes the file, so playback never locks a clip against rename/delete.
    internal const string AutoClipHost = "mcc-clips.local";
    private const int AutoClipChunkBytes = 4 * 1024 * 1024;

    private string AutoClipStreamUrl(string path)
    {
        string root = Path.GetFullPath(AutoClipRoot).TrimEnd('\\');
        string relative = Path.GetRelativePath(root, path);
        string encoded = string.Join("/", relative.Split('\\', '/').Select(Uri.EscapeDataString));
        return $"https://{AutoClipHost}/{encoded}";
    }

    private void RegisterAutoClipStreaming(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter($"https://{AutoClipHost}/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, AutoClipHost, StringComparison.OrdinalIgnoreCase)) return;
            e.Response = AutoClipResponse(core.Environment, uri, e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null);
        };
    }

    private Microsoft.Web.WebView2.Core.CoreWebView2WebResourceResponse AutoClipResponse(Microsoft.Web.WebView2.Core.CoreWebView2Environment environment, Uri uri, string? range)
    {
        try
        {
            string root = Path.GetFullPath(AutoClipRoot).TrimEnd('\\') + "\\";
            string relative = string.Join("\\", uri.AbsolutePath.TrimStart('/').Split('/').Select(Uri.UnescapeDataString));
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !AutoClipExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
                return environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            string type = Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime" : "video/mp4";
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long total = file.Length, start = 0, end = total - 1;
            bool partial = false;
            if (range != null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = range[6..].Split(',')[0].Split('-');
                if (parts[0].Length == 0 && long.TryParse(parts[1], out long suffix)) { start = Math.Max(0, total - suffix); }
                else if (long.TryParse(parts[0], out long from)) { start = from; if (parts.Length > 1 && long.TryParse(parts[1], out long to)) end = Math.Min(to, total - 1); }
                if (start >= total || start > end)
                    return environment.CreateWebResourceResponse(null, 416, "Range Not Satisfiable", $"Content-Range: bytes */{total}");
                partial = true;
            }
            // Cap each answer; the media element asks again for the rest.
            if (partial) end = Math.Min(end, start + AutoClipChunkBytes - 1);
            else if (total > AutoClipChunkBytes) { partial = true; end = AutoClipChunkBytes - 1; }
            var buffer = new byte[end - start + 1];
            file.Seek(start, SeekOrigin.Begin);
            int read = 0;
            while (read < buffer.Length) { int n = file.Read(buffer, read, buffer.Length - read); if (n <= 0) break; read += n; }
            end = start + read - 1;
            string headers = $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {read}\r\nCache-Control: no-store";
            if (partial) headers += $"\r\nContent-Range: bytes {start}-{end}/{total}";
            return environment.CreateWebResourceResponse(new MemoryStream(buffer, 0, read, false), partial ? 206 : 200, partial ? "Partial Content" : "OK", headers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
    }
#endif

    // Keep the monitor's saved-clip records pointing at the renamed file (or
    // drop the link after a delete) so the Highlights library stays accurate.
    private void UpdateMonitorClipFile(string from, string to)
    {
        if (filmMonitor == null) return;
        bool changed = false;
        foreach (var clip in filmMonitor.Data.Films.SelectMany(f => f.Clips))
            if (string.Equals(clip.File, from, StringComparison.OrdinalIgnoreCase)) { clip.File = to; changed = true; }
        if (!changed) return;
        filmMonitor.Save(); monitorSnapshot = filmMonitor.Snapshot();
    }

    private static void RecycleFile(string path)
    {
        var operation = new ShFileOperation { Func = 3 /* FO_DELETE */, From = path + "\0\0", Flags = 0x0040 | 0x0010 | 0x0400 | 0x0004 /* ALLOWUNDO | NOCONFIRMATION | NOERRORUI | SILENT */ };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.AnyOperationsAborted) throw new IOException($"Couldn't move the clip to the Recycle Bin (error {result}). Close any player using it and try again.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public IntPtr Hwnd; public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref ShFileOperation operation);
}
