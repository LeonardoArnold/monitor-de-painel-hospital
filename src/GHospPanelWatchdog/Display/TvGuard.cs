using System.Diagnostics;
using System.Runtime.InteropServices;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Native;

namespace GHospPanelWatchdog.Display;

/// <summary>
/// Mantém a TV exclusiva do painel:
///  1) painel "sempre visível" (topmost) — aplicado com SWP_NOACTIVATE, não rouba foco;
///  2) janelas de outros programas que fiquem sobre a TV são movidas para o monitor principal
///     (ou minimizadas), sempre sem ativação e de forma assíncrona (nunca bloqueia em janela travada).
/// Títulos de janela NÃO são registrados (podem conter dados de pacientes).
/// </summary>
internal static class TvGuard
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const long WS_EX_TOPMOST = 0x8;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_CLOAKED = 14;
    private const uint GUI_INMOVESIZE = 0x2;
    private const int VK_LBUTTON = 0x01;
    private const int WPF_RESTORETOMAXIMIZED = 0x2;

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
        "ForegroundStaging", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Input.InputSite.WindowClass",
    };

    // ------------------------------------------------------------------ Topmost

    public static bool IsTopmost(IntPtr hwnd) =>
        (Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    public static void SetTopmost(IntPtr hwnd, bool on)
    {
        if (IsTopmost(hwnd) == on) return;
        Win32.SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    // ------------------------------------------------------------------ Janelas intrusas

    /// <summary>Janela "de aplicativo" de outro programa cujo centro está na TV.</summary>
    public static bool IsIntruder(IntPtr h, RECT tv, int panelPid)
    {
        if (!Win32.IsWindowVisible(h) || Win32.IsIconic(h)) return false;
        if (Win32.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return false; // diálogos seguem o dono
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
        if ((ex & (Win32.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
        if (!Win32.GetWindowRect(h, out var r) || r.Width < 50 || r.Height < 50) return false;
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        if (cx < tv.Left || cx >= tv.Right || cy < tv.Top || cy >= tv.Bottom) return false;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == panelPid || pid == Environment.ProcessId) return false;
        if (ShellClasses.Contains(Win32.GetClassName(h))) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
        return true;
    }

    /// <summary>Retorna true se o usuário está arrastando/redimensionando a janela agora.</summary>
    private static bool IsBeingDragged(IntPtr h)
    {
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return true;
        uint tid = Win32.GetWindowThreadProcessId(h, out _);
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(tid, ref gi) && (gi.flags & GUI_INMOVESIZE) != 0;
    }

    /// <summary>
    /// Tira da TV as janelas de outros programas. Retorna quantas foram tratadas e se alguma ficou
    /// pendente (arraste em andamento) para rechecagem rápida.
    /// </summary>
    public static (int handled, bool pending) EvictIntruders(MonitorInfo tv, MonitorInfo primary, int panelPid, TvIntruderAction action)
    {
        if (action == TvIntruderAction.None) return (0, false);
        var found = new List<IntPtr>();
        Win32.EnumWindows((h, _) => { if (IsIntruder(h, tv.Bounds, panelPid)) found.Add(h); return true; }, IntPtr.Zero);

        int handled = 0;
        bool pending = false;
        foreach (var h in found)
        {
            if (IsBeingDragged(h)) { pending = true; continue; }
            if (Win32.IsHungAppWindow(h)) continue; // nunca bloquear o watchdog numa janela travada
            string proc = ProcessName(h);
            try
            {
                if (action == TvIntruderAction.Minimize) MinimizeTowardsPrimary(h, primary);
                else MoveToPrimary(h, primary);
                handled++;
                Log.Info($"Janela de \"{proc}\" estava sobre a TV — {(action == TvIntruderAction.Minimize ? "minimizada" : "movida para o monitor principal")} (sem foco)");
            }
            catch (Exception ex)
            {
                Log.WarnThrottled($"Não foi possível tirar a janela de \"{proc}\" da TV: {ex.Message}");
            }
        }
        return (handled, pending);
    }

    private static void MoveToPrimary(IntPtr h, MonitorInfo primary)
    {
        if (Win32.IsZoomed(h))
        {
            // Maximizada na TV: guarda a posição normal no principal e minimiza; ao restaurar,
            // o Windows a maximiza no monitor principal.
            MinimizeTowardsPrimary(h, primary);
            return;
        }
        Win32.GetWindowRect(h, out var r);
        var wa = primary.WorkArea;
        int w = Math.Min(r.Width, wa.Width), hgt = Math.Min(r.Height, wa.Height);
        int x = wa.Left + (wa.Width - w) / 2, y = wa.Top + (wa.Height - hgt) / 2;
        Win32.SetWindowPos(h, IntPtr.Zero, x, y, w, hgt,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Minimiza sem ativar e faz a janela voltar no monitor principal quando for restaurada.</summary>
    private static void MinimizeTowardsPrimary(IntPtr h, MonitorInfo primary)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (Win32.GetWindowPlacement(h, ref wp))
        {
            var n = wp.rcNormalPosition;
            var wa = primary.WorkArea;
            int offX = wa.Left - primary.Bounds.Left, offY = wa.Top - primary.Bounds.Top; // coordenadas de workspace
            int w = Math.Min(n.Width, wa.Width), hgt = Math.Min(n.Height, wa.Height);
            int x = wa.Left + (wa.Width - w) / 2 - offX, y = wa.Top + (wa.Height - hgt) / 2 - offY;
            wp.rcNormalPosition = new RECT { Left = x, Top = y, Right = x + w, Bottom = y + hgt };
            wp.flags = Win32.IsZoomed(h) ? WPF_RESTORETOMAXIMIZED : 0;
            wp.showCmd = Win32.SW_SHOWMINNOACTIVE;
            Win32.SetWindowPlacement(h, ref wp);
        }
        else ShowWindowAsync(h, Win32.SW_SHOWMINNOACTIVE);
    }

    private static string ProcessName(IntPtr h)
    {
        try
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return "?"; }
    }
}

/// <summary>
/// Ganchos de eventos de janela do Windows (fora de processo, sem injeção): janela mostrada,
/// trazida para frente, fim de arraste, restaurada. Filtra no próprio callback para só acordar o
/// watchdog quando a janela estiver sobre a TV. Deve ser criado na thread da interface (tem laço de mensagens).
/// </summary>
internal sealed class WindowEventHooks : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x2;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private readonly WinEventProc _proc; // mantém o delegate vivo
    private readonly List<IntPtr> _hooks = new();
    private readonly Func<RECT?> _tvBounds;
    private readonly Action _onIntruder;

    public WindowEventHooks(Func<RECT?> tvBounds, Action onIntruder)
    {
        _tvBounds = tvBounds;
        _onIntruder = onIntruder;
        _proc = OnEvent;
        foreach (var ev in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MINIMIZEEND, EVENT_OBJECT_SHOW })
        {
            var h = SetWinEventHook(ev, ev, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }
        if (_hooks.Count == 0) Log.Warn("Eventos de janela indisponíveis; a proteção da TV usará apenas a verificação periódica.");
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != 0) return;
            if (GetAncestor(hwnd, 2 /*GA_ROOT*/) != hwnd) return; // só janelas de nível superior
            if (_tvBounds() is not { } tv) return;
            if (!Win32.GetWindowRect(hwnd, out var r)) return;
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            if (cx >= tv.Left && cx < tv.Right && cy >= tv.Top && cy < tv.Bottom) _onIntruder();
        }
        catch { }
    }

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
    }
}
