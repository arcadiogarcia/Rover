using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using zRover.BackgroundManager.Sessions;

namespace zRover.BackgroundManager;

/// <summary>
/// Lifecycle logger and periodic health-check for the BackgroundManager.
/// Sweeps sessions every 10 s and removes any whose MCP endpoint is unreachable.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly SessionRegistry _registry;

    public Worker(ILogger<Worker> logger, SessionRegistry registry)
    {
        _logger = logger;
        _registry = registry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("zRover.BackgroundManager running");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }

            foreach (var session in _registry.Sessions)
            {
                if (!session.IsConnected)
                {
                    _registry.Remove(session.SessionId);
                    continue;
                }

                if (!await IsReachableAsync(session.McpUrl, stoppingToken))
                {
                    _logger.LogInformation("Session {SessionId} unreachable, removing", session.SessionId);
                    _registry.Remove(session.SessionId);
                }
            }
        }

        _logger.LogInformation("zRover.BackgroundManager stopping");
    }

    private static async Task<bool> IsReachableAsync(string mcpUrl, CancellationToken ct)
    {
        try
        {
            var uri = new Uri(mcpUrl);
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
