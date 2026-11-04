using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MCCTheaterCapture;

internal sealed partial class ViewerWindow
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);

    private void ApplyWindowAppearance()
    {
        var window = new WindowInteropHelper(this).Handle;
        // COLORREF is 0x00BBGGRR: desk gold #e7bb72 and button ink #211a10.
        // Keep standard Windows caption buttons, dragging and resizing. The
        // dark-mode request also supplies a non-white fallback on older Windows.
        uint dark = 1, gold = 0x0072bbe7, text = 0x00101a21;
        DwmSetWindowAttribute(window, 20, ref dark, sizeof(uint));
        int captionResult = DwmSetWindowAttribute(window, 35, ref gold, sizeof(uint));
        int textResult = DwmSetWindowAttribute(window, 36, ref text, sizeof(uint));
        Record("window-appearance", new { caption = "#e7bb72", text = "#211a10", captionResult, textResult });
    }
}
