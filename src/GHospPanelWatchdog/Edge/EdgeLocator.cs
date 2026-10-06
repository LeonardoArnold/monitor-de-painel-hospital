using Microsoft.Win32;

namespace GHospPanelWatchdog.Edge;

internal static class EdgeLocator
{
    /// <summary>Localiza o msedge.exe: configuração → App Paths do registro → caminhos padrão.</summary>
    public static string? Find(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
                if (k?.GetValue(null) is string p && File.Exists(p)) return p;
            }
            catch { }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
