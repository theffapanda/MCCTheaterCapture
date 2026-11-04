using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCCTheaterCapture;

internal static class HighlightComposition
{
    public static Task WriteCaptions(string path, HighlightFormat format, HighlightCaption caption, CancellationToken token)
    {
        format.Validate(); caption.Validate();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    Draw(drawing, caption.Above, 120, format.GameplayTop - 56, 88, true);
                    Draw(drawing, caption.Below, format.GameplayTop + HighlightFormat.GameplayHeight + 56, 1690, 50, false);
                }
                var bitmap = new RenderTargetBitmap(1080, 1920, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                token.ThrowIfCancellationRequested();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                encoder.Save(output); completion.SetResult();
            }
            catch (OperationCanceledException) { completion.SetCanceled(token); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Highlight captions" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }

    private static void Draw(DrawingContext drawing, string value, double top, double bottom, double fontSize, bool bold)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Medium, FontStretches.Normal);
        FormattedText Make(Brush brush) => new(value.Replace("\r\n", "\n").Replace('\r', '\n').Trim(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, fontSize, brush, 1) { MaxTextWidth = 900, TextAlignment = TextAlignment.Center };
        var text = Make(Brushes.White);
        while (text.Height > bottom - top && fontSize > 8) { fontSize -= 1; text = Make(Brushes.White); }
        double y = top + (bottom - top - text.Height) / 2;
        drawing.DrawText(Make(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0))), new Point(94, y + 4));
        drawing.DrawText(text, new Point(90, y));
    }
}
