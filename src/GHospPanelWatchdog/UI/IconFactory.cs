using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using GHospPanelWatchdog.Watchdog;

namespace GHospPanelWatchdog.UI;

/// <summary>Ícones da bandeja desenhados em tempo de execução: uma "TV" com um ponto de status.</summary>
internal static class IconFactory
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static Icon Create(HealthLevel level, int size)
    {
        var dot = level switch
        {
            HealthLevel.Ok => Color.FromArgb(46, 160, 67),
            HealthLevel.Working => Color.FromArgb(56, 132, 255),
            HealthLevel.Warning => Color.FromArgb(230, 162, 0),
            HealthLevel.Error => Color.FromArgb(218, 54, 51),
            _ => Color.FromArgb(140, 140, 140),
        };
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var body = new SolidBrush(Color.FromArgb(32, 64, 112));
            using var screen = new SolidBrush(Color.FromArgb(220, 235, 250));
            using var path = Rounded(new RectangleF(1 * s, 4 * s, 30 * s, 20 * s), 3 * s);
            g.FillPath(body, path);
            g.FillRectangle(screen, 4 * s, 7 * s, 24 * s, 14 * s);
            g.FillRectangle(body, 13 * s, 24 * s, 6 * s, 3 * s);
            g.FillRectangle(body, 8 * s, 27 * s, 16 * s, 2.5f * s);
            using var dotBrush = new SolidBrush(dot);
            using var ring = new Pen(Color.White, 2f * s);
            var r = new RectangleF(17 * s, 15 * s, 14 * s, 14 * s);
            g.FillEllipse(dotBrush, r);
            g.DrawEllipse(ring, r);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
