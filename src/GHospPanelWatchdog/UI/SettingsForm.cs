using System.Diagnostics;
using GHospPanelWatchdog.Audio;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Display;
using GHospPanelWatchdog.Edge;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Security;
using GHospPanelWatchdog.Watchdog;

namespace GHospPanelWatchdog.UI;

/// <summary>Configurações (somente administrador). A senha vai direto para o Gerenciador de Credenciais.</summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Action<AppConfig> _save;
    private readonly Action _credentialsChanged;
    private readonly Action _resetProfile;

    // Geral
    private readonly TextBox _url = new() { Width = 420 };
    private readonly NumericUpDown _interval = new() { Minimum = 2, Maximum = 60, Width = 70 };
    private readonly NumericUpDown _reload = new() { Minimum = 0, Maximum = 720, Width = 70 };
    private readonly CheckBox _keepDisplay = new() { Text = "Impedir que o Windows desligue o vídeo (afeta todos os monitores)", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Iniciar com o Windows", AutoSize = true, Enabled = false, Checked = false };

    // Monitor
    private readonly RadioButton _monAuto = new() { Text = "Automático — primeiro monitor secundário (recomendado com 2 monitores)", AutoSize = true };
    private readonly RadioButton _monSpecific = new() { Text = "Monitor específico:", AutoSize = true };
    private readonly ListBox _monitors = new() { Width = 520, Height = 120 };

    // Áudio
    private readonly ComboBox _audio = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _audioConsole = new() { Text = "Definir como padrão (sons e multimídia)", AutoSize = true };
    private readonly CheckBox _audioComms = new() { Text = "Definir também como padrão de comunicações", AutoSize = true };
    private readonly CheckBox _audioFallback = new() { Text = "Se o ID mudar (driver reinstalado), localizar pelo mesmo nome", AutoSize = true };

    // Login
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Label _credState = new() { AutoSize = true };
    private readonly TextBox _selUser = new() { Width = 300 };
    private readonly TextBox _selPass = new() { Width = 300 };
    private readonly TextBox _selSubmit = new() { Width = 300 };

    // Recuperação
    private readonly CheckBox _reopen = new() { Text = "Reabrir painel se fechar", AutoSize = true };
    private readonly CheckBox _hang = new() { Text = "Recuperar após travamento", AutoSize = true };
    private readonly CheckBox _tv = new() { Text = "Recuperar após reconexão da TV (e minimizar o painel se a TV sumir)", AutoSize = true };
    private readonly CheckBox _audioDevice = new() { Text = "Garantir dispositivo de áudio correto", AutoSize = true };
    private readonly CheckBox _unmute = new() { Text = "Garantir áudio desmutado (o volume nunca é alterado)", AutoSize = true };
    private readonly CheckBox _sessionUnmute = new() { Text = "Desmutar o Edge do painel no mixer de volume", AutoSize = true };
    private readonly CheckBox _autoLogin = new() { Text = "Login automático quando o site pedir", AutoSize = true };
    private readonly CheckBox _onTop = new() { Text = "Manter o painel sempre à frente na TV", AutoSize = true };
    private readonly ComboBox _intruders = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };

    // Edge
    private readonly TextBox _edgePath = new() { Width = 420 };
    private readonly TextBox _edgeArgs = new() { Width = 420 };

    private List<MonitorInfo> _monitorList = new();
    private List<AudioDeviceInfo> _audioList = new();

    public SettingsForm(AppConfig cfg, bool firstRun, Action<AppConfig> save, Action credentialsChanged, Action resetProfile)
    {
        _cfg = cfg;
        _save = save;
        _credentialsChanged = credentialsChanged;
        _resetProfile = resetProfile;

        Text = "G-HOSP Panel Watchdog — Configurações";
        Icon = IconFactory.Create(HealthLevel.Ok, 32);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(640, 520);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneral());
        tabs.TabPages.Add(BuildMonitor());
        tabs.TabPages.Add(BuildAudio());
        tabs.TabPages.Add(BuildLogin());
        tabs.TabPages.Add(BuildRecovery());
        tabs.TabPages.Add(BuildEdge());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Text = "Salvar", AutoSize = true };
        ok.Click += (_, _) => { if (SaveAll()) Close(); };
        bottom.Controls.AddRange([cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(bottom);

        LoadValues();
        if (firstRun)
        {
            Shown += (_, _) => MessageBox.Show(this,
                "Primeira execução.\n\nConfira a URL do painel, o monitor da TV, o dispositivo de áudio (ex.: \"Multi\") " +
                "e cadastre o usuário e a senha do G-HOSP na aba Login.\n\nA inicialização automática com o Windows NÃO é configurada nesta versão.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ------------------------------------------------------------------ Abas

    private static TabPage Page(string title, out FlowLayoutPanel body)
    {
        var page = new TabPage(title) { Padding = new Padding(12), AutoScroll = true };
        body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.Add(body);
        return page;
    }

    private static Label Caption(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(0, 10, 0, 3),
        Font = bold ? new Font("Segoe UI", 9.5f, FontStyle.Bold) : null!,
    };

    private static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(580, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4),
    };

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(controls);
        return p;
    }

    private TabPage BuildGeneral()
    {
        var page = Page("Geral", out var b);
        var def = new Button { Text = "Padrão", AutoSize = true };
        def.Click += (_, _) => _url.Text = AppConfig.DefaultPanelUrl;
        b.Controls.Add(Caption("URL do painel", true));
        b.Controls.Add(Inline(_url, def));
        b.Controls.Add(Caption("Watchdog", true));
        b.Controls.Add(Inline(new Label { Text = "Verificação periódica a cada", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _interval,
            new Label { Text = "segundos (eventos do Windows disparam verificações imediatas)", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        b.Controls.Add(Inline(new Label { Text = "Recarregar o painel a cada", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _reload,
            new Label { Text = "horas (0 = nunca)", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }));
        b.Controls.Add(_keepDisplay);
        b.Controls.Add(Caption("Inicialização", true));
        b.Controls.Add(_startWithWindows);
        b.Controls.Add(Note("Desativado nesta fase de testes. O programa não altera a inicialização do Windows; " +
                            "veja no README como configurar manualmente depois que os testes forem aprovados."));
        return page;
    }

    private TabPage BuildMonitor()
    {
        var page = Page("Monitor", out var b);
        b.Controls.Add(Caption("Monitor onde o painel é exibido (TV)", true));
        b.Controls.Add(_monAuto);
        b.Controls.Add(_monSpecific);
        b.Controls.Add(_monitors);
        var refresh = new Button { Text = "Atualizar lista", AutoSize = true };
        refresh.Click += (_, _) => FillMonitors();
        b.Controls.Add(refresh);
        b.Controls.Add(Note("O monitor é identificado pelo caminho do dispositivo (estável entre reconexões), não por coordenadas. " +
                            "Se a TV for desconectada, o painel é minimizado sem foco e volta sozinho quando ela reaparecer."));
        _monitors.SelectedIndexChanged += (_, _) => { if (_monitors.SelectedIndex >= 0) _monSpecific.Checked = true; };
        return page;
    }

    private TabPage BuildAudio()
    {
        var page = Page("Áudio", out var b);
        b.Controls.Add(Caption("Dispositivo de saída do painel (ex.: \"Multi\" / HDMI da TV)", true));
        var refresh = new Button { Text = "Atualizar", AutoSize = true };
        refresh.Click += (_, _) => FillAudio(_audio.SelectedItem as AudioDeviceInfo);
        b.Controls.Add(Inline(_audio, refresh));
        b.Controls.Add(Note("O dispositivo é salvo pelo seu ID persistente do Windows; o nome é usado só para exibição. " +
                            "Se a TV/driver mudar, selecione novamente aqui."));
        b.Controls.Add(_audioConsole);
        b.Controls.Add(_audioComms);
        b.Controls.Add(_audioFallback);
        b.Controls.Add(Note("O watchdog troca o dispositivo padrão e remove o mute pelas APIs do Windows — sem cliques, sem abrir o menu de volume. " +
                            "O nível de volume nunca é alterado."));
        return page;
    }

    private TabPage BuildLogin()
    {
        var page = Page("Login", out var b);
        b.Controls.Add(Caption("Credenciais do G-HOSP", true));
        b.Controls.Add(Inline(new Label { Text = "Usuário:", AutoSize = true, Width = 70, Margin = new Padding(0, 6, 4, 0) }, _user));
        b.Controls.Add(Inline(new Label { Text = "Senha:", AutoSize = true, Width = 70, Margin = new Padding(0, 6, 12, 0) }, _password));
        b.Controls.Add(_credState);
        var remove = new Button { Text = "Remover credenciais", AutoSize = true };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remover usuário e senha do Gerenciador de Credenciais do Windows?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { CredentialStore.Delete(); Log.Info("Credenciais removidas pelo administrador"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text); }
            _user.Text = "";
            _password.Text = "";
            UpdateCredState();
        };
        b.Controls.Add(remove);
        b.Controls.Add(Note("A senha é guardada no Gerenciador de Credenciais do Windows (protegida pelo DPAPI, vinculada a este usuário do Windows). " +
                            "Nunca é gravada em arquivo, log ou mensagem. Deixe a senha em branco para manter a atual."));
        b.Controls.Add(Caption("Avançado — seletores CSS (opcional; deixe vazio para detecção automática)", true));
        b.Controls.Add(Inline(new Label { Text = "Usuário:", AutoSize = true, Width = 90, Margin = new Padding(0, 6, 4, 0) }, _selUser));
        b.Controls.Add(Inline(new Label { Text = "Senha:", AutoSize = true, Width = 90, Margin = new Padding(0, 6, 4, 0) }, _selPass));
        b.Controls.Add(Inline(new Label { Text = "Botão entrar:", AutoSize = true, Width = 90, Margin = new Padding(0, 6, 4, 0) }, _selSubmit));
        b.Controls.Add(Note("Exemplos: #usuario, input[name=login], button[type=submit]"));
        return page;
    }

    private TabPage BuildRecovery()
    {
        var page = Page("Recuperação", out var b);
        b.Controls.Add(Caption("Ações automáticas", true));
        b.Controls.AddRange([_reopen, _hang, _tv, _audioDevice, _unmute, _sessionUnmute, _autoLogin]);
        b.Controls.Add(Caption("TV exclusiva do painel", true));
        b.Controls.Add(_onTop);
        _intruders.Items.AddRange(["Mover para o monitor principal (recomendado)", "Minimizar", "Não fazer nada"]);
        b.Controls.Add(Inline(new Label { Text = "Janelas abertas sobre a TV:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, _intruders));
        b.Controls.Add(Note("Se alguém abrir ou arrastar outro programa para a TV, ele volta para o monitor principal sem tirar o foco " +
                            "(o funcionário continua digitando). Para usar a TV em manutenção, pause o watchdog pela bandeja."));
        b.Controls.Add(Note("Reaberturas usam espera progressiva (5 s, 10 s, 20 s … até 5 min). Queda de internet não reinicia o Edge: " +
                            "o painel aguarda e recarrega quando o servidor voltar."));
        return page;
    }

    private TabPage BuildEdge()
    {
        var page = Page("Edge", out var b);
        var browse = new Button { Text = "Procurar…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "msedge.exe|msedge.exe", Title = "Localizar o Microsoft Edge" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _edgePath.Text = dlg.FileName;
        };
        b.Controls.Add(Caption("Caminho do msedge.exe (vazio = automático)", true));
        b.Controls.Add(Inline(_edgePath, browse));
        b.Controls.Add(Note("Detectado: " + (EdgeLocator.Find("") ?? "não encontrado")));
        b.Controls.Add(Caption("Argumentos extras (avançado)", true));
        b.Controls.Add(_edgeArgs);
        b.Controls.Add(Caption("Perfil exclusivo do painel", true));
        b.Controls.Add(Note(AppPaths.EdgeProfileDir));
        var open = new Button { Text = "Abrir pasta do perfil", AutoSize = true };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.EdgeProfileDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.EdgeProfileDir}\"") { UseShellExecute = true });
        };
        var reset = new Button { Text = "Limpar perfil do painel…", AutoSize = true };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Fechar o painel, apagar o perfil exclusivo dele (cookies/sessão) e reabrir?\n\n" +
                                      "O Edge do funcionário não é afetado. O login automático fará uma nova autenticação.",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                _resetProfile();
        };
        b.Controls.Add(Inline(open, reset));
        return page;
    }

    // ------------------------------------------------------------------ Valores

    private void LoadValues()
    {
        _url.Text = _cfg.PanelUrl;
        _interval.Value = Math.Clamp(_cfg.CheckIntervalSeconds, 2, 60);
        _reload.Value = Math.Clamp(_cfg.PeriodicReloadHours, 0, 720);
        _keepDisplay.Checked = _cfg.KeepDisplayOn;

        _monAuto.Checked = _cfg.Monitor.Mode == MonitorMode.AutoSecondary;
        _monSpecific.Checked = _cfg.Monitor.Mode == MonitorMode.Specific;
        FillMonitors();

        FillAudio(null);
        _audioConsole.Checked = _cfg.Audio.SetForConsoleAndMultimedia;
        _audioComms.Checked = _cfg.Audio.SetForCommunications;
        _audioFallback.Checked = _cfg.Audio.AllowNameFallback;

        _user.Text = CredentialStore.ReadUserName() ?? "";
        UpdateCredState();
        _selUser.Text = _cfg.Login.UserSelector;
        _selPass.Text = _cfg.Login.PasswordSelector;
        _selSubmit.Text = _cfg.Login.SubmitSelector;

        _reopen.Checked = _cfg.Recovery.ReopenIfClosed;
        _hang.Checked = _cfg.Recovery.RecoverAfterHang;
        _tv.Checked = _cfg.Recovery.RecoverAfterDisplayReconnect;
        _audioDevice.Checked = _cfg.Recovery.EnsureAudioDevice;
        _unmute.Checked = _cfg.Recovery.EnsureUnmuted;
        _sessionUnmute.Checked = _cfg.Recovery.UnmutePanelSession;
        _autoLogin.Checked = _cfg.Recovery.AutoLogin;
        _onTop.Checked = _cfg.Recovery.PanelAlwaysOnTop;
        _intruders.SelectedIndex = (int)_cfg.Recovery.TvIntruders;

        _edgePath.Text = _cfg.EdgePath;
        _edgeArgs.Text = _cfg.ExtraEdgeArgs;
    }

    private void UpdateCredState() =>
        _credState.Text = CredentialStore.HasCredential() ? "✔ Senha cadastrada no Gerenciador de Credenciais" : "✖ Nenhuma senha cadastrada";

    private void FillMonitors()
    {
        _monitorList = MonitorService.GetMonitors();
        _monitors.Items.Clear();
        foreach (var m in _monitorList) _monitors.Items.Add(m.Describe());
        var target = MonitorService.ResolveTarget(_cfg.Monitor, _monitorList);
        if (_cfg.Monitor.Mode == MonitorMode.Specific && target != null)
            _monitors.SelectedIndex = _monitorList.IndexOf(target);
        if (_cfg.Monitor.Mode == MonitorMode.Specific && target == null && !string.IsNullOrEmpty(_cfg.Monitor.FriendlyName))
        {
            _monitors.Items.Add($"(configurado, desconectado agora) {_cfg.Monitor.FriendlyName}");
        }
        if (_cfg.Monitor.Mode == MonitorMode.AutoSecondary) _monAuto.Checked = true;
    }

    private void FillAudio(AudioDeviceInfo? keep)
    {
        _audioList = AudioService.ListOutputDevices();
        _audio.Items.Clear();
        _audio.Items.Add("(não gerenciar o áudio)");
        foreach (var d in _audioList) _audio.Items.Add(d);
        string wanted = keep?.Id ?? _cfg.Audio.DeviceId;
        var sel = _audioList.FirstOrDefault(d => d.Id == wanted);
        if (sel != null) _audio.SelectedItem = sel;
        else if (!string.IsNullOrEmpty(_cfg.Audio.DeviceId) && keep == null)
        {
            var missing = new AudioDeviceInfo(_cfg.Audio.DeviceId, _cfg.Audio.FriendlyName, 0, false);
            _audio.Items.Add(missing);
            _audio.SelectedItem = missing;
        }
        else _audio.SelectedIndex = 0;
    }

    private bool SaveAll()
    {
        var url = _url.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
        {
            MessageBox.Show(this, "URL do painel inválida.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (_monSpecific.Checked && (_monitors.SelectedIndex < 0 || _monitors.SelectedIndex >= _monitorList.Count) &&
            string.IsNullOrEmpty(_cfg.Monitor.DevicePath))
        {
            MessageBox.Show(this, "Selecione o monitor na lista ou use o modo automático.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (_monSpecific.Checked && _monitors.SelectedIndex >= 0 && _monitors.SelectedIndex < _monitorList.Count &&
            _monitorList[_monitors.SelectedIndex].IsPrimary &&
            MessageBox.Show(this, "O monitor selecionado é o PRINCIPAL (do funcionário). O painel ocupará a tela inteira dele.\n\nUse apenas para testes. Continuar?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        // Credenciais (antes de salvar o restante).
        try
        {
            var user = _user.Text.Trim();
            if (_password.Text.Length > 0)
            {
                if (user.Length == 0)
                {
                    MessageBox.Show(this, "Informe o usuário.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                CredentialStore.Write(user, _password.Text);
                _password.Text = "";
                Log.Info("Credenciais do G-HOSP atualizadas pelo administrador");
                _credentialsChanged();
            }
            else if (CredentialStore.Read() is { } existing && existing.UserName != user && user.Length > 0)
            {
                CredentialStore.Write(user, existing.Password);
                Log.Info("Usuário do G-HOSP atualizado pelo administrador");
                _credentialsChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao gravar credenciais", ex);
            MessageBox.Show(this, "Não foi possível gravar as credenciais no Gerenciador de Credenciais do Windows.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        var c = _cfg;
        c.PanelUrl = url;
        c.CheckIntervalSeconds = (int)_interval.Value;
        c.PeriodicReloadHours = (int)_reload.Value;
        c.KeepDisplayOn = _keepDisplay.Checked;
        c.StartWithWindows = false;

        if (_monAuto.Checked) c.Monitor = new MonitorSelection { Mode = MonitorMode.AutoSecondary };
        else if (_monitors.SelectedIndex >= 0 && _monitors.SelectedIndex < _monitorList.Count)
        {
            var m = _monitorList[_monitors.SelectedIndex];
            c.Monitor = new MonitorSelection { Mode = MonitorMode.Specific, DevicePath = m.DevicePath, GdiName = m.GdiName, FriendlyName = m.FriendlyName };
        }

        if (_audio.SelectedItem is AudioDeviceInfo d) { c.Audio.DeviceId = d.Id; c.Audio.FriendlyName = d.FriendlyName; }
        else { c.Audio.DeviceId = ""; c.Audio.FriendlyName = ""; }
        c.Audio.SetForConsoleAndMultimedia = _audioConsole.Checked;
        c.Audio.SetForCommunications = _audioComms.Checked;
        c.Audio.AllowNameFallback = _audioFallback.Checked;

        c.Login.UserSelector = _selUser.Text.Trim();
        c.Login.PasswordSelector = _selPass.Text.Trim();
        c.Login.SubmitSelector = _selSubmit.Text.Trim();

        c.Recovery.ReopenIfClosed = _reopen.Checked;
        c.Recovery.RecoverAfterHang = _hang.Checked;
        c.Recovery.RecoverAfterDisplayReconnect = _tv.Checked;
        c.Recovery.EnsureAudioDevice = _audioDevice.Checked;
        c.Recovery.EnsureUnmuted = _unmute.Checked;
        c.Recovery.UnmutePanelSession = _sessionUnmute.Checked;
        c.Recovery.AutoLogin = _autoLogin.Checked;
        c.Recovery.PanelAlwaysOnTop = _onTop.Checked;
        c.Recovery.TvIntruders = (TvIntruderAction)Math.Max(0, _intruders.SelectedIndex);

        c.EdgePath = _edgePath.Text.Trim();
        c.ExtraEdgeArgs = _edgeArgs.Text.Trim();

        _save(c.Clone());
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _password.Text = "";
        Icon?.Dispose();
        base.OnFormClosed(e);
    }
}
