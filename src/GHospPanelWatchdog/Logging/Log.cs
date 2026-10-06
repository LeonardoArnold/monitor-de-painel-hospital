using System.Text;

namespace GHospPanelWatchdog.Logging;

/// <summary>
/// Log simples, thread-safe, com rotação por tamanho e supressão de mensagens repetidas.
/// Nunca recebe senha, cookies, tokens ou conteúdo de página — quem chama é responsável por isso,
/// e por segurança o texto passa por <see cref="Sanitize"/>.
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxArchives = 5;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastSeen = new();
    private static StreamWriter? _writer;
    private static string _path = "";

    public static string FilePath => _path;

    public static void Init(string logsDir)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(logsDir);
            _path = Path.Combine(logsDir, "watchdog.log");
            OpenWriter();
        }
    }

    public static void Info(string message) => Write("INFO ", message, false);
    public static void Warn(string message) => Write("AVISO", message, false);
    public static void Error(string message) => Write("ERRO ", message, false);
    public static void Error(string message, Exception ex) =>
        Write("ERRO ", $"{message}: {ex.GetType().Name}: {ex.Message}", false);

    /// <summary>Registra apenas se a mesma mensagem não apareceu nos últimos minutos.</summary>
    public static void InfoThrottled(string message) => Write("INFO ", message, true);
    public static void WarnThrottled(string message) => Write("AVISO", message, true);

    private static void Write(string level, string message, bool throttle)
    {
        message = Sanitize(message);
        lock (Gate)
        {
            var now = DateTime.Now;
            if (throttle)
            {
                if (LastSeen.TryGetValue(message, out var last) && now - last < RepeatWindow) return;
                LastSeen[message] = now;
                if (LastSeen.Count > 500) LastSeen.Clear();
            }
            try
            {
                if (_writer == null) return;
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
                _writer.Flush();
                if (_writer.BaseStream.Length > MaxFileBytes) Rotate();
            }
            catch
            {
                // O log nunca pode derrubar o watchdog.
            }
        }
    }

    private static void OpenWriter()
    {
        try
        {
            var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
        }
        catch
        {
            _writer = null;
        }
    }

    private static void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            var oldest = Path.Combine(dir, $"watchdog.{MaxArchives}.log");
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = MaxArchives - 1; i >= 1; i--)
            {
                var src = Path.Combine(dir, $"watchdog.{i}.log");
                if (File.Exists(src)) File.Move(src, Path.Combine(dir, $"watchdog.{i + 1}.log"));
            }
            File.Move(_path, Path.Combine(dir, "watchdog.1.log"));
        }
        catch
        {
            // Se não conseguir rotacionar (arquivo aberto no Bloco de Notas etc.), continua no mesmo.
        }
        OpenWriter();
    }

    /// <summary>Defesa extra: remove quebras de linha e trechos que pareçam segredos.</summary>
    private static string Sanitize(string s)
    {
        if (s.Length > 600) s = s[..600] + "…";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        foreach (var marker in new[] { "password", "senha", "token", "cookie" })
        {
            int idx = s.IndexOf(marker + "=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) s = s[..(idx + marker.Length + 1)] + "***";
        }
        return s;
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
