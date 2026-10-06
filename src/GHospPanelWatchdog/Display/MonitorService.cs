using System.Runtime.InteropServices;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Native;

namespace GHospPanelWatchdog.Display;

internal sealed record MonitorInfo(
    IntPtr Handle,
    string GdiName,        // \\.\DISPLAY2
    RECT Bounds,           // pixels físicos
    RECT WorkArea,
    bool IsPrimary,
    string FriendlyName,   // "SAMSUNG"
    string DevicePath,     // \\?\DISPLAY#SAM0F00#... (estável)
    string Connection,     // HDMI, DisplayPort...
    uint Dpi)
{
    public string Describe() =>
        $"{(string.IsNullOrEmpty(FriendlyName) ? "Monitor" : FriendlyName)}" +
        $"{(string.IsNullOrEmpty(Connection) ? "" : $" ({Connection})")}" +
        $" {Bounds.Width}x{Bounds.Height} — {GdiName.Replace(@"\\.\", "")}" +
        (IsPrimary ? " [principal]" : "");
}

/// <summary>Enumeração de monitores via APIs do Windows. Nenhuma coordenada fixa.</summary>
internal static class MonitorService
{
    public static List<MonitorInfo> GetMonitors()
    {
        var names = QueryTargetNames();
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(h, ref mi))
            {
                names.TryGetValue(mi.szDevice, out var t);
                uint dpi = 96;
                try { if (Win32.GetDpiForMonitor(h, 0, out var dx, out _) == 0) dpi = dx; } catch { }
                list.Add(new MonitorInfo(h, mi.szDevice, mi.rcMonitor, mi.rcWork,
                    (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                    t.friendly ?? "", t.path ?? "", t.tech ?? "", dpi));
            }
            return true;
        }, IntPtr.Zero);
        // Ordem estável: principal primeiro, depois da esquerda para a direita.
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    /// <summary>Resolve o monitor do painel segundo a configuração. Null = TV ausente.</summary>
    public static MonitorInfo? ResolveTarget(MonitorSelection sel, IReadOnlyList<MonitorInfo> monitors)
    {
        if (sel.Mode == MonitorMode.Specific)
        {
            if (!string.IsNullOrEmpty(sel.DevicePath))
            {
                var byPath = monitors.FirstOrDefault(m => string.Equals(m.DevicePath, sel.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (byPath != null) return byPath;
            }
            // Fallback: mesmo nome GDI e mesmo nome amigável (driver pode ter mudado o caminho).
            if (!string.IsNullOrEmpty(sel.GdiName))
            {
                var byGdi = monitors.FirstOrDefault(m => m.GdiName == sel.GdiName &&
                    (string.IsNullOrEmpty(sel.FriendlyName) || m.FriendlyName == sel.FriendlyName));
                if (byGdi != null) return byGdi;
            }
            return null;
        }

        var secondaries = monitors.Where(m => !m.IsPrimary).ToList();
        if (secondaries.Count == 0) return null;
        // Com vários secundários, prefere o último escolhido (se houver), depois HDMI, depois o primeiro.
        if (!string.IsNullOrEmpty(sel.DevicePath))
        {
            var pref = secondaries.FirstOrDefault(m => string.Equals(m.DevicePath, sel.DevicePath, StringComparison.OrdinalIgnoreCase));
            if (pref != null) return pref;
        }
        return secondaries.FirstOrDefault(m => m.Connection == "HDMI") ?? secondaries[0];
    }

    // ------------------------- QueryDisplayConfig -------------------------

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum; public uint refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { [FieldOffset(0)] public uint infoType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);

    private static Dictionary<string, (string? friendly, string? path, string? tech)> QueryTargetNames()
    {
        var map = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var np, out var nm) != 0) return map;
                var paths = new DISPLAYCONFIG_PATH_INFO[np];
                var modes = new DISPLAYCONFIG_MODE_INFO[nm];
                int rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: topologia mudou entre as chamadas
                if (rc != 0) return map;

                for (int i = 0; i < np; i++)
                {
                    var p = paths[i];
                    var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                            adapterId = p.sourceInfo.adapterId,
                            id = p.sourceInfo.id,
                        },
                    };
                    if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                    var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                            size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = p.targetInfo.adapterId,
                            id = p.targetInfo.id,
                        },
                    };
                    if (DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;
                    // Em modo "duplicar", vários alvos compartilham a mesma origem; mantém o primeiro.
                    map.TryAdd(src.viewGdiDeviceName, (tgt.monitorFriendlyDeviceName, tgt.monitorDevicePath, TechName(tgt.outputTechnology)));
                }
                break;
            }
        }
        catch
        {
            // Sem nomes amigáveis o watchdog continua funcionando com o nome GDI.
        }
        return map;
    }

    private static string TechName(uint t) => t switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 or 11 => "DisplayPort",
        15 => "Miracast",
        0x80000000 => "Interno",
        _ => "",
    };
}
