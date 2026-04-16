using System.Text.Json.Nodes;
using DMMS.BuildingBlocks.Globalization;
using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using DMMS.InfrastructureMonitor.Contracts.IntegrationsEvents.V1;
using DMMS.InfrastructureMonitor.Contracts.Models;
using DMMS.Positioning.Contracts.Constants;
using DMMS.Positioning.Contracts.IntegrationEvents.V1.TrackerRegistrations.Origin;
using DMMS.Positioning.Contracts.TrackerRegistrations.Origin;
using DMMS.ResourceManagement.Contracts.Constants;
using DMMS.ResourceManagement.Contracts.Enums;

using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace DataReplayer.Services;

public class RtlsReplaySessionCommand
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public List<string>? MacFilter { get; set; }
    public string? TargetNativeId { get; set; } // If we want to override NativeId during replay
}

public class RtlsReplayService : BackgroundService
{
    private readonly ILogger<RtlsReplayService> _logger;
    private readonly IServiceProvider _sp;

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;

    public bool IsPlaying { get; private set; }
    public RtlsReplaySessionCommand? CurrentSession { get; private set; }
    public int ProcessedCount { get; private set; }
    public int TotalSessionEvents { get; private set; }
    
    // MAC → NativeId cache (populated only from TargetNativeId override, kept for future lookup extension)
    private readonly Dictionary<string, string?> _macCache = new();

    public RtlsReplayService(ILogger<RtlsReplayService> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    public async Task StartSessionAsync(RtlsReplaySessionCommand cmd)
    {
        if (IsPlaying) throw new InvalidOperationException("Session is already running");

        // Pre-count events
        using var scope = _sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();

        var query = ctx.RecordedRtlsEvents
            .Where(e => e.ReceivedAt >= cmd.StartTime && e.ReceivedAt <= cmd.EndTime);

        if (cmd.MacFilter != null && cmd.MacFilter.Any())
            query = query.Where(e => cmd.MacFilter.Contains(e.UwbMacAddress));

        TotalSessionEvents = await query.CountAsync();
        if (TotalSessionEvents == 0) throw new InvalidOperationException("No events found in DB for this period/filter.");

        _sessionCts = new CancellationTokenSource();
        CurrentSession = cmd;
        IsPlaying = true;
        ProcessedCount = 0;

        _sessionTask = RunSessionAsync(cmd, _sessionCts.Token);
    }

    public Task StopSessionAsync()
    {
        _sessionCts?.Cancel();
        IsPlaying = false;
        return Task.CompletedTask;
    }

    private async Task RunSessionAsync(RtlsReplaySessionCommand cmd, CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
            var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            var query = ctx.RecordedRtlsEvents
                .Where(e => e.ReceivedAt >= cmd.StartTime && e.ReceivedAt <= cmd.EndTime);

            if (cmd.MacFilter != null && cmd.MacFilter.Any())
                query = query.Where(e => cmd.MacFilter.Contains(e.UwbMacAddress));

            var events = await query
                .OrderBy(e => e.ReceivedAt)
                .ToListAsync(ct);

            if (events.Count == 0) return;

            DateTime? firstEventTime = events.First().ReceivedAt;
            DateTime playStartTime = DateTime.UtcNow;

            foreach (var evt in events)
            {
                if (ct.IsCancellationRequested) break;

                var offset = evt.ReceivedAt - firstEventTime.Value;
                var targetWaitMs = (int)(offset.TotalMilliseconds / cmd.SpeedMultiplier);
                var elapsedMs = (DateTime.UtcNow - playStartTime).TotalMilliseconds;

                var sleepMs = targetWaitMs - elapsedMs;
                if (sleepMs > 0)
                {
                    await Task.Delay((int)sleepMs, ct);
                }

                await ProcessAndPublishEventAsync(evt, cmd.TargetNativeId, publishEndpoint, ct);
                ProcessedCount++;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during RTLS replay session");
        }
        finally
        {
            IsPlaying = false;
            CurrentSession = null;
        }
    }

    private async Task ProcessAndPublishEventAsync(
        RecordedRtlsEvent evt,
        string? overrideNativeId,
        IPublishEndpoint publisher,
        CancellationToken ct)
    {
        try
        {
            var node = JsonNode.Parse(evt.RawPayload);
            if (node == null) return;
            
            var body = node["body"];
            if (body == null) body = node; // fallback

            string nativeId;
            if (!string.IsNullOrWhiteSpace(overrideNativeId))
            {
                nativeId = overrideNativeId;
            }
            else
            {
                // gRPC доступ к основной системе недоступен.
                // Укажите TargetNativeId в настройках воспроизведения.
                if (!_macCache.TryGetValue(evt.UwbMacAddress, out var cached))
                {
                    _logger.LogWarning(
                        "[RTLS Replay] Не могу определить NativeId для MAC {Mac} — gRPC недоступен. "
                        + "Укажите TargetNativeId вручную. Событие пропущено.",
                        evt.UwbMacAddress);
                    _macCache[evt.UwbMacAddress] = null; // кешируем промах
                    return;
                }
                if (string.IsNullOrEmpty(cached))
                {
                    return; // уже логировали раньше
                }
                nativeId = cached;
            }

            // Extract Data
            var dataStreams = body["dataStreams"];
            var posX = dataStreams?["posX"]?.GetValue<float>() ?? 0f;
            var posY = dataStreams?["posY"]?.GetValue<float>() ?? 0f;
            var posZ = dataStreams?["posZ"]?.GetValue<float>() ?? 0f;
            var pressure = dataStreams?["calibratedPressure"]?.GetValue<float?>() ?? 0f;

            var masterReader = body["readersInfo"]?["master"]?.GetValue<string>() ?? "000000";
            masterReader = $"0x{masterReader}";

            var simplePoint = new DMMS.BuildingBlocks.Geometries.SimplePoint { X = posX, Y = posY, Z = posZ };

            // 1. RtlsPrecisePositionReadEvent
            var posEvent = new RtlsPrecisePositionReadEvent
            {
                RegistrationId = Guid.NewGuid(),
                ReaderId = masterReader,
                TrackerId = nativeId,
                Timestamp = DateTime.UtcNow,
                Position = simplePoint,
                Origin = RegistrationOrigins.Rtls,
                CalibratedPressure = pressure
            };
            
            await publisher.Publish(posEvent, ct);

            // 2. Monitoring indicator for Tracker (LastActivityTime)
            var trackerIndicator = new DeviceIndicatorValuePacketEvent
            {
                Timestamp = DateTime.UtcNow,
                NativeId = nativeId,
                DeviceType = DeviceType.Tracker,
                Indicators = new[]
                {
                    new IndicatorModel
                    {
                        Code = IndicatorTypes.LastActivityTime,
                        Name = Translation.NewRepeated(IndicatorTypes.LastActivityTime),
                        StringValue = masterReader
                    }
                }
            };
            await publisher.Publish(trackerIndicator, ct);

            // We can also extract Slaves to simulate Anchor DeviceIndicatorValuePacketEvent but that requires parsing "readersInfo/slaves".
            // For now, these 2 events are exactly enough to make the tracker move on the map and stay online!
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish RTLS event for DB id {Id}", evt.Id);
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
