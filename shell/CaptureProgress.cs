namespace MCCTheaterCapture;

internal static class CaptureProgress
{
    public static double? ParallelRemaining(double? videoSeconds,double? audioSeconds) =>
        videoSeconds is >=0 && audioSeconds is >=0 ? Math.Max(videoSeconds.Value,audioSeconds.Value) : null;
    public static double? FilmWork(double frames,double? fps,bool audioCached,bool isolatedAudioPass=false) =>
        frames>=0 && fps is >0 ? isolatedAudioPass ? frames/fps.Value+(audioCached?0:frames/60.0) : Math.Max(frames/fps.Value,audioCached?0:frames/60.0) : null;
    public static double TargetTicks(int totalTicks, double? sampleSeconds) =>
        sampleSeconds is > 0 ? Math.Min(totalTicks, sampleSeconds.Value * 60) : totalTicks;

    public static double? Fraction(int tick, double targetTicks) =>
        targetTicks > 0 ? Math.Clamp(tick / targetTicks, 0, .99) : null;

    public static double? Remaining(int tick, int startTick, double targetTicks, double elapsedSeconds) =>
        elapsedSeconds >= 3 && tick > startTick && targetTicks > tick
            ? (targetTicks - tick) * elapsedSeconds / (tick - startTick) : null;
}
