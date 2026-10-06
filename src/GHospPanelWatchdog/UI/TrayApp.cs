using System.Diagnostics;
using GHospPanelWatchdog.Audio;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Display;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Native;
using GHospPanelWatchdog.Watchdog;
using Microsoft.Win32;

namespace GHospPanelWatchdog.UI;

/// <summary>
/// Aplicativo de bandeja. Não existe janela principal: só o ícone perto do relógio.
/// Telas (status/configurações) aparecem apenas quando o administrador as abre.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly WatchdogEngine _engine;
    private readonly Dictionary<HealthLevel, Icon> _icons = new();
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly SynchronizationContext _ui;
    private readonly List<RegisteredWaitHandle> _waits = new();
    private readonly List<EventWaitHandle> _events = new();
    private readonly ToolStripMenuItem _pauseItem;
    private readonly WindowEventHooks _windowHooks;
    private StatusForm? _statusForm;
    private SettingsForm? _settingsForm;
    private HealthLevel _shownLevel = (HealthLevel)(-1);
    private bool _exiting;

    public TrayApp(bool openSettings, bool firstRun)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var cfg = ConfigStore.Load();
        if (firstRun) ConfigStore.Save(cfg); // cria o arquivo com os padrões
        ApplyKeepDisplayOn(cfg.KeepDisplayOn);

        int iconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
        foreach (HealthLevel l in Enum.GetValues<HealthLevel>()) _icons[l] = IconFactory.Create(l, iconSize);

        var menu = new ContextMenuStrip();
        var title = new ToolStripMenuItem(AppPaths.AppName) { Enabled = false };
        title.Font = new Font(title.Font, FontStyle.Bold);
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status", null, (_, _) => ShowStatus());
        menu.Items.Add("Abrir painel", null, (_, _) => _engine!.OpenPanel());
        menu.Items.Add("Reiniciar painel", null, (_, _) => _engine!.RequestRestart());
        var monitorMenu = new ToolStripMenuItem("Selecionar monitor");
        monitorMenu.DropDownItems.Add("(carregando)");
        monitorMenu.DropDownOpening += (_, _) => FillMonitorMenu(monitorMenu);
        menu.Items.Add(monitorMenu);
        var audioMenu = new ToolStripMenuItem("Selecionar áudio");
        audioMenu.DropDownItems.Add("(carregando)");
        audioMenu.DropDownOpening += (_, _) => FillAudioMenu(audioMenu);
        menu.Items.Add(audioMenu);
        menu.Items.Add("Testar painel", null, async (_, _) => await TestPanelAsync());
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pausar watchdog", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Configurações", null, (_, _) => ShowSettings());
        menu.Items.Add("Abrir logs", null, (_, _) => OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => AskExit());

        _tray = new NotifyIcon
        {
            Icon = _icons[HealthLevel.Working],
            Text = AppPaths.AppName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowStatus();

        _engine = new WatchdogEngine(cfg);
        _engine.Start();

        // Janela aberta/arrastada/restaurada sobre a TV → proteção da TV imediata (sem foco).
        _windowHooks = new WindowEventHooks(() => _engine.GuardBounds, _engine.SignalWindows);

        // Eventos nativos do Windows → verificação imediata (+ rechecagens enquanto a topologia estabiliza).
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        RegisterSignal(InstanceSignals.ShowStatus, ShowStatus);
        RegisterSignal(InstanceSignals.ShowSettings, ShowSettings);
        RegisterSignal(InstanceSignals.Exit, () => ExitApp(false));
        RegisterSignal(InstanceSignals.ExitClosePanel, () => ExitApp(true));

        _uiTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _uiTimer.Tick += (_, _) => RefreshTray();
        _uiTimer.Start();

        if (openSettings) _ui.Post(_ => ShowSettings(firstRun), null);
    }

    // ------------------------------------------------------------------ Eventos do sistema

    private void OnDisplayChanged(object? s, EventArgs e) => _engine.SignalWithFollowUps("display", 1500, 4000, 8000);

    private void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("Windows retomou da suspensão");
            _engine.SignalWithFollowUps("resume", 3000, 10000);
        }
    }

    private void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteDisconnect)
            _engine.SignalWithFollowUps("session", 2000, 6000);
    }

    private void RegisterSignal(string name, Action action)
    {
        try
        {
            var ev = InstanceSignals.Create(name);
            _events.Add(ev);
            _waits.Add(ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => _ui.Post(_ => action(), null), null, -1, false));
        }
        catch (Exception ex)
        {
            Log.Warn($"Sinal entre instâncias indisponível ({name}): {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Bandeja

    private void RefreshTray()
    {
        var s = _engine.Status;
        if (s.Level != _shownLevel)
        {
            _shownLevel = s.Level;
            _tray.Icon = _icons[s.Level];
        }
        var text = $"G-HOSP Panel Watchdog\n{s.Summary}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _pauseItem.Checked = _engine.Paused;
    }

    private void TogglePause() => _engine.SetPaused(!_engine.Paused);

    private void FillMonitorMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var cfg = _engine.Config;
        var auto = new ToolStripMenuItem("Automático (monitor secundário)")
        {
            Checked = cfg.Monitor.Mode == MonitorMode.AutoSecondary,
        };
        auto.Click += (_, _) => UpdateConfig(c => c.Monitor = new MonitorSelection { Mode = MonitorMode.AutoSecondary });
        parent.DropDownItems.Add(auto);
        parent.DropDownItems.Add(new ToolStripSeparator());
        foreach (var m in MonitorService.GetMonitors())
        {
            var item = new ToolStripMenuItem(m.Describe())
            {
                Checked = cfg.Monitor.Mode == MonitorMode.Specific && string.Equals(cfg.Monitor.DevicePath, m.DevicePath, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                if (m.IsPrimary && MessageBox.Show(
                        "Este é o monitor PRINCIPAL (do funcionário). O painel ocupará a tela inteira dele.\n\nUse apenas para testes. Continuar?",
                        AppPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                UpdateConfig(c => c.Monitor = new MonitorSelection
                {
                    Mode = MonitorMode.Specific, DevicePath = m.DevicePath, GdiName = m.GdiName, FriendlyName = m.FriendlyName,
                });
            };
            parent.DropDownItems.Add(item);
        }
    }

    private void FillAudioMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var cfg = _engine.Config;
        var devices = AudioService.ListOutputDevices();
        if (devices.Count == 0) parent.DropDownItems.Add(new ToolStripMenuItem("(nenhuma saída de áudio ativa)") { Enabled = false });
        foreach (var d in devices)
        {
            var item = new ToolStripMenuItem(d.ToString()) { Checked = d.Id == cfg.Audio.DeviceId };
            item.Click += (_, _) => UpdateConfig(c =>
            {
                c.Audio.DeviceId = d.Id;
                c.Audio.FriendlyName = d.FriendlyName;
            });
            parent.DropDownItems.Add(item);
        }
        parent.DropDownItems.Add(new ToolStripSeparator());
        var none = new ToolStripMenuItem("Não gerenciar o áudio") { Checked = string.IsNullOrEmpty(cfg.Audio.DeviceId) };
        none.Click += (_, _) => UpdateConfig(c => { c.Audio.DeviceId = ""; c.Audio.FriendlyName = ""; });
        parent.DropDownItems.Add(none);
    }

    private void UpdateConfig(Action<AppConfig> change)
    {
        var cfg = _engine.Config.Clone();
        change(cfg);
        SaveAndApply(cfg);
    }

    public void SaveAndApply(AppConfig cfg)
    {
        try
        {
            ConfigStore.Save(cfg);
            Log.Info("Configuração salva");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar a configuração", ex);
            MessageBox.Show("Não foi possível salvar a configuração. Veja o log.", AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        ApplyKeepDisplayOn(cfg.KeepDisplayOn);
        _engine.ApplyConfig(cfg);
    }

    private static void ApplyKeepDisplayOn(bool on)
    {
        // Precisa ser chamado na thread da interface (thread persistente).
        Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS | (on ? Win32.ES_DISPLAY_REQUIRED : 0));
    }

    // ------------------------------------------------------------------ Telas

    private void ShowStatus()
    {
        if (_exiting) return;
        if (_statusForm == null || _statusForm.IsDisposed)
        {
            _statusForm = new StatusForm(_engine, TestPanelAsync, OpenLogs);
            _statusForm.FormClosed += (_, _) => _statusForm = null;
            _statusForm.Show();
        }
        _statusForm.Activate();
    }

    private void ShowSettings() => ShowSettings(false);

    private void ShowSettings(bool firstRun)
    {
        if (_exiting) return;
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_engine.Config.Clone(), firstRun, SaveAndApply,
                () => _engine.ResetLoginGuard(), () => _engine.RequestProfileReset());
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        _settingsForm.Activate();
    }

    private async Task TestPanelAsync()
    {
        _engine.Signal("test");
        string report;
        try { report = await Task.Run(_engine.RunDiagnosticsAsync); }
        catch (Exception ex) { report = "Falha no teste: " + ex.Message; }
        Log.Info("Teste do painel executado pelo administrador");
        MessageBox.Show(report, $"{AppPaths.AppName} — Teste", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Não foi possível abrir a pasta de logs: {ex.Message}"); }
    }

    private void AskExit()
    {
        var r = MessageBox.Show(
            "Encerrar o G-HOSP Panel Watchdog?\n\n" +
            "SIM — encerrar o watchdog e manter o painel aberto na TV (sem monitoramento).\n" +
            "NÃO — encerrar o watchdog e fechar também o painel.\n" +
            "CANCELAR — continuar monitorando.",
            AppPaths.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
        if (r == DialogResult.Yes) ExitApp(false);
        else if (r == DialogResult.No) ExitApp(true);
    }

    private async void ExitApp(bool closePanel)
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info(closePanel ? "Encerrando (fechando o painel)" : "Encerrando (painel permanece aberto)");
        _uiTimer.Stop();
        _statusForm?.Close();
        _settingsForm?.Close();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _windowHooks.Dispose();
        foreach (var w in _waits) w.Unregister(null);
        foreach (var e in _events) e.Dispose();
        try
        {
            await Task.Run(async () =>
            {
                await _engine.StopAsync(closePanel);
                await _engine.DisposeAsync();
            }).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { Log.Warn($"Encerramento incompleto: {ex.Message}"); }
        ApplyKeepDisplayOn(false);
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        ExitThread();
    }
}
