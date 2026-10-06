using System.Net.Sockets;

namespace GHospPanelWatchdog.Watchdog;

/// <summary>Teste de conectividade barato: DNS + conexão TCP ao host do painel (sem baixar a página).</summary>
internal static class NetProbe
{
    public static async Task<bool> CanReachAsync(string url, TimeSpan timeout)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
