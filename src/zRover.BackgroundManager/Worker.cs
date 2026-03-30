using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace zRover.BackgroundManager;

/// <summary>
/// Lifecycle logger for the BackgroundManager. The actual server work is done
/// by the ASP.NET Core web host (MCP endpoint + session registration endpoint)
/// configured in Program.cs.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("zRover.BackgroundManager running");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        _logger.LogInformation("zRover.BackgroundManager stopping");
    }
}
