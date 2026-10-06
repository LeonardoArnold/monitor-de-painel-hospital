namespace GHospPanelWatchdog.Watchdog;

internal enum HealthLevel { Ok, Working, Warning, Error, Paused }

/// <summary>Fotografia imutável do estado, lida pela UI (bandeja / tela de status).</summary>
internal sealed record StatusSnapshot
{
    public HealthLevel Level { get; init; } = HealthLevel.Working;
    public string Panel { get; init; } = "Iniciando…";
    public string Edge { get; init; } = "—";
    public string Monitor { get; init; } = "—";
    public string Audio { get; init; } = "—";
    public string Mute { get; init; } = "—";
    public string Internet { get; init; } = "—";
    public string Login { get; init; } = "—";
    public string Watchdog { get; init; } = "Ativo";
    public string LastAction { get; init; } = "";
    public DateTime UpdatedAt { get; init; } = DateTime.Now;

    public string Summary => Level switch
    {
        HealthLevel.Ok => "Painel OK",
        HealthLevel.Paused => "Watchdog pausado",
        _ => $"Painel: {Panel}",
    };
}
