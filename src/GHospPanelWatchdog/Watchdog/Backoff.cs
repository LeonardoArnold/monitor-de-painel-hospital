namespace GHospPanelWatchdog.Watchdog;

/// <summary>Backoff exponencial: inicial, ×2 a cada falha, com teto.</summary>
internal sealed class Backoff(TimeSpan initial, TimeSpan max)
{
    private readonly TimeSpan _initial = initial;
    private TimeSpan _current = initial;

    public int Failures { get; private set; }

    /// <summary>Retorna a espera atual e dobra a próxima.</summary>
    public TimeSpan Next()
    {
        var d = _current;
        Failures++;
        _current = TimeSpan.FromTicks(Math.Min(max.Ticks, _current.Ticks * 2));
        return d;
    }

    public void Reset()
    {
        _current = _initial;
        Failures = 0;
    }
}
