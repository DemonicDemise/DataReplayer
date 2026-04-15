using System.Text.Json.Nodes;
using DataReplayer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Client;

namespace DataReplayer.Services;

public class ReplaySessionCommand
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
    public string TimestampJsonPath { get; set; } = "timestamp";
    public List<string>? TrackerFilter { get; set; }
}

public interface IReplayService
{
    bool IsPlaying { get; }
    ReplayProgressInfo Progress { get; }
    Task StartSessionAsync(ReplaySessionCommand command, CancellationToken ct = default);
    void StopSession();
}

public class ReplayProgressInfo
{
    public int Total { get; set; }
    public int Sent { get; set; }
    public string? CurrentTopic { get; set; }
    public DateTime? CurrentEventTime { get; set; }
}

public class ReplayService : BackgroundService, IReplayService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ReplayService> _logger;
    private CancellationTokenSource? _sessionCts;
    private ReplaySessionCommand? _currentSession;
    private IMqttClient? _mqttClient;

    public bool IsPlaying => _sessionCts is { IsCancellationRequested: false };
    public ReplayProgressInfo Progress { get; } = new();

    public ReplayService(IServiceProvider sp, ILogger<ReplayService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task StartSessionAsync(ReplaySessionCommand command, CancellationToken ct = default)
    {
        StopSession();
        _currentSession = command;
        _sessionCts = new CancellationTokenSource();

        await EnsureMqttConnectedAsync();

        _logger.LogInformation("Replay session started: {Start} -> {End} at {Speed}x",
            command.StartTime, command.EndTime, command.SpeedMultiplier);
    }

    public void StopSession()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        Progress.Sent = 0;
        Progress.Total = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsPlaying && _currentSession is not null)
            {
                try
                {
                    await RunSessionAsync(_currentSession, _sessionCts!.Token);
                }
                catch (OperationCanceledException) { /* stopped by user */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during replay session");
                }
                finally
                {
                    StopSession();
                }
            }
            await Task.Delay(500, stoppingToken);
        }
    }

    private async Task RunSessionAsync(ReplaySessionCommand cmd, CancellationToken sessionToken)
    {
        using var scope = _sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();

        var query = ctx.RecordedEvents
            .Where(e => e.ReceivedAt >= cmd.StartTime && e.ReceivedAt <= cmd.EndTime);

        if (cmd.TrackerFilter is { Count: > 0 })
            query = query.Where(e => cmd.TrackerFilter.Contains(e.TrackerId));

        query = query.OrderBy(e => e.ReceivedAt);

        var events = await query.ToListAsync(sessionToken);
        Progress.Total = events.Count;
        Progress.Sent = 0;

        if (events.Count == 0)
        {
            _logger.LogWarning("No events found in the selected range.");
            return;
        }

        var firstEventTime = events[0].ReceivedAt;
        var playbackStart = DateTime.UtcNow;

        foreach (var evt in events)
        {
            if (sessionToken.IsCancellationRequested) break;

            // Calculate how long from the real start this event should fire
            var originalOffset = evt.ReceivedAt - firstEventTime;
            var adjustedDelay = TimeSpan.FromMilliseconds(originalOffset.TotalMilliseconds / cmd.SpeedMultiplier);

            var elapsed = DateTime.UtcNow - playbackStart;
            var sleepTime = adjustedDelay - elapsed;
            if (sleepTime > TimeSpan.Zero)
                await Task.Delay(sleepTime, sessionToken);

            // Adjust timestamp in payload
            var finalPayload = AdjustTimestamp(evt.Payload, cmd.TimestampJsonPath, DateTime.UtcNow);

            // Publish to MQTT
            if (_mqttClient is { IsConnected: true })
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(evt.Endpoint)
                    .WithPayload(finalPayload)
                    .Build();
                await _mqttClient.PublishAsync(msg, sessionToken);
            }

            Progress.Sent++;
            Progress.CurrentTopic = evt.Endpoint;
            Progress.CurrentEventTime = evt.ReceivedAt;

            _logger.LogDebug("Replayed to {Topic} (event {Sent}/{Total})", evt.Endpoint, Progress.Sent, Progress.Total);
        }

        _logger.LogInformation("Replay session completed. {Sent}/{Total} events sent.", Progress.Sent, Progress.Total);
    }

    private async Task EnsureMqttConnectedAsync()
    {
        var factory = new MqttFactory();
        _mqttClient ??= factory.CreateMqttClient();

        if (_mqttClient.IsConnected) return;

        using var scope = _sp.CreateScope();
        var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = await settingsSvc.GetSettingsAsync();

        var optsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.MqttBrokerHost, settings.MqttBrokerPort);

        if (!string.IsNullOrEmpty(settings.MqttUsername))
            optsBuilder.WithCredentials(settings.MqttUsername, settings.MqttPassword);

        try { await _mqttClient.ConnectAsync(optsBuilder.Build()); }
        catch (Exception ex) { _logger.LogError(ex, "Replay: failed to connect to MQTT broker"); }
    }

    private static string AdjustTimestamp(string payload, string jsonPath, DateTime newValue)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            if (node is not JsonObject obj) return payload;

            // Supports simple "$.field" or just "field"
            var key = jsonPath.TrimStart('$').TrimStart('.');
            if (obj.ContainsKey(key))
                obj[key] = newValue.ToString("O");

            return obj.ToJsonString();
        }
        catch
        {
            return payload;
        }
    }
}
