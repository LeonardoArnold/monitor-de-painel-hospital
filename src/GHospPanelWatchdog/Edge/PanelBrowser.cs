using System.Diagnostics;
using System.Text.Json.Nodes;
using GHospPanelWatchdog.Config;
using GHospPanelWatchdog.Display;
using GHospPanelWatchdog.Logging;
using GHospPanelWatchdog.Native;
using GHospPanelWatchdog.Security;

namespace GHospPanelWatchdog.Edge;

internal sealed record PageState(
    string Url,
    string ReadyState,
    bool HasLoginForm,
    int? HttpStatus)
{
    public bool IsErrorPage =>
        Url.StartsWith("chrome-error:", StringComparison.OrdinalIgnoreCase) ||
        Url.StartsWith("edge-error:", StringComparison.OrdinalIgnoreCase);
    public bool IsServerError => HttpStatus is >= 500;
}

/// <summary>
/// Controla exclusivamente a instância do Edge do painel (perfil dedicado).
/// Nunca enumera, fecha ou altera processos do Edge que não usem o perfil do painel.
/// </summary>
internal sealed class PanelBrowser : IAsyncDisposable
{
    private readonly string _profileDir;
    private readonly Action<string> _onChange;
    private Process? _browser;
    private int _browserPid;

    private CdpClient? _cdp;
    private string? _pageTargetId;
    private string? _pageSession;
    private volatile bool _pageCrashed;

    public PanelBrowser(string profileDir, Action<string> onChange)
    {
        _profileDir = Path.GetFullPath(profileDir).TrimEnd('\\');
        _onChange = onChange;
    }

    public string ProfileDir => _profileDir;
    public int BrowserPid => _browserPid;
    public bool PageCrashed => _pageCrashed;
    public bool CdpConnected => _cdp?.IsOpen == true;

    // ================================================================ Processo

    /// <summary>
    /// Retorna o PID do processo principal do Edge do painel, ou 0. Usa o PID em cache enquanto
    /// ele estiver vivo; só varre os processos msedge.exe (lendo a linha de comando) quando necessário.
    /// </summary>
    public int FindBrowser()
    {
        if (_browser != null)
        {
            try { if (!_browser.HasExited) return _browserPid; } catch { }
            ForgetBrowser();
        }

        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            bool keep = false;
            try
            {
                var cmd = ProcessNative.GetCommandLine(p.Id);
                if (cmd != null && IsPanelBrowserCommandLine(cmd))
                {
                    p.EnableRaisingEvents = true;
                    p.Exited += OnBrowserExited;
                    _browser = p;
                    _browserPid = p.Id;
                    keep = true;
                    return _browserPid;
                }
            }
            catch { }
            finally { if (!keep) p.Dispose(); }
        }
        return 0;
    }

    private bool IsPanelBrowserCommandLine(string cmd) =>
        cmd.Contains("--user-data-dir", StringComparison.OrdinalIgnoreCase) &&
        cmd.Contains(_profileDir, StringComparison.OrdinalIgnoreCase) &&
        !cmd.Contains("--type=", StringComparison.OrdinalIgnoreCase);

    private bool IsPanelProcessCommandLine(string cmd) =>
        cmd.Contains(_profileDir, StringComparison.OrdinalIgnoreCase);

    private void OnBrowserExited(object? sender, EventArgs e) => _onChange("edge-exited");

    private void ForgetBrowser()
    {
        if (_browser != null)
        {
            _browser.Exited -= OnBrowserExited;
            _browser.Dispose();
        }
        _browser = null;
        _browserPid = 0;
        _ = DropCdpAsync();
    }

    /// <summary>PIDs do Edge do painel (processo principal + filhos: renderer, GPU, áudio...).</summary>
    public HashSet<int>? GetProcessTree() => _browserPid == 0 ? null : ProcessNative.GetProcessTree(_browserPid);

    /// <summary>
    /// Inicia o Edge do painel. A janela já nasce dentro do monitor da TV e é exibida sem ativação.
    /// Se já houver um Edge com este perfil, o Chromium apenas abre uma nova janela nele.
    /// </summary>
    public int Launch(string edgePath, AppConfig cfg, MonitorInfo target, MonitorInfo? primary)
    {
        Directory.CreateDirectory(_profileDir);
        SeedPreferences();
        if (FindBrowser() == 0)
        {
            try { File.Delete(Path.Combine(_profileDir, "DevToolsActivePort")); } catch { }
        }

        // --window-position usa coordenadas lógicas (DIP) do Chromium; estimativa conservadora
        // que cai dentro da TV. A posição exata é corrigida em seguida com SetWindowPos.
        double primaryScale = (primary?.Dpi ?? 96) / 96.0;
        int x = (int)(target.Bounds.Left / primaryScale) + 40;
        int y = (int)(target.Bounds.Top / primaryScale) + 40;

        var args = new List<string>
        {
            $"--user-data-dir=\"{_profileDir}\"",
            $"--app=\"{cfg.PanelUrl}\"",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required",
            "--hide-crash-restore-bubble",
            "--disable-session-crashed-bubble",
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-extensions",
            "--disable-sync",
            "--disable-background-mode",
            "--disable-features=Translate",
            "--no-pings",
            "--overscroll-history-navigation=0",
            "--disable-pinch",
            $"--window-position={x},{y}",
            "--window-size=800,600",
        };
        if (!string.IsNullOrWhiteSpace(cfg.ExtraEdgeArgs)) args.Add(cfg.ExtraEdgeArgs.Trim());

        int pid = ProcessNative.StartNoActivate(edgePath, string.Join(" ", args), Path.GetDirectoryName(edgePath)!);
        Log.Info($"Edge do painel iniciado (PID inicial {pid}) no monitor {target.Describe()}");
        return pid;
    }

    /// <summary>
    /// Pré-configura o perfil novo: sem gerenciador de senhas (sem balão "Salvar senha?" na TV)
    /// e sem tradutor. Só escreve se o perfil ainda não tiver preferências.
    /// </summary>
    private void SeedPreferences()
    {
        try
        {
            var dir = Path.Combine(_profileDir, "Default");
            var file = Path.Combine(dir, "Preferences");
            if (File.Exists(file)) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, """
                {
                  "credentials_enable_service": false,
                  "profile": { "password_manager_enabled": false, "default_content_setting_values": { "notifications": 2 } },
                  "translate": { "enabled": false },
                  "autofill": { "profile_enabled": false, "credit_card_enabled": false },
                  "browser": { "has_seen_welcome_page": true }
                }
                """);
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível pré-configurar o perfil do Edge: {ex.Message}");
        }
    }

    /// <summary>Encerra SOMENTE o Edge do painel (árvore do processo principal + remanescentes do perfil).</summary>
    public async Task KillAsync(string reason)
    {
        Log.Warn($"Encerrando o Edge do painel: {reason}");
        await DropCdpAsync().ConfigureAwait(false);
        int pid = FindBrowser();
        if (pid != 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex) { Log.Warn($"Falha ao encerrar o Edge do painel (PID {pid}): {ex.Message}"); }
        }
        ForgetBrowser();

        // Remanescentes que usem o perfil do painel (ex.: crashpad). Nunca toca em outros perfis.
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            using (p)
            {
                try
                {
                    var cmd = ProcessNative.GetCommandLine(p.Id);
                    if (cmd != null && IsPanelProcessCommandLine(cmd)) { p.Kill(); p.WaitForExit(2000); }
                }
                catch { }
            }
        }
    }

    /// <summary>Encerramento gentil (usado em "Sair e fechar painel").</summary>
    public async Task CloseGracefullyAsync()
    {
        try
        {
            if (await EnsureCdpAsync().ConfigureAwait(false))
            {
                await _cdp!.SendAsync("Browser.close", timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                for (int i = 0; i < 20 && FindBrowser() != 0; i++) await Task.Delay(250).ConfigureAwait(false);
            }
        }
        catch { }
        if (FindBrowser() != 0) await KillAsync("encerramento solicitado").ConfigureAwait(false);
    }

    // ================================================================ Janela

    /// <summary>Janela principal (modo app) do Edge do painel, ou IntPtr.Zero.</summary>
    public IntPtr FindWindow()
    {
        int pid = _browserPid;
        if (pid == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint wpid);
            if (wpid != pid || !Win32.IsWindowVisible(h)) return true;
            if (Win32.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            if ((Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            if (Win32.GetClassName(h) != "Chrome_WidgetWin_1") return true;
            Win32.GetWindowRect(h, out var r);
            long area = Math.Max(1, (long)r.Width * r.Height);
            if (Win32.IsIconic(h)) area = 1; // minimizada ainda é a janela do painel
            if (area > bestArea) { best = h; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ================================================================ CDP

    public async Task<bool> EnsureCdpAsync()
    {
        if (_cdp?.IsOpen == true) return true;
        await DropCdpAsync().ConfigureAwait(false);

        var portFile = Path.Combine(_profileDir, "DevToolsActivePort");
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(portFile).ConfigureAwait(false); }
        catch { return false; }
        if (lines.Length < 2 || !int.TryParse(lines[0], out int port)) return false;

        try
        {
            var cdp = await CdpClient.ConnectAsync(port, lines[1].Trim(), TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            cdp.EventReceived += OnCdpEvent;
            await cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }).ConfigureAwait(false);
            _cdp = cdp;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnCdpEvent(string method, string? sessionId, JsonNode? p)
    {
        string? targetId = p?["targetId"]?.GetValue<string>();
        string? session = p?["sessionId"]?.GetValue<string>();
        if (method == "Page.javascriptDialogOpening" && sessionId != null && sessionId == _pageSession)
        {
            // alert()/confirm() nativo bloqueia o JavaScript da página: aceita automaticamente.
            // Não pode aguardar aqui (estamos no laço de recepção do CDP).
            var msg = p?["message"]?.GetValue<string>() ?? "";
            var type = p?["type"]?.GetValue<string>() ?? "";
            var cdp = _cdp;
            _ = Task.Run(async () =>
            {
                try
                {
                    await cdp!.SendAsync("Page.handleJavaScriptDialog", new JsonObject { ["accept"] = true }, sessionId).ConfigureAwait(false);
                    Log.Info($"Diálogo nativo do site fechado automaticamente ({type}): \"{(msg.Length > 80 ? msg[..80] + "…" : msg)}\"");
                }
                catch { }
            });
            return;
        }
        if (method == "Target.targetCrashed" && targetId != null && targetId == _pageTargetId)
        {
            _pageCrashed = true;
            _onChange("page-crashed");
        }
        else if ((method == "Target.targetDestroyed" && targetId != null && targetId == _pageTargetId) ||
                 (method == "Target.detachedFromTarget" && session != null && session == _pageSession))
        {
            _pageSession = null;
            _pageTargetId = null;
            _onChange("page-gone");
        }
    }

    public async Task DropCdpAsync()
    {
        var c = _cdp;
        _cdp = null;
        _pageSession = null;
        _pageTargetId = null;
        if (c != null)
        {
            c.EventReceived -= OnCdpEvent;
            await c.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string?> EnsurePageSessionAsync()
    {
        if (_pageSession != null) return _pageSession;
        if (!await EnsureCdpAsync().ConfigureAwait(false)) return null;
        var res = await _cdp!.SendAsync("Target.getTargets").ConfigureAwait(false);
        var page = res?["targetInfos"]?.AsArray()
            .Select(t => t!)
            .FirstOrDefault(t => t["type"]?.GetValue<string>() == "page" &&
                                 !(t["url"]?.GetValue<string>() ?? "").StartsWith("devtools:", StringComparison.OrdinalIgnoreCase));
        if (page == null) return null;
        var targetId = page["targetId"]!.GetValue<string>();
        var att = await _cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }).ConfigureAwait(false);
        _pageTargetId = targetId;
        _pageSession = att?["sessionId"]?.GetValue<string>();
        _pageCrashed = false;
        // Page.enable: recebe Page.javascriptDialogOpening (inclusive de um diálogo já aberto).
        if (_pageSession != null)
        {
            try { await _cdp.SendAsync("Page.enable", null, _pageSession).ConfigureAwait(false); } catch { }
        }
        return _pageSession;
    }

    private const string ProbeScript = """
        (() => {
          const vis = e => !!e && !e.disabled && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const sel = %PASS_SEL%;
          // Avisos SweetAlert (v1: .sweet-alert / v2: .swal2-popup), ex.: "Oops..." antes do login.
          // Fecha só avisos informativos (sem botão Cancelar visível), clicando no OK pelo DOM.
          let dismissed = null;
          try {
            for (const box of document.querySelectorAll('.sweet-alert, .swal2-popup')) {
              if (!vis(box) || box.classList.contains('hideSweetAlert')) continue;
              const cancel = box.querySelector('button.cancel, .swal2-cancel, .swal2-deny');
              if (cancel && vis(cancel)) continue;
              const ok = box.querySelector('button.confirm, .swal2-confirm');
              if (ok && vis(ok)) {
                const t = box.querySelector('h2, .swal2-title');
                dismissed = ((t && t.innerText) || 'aviso').trim().slice(0, 60);
                ok.click();
              }
            }
          } catch (e) {}
          let pw = [];
          try { pw = Array.from(document.querySelectorAll(sel || 'input[type=password]')).filter(vis); } catch (e) {}
          let status = null;
          try { const n = performance.getEntriesByType('navigation')[0]; if (n && n.responseStatus) status = n.responseStatus; } catch (e) {}
          return { url: location.href, ready: document.readyState, login: pw.length > 0, status, dismissed };
        })()
        """;

    /// <summary>
    /// Lê o estado da página. Lança TimeoutException se o renderer não responder (possível travamento).
    /// Retorna null se a sessão CDP não estiver disponível.
    /// </summary>
    public async Task<PageState?> ProbeAsync(LoginOptions login)
    {
        var session = await EnsurePageSessionAsync().ConfigureAwait(false);
        if (session == null) return null;
        var script = ProbeScript.Replace("%PASS_SEL%", JsonValue.Create(login.PasswordSelector ?? "")!.ToJsonString());
        JsonNode? res;
        try
        {
            res = await _cdp!.SendAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = script,
                ["returnByValue"] = true,
            }, session, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (CdpException)
        {
            _pageSession = null; // sessão inválida (página trocou de processo etc.) — reconecta na próxima
            throw;
        }
        var v = res?["result"]?["value"];
        if (v == null) return null;
        if (v["dismissed"] is JsonValue dv && dv.TryGetValue<string>(out var title))
            Log.Info($"Aviso do site fechado automaticamente: \"{title}\"");
        return new PageState(
            v["url"]?.GetValue<string>() ?? "",
            v["ready"]?.GetValue<string>() ?? "",
            v["login"]?.GetValue<bool>() ?? false,
            v["status"] is JsonValue sv && sv.TryGetValue<int>(out var st) ? st : null);
    }

    private const string LoginFunction = """
        async function (user, pass, userSel, passSel, submitSel) {
          const vis = e => !!e && !e.disabled && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const first = s => { if (!s) return null; try { return Array.from(document.querySelectorAll(s)).find(vis) || null; } catch (e) { return null; } };
          const p = first(passSel) || Array.from(document.querySelectorAll('input[type=password]')).find(vis);
          if (!p) return 'sem-campo-senha';
          let u = first(userSel);
          if (!u) {
            const scope = p.form || document;
            const cands = Array.from(scope.querySelectorAll('input')).filter(e => vis(e) && e !== p &&
              /^(text|email|tel|number|)$/i.test(e.getAttribute('type') || ''));
            u = cands.filter(e => e.compareDocumentPosition(p) & Node.DOCUMENT_POSITION_FOLLOWING).pop() || cands[0];
          }
          if (!u) return 'sem-campo-usuario';
          const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
          const fill = (el, v) => {
            el.focus();
            setter.call(el, v);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
            el.dispatchEvent(new Event('blur', { bubbles: true }));
          };
          fill(u, user);
          fill(p, pass);
          await new Promise(r => setTimeout(r, 400));
          let b = first(submitSel);
          const form = p.form;
          if (!b && form) b = Array.from(form.querySelectorAll('button[type=submit],input[type=submit],button:not([type])')).find(vis);
          if (!b) b = Array.from(document.querySelectorAll('button,input[type=submit],a[role=button],[role=button]')).filter(vis)
                        .find(e => /entrar|login|acessar|logar|sign in|confirmar/i.test((e.innerText || e.value || '').trim()));
          if (b) { b.click(); return 'enviado'; }
          if (form) { if (form.requestSubmit) form.requestSubmit(); else form.submit(); return 'enviado'; }
          p.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
          return 'enviado';
        }
        """;

    /// <summary>
    /// Preenche e envia o formulário de login dentro da página (DOM), sem mouse/teclado do Windows.
    /// Usuário e senha vão como argumentos da função — nunca concatenados em código ou registrados.
    /// </summary>
    public async Task<string> LoginAsync(PanelCredential cred, LoginOptions opt)
    {
        var session = await EnsurePageSessionAsync().ConfigureAwait(false);
        if (session == null) return "sem-sessao";
        var doc = await _cdp!.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = "document" }, session).ConfigureAwait(false);
        var objectId = doc?["result"]?["objectId"]?.GetValue<string>();
        if (objectId == null) return "sem-documento";
        var res = await _cdp.SendAsync("Runtime.callFunctionOn", new JsonObject
        {
            ["objectId"] = objectId,
            ["functionDeclaration"] = LoginFunction,
            ["arguments"] = new JsonArray(
                new JsonObject { ["value"] = cred.UserName },
                new JsonObject { ["value"] = cred.Password },
                new JsonObject { ["value"] = opt.UserSelector ?? "" },
                new JsonObject { ["value"] = opt.PasswordSelector ?? "" },
                new JsonObject { ["value"] = opt.SubmitSelector ?? "" }),
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
        }, session, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return res?["result"]?["value"]?.GetValue<string>() ?? "sem-resultado";
    }

    public async Task NavigateAsync(string url)
    {
        var session = await EnsurePageSessionAsync().ConfigureAwait(false);
        if (session == null) return;
        await _cdp!.SendAsync("Page.navigate", new JsonObject { ["url"] = url }, session, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    public async Task ReloadAsync()
    {
        var session = await EnsurePageSessionAsync().ConfigureAwait(false);
        if (session == null) return;
        _pageCrashed = false;
        await _cdp!.SendAsync("Page.reload", new JsonObject { ["ignoreCache"] = false }, session, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    /// <summary>Estado da janela segundo o Chromium: normal, minimized, maximized, fullscreen.</summary>
    public async Task<(int windowId, string state)?> GetWindowStateAsync()
    {
        if (await EnsurePageSessionAsync().ConfigureAwait(false) == null || _pageTargetId == null) return null;
        var w = await _cdp!.SendAsync("Browser.getWindowForTarget", new JsonObject { ["targetId"] = _pageTargetId }).ConfigureAwait(false);
        int? id = w?["windowId"]?.GetValue<int>();
        if (id == null) return null;
        return (id.Value, w?["bounds"]?["windowState"]?.GetValue<string>() ?? "normal");
    }

    /// <summary>
    /// Altera o estado da janela pelo próprio Chromium. A tela cheia do Chromium no Windows é aplicada
    /// com SWP_NOACTIVATE — não rouba o foco — e ocupa o monitor onde a janela está.
    /// </summary>
    public async Task SetWindowStateAsync(int windowId, string state)
    {
        await _cdp!.SendAsync("Browser.setWindowBounds", new JsonObject
        {
            ["windowId"] = windowId,
            ["bounds"] = new JsonObject { ["windowState"] = state },
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DropCdpAsync().ConfigureAwait(false);
        if (_browser != null)
        {
            _browser.Exited -= OnBrowserExited;
            _browser.Dispose();
            _browser = null;
        }
    }
}
