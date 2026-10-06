using System.Text.Json;
using System.Text.Json.Serialization;
using GHospPanelWatchdog.Logging;

namespace GHospPanelWatchdog.Config;

public enum MonitorMode
{
    /// <summary>Primeiro monitor que não é o principal.</summary>
    AutoSecondary,
    /// <summary>Monitor escolhido manualmente (identificado pelo caminho do dispositivo).</summary>
    Specific,
}

public sealed class MonitorSelection
{
    public MonitorMode Mode { get; set; } = MonitorMode.AutoSecondary;
    /// <summary>Caminho estável do monitor (\\?\DISPLAY#...). Identificador principal.</summary>
    public string DevicePath { get; set; } = "";
    /// <summary>Nome GDI (\\.\DISPLAY2). Usado só como fallback.</summary>
    public string GdiName { get; set; } = "";
    /// <summary>Somente para exibição.</summary>
    public string FriendlyName { get; set; } = "";
}

public sealed class AudioSelection
{
    /// <summary>ID persistente do endpoint ({0.0.0.00000000}.{guid}). Vazio = não gerenciar áudio.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Somente para exibição e fallback opcional.</summary>
    public string FriendlyName { get; set; } = "";
    public bool AllowNameFallback { get; set; } = true;
    public bool SetForConsoleAndMultimedia { get; set; } = true;
    public bool SetForCommunications { get; set; } = true;
}

public enum TvIntruderAction
{
    /// <summary>Move a janela de volta para o monitor principal, sem foco (padrão).</summary>
    MoveToPrimary,
    /// <summary>Minimiza a janela; ao restaurar, ela volta no monitor principal.</summary>
    Minimize,
    /// <summary>Não mexe em janelas de outros programas.</summary>
    None,
}

public sealed class RecoveryOptions
{
    /// <summary>Painel "sempre visível" na TV (topmost, sem foco).</summary>
    public bool PanelAlwaysOnTop { get; set; } = true;
    /// <summary>O que fazer com janelas de outros programas abertas/arrastadas para a TV.</summary>
    public TvIntruderAction TvIntruders { get; set; } = TvIntruderAction.MoveToPrimary;
    public bool ReopenIfClosed { get; set; } = true;
    public bool RecoverAfterHang { get; set; } = true;
    public bool RecoverAfterDisplayReconnect { get; set; } = true;
    public bool EnsureAudioDevice { get; set; } = true;
    public bool EnsureUnmuted { get; set; } = true;
    public bool UnmutePanelSession { get; set; } = true;
    public bool AutoLogin { get; set; } = true;
}

public sealed class LoginOptions
{
    /// <summary>Seletores CSS opcionais. Vazios = detecção automática.</summary>
    public string UserSelector { get; set; } = "";
    public string PasswordSelector { get; set; } = "";
    public string SubmitSelector { get; set; } = "";
}

public sealed class AppConfig
{
    public const string DefaultPanelUrl = "https://painel.g-hosp.com.br/paineis/398";

    public int ConfigVersion { get; set; } = 1;
    public string PanelUrl { get; set; } = DefaultPanelUrl;
    public int CheckIntervalSeconds { get; set; } = 5;
    /// <summary>Recarregar o painel periodicamente (horas). 0 = desativado.</summary>
    public int PeriodicReloadHours { get; set; } = 0;
    /// <summary>Impede o Windows de desligar o vídeo (afeta todos os monitores). Desativado por padrão.</summary>
    public bool KeepDisplayOn { get; set; } = false;

    public MonitorSelection Monitor { get; set; } = new();
    public AudioSelection Audio { get; set; } = new();
    public RecoveryOptions Recovery { get; set; } = new();
    public LoginOptions Login { get; set; } = new();

    /// <summary>Caminho do msedge.exe. Vazio = localizar automaticamente.</summary>
    public string EdgePath { get; set; } = "";
    public string ExtraEdgeArgs { get; set; } = "";

    /// <summary>
    /// Reservado para fase futura. NÃO é aplicado pelo programa nesta versão —
    /// o watchdog nunca altera a inicialização do Windows.
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;

    public void Normalize()
    {
        PanelUrl = (PanelUrl ?? "").Trim();
        if (!Uri.TryCreate(PanelUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            PanelUrl = DefaultPanelUrl;
        CheckIntervalSeconds = Math.Clamp(CheckIntervalSeconds, 2, 60);
        PeriodicReloadHours = Math.Clamp(PeriodicReloadHours, 0, 720);
        Monitor ??= new();
        Audio ??= new();
        Recovery ??= new();
        Login ??= new();
        EdgePath ??= "";
        ExtraEdgeArgs ??= "";
        StartWithWindows = false; // fase de testes: sempre desligado
    }
}

internal static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static bool Exists => File.Exists(AppPaths.ConfigFile);

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), JsonOptions) ?? new AppConfig();
                cfg.Normalize();
                return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Configuração inválida; usando padrões (arquivo preservado como config.invalid.json)", ex);
            try { File.Copy(AppPaths.ConfigFile, Path.Combine(AppPaths.DataDir, "config.invalid.json"), true); } catch { }
        }
        var def = new AppConfig();
        def.Normalize();
        return def;
    }

    /// <summary>Gravação atômica: arquivo temporário + substituição (resiste a queda de energia).</summary>
    public static void Save(AppConfig cfg)
    {
        cfg.Normalize();
        Directory.CreateDirectory(AppPaths.DataDir);
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        if (File.Exists(AppPaths.ConfigFile))
            File.Replace(tmp, AppPaths.ConfigFile, null, true);
        else
            File.Move(tmp, AppPaths.ConfigFile);
    }
}
