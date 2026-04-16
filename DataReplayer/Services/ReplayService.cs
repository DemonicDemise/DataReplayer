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
    public string TimestampJsonPath { get; set; } = ReplayService.TimestampPath;
    public List<string>? TrackerFilter { get; set; }
    public string? TargetTrackerId { get; set; }
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
    /// <summary>Fixed JSON path to the epoch timestamp field inside the MQTT payload.</summary>
    public const string TimestampPath = "$.message.b0.ts";

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

            var finalTopic = evt.Endpoint;
            if (!string.IsNullOrWhiteSpace(cmd.TargetTrackerId))
            {
                var segments = finalTopic.Split('/');
                if (segments.Length > 1)
                {
                    segments[1] = cmd.TargetTrackerId;
                    finalTopic = string.Join('/', segments);
                }
            }

            _logger.LogDebug(
                "[Replay] Preparing to publish:\n" +
                "  Topic   : {Topic}\n" +
                "  Original: {Original}\n" +
                "  Modified: {Modified}",
                finalTopic, evt.Payload, finalPayload);

            // Publish to MQTT
            if (_mqttClient is null)
            {
                _logger.LogWarning("[Replay] MQTT client is null — skipping event {Sent}/{Total} on topic {Topic}",
                    Progress.Sent + 1, Progress.Total, finalTopic);
            }
            else if (!_mqttClient.IsConnected)
            {
                _logger.LogWarning("[Replay] MQTT client is NOT connected — skipping event {Sent}/{Total} on topic {Topic}",
                    Progress.Sent + 1, Progress.Total, finalTopic);
            }
            else
            {
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(finalTopic)
                    .WithPayload(finalPayload)
                    .Build();

                _logger.LogInformation(
                    "[Replay] Publishing event {Sent}/{Total}:\n" +
                    "  Topic  : {Topic}\n" +
                    "  Payload: {Payload}",
                    Progress.Sent + 1, Progress.Total, finalTopic, finalPayload);

                await _mqttClient.PublishAsync(msg, sessionToken);

                _logger.LogInformation("[Replay] Published OK → {Topic}", finalTopic);
            }

            Progress.Sent++;
            Progress.CurrentTopic = finalTopic;
            Progress.CurrentEventTime = evt.ReceivedAt;
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

        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var mqttCfg = config.GetSection("Mqtt");
        var host = mqttCfg["Host"] ?? "localhost";
        var port = int.TryParse(mqttCfg["Port"], out var p) ? p : 1883;

        var optsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port);

        var user = mqttCfg["Username"];
        var pass = mqttCfg["Password"];
        if (!string.IsNullOrEmpty(user))
            optsBuilder.WithCredentials(user, pass);

        try { await _mqttClient.ConnectAsync(optsBuilder.Build()); }
        catch (Exception ex) { _logger.LogError(ex, "Replay: failed to connect to MQTT broker {Host}:{Port}", host, port); }
    }

    private string AdjustTimestamp(string payload, string jsonPath, DateTime newValue)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            _logger.LogDebug("[Replay] TimestampJsonPath is empty — payload sent as-is");
            return payload;
        }
        
        try
        {
            var node = JsonNode.Parse(payload);
            if (node == null)
            {
                _logger.LogWarning("[Replay] Failed to parse payload as JSON — sending original. Payload: {Payload}", payload);
                return payload;
            }

            // e.g. "$.message.b0.ts" -> ["message", "b0", "ts"]
            var parts = jsonPath.Replace("$", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return payload;

            var current = node;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (current is JsonObject obj && obj.ContainsKey(parts[i]))
                {
                    current = obj[parts[i]];
                }
                else
                {
                    _logger.LogWarning(
                        "[Replay] Timestamp path segment '{Segment}' not found in payload — sending original.\n" +
                        "  Path   : {Path}\n" +
                        "  Payload: {Payload}",
                        parts[i], jsonPath, payload);
                    return payload;
                }
            }

            string lastPart = parts[^1];
            if (current is JsonObject finalObj && finalObj.ContainsKey(lastPart))
            {
                var existingVal = finalObj[lastPart];
                
                // Try numeric (epoch). JSON numbers can be int/long/double — try all.
                bool isNumeric = existingVal is JsonValue v &&
                    (v.TryGetValue(out long _) || v.TryGetValue(out int _) || v.TryGetValue(out double _));

                if (isNumeric)
                {
                    var newEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    _logger.LogInformation(
                        "[Replay] Timestamp updated (epoch): {OldTs} → {NewTs}  (path: '{Path}')",
                        existingVal, newEpoch, jsonPath);
                    finalObj[lastPart] = newEpoch;
                }
                else
                {
                    var newStr = newValue.ToString("O");
                    _logger.LogInformation(
                        "[Replay] Timestamp updated (string): {OldTs} → {NewTs}  (path: '{Path}')",
                        existingVal?.ToJsonString(), newStr, jsonPath);
                    finalObj[lastPart] = newStr;
                }
            }
            else
            {
                _logger.LogWarning(
                    "[Replay] Timestamp key '{Key}' not found at path '{Path}' — sending original.\n" +
                    "  Payload: {Payload}",
                    lastPart, jsonPath, payload);
            }

            return node.ToJsonString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Replay] Exception while adjusting timestamp — sending original payload. Payload: {Payload}", payload);
            return payload;
        }
    }
}
