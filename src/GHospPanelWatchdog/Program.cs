using System.Diagnostics;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Edge;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Security;
using GHospPanelWatchdog.UI;

namespace GHospPanelWatchdog;

internal static class Program
{
    /// <summary>
    /// Argumentos:
    ///   (nenhum)       inicia o watchdog na bandeja
    ///   --config       inicia e abre as configurações (ou pede à instância em execução)
    ///   --status       pede à instância em execução para mostrar o status
    ///   --stop         encerra a instância em execução (o painel continua aberto)
    ///   --stop-all     encerra a instância em execução e fecha o painel
    ///   --diag         grava um relatório somente leitura (monitores, áudio, Edge) em logs\diagnostico.txt e o abre
    ///   --purge-data   encerra tudo e apaga credenciais, configuração, perfil do painel e logs (desinstalador)
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var a = new HashSet<string>(args.Select(x => x.Trim().ToLowerInvariant()));

        if (a.Contains("--stop")) return InstanceSignals.SignalAndWait(InstanceSignals.Exit) ? 0 : 1;
        if (a.Contains("--stop-all")) return InstanceSignals.SignalAndWait(InstanceSignals.ExitClosePanel) ? 0 : 1;
        if (a.Contains("--purge-data")) return PurgeData();
        if (a.Contains("--diag")) return WriteDiagnostics();

        using var mutex = new Mutex(true, InstanceSignals.MutexName, out bool isFirst);
        if (!isFirst)
        {
            InstanceSignals.Signal(a.Contains("--config") ? InstanceSignals.ShowSettings : InstanceSignals.ShowStatus);
            return 0;
        }

        AppPaths.EnsureDirectories();
        Log.Init(AppPaths.LogsDir);
        Log.Info($"Aplicativo iniciado — versão {Application.ProductVersion}, PID {Environment.ProcessId}, usuário {Environment.UserName}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Exceção não tratada", e.ExceptionObject as Exception ?? new Exception("desconhecida"));
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Exceção na interface", e.Exception);

        ApplicationConfiguration.Initialize();
        bool firstRun = !ConfigStore.Exists;
        try
        {
            Application.Run(new TrayApp(openSettings: firstRun || a.Contains("--config"), firstRun));
        }
        finally
        {
            Log.Info("Aplicativo encerrado");
            Log.Shutdown();
        }
        return 0;
    }

    private static int WriteDiagnostics()
    {
        AppPaths.EnsureDirectories();
        var cfg = ConfigStore.Load();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{AppPaths.AppName} {Application.ProductVersion} — diagnóstico {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}  Usuário: {Environment.UserName}");
        sb.AppendLine($"Dados: {AppPaths.DataDir}");
        sb.AppendLine($"URL: {cfg.PanelUrl}");
        sb.AppendLine($"Edge: {EdgeLocator.Find(cfg.EdgePath) ?? "NÃO ENCONTRADO"}");
        sb.AppendLine($"Credenciais cadastradas: {(CredentialStore.HasCredential() ? "sim" : "não")}");
        sb.AppendLine();
        var monitors = GHospPanelWatchdog.Display.MonitorService.GetMonitors();
        var target = GHospPanelWatchdog.Display.MonitorService.ResolveTarget(cfg.Monitor, monitors);
        sb.AppendLine($"Monitores ({monitors.Count}) — modo {cfg.Monitor.Mode}:");
        foreach (var m in monitors)
            sb.AppendLine($"  {(m == target ? "►" : " ")} {m.Describe()}  pos {m.Bounds}  DPI {m.Dpi}\r\n      {m.DevicePath}");
        sb.AppendLine(target == null ? "  Monitor do painel: NÃO encontrado" : $"  Monitor do painel: {target.Describe()}");
        sb.AppendLine();
        sb.AppendLine("Saídas de áudio:");
        foreach (var d in GHospPanelWatchdog.Audio.AudioService.ListOutputDevices(includeInactive: true))
            sb.AppendLine($"  {(d.Id == cfg.Audio.DeviceId ? "►" : " ")} {d}\r\n      {d.Id}");
        sb.AppendLine(string.IsNullOrEmpty(cfg.Audio.DeviceId) ? "  Áudio do painel: não configurado" : $"  Áudio do painel: {cfg.Audio.FriendlyName}");
        var panel = new PanelBrowser(AppPaths.EdgeProfileDir, _ => { });
        int pid = panel.FindBrowser();
        sb.AppendLine();
        sb.AppendLine(pid == 0 ? "Edge do painel: não está em execução" : $"Edge do painel: PID {pid}, janela {(panel.FindWindow() != IntPtr.Zero ? "encontrada" : "não encontrada")}");
        var file = Path.Combine(AppPaths.LogsDir, "diagnostico.txt");
        File.WriteAllText(file, sb.ToString(), new System.Text.UTF8Encoding(true));
        if (!args_NoOpen) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } }
        return 0;
    }

    private static bool args_NoOpen => Environment.GetCommandLineArgs().Any(x => x.Equals("--no-open", StringComparison.OrdinalIgnoreCase));

    private static int PurgeData()
    {
        // 1. Encerra a instância em execução fechando o painel.
        InstanceSignals.SignalAndWait(InstanceSignals.ExitClosePanel);

        // 2. Garante que nenhum processo do perfil do painel ficou aberto (apenas o perfil do painel).
        try
        {
            var browser = new PanelBrowser(AppPaths.EdgeProfileDir, _ => { });
            if (browser.FindBrowser() != 0) browser.KillAsync("desinstalação").GetAwaiter().GetResult();
        }
        catch { }

        // 3. Credencial do Gerenciador de Credenciais.
        try { CredentialStore.Delete(); } catch { }

        // 4. Dados locais.
        for (int i = 0; i < 10 && Directory.Exists(AppPaths.DataDir); i++)
        {
            try { Directory.Delete(AppPaths.DataDir, true); }
            catch { Thread.Sleep(500); }
        }
        return 0;
    }
}

/// <summary>Comunicação simples entre instâncias (instância única por sessão do Windows).</summary>
internal static class InstanceSignals
{
    public const string MutexName = @"Local\GHospPanelWatchdog.Instance";
    public const string ShowStatus = @"Local\GHospPanelWatchdog.ShowStatus";
    public const string ShowSettings = @"Local\GHospPanelWatchdog.ShowSettings";
    public const string Exit = @"Local\GHospPanelWatchdog.Exit";
    public const string ExitClosePanel = @"Local\GHospPanelWatchdog.ExitClosePanel";

    public static EventWaitHandle Create(string name) => new(false, EventResetMode.AutoReset, name);

    public static bool Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var h)) { using (h) h.Set(); return true; }
        }
        catch { }
        return false;
    }

    /// <summary>Sinaliza e aguarda a instância em execução terminar (até 20 s).</summary>
    public static bool SignalAndWait(string name)
    {
        if (!Signal(name)) return true; // nada em execução
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            try
            {
                if (!Mutex.TryOpenExisting(MutexName, out var m)) return true;
                m.Dispose();
            }
            catch { return true; }
            Thread.Sleep(250);
        }
        return false;
    }
}
