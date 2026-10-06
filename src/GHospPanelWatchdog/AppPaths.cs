namespace GHospPanelWatchdog;

/// <summary>Caminhos usados pelo aplicativo. Nunca depende do diretório de trabalho.</summary>
internal static class AppPaths
{
    public const string AppName = "G-HOSP Panel Watchdog";
    public const string AppId = "GHospPanelWatchdog";

    /// <summary>
    /// %LOCALAPPDATA%\GHospPanelWatchdog. A variável GHOSP_WATCHDOG_DATA_DIR permite isolar os dados
    /// (útil para testes em outra máquina sem tocar na configuração real).
    /// </summary>
    public static string DataDir { get; } =
        Environment.GetEnvironmentVariable("GHOSP_WATCHDOG_DATA_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string EdgeProfileDir => Path.Combine(DataDir, "EdgeProfile");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(EdgeProfileDir);
    }
}
