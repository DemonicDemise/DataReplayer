using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataReplayer.Services;

public interface ISettingsService
{
    Task<ReplayerSettings> GetSettingsAsync(CancellationToken ct = default);
    Task UpdateSettingsAsync(ReplayerSettings input, CancellationToken ct = default);
}

public class SettingsService : ISettingsService
{
    private readonly IServiceProvider _sp;

    public SettingsService(IServiceProvider sp) => _sp = sp;

    public async Task<ReplayerSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();

        var settings = await ctx.Settings.FirstOrDefaultAsync(ct);
        if (settings is not null) return settings;

        // No settings yet — try to seed defaults.
        // Multiple background services may hit this concurrently on first run,
        // so guard against duplicate PK with catch + re-query.
        try
        {
            settings = new ReplayerSettings();
            ctx.Settings.Add(settings);
            await ctx.SaveChangesAsync(ct);
            return settings;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Another thread already inserted. Create a fresh context and re-read.
            using var retryScope = _sp.CreateScope();
            var retryCtx = retryScope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
            return (await retryCtx.Settings.FirstOrDefaultAsync(ct))!;
        }
    }

    public async Task UpdateSettingsAsync(ReplayerSettings input, CancellationToken ct = default)
    {
        using var scope = _sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
        var settings = await ctx.Settings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new ReplayerSettings();
            ctx.Settings.Add(settings);
        }

        settings.RetentionHours              = input.RetentionHours;
        settings.TrackersWhiteList            = input.TrackersWhiteList;
        settings.SubscribedTopics             = input.SubscribedTopics;
        settings.IsRecordingEnabled           = input.IsRecordingEnabled;
        settings.TrackerIdTopicSegmentIndex   = input.TrackerIdTopicSegmentIndex;
        settings.IsRtlsRecordingEnabled       = input.IsRtlsRecordingEnabled;

        await ctx.SaveChangesAsync(ct);
    }
}
