using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed record AudioDeliveryVerification(int SampleRate, int Channels, long SamplesVerified,
    int WindowsVerified, int StrongWindows, int QuietWindows, double MinimumCorrelation,
    double MaximumRmsError, string Method = "same-capture-pcm", int AnalysisLowpassHz = 4000,
    double InteriorCorrelationLimit = .98, double BoundaryCorrelationLimit = .90,
    double InteriorRelativeRmsLimit = .20, double BoundaryRelativeRmsLimit = .40,
    double MinimumInteriorCorrelation = 1, double MinimumBoundaryCorrelation = 1);

internal static class AudioDeliveryVerifier
{
    private const int Rate = 48000, Channels = 2, WindowFrames = Rate / 2, HopFrames = WindowFrames / 2;
    // Keep the 48 kHz sample grid, but compare a phase-preserving 4 kHz
    // low-pass timing band. AAC can regenerate high-frequency noise; lowering
    // correlation thresholds to accommodate that noise would weaken detection.
    private const double MinimumCorrelation = .98, MaximumRelativeRmsError = .20;
    // Below -80 dBFS, compare bounded sample error rather than inventing a
    // correlation or an independent timing anchor for effectively quiet data.
    private const double QuietRms = .0001, QuietError = 2.0 / 32768;

    public static async Task<AudioDeliveryVerification> VerifyAsync(string sourceFile, string outputFile,
        long sourceStartSample, long sampleCount, CancellationToken token)
    {
        if (sourceStartSample < 0 || sampleCount <= 0 || sourceStartSample > long.MaxValue / 8 || sampleCount > long.MaxValue / 8)
            throw new IOException("Invalid captured-audio sample range.");
        token.ThrowIfCancellationRequested();
        string ffmpeg = BatchRecording.FindTool("ffmpeg") ?? throw new IOException("Audio delivery verification requires ffmpeg.exe.");
        string ffprobe = BatchRecording.FindTool("ffprobe") ?? throw new IOException("Audio delivery verification requires ffprobe.exe.");
        await CheckLogicalRange(ffprobe, sourceFile, checked(sourceStartSample + sampleCount), false, token);
        await CheckLogicalRange(ffprobe, outputFile, sampleCount, true, token);
        await using var source = new Decoder(ffmpeg, sourceFile, token);
        await using var output = new Decoder(ffmpeg, outputFile, token);
        await source.SkipFrames(sourceStartSample, token);
        float[] expected = new float[WindowFrames * Channels], actual = new float[expected.Length];
        float[] rawExpected = new float[expected.Length], rawActual = new float[expected.Length];
        int filled = (int)Math.Min(sampleCount, WindowFrames);
        await source.ReadFrames(expected, rawExpected, 0, filled, token);
        await output.ReadFrames(actual, rawActual, 0, filled, token);
        int windows = 0, strong = 0, quiet = 0;
        double minimum = 1, minimumInterior = 1, minimumBoundary = 1, maximumError = 0;
        void Compare(int offset, int count, long firstSample, bool boundary = false)
        {
            bool audible = false;
            for (int channel = 0; channel < Channels; channel++)
            {
                double sa = 0, sb = 0, aa = 0, bb = 0, ab = 0, error = 0, rawAa = 0, rawBb = 0;
                for (int frame = 0; frame < count; frame++)
                {
                    double a = expected[(offset + frame) * Channels + channel], b = actual[(offset + frame) * Channels + channel];
                    if (!double.IsFinite(a) || !double.IsFinite(b)) throw new IOException("Decoded audio contains a non-finite sample.");
                    sa += a; sb += b; aa += a * a; bb += b * b; ab += a * b; error += (a - b) * (a - b);
                    double rawA = rawExpected[(offset + frame) * Channels + channel], rawB = rawActual[(offset + frame) * Channels + channel];
                    rawAa += rawA * rawA; rawBb += rawB * rawB;
                }
                double rms = Math.Sqrt(aa / count), rmsError = Math.Sqrt(error / count);
                double rawRms = Math.Sqrt(rawAa / count), rawOutputRms = Math.Sqrt(rawBb / count);
                if (rawRms >= QuietRms && rms < Math.Max(QuietRms, rawRms * .01))
                    throw Mismatch(firstSample, channel, "insufficient in-band timing content; high-frequency-only audio is not verified silence");
                if (rawRms < QuietRms && rawOutputRms > QuietRms + QuietError)
                    throw Mismatch(firstSample, channel, "audible content appeared in captured silence");
                maximumError = Math.Max(maximumError, rmsError);
                double varianceA = Math.Max(0, aa - sa * sa / count), varianceB = Math.Max(0, bb - sb * sb / count);
                bool lowInformation = rms < QuietRms || varianceA / count < 1e-14;
                if (lowInformation)
                {
                    if (rmsError > QuietError + rms * .1) throw Mismatch(firstSample, channel, "quiet captured content changed");
                    continue;
                }
                audible = true;
                double correlation = varianceB > 0 ? (ab - sa * sb / count) / Math.Sqrt(varianceA * varianceB) : 0;
                correlation = Math.Clamp(correlation, -1, 1);
                double gain = (ab - sa * sb / count) / varianceA;
                minimum = Math.Min(minimum, correlation);
                if (boundary) minimumBoundary = Math.Min(minimumBoundary, correlation); else minimumInterior = Math.Min(minimumInterior, correlation);
                // AAC startup/flush reconstruction has less context at clip
                // edges. Actual nonperiodic codec fixtures measure ~.961 in
                // the first 100 ms while their full windows exceed .991.
                double correlationLimit = boundary ? .90 : MinimumCorrelation;
                double errorLimit = boundary ? .40 : MaximumRelativeRmsError;
                if (correlation < correlationLimit || rmsError > rms * errorLimit || gain < .80 || gain > 1.20)
                    throw Mismatch(firstSample, channel, $"captured samples shifted or changed (correlation {correlation:F6}, RMS error {rmsError:G5})");
            }
            windows++; if (audible) strong++; else quiet++;
        }
        Compare(0, filled, 0);
        // Short boundary windows prevent a damaged beginning/end from being
        // diluted by a longer, correctly delivered neighboring interval.
        Compare(0, Math.Min(filled, Rate / 10), 0, true);
        long consumed = filled;
        while (consumed < sampleCount)
        {
            token.ThrowIfCancellationRequested();
            int retained = Math.Min(filled, WindowFrames - HopFrames);
            Array.Copy(expected, (filled - retained) * Channels, expected, 0, retained * Channels);
            Array.Copy(actual, (filled - retained) * Channels, actual, 0, retained * Channels);
            Array.Copy(rawExpected, (filled - retained) * Channels, rawExpected, 0, retained * Channels);
            Array.Copy(rawActual, (filled - retained) * Channels, rawActual, 0, retained * Channels);
            int read = (int)Math.Min(sampleCount - consumed, WindowFrames - retained);
            await source.ReadFrames(expected, rawExpected, retained, read, token);
            await output.ReadFrames(actual, rawActual, retained, read, token);
            consumed += read; filled = retained + read;
            Compare(0, filled, consumed - filled);
        }
        int boundary = Math.Min(filled, Rate / 10);
        Compare(filled - boundary, boundary, consumed - boundary, true);
        return new(Rate, Channels, sampleCount, windows, strong, quiet, minimum, maximumError,
            MinimumInteriorCorrelation: minimumInterior, MinimumBoundaryCorrelation: minimumBoundary);
    }

    private static IOException Mismatch(long firstSample, int channel, string reason) =>
        new($"Audio delivery verification failed at {firstSample / (double)Rate:F3}s, channel {channel + 1}: {reason}.");

    private static async Task CheckLogicalRange(string executable, string file, long endSample, bool exact, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels,start_time,duration_ts,time_base", "-of", "json", file }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot inspect audio sample coverage.");
        using var cancellation = token.Register(() => Stop(process));
        try
        {
            var output = ReadText(process.StandardOutput, 32768);
            var error = ReadText(process.StandardError, 8192);
            await process.WaitForExitAsync(token);
            string json = await output, detail = await error;
            if (process.ExitCode != 0) throw new IOException("Cannot inspect audio sample coverage: " + detail.Trim());
            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() != 1) throw new IOException("Audio delivery requires one readable audio stream.");
            var stream = streams[0];
            string Value(string key) => stream.TryGetProperty(key, out var value) ? value.ToString() : "";
            string[] clock = Value("time_base").Split('/');
            if (Value("sample_rate") != "48000" || Value("channels") != "2" ||
                !decimal.TryParse(Value("start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) || Math.Abs(start) > 1m / Rate ||
                !long.TryParse(Value("duration_ts"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks < 0 || clock.Length != 2 ||
                !long.TryParse(clock[0], out var numerator) || numerator <= 0 || !long.TryParse(clock[1], out var denominator) || denominator <= 0)
                throw new IOException("Audio delivery requires a zero-origin stereo 48 kHz sample timeline.");
            decimal available;
            try { available = checked((decimal)ticks * numerator * Rate / denominator); }
            catch (OverflowException failure) { throw new IOException("Audio sample timeline exceeds supported bounds.", failure); }
            // Codec packets may contain padding after their logical edit. Do
            // not mistake those decoded padding samples for a missing tail.
            if (available < endSample) throw new IOException($"Audio logical timeline ends {endSample - available:0.###} samples before its required range. AAC padding cannot replace captured samples.");
            if (exact && available > endSample + 1) throw new IOException("Delivered audio logical timeline extends past the selected clip.");
        }
        catch (JsonException failure) { throw new IOException("Invalid audio timeline metadata.", failure); }
        finally { Stop(process); }
    }

    private static async Task<string> ReadText(StreamReader reader, int limit)
    {
        var result = new StringBuilder(); char[] block = new char[2048]; int count;
        while ((count = await reader.ReadAsync(block)) != 0) if (result.Length < limit) result.Append(block, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
    private static void Stop(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }

    private sealed class Decoder : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        private readonly CancellationTokenRegistration cancellation;
        private readonly byte[] bytes = new byte[WindowFrames * Channels * sizeof(float)];
        private readonly Lowpass[] filters = { new(), new(), new(), new() };

        public Decoder(string executable, string file, CancellationToken token)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Decode from the physical beginning, including the container's
            // priming edit. Integer sample skipping is independent of the trim
            // encoder's input-side -ss implementation and fractional time text.
            foreach (string argument in new[] { "-hide_banner", "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-i", file,
                "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "48000", "-af", "aresample=48000:async=1:min_hard_comp=0:first_pts=0", "-f", "f32le", "pipe:1" }) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new IOException("Cannot start captured-audio verification decoder.");
            errors = ReadText(process.StandardError, 8192);
            cancellation = token.Register(Kill);
        }

        private void Kill() => Stop(process);
        private async Task ReadBytes(int count, CancellationToken token)
        {
            int read = 0;
            while (read < count)
            {
                int next = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(read, count - read), token);
                if (next == 0)
                {
                    await process.WaitForExitAsync(token);
                    string detail = await errors;
                    throw new IOException(process.ExitCode != 0 ? "Audio verification decoder failed: " + detail.Trim() : "Captured or delivered audio ends before the complete requested sample range.");
                }
                read += next;
            }
        }
        public async Task SkipFrames(long frames, CancellationToken token)
        {
            long remaining = checked(frames * Channels * sizeof(float));
            while (remaining > 0) { int read = (int)Math.Min(remaining, bytes.Length); await ReadBytes(read, token); remaining -= read; }
        }
        public async Task ReadFrames(float[] destination, float[] raw, int offset, int count, CancellationToken token)
        {
            int length = checked(count * Channels * sizeof(float));
            await ReadBytes(length, token);
            Buffer.BlockCopy(bytes, 0, raw, offset * Channels * sizeof(float), length);
            for (int frame = 0; frame < count; frame++) for (int channel = 0; channel < Channels; channel++)
            {
                int index = (offset + frame) * Channels + channel;
                float input = raw[index];
                if (!float.IsFinite(input)) throw new IOException("Decoded audio contains a non-finite sample.");
                destination[index] = (float)filters[channel * 2 + 1].Apply(filters[channel * 2].Apply(input));
            }
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Dispose(); Kill();
            try { await process.WaitForExitAsync(); await errors; }
            finally { process.Dispose(); }
        }
    }

    private sealed class Lowpass
    {
        private static readonly double cosine = Math.Cos(2 * Math.PI * 4000 / Rate);
        private static readonly double alpha = Math.Sin(2 * Math.PI * 4000 / Rate) / Math.Sqrt(2);
        private static readonly double b0 = (1 - cosine) / (2 * (1 + alpha)), b1 = 2 * b0,
            a1 = -2 * cosine / (1 + alpha), a2 = (1 - alpha) / (1 + alpha);
        private double x1, x2, y1, y2;
        public double Apply(double value)
        {
            double output = b0 * value + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = value; y2 = y1; y1 = output; return output;
        }
    }
}
