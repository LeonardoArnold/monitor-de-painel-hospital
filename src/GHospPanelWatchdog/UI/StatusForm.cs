using GHospPanelWatchdog.Watchdog;

namespace GHospPanelWatchdog.UI;

/// <summary>Tela de diagnóstico. Só aparece quando o administrador a abre pela bandeja.</summary>
internal sealed class StatusForm : Form
{
    private readonly WatchdogEngine _engine;
    private readonly Dictionary<string, Label> _values = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _header;

    public StatusForm(WatchdogEngine engine, Func<Task> test, Action openLogs)
    {
        _engine = engine;
        Text = "G-HOSP Panel Watchdog — Status";
        Icon = IconFactory.Create(HealthLevel.Ok, 32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        _header = new Label { Text = "PAINEL G-HOSP", Font = new Font("Segoe UI", 14f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(_header);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 460));
        foreach (var key in new[] { "Painel", "Edge", "Monitor", "Áudio", "Mute", "Internet", "Login", "Watchdog", "Última ação", "Atualizado" })
        {
            grid.Controls.Add(new Label { Text = key + ":", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 4, 8, 4) });
            var v = new Label { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 4, 0, 4) };
            _values[key] = v;
            grid.Controls.Add(v);
        }
        root.Controls.Add(grid);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 14, 0, 0) };
        var restart = new Button { Text = "Reiniciar painel", AutoSize = true };
        restart.Click += (_, _) =>
        {
            if (MessageBox.Show("Reiniciar o Edge do painel agora?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _engine.RequestRestart();
        };
        var testBtn = new Button { Text = "Testar painel", AutoSize = true };
        testBtn.Click += async (_, _) => { testBtn.Enabled = false; try { await test(); } finally { testBtn.Enabled = true; } };
        var logs = new Button { Text = "Abrir logs", AutoSize = true };
        logs.Click += (_, _) => openLogs();
        var copy = new Button { Text = "Copiar", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(BuildText()); } catch { } };
        var close = new Button { Text = "Fechar", AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        buttons.Controls.AddRange([restart, testBtn, logs, copy, close]);
        root.Controls.Add(buttons);
        CancelButton = close;

        Controls.Add(root);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshValues();
        _timer.Start();
        RefreshValues();
    }

    private void RefreshValues()
    {
        var s = _engine.Status;
        _values["Painel"].Text = s.Panel;
        _values["Edge"].Text = s.Edge;
        _values["Monitor"].Text = s.Monitor;
        _values["Áudio"].Text = s.Audio;
        _values["Mute"].Text = s.Mute;
        _values["Internet"].Text = s.Internet;
        _values["Login"].Text = s.Login;
        _values["Watchdog"].Text = s.Watchdog;
        _values["Última ação"].Text = string.IsNullOrEmpty(s.LastAction) ? "—" : s.LastAction;
        _values["Atualizado"].Text = s.UpdatedAt.ToString("HH:mm:ss");
        _values["Painel"].ForeColor = s.Level switch
        {
            HealthLevel.Ok => Color.FromArgb(30, 130, 50),
            HealthLevel.Warning => Color.FromArgb(180, 120, 0),
            HealthLevel.Error => Color.FromArgb(190, 40, 40),
            _ => SystemColors.ControlText,
        };
    }

    private string BuildText() =>
        "PAINEL G-HOSP\r\n" + string.Join("\r\n", _values.Select(kv => $"{kv.Key,-12} {kv.Value.Text}"));

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        Icon?.Dispose();
        base.OnFormClosed(e);
    }
}
