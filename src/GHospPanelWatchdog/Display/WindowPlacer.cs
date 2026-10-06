using System.Runtime.InteropServices;
using GHospPanelWatchdog.Native;

namespace GHospPanelWatchdog.Display;

/// <summary>
/// Posicionamento da janela do painel SEM ativar/roubar foco: só SetWindowPos com SWP_NOACTIVATE,
/// SetWindowPlacement com SW_SHOWNOACTIVATE e ShowWindow com SW_SHOWMINNOACTIVE.
/// </summary>
internal static class WindowPlacer
{
    private const uint NoActivateFlags = Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER;

    /// <summary>O centro da janela está dentro do monitor alvo (e ela não está minimizada).</summary>
    public static bool IsOnMonitor(IntPtr hwnd, MonitorInfo target)
    {
        if (Win32.IsIconic(hwnd) || !Win32.GetWindowRect(hwnd, out var r)) return false;
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        var b = target.Bounds;
        return cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom;
    }

    /// <summary>A janela cobre exatamente o monitor (tolerância de alguns pixels).</summary>
    public static bool CoversMonitor(IntPtr hwnd, MonitorInfo target)
    {
        if (Win32.IsIconic(hwnd) || !Win32.GetWindowRect(hwnd, out var r)) return false;
        var b = target.Bounds;
        const int tol = 2;
        return Math.Abs(r.Left - b.Left) <= tol && Math.Abs(r.Top - b.Top) <= tol &&
               Math.Abs(r.Right - b.Right) <= tol && Math.Abs(r.Bottom - b.Bottom) <= tol;
    }

    /// <summary>Retângulo "normal" centralizado dentro do monitor (antes da tela cheia).</summary>
    private static RECT InnerRect(MonitorInfo m)
    {
        var w = m.WorkArea;
        int width = Math.Max(400, w.Width * 3 / 4), height = Math.Max(300, w.Height * 3 / 4);
        int left = w.Left + (w.Width - width) / 2, top = w.Top + (w.Height - height) / 2;
        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>Move a janela para dentro do monitor alvo sem ativá-la (restaura de minimizado se preciso).</summary>
    public static void MoveToMonitor(IntPtr hwnd, MonitorInfo target, MonitorInfo? primary)
    {
        var inner = InnerRect(target);
        if (Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd))
        {
            // rcNormalPosition usa coordenadas de "workspace" (relativas à área de trabalho do principal).
            int offX = primary == null ? 0 : primary.WorkArea.Left - primary.Bounds.Left;
            int offY = primary == null ? 0 : primary.WorkArea.Top - primary.Bounds.Top;
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Win32.GetWindowPlacement(hwnd, ref wp);
            wp.showCmd = Win32.SW_SHOWNOACTIVATE;
            wp.flags = 0;
            wp.rcNormalPosition = new RECT
            {
                Left = inner.Left - offX, Top = inner.Top - offY,
                Right = inner.Right - offX, Bottom = inner.Bottom - offY,
            };
            Win32.SetWindowPlacement(hwnd, ref wp);
        }
        Win32.SetWindowPos(hwnd, IntPtr.Zero, inner.Left, inner.Top, inner.Width, inner.Height, NoActivateFlags);
    }

    /// <summary>Fallback sem CDP: ocupa o monitor inteiro com SetWindowPos (sem ativar).</summary>
    public static void FillMonitor(IntPtr hwnd, MonitorInfo target)
    {
        var b = target.Bounds;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, NoActivateFlags);
    }

    /// <summary>Minimiza sem ativar outra janela nem roubar foco.</summary>
    public static void MinimizeNoActivate(IntPtr hwnd) => Win32.ShowWindow(hwnd, Win32.SW_SHOWMINNOACTIVE);
}
