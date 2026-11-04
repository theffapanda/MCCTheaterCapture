using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MCCTheaterCapture;

internal sealed record ToolResourceUsage(double? CpuPercent, double? GpuPercent);

// One sampler per window. Includes this app, WebView, renderer and encoder children.
internal sealed class ToolResourceMonitor : IDisposable
{
    private IntPtr query, gpu;
    private bool primed;
    private readonly Dictionary<int, (long Start, double Cpu)> previous = new();
    private long lastSample = Stopwatch.GetTimestamp();

    public ToolResourceMonitor()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out query) == 0)
            PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out gpu);
    }

    public ToolResourceUsage Sample()
    {
        var ids = ProcessTree();
        if (ids == null) return new(null, null);
        var now = Stopwatch.GetTimestamp();
        double seconds = (now - lastSample) / (double)Stopwatch.Frequency, cpu = 0;
        bool cpuAvailable = true;
        var next = new Dictionary<int, (long Start, double Cpu)>();
        foreach (int id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                long start = process.StartTime.ToUniversalTime().Ticks;
                double used = process.TotalProcessorTime.TotalSeconds;
                next[id] = (start, used);
                if (previous.TryGetValue(id, out var old) && old.Start == start) cpu += Math.Max(0, used - old.Cpu);
                else if (primed) cpu += Math.Max(0, used); // Child born during this interval.
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { cpuAvailable = false; }
        }
        double? cpuPercent = primed && cpuAvailable && seconds > 0 ? Math.Clamp(cpu / seconds / Environment.ProcessorCount * 100, 0, 100) : null;
        previous.Clear(); foreach (var pair in next) previous.Add(pair.Key, pair.Value);
        lastSample = now;
        double? gpuPercent = ReadGpu(ids);
        primed = true;
        return new(cpuPercent, gpuPercent);
    }

    private double? ReadGpu(HashSet<int> ids)
    {
        if (gpu == IntPtr.Zero || PdhCollectQueryData(query) != 0 || !primed) return null;
        uint bytes = 0, count = 0;
        const uint format = 0x200; // PDH_FMT_DOUBLE
        uint result = PdhGetFormattedCounterArrayW(gpu, format, ref bytes, out count, IntPtr.Zero);
        if (result != 0x800007D2 || bytes == 0) return null; // PDH_MORE_DATA
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArrayW(gpu, format, ref bytes, out count, buffer) != 0) return null;
            var engines = new Dictionary<string, double>();
            bool valid = false;
            int size = Marshal.SizeOf<CounterItem>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                if (item.Status > 1 || !double.IsFinite(item.Value)) continue;
                valid = true;
                int end = name.IndexOf('_', 4);
                if (!name.StartsWith("pid_") || end < 0 || !int.TryParse(name.AsSpan(4, end - 4), out int pid) || !ids.Contains(pid)) continue;
                // Sum owned processes on each physical engine, then report the busiest engine.
                string engine = name[(end + 1)..];
                engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, item.Value);
            }
            return valid ? Math.Clamp(engines.Values.DefaultIfEmpty(0).Max(), 0, 100) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static HashSet<int>? ProcessTree()
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            var parents = new Dictionary<int, int>();
            if (!Process32FirstW(snapshot, ref entry)) return null;
            do { parents[(int)entry.Id] = (int)entry.ParentId; } while (Process32NextW(snapshot, ref entry));
            var ids = new HashSet<int> { Environment.ProcessId };
            bool added;
            do { added = false; foreach (var pair in parents) if (ids.Contains(pair.Value)) added |= ids.Add(pair.Key); } while (added);
            return ids;
        }
        finally { CloseHandle(snapshot); }
    }

    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = gpu = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem { [FieldOffset(0)] public IntPtr Name; [FieldOffset(8)] public uint Status; [FieldOffset(16)] public double Value; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, ParentId; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
