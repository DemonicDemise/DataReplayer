using DataReplayer.Infrastructure.Persistence;
using DataReplayer.Services;
using Microsoft.EntityFrameworkCore;

namespace DataReplayer.BackgroundJobs;

public class DataCleanupService : BackgroundService
{
    private readonly ILogger<DataCleanupService> _logger;
    private readonly IServiceProvider _sp;

    public DataCleanupService(ILogger<DataCleanupService> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                var settings = await settingsSvc.GetSettingsAsync(stoppingToken);

                if (settings.RetentionHours > 0)
                {
                    var threshold = DateTime.UtcNow.AddHours(-settings.RetentionHours);
                    var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
                    var deleted = await ctx.RecordedEvents
                        .Where(e => e.ReceivedAt < threshold)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (deleted > 0)
                        _logger.LogInformation("Cleanup: deleted {Count} events older than {Hours}h", deleted, settings.RetentionHours);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data cleanup");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
