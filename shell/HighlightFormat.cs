using System.Globalization;
using System.IO;

namespace MCCTheaterCapture;

internal sealed class HighlightCaption
{
    public string Above { get; set; } = "";
    public string Below { get; set; } = "";

    public void Validate()
    {
        Check(Above, 70); Check(Below, 100);
        static void Check(string text, int limit)
        {
            if (text == null || text.Length > limit || text.Replace("\r\n", "\n").Replace('\r', '\n').Count(c => c == '\n') > 3 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n')))
                throw new IOException($"Clip captions allow up to {limit} characters and four lines, with no control characters.");
        }
    }
}

internal sealed class HighlightFormat
{
    public string Layout { get; set; } = "original";
    public int Blur { get; set; } = 22;
    public int Dim { get; set; } = 40;
    public int GameplayPosition { get; set; } = 48;
    public bool IsPortrait => Layout == "soft-focus";
    public int Width => IsPortrait ? 1080 : 2560;
    public int Height => IsPortrait ? 1920 : 1440;
    public const int GameplayHeight = 608;
    public int GameplayTop => (int)Math.Round(1920 * GameplayPosition / 100.0 - GameplayHeight / 2.0);

    public void Validate()
    {
        if (Layout is not ("original" or "soft-focus") || Blur is < 0 or > 40 || Dim is < 0 or > 80 || GameplayPosition is < 30 or > 65)
            throw new IOException("Choose Original or Soft focus, blur 0–40, dim 0–80%, and gameplay position 30–65%.");
    }

    // Text is rasterized separately, never interpreted as an FFmpeg expression.
    public string FilterGraph()
    {
        Validate();
        if (!IsPortrait) throw new InvalidOperationException("Original clips need no composition filter.");
        string blur = Blur == 0 ? "" : $",gblur=sigma={Blur}:steps=2";
        string dim = (Dim / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        return $"[0:v]split=2[bg][fg];[bg]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1{blur},drawbox=color=black@{dim}:t=fill[back];" +
            $"[fg]scale=1080:{GameplayHeight},setsar=1[game];[back][game]overlay=x=0:y={GameplayTop}[scene];" +
            "[scene][1:v]overlay=0:0:eof_action=repeat,setsar=1,format=yuv420p[out]";
    }
}
