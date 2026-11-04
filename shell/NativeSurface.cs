using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace MCCTheaterCapture;

internal sealed class NativeSurface : HwndHost
{
    private const int RenderWidth=2560,RenderHeight=1440;
    private IntPtr container, child;
    private Process? process;
    private bool stopping;
    private readonly object inputGate = new(), logGate = new();
    private readonly string executable, film, mccRoot, output;
    private readonly string? settingsIni;
    private readonly int fov;
    private string? backendFailure;
    public event Action<JsonElement>? NativeEvent;
    public event Action<string>? Failure;
    public event Action<IntPtr, IntPtr>? ChildAttached;
    public NativeSurface(string executable, string film, string mccRoot, string output, string? settingsIni=null, int fov=91)
    {
        this.executable = executable; this.film = film; this.mccRoot = mccRoot; this.output = output;
        this.settingsIni = settingsIni;
        this.fov = fov;
        Focusable = true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        container = CreateWindowEx(0, "static", "Native Halo viewport", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000,
            0, 0, RenderWidth, RenderHeight, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (container == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Dispatcher.BeginInvoke(StartBackend);
        return new HandleRef(this, container);
    }

    private void StartBackend()
    {
        if (stopping || container == IntPtr.Zero) return;
        try
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("The native replay backend has not been built.", executable);
            Directory.CreateDirectory(output);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            void Arg(string key, string value) { info.ArgumentList.Add(key); info.ArgumentList.Add(value); }
            Arg("--parent-hwnd", container.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Arg("--film", film); Arg("--mcc-root", mccRoot); Arg("--output", output);
            if(settingsIni!=null)Arg("--settings-ini",settingsIni);
            Arg("--fov",fov.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Arg("--width", RenderWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)); Arg("--height", RenderHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var outputClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) ReadEvent(e.Data); else outputClosed.TrySetResult(true); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog("backend-stderr.log", e.Data); };
            process.Exited += async (_, _) => {
                await outputClosed.Task;
                _ = Dispatcher.BeginInvoke(() => { if (!stopping) Failure?.Invoke(backendFailure ?? "The native host exited. Open the recording again to restart playback. Logs: " + output); });
            };
            if (!process.Start()) throw new IOException("Unable to start the native replay host.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            AppendLog("shell-process.jsonl", JsonSerializer.Serialize(new { type="started", processId=process.Id, executable, film, container=container.ToInt64(), utc=DateTimeOffset.UtcNow }));
        }
        catch (Exception error) { Failure?.Invoke(error.Message); }
    }

    private void ReadEvent(string line)
    {
        if (line.Length > 65536) return;
        AppendLog("backend-events.jsonl", line);
        try
        {
            using var parsed = JsonDocument.Parse(line);
            var value = parsed.RootElement.Clone();
            if(value.TryGetProperty("type",out var eventType)&&eventType.GetString()=="error"&&value.TryGetProperty("message",out var message)&&message.ValueKind==JsonValueKind.String)
                backendFailure=message.GetString();
            Dispatcher.BeginInvoke(() =>
            {
                if (stopping) return;
                if (value.TryGetProperty("type", out var type) && type.GetString() == "window" && value.TryGetProperty("hwnd", out var hwnd))
                {
                    long number = hwnd.ValueKind == JsonValueKind.String ? long.Parse(hwnd.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : hwnd.GetInt64();
                    var candidate = new IntPtr(number);
                    if (GetParent(candidate) == container) { child = candidate; SizeChild(); ChildAttached?.Invoke(container, child); }
                    else Failure?.Invoke("The native renderer did not attach to this viewer's viewport.");
                }
                NativeEvent?.Invoke(value);
            });
        }
        catch (JsonException) { }
        catch (Exception error) { AppendLog("shell-errors.log", error.ToString()); }
    }

    public void Command(object command)
    {
        lock (inputGate)
        {
            if (process == null || stopping) return;
            try { if (!process.HasExited) { process.StandardInput.WriteLine(JsonSerializer.Serialize(command)); process.StandardInput.Flush(); } }
            catch (Exception error) when (error is IOException or InvalidOperationException) { Failure?.Invoke("Native command channel closed: " + error.Message); }
        }
    }

    private void AppendLog(string name, string line)
    {
        try { lock (logGate) { Directory.CreateDirectory(output); File.AppendAllText(Path.Combine(output, name), line + Environment.NewLine); } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox) { base.OnWindowPositionChanged(rcBoundingBox); SizeChild(); }
    private void SizeChild()
    {
        if (container != IntPtr.Zero && child != IntPtr.Zero && GetClientRect(container, out var rect))
            SetWindowPos(child, IntPtr.Zero, 0, 0, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom), 0x0014);
    }

    public async Task StopAsync(bool finalizingCapture=false)
    {
        if (stopping) return;
        Command(new { command = "close" }); stopping = true;
        var owned = process;
        if (owned == null) return;
        try
        {
            owned.StandardInput.Close();
            if (!owned.HasExited)
            {
                var exited = owned.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(finalizingCapture?30000:5000)) != exited && !owned.HasExited) owned.Kill(false);
                await owned.WaitForExitAsync();
            }
            AppendLog("shell-process.jsonl", JsonSerializer.Serialize(new { type="stopped", processId=owned.Id, exitCode=owned.ExitCode, utc=DateTimeOffset.UtcNow }));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or Win32Exception) { AppendLog("shell-errors.log", error.ToString()); }
        finally { owned.Dispose(); process = null; }
    }

    protected override void DestroyWindowCore(HandleRef hwnd) { stopping = true; if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle); container = child = IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateWindowEx(int extended, string cls, string title, int style, int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
}
