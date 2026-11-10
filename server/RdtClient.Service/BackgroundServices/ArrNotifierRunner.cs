using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RdtClient.Service.Services;

namespace RdtClient.Service.BackgroundServices;

public class ArrNotifierRunner(ILogger<ArrNotifierRunner> logger, IServiceProvider serviceProvider, ArrNotifier arrNotifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!Startup.Ready)
        {
            await Task.Delay(1000, stoppingToken);
        }

        logger.LogInformation("ArrNotifierRunner started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();

                var rejectedTorrents = scope.ServiceProvider.GetRequiredService<RejectedTorrents>();
                await rejectedTorrents.Cleanup();

                var torrents = scope.ServiceProvider.GetRequiredService<Torrents>();
                var allTorrents = await torrents.Get();

                await arrNotifier.Check(allTorrents, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error occurred in ArrNotifierRunner: {message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }

        logger.LogInformation("ArrNotifierRunner stopped.");
    }
}
