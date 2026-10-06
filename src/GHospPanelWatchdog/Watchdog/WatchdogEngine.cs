using System.Text;
using System.Threading.Channels;
using GHospPanelWatchdog.Audio;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Display;
using GHospPanelWatchdog.Edge;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Native;
using GHospPanelWatchdog.Security;

namespace GHospPanelWatchdog.Watchdog;

/// <summary>
/// Laço de reconciliação: compara o estado real com o desejado e corrige somente o que divergir.
/// Uma única passada por vez (serializado). Acordado por eventos ou pelo timer de fallback.
/// </summary>
internal sealed class WatchdogEngine : IAsyncDisposable
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthyResetAfter = TimeSpan.FromMinutes(2);
    private const int HangStrikesToRestart = 3;

    private readonly Channel<string> _signals = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly AudioService _audio;
    private readonly PanelBrowser _browser;
    private Task? _loop;
    private volatile AppConfig _cfg;
    private volatile bool _paused;
    private volatile bool _restartRequested;
    private volatile bool _profileResetRequested;
    private bool _quickWake;

    // ---- estado do painel
    private DateTime? _launchStartedAt;
    private readonly Backoff _launchBackoff = new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
    private DateTime _nextLaunchAllowed = DateTime.MinValue;
    private IntPtr _foregroundBeforeLaunch;
    private bool _hadWindow;
    private bool _launchedOnce;
    private int _hangStrikes;
    private DateTime? _healthySince;
    private bool? _tvPresent;
    private string _lastTargetPath = "";
    private bool _minimizedForMissingTv;
    private DateTime _lastFullscreenFix = DateTime.MinValue;
    private DateTime? _cdpMissingSince;
    private bool _cdpRestartTried;
    private bool _cdpLimitedLogged;

    // ---- rede
    private readonly Backoff _netBackoff = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60));
    private DateTime _nextNetAttempt = DateTime.MinValue;
    private bool _netDown;
    private string _internet = "—";

    // ---- login
    private int _loginFailures;
    private DateTime _nextLoginAllowed = DateTime.MinValue;
    private DateTime? _loginSubmittedAt;
    private string _login = "—";
    private bool _loginNeededLogged;

    // ---- navegação
    private DateTime? _offPanelSince;
    private DateTime _nextNavigateAllowed = DateTime.MinValue;
    private DateTime _lastPanelLoad = DateTime.Now;

    public WatchdogEngine(AppConfig cfg)
    {
        _cfg = cfg;
        _audio = new AudioService(Signal);
        _browser = new PanelBrowser(AppPaths.EdgeProfileDir, Signal);
    }

    public StatusSnapshot Status { get; private set; } = new();
    public event Action? StatusChanged;
    public bool Paused => _paused;
    public AppConfig Config => _cfg;

    public void Start()
    {
        _loop = Task.Run(async () =>
        {
            _audio.Start(); // criado na thread MTA do pool
            try { await RunAsync(_stop.Token).ConfigureAwait(false); }
            finally { _audio.Dispose(); }
        });
    }

    /// <summary>Acorda o laço (eventos do Windows, comandos do menu). Coalescente.</summary>
    public void Signal(string reason)
    {
        Interlocked.Exchange(ref _fullPending, 1);
        _signals.Writer.TryWrite(reason);
    }

    /// <summary>Acorda agora e de novo após alguns segundos (topologia de vídeo/áudio estabilizando).</summary>
    public void SignalWithFollowUps(string reason, params int[] delaysMs)
    {
        Signal(reason);
        foreach (var d in delaysMs)
            _ = Task.Delay(d, _stop.Token).ContinueWith(_ => Signal(reason), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void ApplyConfig(AppConfig cfg)
    {
        var old = _cfg;
        _cfg = cfg;
        if (old.PanelUrl != cfg.PanelUrl) Log.Info($"URL do painel alterada para {cfg.PanelUrl}");
        ResetLoginGuard();
        _launchBackoff.Reset();
        _nextLaunchAllowed = DateTime.MinValue;
        Signal("config");
    }

    public void ResetLoginGuard()
    {
        _loginFailures = 0;
        _nextLoginAllowed = DateTime.MinValue;
        _loginSubmittedAt = null;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Log.Info(paused ? "Watchdog pausado pelo administrador" : "Watchdog retomado pelo administrador");
        Signal("pause");
    }

    /// <summary>Apaga o perfil do Edge do painel (sessão/cookies) e reabre. Executado dentro do laço.</summary>
    public void RequestProfileReset()
    {
        Log.Info("Limpeza do perfil do painel solicitada pelo administrador");
        _profileResetRequested = true;
        _paused = false;
        Signal("profile-reset");
    }

    public void RequestRestart()
    {
        Log.Info("Reinício do painel solicitado pelo administrador");
        _restartRequested = true;
        _paused = false;
        Signal("restart");
    }

    public void OpenPanel()
    {
        _paused = false;
        _launchedOnce = false;
        _launchBackoff.Reset();
        _nextLaunchAllowed = DateTime.MinValue;
        Signal("open");
    }

    // ================================================================ Laço

    private async Task RunAsync(CancellationToken ct)
    {
        Log.Info("Watchdog ativo");
        bool full = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (full) await ReconcileAsync().ConfigureAwait(false);
                else GuardTv();
            }
            catch (Exception ex)
            {
                Log.Error("Erro inesperado na verificação (o watchdog continua)", ex);
            }

            var delay = _quickWake ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(_cfg.CheckIntervalSeconds);
            _quickWake = false;
            full = true;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(delay);
                await _signals.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false); // agrupa rajadas de eventos
                while (_signals.Reader.TryRead(out _)) { }
                // Só eventos de janela → caminho rápido (apenas proteção da TV).
                full = Interlocked.Exchange(ref _fullPending, 0) == 1;
            }
            catch (OperationCanceledException) { }
        }
    }

    // ================================================================ Proteção da TV

    private sealed record GuardTarget(MonitorInfo Tv, MonitorInfo Primary, int PanelPid, IntPtr PanelHwnd);
    private volatile GuardTarget? _guard;
    private int _fullPending = 1;

    /// <summary>Limites da TV enquanto a proteção está ativa (lido pelos ganchos de janela na thread da UI).</summary>
    public RECT? GuardBounds => _guard?.Tv.Bounds;

    /// <summary>Evento de janela sobre a TV: aciona só o caminho rápido.</summary>
    public void SignalWindows() => _signals.Writer.TryWrite("windows");

    private void ApplyTvGuard(IntPtr hwnd, int pid, MonitorInfo target, MonitorInfo? primary, AppConfig cfg)
    {
        // Em modo de teste com o painel no monitor principal, nunca mexe nas janelas do funcionário.
        if (target.IsPrimary || primary == null)
        {
            TvGuard.SetTopmost(hwnd, false);
            return;
        }
        TvGuard.SetTopmost(hwnd, cfg.Recovery.PanelAlwaysOnTop);
        _guard = new GuardTarget(target, primary, pid, hwnd);
        GuardTv();
    }

    private void GuardTv()
    {
        var g = _guard;
        if (g == null || _paused) return;
        var cfg = _cfg;
        if (cfg.Recovery.PanelAlwaysOnTop && Win32.IsWindow(g.PanelHwnd) && !Win32.IsHungAppWindow(g.PanelHwnd))
            TvGuard.SetTopmost(g.PanelHwnd, true);
        var (handled, pending) = TvGuard.EvictIntruders(g.Tv, g.Primary, g.PanelPid, cfg.Recovery.TvIntruders);
        if (handled > 0)
            Status = Status with { LastAction = $"{DateTime.Now:HH:mm:ss} Janela de outro programa retirada da TV" };
        if (pending) // usuário ainda arrastando: confere de novo logo após soltar
            _ = Task.Delay(800, _stop.Token).ContinueWith(_ => SignalWindows(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private async Task ReconcileAsync()
    {
        var cfg = _cfg;
        var now = DateTime.Now;
        // A proteção da TV só vale quando o painel está saudável e posicionado (reativada no passo 8).
        // Evita que um painel recém-aberto, com outro PID, seja tratado como "intruso".
        _guard = null;

        if (_paused)
        {
            Publish(Status with { Level = HealthLevel.Paused, Watchdog = "Pausado", UpdatedAt = now });
            return;
        }

        // ---------------- 1. Áudio (independente do painel)
        var audio = _audio.Ensure(cfg.Audio, cfg.Recovery, _browser.GetProcessTree);
        string audioText = !audio.Configured ? "Não configurado" :
            !audio.Present ? $"{audio.DeviceName} — ausente (aguardando)" :
            audio.IsDefault ? $"{audio.DeviceName}" : $"{audio.DeviceName} — não é o padrão";
        string muteText = audio.Muted switch
        {
            null => "—",
            true => "ATIVADO (mutado)",
            false => $"Desativado (volume {audio.VolumePercent}%)",
        };

        // ---------------- 2. Monitores
        var monitors = MonitorService.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        var target = MonitorService.ResolveTarget(cfg.Monitor, monitors);
        TrackTvPresence(target, monitors.Count);
        string monitorText = target?.Describe() ?? "TV ausente — aguardando";

        // ---------------- 3. Reinício solicitado
        if (_restartRequested || _profileResetRequested)
        {
            _restartRequested = false;
            await _browser.KillAsync("reinício solicitado").ConfigureAwait(false);
            if (_profileResetRequested)
            {
                _profileResetRequested = false;
                await DeleteProfileAsync().ConfigureAwait(false);
            }
            ResetLoginGuard();
            _launchStartedAt = null;
            _launchBackoff.Reset();
            _nextLaunchAllowed = DateTime.MinValue;
            _hadWindow = false;
        }

        int pid = _browser.FindBrowser();
        IntPtr hwnd = pid != 0 ? _browser.FindWindow() : IntPtr.Zero;

        var baseStatus = new StatusSnapshot
        {
            Audio = audioText,
            Mute = muteText,
            Monitor = monitorText,
            Internet = _internet,
            Login = _login,
            Watchdog = "Ativo",
            Edge = pid != 0 ? $"Em execução (PID {pid})" : "Não está em execução",
            LastAction = Status.LastAction,
            UpdatedAt = now,
        };
        HealthLevel audioLevel = audio.Configured && (!audio.Present || !audio.IsDefault || audio.Muted == true)
            ? HealthLevel.Warning : HealthLevel.Ok;

        // ---------------- 4. TV ausente → proteger o monitor do funcionário e aguardar
        if (target == null)
        {
            if (hwnd != IntPtr.Zero && !Win32.IsHungAppWindow(hwnd)) TvGuard.SetTopmost(hwnd, false);
            if (hwnd != IntPtr.Zero && !Win32.IsIconic(hwnd) && cfg.Recovery.RecoverAfterDisplayReconnect)
            {
                WindowPlacer.MinimizeNoActivate(hwnd);
                _minimizedForMissingTv = true;
                Act("TV ausente: painel minimizado (sem foco) para não cobrir o monitor do funcionário");
            }
            Publish(baseStatus with { Level = HealthLevel.Warning, Panel = "Aguardando a TV", LastAction = Status.LastAction });
            return;
        }

        // ---------------- 5. Lançamento em andamento
        if (_launchStartedAt != null)
        {
            if (hwnd != IntPtr.Zero)
            {
                _launchStartedAt = null;
                Log.Info("Janela do painel detectada");
                GuardFocus(hwnd);
            }
            else if (now - _launchStartedAt < LaunchTimeout)
            {
                _quickWake = true;
                Publish(baseStatus with { Level = HealthLevel.Working, Panel = "Iniciando…" });
                return;
            }
            else
            {
                _launchStartedAt = null;
                Log.Warn($"O Edge do painel não abriu a janela em {LaunchTimeout.TotalSeconds:0}s");
                if (pid != 0) await _browser.KillAsync("janela não apareceu após iniciar").ConfigureAwait(false);
                pid = 0;
            }
        }

        // ---------------- 6. Edge/janela ausente → reabrir (com backoff)
        if (pid == 0 || hwnd == IntPtr.Zero)
        {
            _healthySince = null;
            if (_hadWindow)
            {
                Act(pid == 0 ? "Painel fechado (Edge do painel encerrado)" : "Janela do painel fechada");
                _hadWindow = false;
            }
            if (!cfg.Recovery.ReopenIfClosed && _launchedOnce)
            {
                Publish(baseStatus with { Level = HealthLevel.Warning, Panel = "Fechado (reabertura automática desativada)" });
                return;
            }
            if (now < _nextLaunchAllowed)
            {
                var wait = (int)Math.Ceiling((_nextLaunchAllowed - now).TotalSeconds);
                Publish(baseStatus with { Level = HealthLevel.Warning, Panel = $"Reabrindo em {wait}s (tentativa {_launchBackoff.Failures + 1})" });
                if (wait <= cfg.CheckIntervalSeconds) _quickWake = true;
                return;
            }
            var edge = EdgeLocator.Find(cfg.EdgePath);
            if (edge == null)
            {
                Log.WarnThrottled("Microsoft Edge não encontrado. Informe o caminho do msedge.exe nas configurações.");
                _nextLaunchAllowed = now + _launchBackoff.Next();
                Publish(baseStatus with { Level = HealthLevel.Error, Panel = "Edge não encontrado", Edge = "Não encontrado" });
                return;
            }
            try
            {
                _foregroundBeforeLaunch = Win32.GetForegroundWindow();
                _browser.Launch(edge, cfg, target, primary);
                _launchedOnce = true;
                _launchStartedAt = now;
                _offPanelSince = null;
                _nextLaunchAllowed = now + _launchBackoff.Next();
                _quickWake = true;
                Act(_launchBackoff.Failures > 1 ? $"Painel sendo recuperado (tentativa {_launchBackoff.Failures})" : "Iniciando o painel");
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao iniciar o Edge do painel", ex);
                _nextLaunchAllowed = now + _launchBackoff.Next();
            }
            Publish(baseStatus with { Level = HealthLevel.Working, Panel = "Iniciando…" });
            return;
        }
        if (!_hadWindow) Log.Info("Painel em execução");
        _hadWindow = true;

        // ---------------- 7. Travamento
        bool hungWindow = Win32.IsHungAppWindow(hwnd);
        PageState? page = null;
        bool pageTimeout = false;
        try { page = await _browser.ProbeAsync(cfg.Login).ConfigureAwait(false); }
        catch (TimeoutException) { pageTimeout = true; }
        catch (Exception) { /* CDP indisponível nesta passada */ }

        if (hungWindow || pageTimeout)
        {
            _hangStrikes++;
            Log.Warn($"Painel não está respondendo ({(hungWindow ? "janela" : "página")}) — {_hangStrikes}/{HangStrikesToRestart}");
            if (_hangStrikes >= HangStrikesToRestart && cfg.Recovery.RecoverAfterHang)
            {
                _hangStrikes = 0;
                Act("Edge do painel travado — reiniciando somente a instância do painel");
                await _browser.KillAsync("travamento").ConfigureAwait(false);
                _hadWindow = false;
                _quickWake = true;
                Publish(baseStatus with { Level = HealthLevel.Error, Panel = "Travado — reiniciando" });
                return;
            }
            Publish(baseStatus with { Level = HealthLevel.Warning, Panel = "Não está respondendo" });
            return;
        }
        _hangStrikes = 0;

        if (_browser.PageCrashed)
        {
            Act("Página do painel caiu (renderer) — recarregando");
            try { await _browser.ReloadAsync().ConfigureAwait(false); } catch { }
            _quickWake = true;
        }

        // ---------------- 8. Posição na TV + tela cheia (sem foco)
        bool cdp = _browser.CdpConnected;
        await EnsurePlacementAsync(hwnd, target, primary, cdp).ConfigureAwait(false);
        ApplyTvGuard(hwnd, pid, target, primary, cfg);

        // ---------------- 9. CDP indisponível (ex.: política do Edge bloqueia depuração remota)
        if (!cdp)
        {
            _cdpMissingSince ??= now;
            if (!_cdpRestartTried && now - _cdpMissingSince > TimeSpan.FromSeconds(60))
            {
                _cdpRestartTried = true;
                Log.Warn("Sem canal de controle (CDP) com o Edge do painel — reiniciando a instância do painel uma vez");
                await _browser.KillAsync("sem canal de controle").ConfigureAwait(false);
                _hadWindow = false;
                _cdpMissingSince = null;
                return;
            }
            if (_cdpRestartTried && !_cdpLimitedLogged)
            {
                _cdpLimitedLogged = true;
                Log.Warn("Modo limitado: o Edge não aceitou o canal de controle (política RemoteDebuggingAllowed?). " +
                         "Posicionamento continua; login automático e detecção de página ficam indisponíveis.");
            }
            Publish(baseStatus with { Level = HealthLevel.Warning, Panel = "Aberto (modo limitado)" });
            return;
        }
        _cdpMissingSince = null;

        // ---------------- 10. Página: internet, login, URL
        if (page == null)
        {
            Publish(baseStatus with { Level = HealthLevel.Working, Panel = "Carregando…" });
            return;
        }
        var (panelText, panelLevel) = await HandlePageAsync(page, cfg, now).ConfigureAwait(false);

        var level = new[] { panelLevel, audioLevel }.Max();
        if (level == HealthLevel.Ok)
        {
            _healthySince ??= now;
            if (now - _healthySince > HealthyResetAfter && _launchBackoff.Failures > 0) _launchBackoff.Reset();
        }
        else _healthySince = null;

        Publish(baseStatus with
        {
            Level = level,
            Panel = panelText,
            Internet = _internet,
            Login = _login,
        });
    }

    // ================================================================ Etapas

    private void TrackTvPresence(MonitorInfo? target, int monitorCount)
    {
        bool present = target != null;
        if (_tvPresent == present && (target?.DevicePath ?? "") == _lastTargetPath) return;
        if (present)
        {
            if (_tvPresent == false) Act($"TV reconectada: {target!.Describe()}");
            else Log.Info($"Monitores detectados: {monitorCount}. TV/painel: {target!.Describe()}");
        }
        else
        {
            if (_tvPresent == true) Act("TV desconectada — aguardando reconexão");
            else Log.Info($"Monitores detectados: {monitorCount}. TV não encontrada — aguardando");
        }
        _tvPresent = present;
        _lastTargetPath = target?.DevicePath ?? "";
    }

    private async Task EnsurePlacementAsync(IntPtr hwnd, MonitorInfo target, MonitorInfo? primary, bool cdp)
    {
        var cfg = _cfg;
        bool onTarget = WindowPlacer.IsOnMonitor(hwnd, target);
        (int windowId, string state)? ws = null;
        if (cdp) { try { ws = await _browser.GetWindowStateAsync().ConfigureAwait(false); } catch { } }

        if (!onTarget)
        {
            if (_minimizedForMissingTv && !cfg.Recovery.RecoverAfterDisplayReconnect) return;
            Act(_minimizedForMissingTv ? "Restaurando o painel na TV" : "Painel fora da TV — reposicionando");
            _minimizedForMissingTv = false;
            if (ws is { } w && w.state is "fullscreen" or "maximized")
            {
                try { await _browser.SetWindowStateAsync(w.windowId, "normal").ConfigureAwait(false); } catch { }
                await Task.Delay(300).ConfigureAwait(false);
            }
            WindowPlacer.MoveToMonitor(hwnd, target, primary);
            await Task.Delay(300).ConfigureAwait(false);
            if (cdp) { try { ws = await _browser.GetWindowStateAsync().ConfigureAwait(false); } catch { } }
        }

        if (ws is { } s)
        {
            if (s.state != "fullscreen")
            {
                try
                {
                    await _browser.SetWindowStateAsync(s.windowId, "fullscreen").ConfigureAwait(false);
                    Log.Info($"Painel em tela cheia na TV ({target.Bounds.Width}x{target.Bounds.Height})");
                }
                catch (Exception ex) { Log.WarnThrottled($"Falha ao colocar o painel em tela cheia: {ex.Message}"); }
            }
            else if (!WindowPlacer.CoversMonitor(hwnd, target) && DateTime.Now - _lastFullscreenFix > TimeSpan.FromSeconds(30))
            {
                // Resolução/escala da TV mudou: reaplica a tela cheia no novo tamanho.
                _lastFullscreenFix = DateTime.Now;
                Log.Info("Tamanho da TV mudou — reajustando a tela cheia do painel");
                try
                {
                    await _browser.SetWindowStateAsync(s.windowId, "normal").ConfigureAwait(false);
                    await Task.Delay(300).ConfigureAwait(false);
                    WindowPlacer.MoveToMonitor(hwnd, target, primary);
                    await Task.Delay(200).ConfigureAwait(false);
                    await _browser.SetWindowStateAsync(s.windowId, "fullscreen").ConfigureAwait(false);
                }
                catch { }
            }
        }
        else if (!WindowPlacer.CoversMonitor(hwnd, target))
        {
            WindowPlacer.FillMonitor(hwnd, target);
        }
    }

    private async Task<(string text, HealthLevel level)> HandlePageAsync(PageState page, AppConfig cfg, DateTime now)
    {
        // ---- Sem internet / site fora do ar: aguarda com backoff; nunca reinicia o Edge por isso.
        if (page.IsErrorPage || page.IsServerError)
        {
            if (now >= _nextNetAttempt)
            {
                bool reachable = await NetProbe.CanReachAsync(cfg.PanelUrl, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                _nextNetAttempt = now + _netBackoff.Next();
                if (!_netDown)
                {
                    Act(page.IsServerError ? $"Site do painel retornou erro HTTP {page.HttpStatus}" : "Falha de conexão com o painel");
                    _netDown = true;
                }
                _internet = reachable ? (page.IsServerError ? $"OK (site com erro {page.HttpStatus})" : "OK") : "Sem conexão com o servidor";
                if (reachable)
                {
                    Log.Info("Servidor do painel acessível — recarregando o painel");
                    try { await _browser.NavigateAsync(cfg.PanelUrl).ConfigureAwait(false); } catch { }
                }
            }
            return (page.IsServerError ? "Erro do site — tentando novamente" : "Sem conexão — aguardando", HealthLevel.Warning);
        }
        if (_netDown && page.ReadyState == "complete")
        {
            _netDown = false;
            Act("Conexão restabelecida — painel recuperado");
        }
        _netBackoff.Reset();
        _nextNetAttempt = DateTime.MinValue;
        _internet = "OK";

        // ---- Login
        if (page.HasLoginForm)
        {
            _offPanelSince = null; // tela de login não conta como "fora do painel"
            if (!_loginNeededLogged) { Log.Info("Login necessário"); _loginNeededLogged = true; }
            _login = "Login necessário";
            if (!cfg.Recovery.AutoLogin) { _login = "Login necessário (automático desativado)"; return ("Aguardando login", HealthLevel.Warning); }
            if (page.ReadyState != "complete") return ("Carregando login…", HealthLevel.Working);

            if (_loginSubmittedAt != null)
            {
                if (now - _loginSubmittedAt < TimeSpan.FromSeconds(15)) return ("Autenticando…", HealthLevel.Working);
                _loginSubmittedAt = null;
                _loginFailures++;
                var wait = _loginFailures switch { 1 => TimeSpan.FromSeconds(30), 2 => TimeSpan.FromMinutes(2), _ => TimeSpan.FromMinutes(30) };
                _nextLoginAllowed = now + wait;
                Log.Warn($"Login não foi aceito pelo site (tentativa {_loginFailures}); próxima em {wait.TotalMinutes:0.#} min. Verifique as credenciais.");
            }
            if (now < _nextLoginAllowed)
            {
                _login = _loginFailures >= 3 ? "Login falhou — verifique usuário/senha" : "Login falhou — nova tentativa em breve";
                return ("Aguardando login", HealthLevel.Error);
            }
            var cred = CredentialStore.Read();
            if (cred == null || string.IsNullOrEmpty(cred.Password))
            {
                _login = "Login necessário — credenciais não cadastradas";
                Log.WarnThrottled("Login necessário, mas não há credenciais cadastradas (Configurações → Login)");
                return ("Aguardando login", HealthLevel.Error);
            }
            _login = "Autenticando…";
            Act("Login necessário — autenticando automaticamente");
            string r;
            try { r = await _browser.LoginAsync(cred, cfg.Login).ConfigureAwait(false); }
            catch (Exception ex) { r = "erro: " + ex.GetType().Name; }
            if (r == "enviado") _loginSubmittedAt = now;
            else
            {
                Log.Warn($"Login automático não concluído: {r}. Ajuste os seletores em Configurações → Login se o site mudou.");
                _nextLoginAllowed = now + TimeSpan.FromSeconds(60);
            }
            _quickWake = true;
            return ("Autenticando…", HealthLevel.Working);
        }
        if (_loginSubmittedAt != null)
        {
            _loginSubmittedAt = null;
            _loginFailures = 0;
            Act("Login recuperado");
        }
        _login = "Autenticado";
        _loginNeededLogged = false;

        // ---- Página certa?
        if (!IsPanelUrl(page.Url, cfg.PanelUrl))
        {
            _offPanelSince ??= now;
            if (now - _offPanelSince > TimeSpan.FromSeconds(15) && now >= _nextNavigateAllowed)
            {
                Act("Página diferente do painel — voltando para o painel");
                _nextNavigateAllowed = now + TimeSpan.FromSeconds(30);
                _offPanelSince = null;
                try { await _browser.NavigateAsync(cfg.PanelUrl).ConfigureAwait(false); _lastPanelLoad = now; } catch { }
            }
            return ("Fora da página do painel", HealthLevel.Working);
        }
        _offPanelSince = null;

        // ---- Recarga periódica opcional
        if (cfg.PeriodicReloadHours > 0 && now - _lastPanelLoad > TimeSpan.FromHours(cfg.PeriodicReloadHours))
        {
            _lastPanelLoad = now;
            Log.Info($"Recarga periódica do painel ({cfg.PeriodicReloadHours} h)");
            try { await _browser.ReloadAsync().ConfigureAwait(false); } catch { }
        }

        return (page.ReadyState == "complete" ? "OK" : "Carregando…", page.ReadyState == "complete" ? HealthLevel.Ok : HealthLevel.Working);
    }

    private static bool IsPanelUrl(string current, string configured)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var c) || !Uri.TryCreate(configured, UriKind.Absolute, out var p)) return false;
        if (!string.Equals(c.Host, p.Host, StringComparison.OrdinalIgnoreCase)) return false;
        return c.AbsolutePath.TrimEnd('/').StartsWith(p.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Se, apesar de tudo, o painel recém-aberto tomou o primeiro plano, devolve o foco.</summary>
    private void GuardFocus(IntPtr panel)
    {
        var prev = _foregroundBeforeLaunch;
        _foregroundBeforeLaunch = IntPtr.Zero;
        if (prev == IntPtr.Zero || prev == panel || !Win32.IsWindow(prev)) return;
        if (Win32.GetForegroundWindow() != panel) return;
        bool ok = Win32.SetForegroundWindow(prev);
        Log.Warn($"O painel recebeu o foco ao abrir — {(ok ? "foco devolvido à janela anterior" : "não foi possível devolver o foco")}");
    }

    private void Act(string message)
    {
        Log.Info(message);
        Status = Status with { LastAction = $"{DateTime.Now:HH:mm:ss} {message}" };
    }

    private void Publish(StatusSnapshot s)
    {
        Status = s with { LastAction = Status.LastAction, Watchdog = _paused ? "Pausado" : "Ativo" };
        try { StatusChanged?.Invoke(); } catch { }
    }

    // ================================================================ Diagnóstico

    public async Task<string> RunDiagnosticsAsync()
    {
        var cfg = _cfg;
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {cfg.PanelUrl}");
        bool net = await NetProbe.CanReachAsync(cfg.PanelUrl, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        sb.AppendLine($"Servidor do painel acessível: {(net ? "SIM" : "NÃO")}");
        sb.AppendLine($"Edge: {EdgeLocator.Find(cfg.EdgePath) ?? "NÃO ENCONTRADO"}");
        sb.AppendLine($"Credenciais cadastradas: {(CredentialStore.HasCredential() ? "SIM" : "NÃO")}");
        sb.AppendLine();
        sb.AppendLine("Monitores:");
        var monitors = MonitorService.GetMonitors();
        var target = MonitorService.ResolveTarget(cfg.Monitor, monitors);
        foreach (var m in monitors)
            sb.AppendLine($"  {(m == target ? "► " : "  ")}{m.Describe()}");
        if (target == null) sb.AppendLine("  (monitor do painel NÃO encontrado)");
        sb.AppendLine();
        sb.AppendLine("Áudio (saídas ativas):");
        foreach (var d in AudioService.ListOutputDevices())
            sb.AppendLine($"  {(d.Id == cfg.Audio.DeviceId ? "► " : "  ")}{d}");
        sb.AppendLine();
        var s = Status;
        sb.AppendLine($"Painel: {s.Panel}");
        sb.AppendLine($"Edge: {s.Edge}");
        sb.AppendLine($"Canal de controle (CDP): {(_browser.CdpConnected ? "conectado" : "desconectado")}");
        sb.AppendLine($"Login: {s.Login}");
        sb.AppendLine($"Internet: {s.Internet}");
        sb.AppendLine($"Mute: {s.Mute}");
        return sb.ToString();
    }

    // ================================================================ Encerramento

    public async Task StopAsync(bool closePanel)
    {
        _stop.Cancel();
        if (_loop != null) { try { await _loop.ConfigureAwait(false); } catch { } }
        if (closePanel)
        {
            Log.Info("Fechando o painel a pedido do administrador");
            await _browser.CloseGracefullyAsync().ConfigureAwait(false);
        }
    }

    private async Task DeleteProfileAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(_browser.ProfileDir)) Directory.Delete(_browser.ProfileDir, true);
                Log.Info("Perfil do Edge do painel apagado (será recriado; login automático fará nova autenticação)");
                return;
            }
            catch (IOException) { await Task.Delay(500).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { await Task.Delay(500).ConfigureAwait(false); }
        }
        Log.Warn("Não foi possível apagar completamente o perfil do painel (arquivos em uso)");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await StopAsync(false).ConfigureAwait(false);
        await _browser.DisposeAsync().ConfigureAwait(false);
    }
}
