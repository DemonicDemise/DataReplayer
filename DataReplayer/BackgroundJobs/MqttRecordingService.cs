using System.Text;
using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using DataReplayer.Services;
using MQTTnet;
using MQTTnet.Client;

namespace DataReplayer.BackgroundJobs;

public class MqttRecordingService : BackgroundService
{
    private readonly ILogger<MqttRecordingService> _logger;
    private readonly IServiceProvider _sp;
    private IMqttClient? _client;

    public MqttRecordingService(ILogger<MqttRecordingService> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += e => ProcessAsync(e.ApplicationMessage, stoppingToken);
        _client.DisconnectedAsync += async _ =>
        {
            _logger.LogWarning("MQTT disconnected. Reconnecting in 5s...");
            await Task.Delay(5000, stoppingToken);
            await TryConnectAsync(stoppingToken);
        };

        await TryConnectAsync(stoppingToken);

        // Periodically refresh topic subscriptions when settings change
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            if (_client.IsConnected)
                await SubscribeTopicsAsync();
        }
    }

    private async Task TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _sp.CreateScope();
            var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var settings = await settingsSvc.GetSettingsAsync(ct);

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.MqttBrokerHost, settings.MqttBrokerPort);

            if (!string.IsNullOrEmpty(settings.MqttUsername))
                optionsBuilder.WithCredentials(settings.MqttUsername, settings.MqttPassword);

            await _client!.ConnectAsync(optionsBuilder.Build(), ct);
            _logger.LogInformation("Connected to MQTT broker {Host}:{Port}", settings.MqttBrokerHost, settings.MqttBrokerPort);
            await SubscribeTopicsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker");
        }
    }

    private async Task SubscribeTopicsAsync()
    {
        using var scope = _sp.CreateScope();
        var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = await settingsSvc.GetSettingsAsync();

        if (settings.SubscribedTopics.Count == 0) return;

        var factory = new MqttFactory();
        foreach (var topic in settings.SubscribedTopics)
        {
            await _client!.SubscribeAsync(factory.CreateTopicFilterBuilder().WithTopic(topic).Build());
            _logger.LogInformation("Subscribed to topic: {Topic}", topic);
        }
    }

    private async Task ProcessAsync(MqttApplicationMessage message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetString(message.PayloadSegment);
        var topic = message.Topic;

        using var scope = _sp.CreateScope();
        var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = await settingsSvc.GetSettingsAsync(ct);

        if (!settings.IsRecordingEnabled) return;

        // Extract tracker ID from the configured segment of the topic path.
        // Example: "BADGE/9F31510F9918CE60/up/pressure" with index=1 → "9F31510F9918CE60"
        var segments = topic.Split('/');
        string? extractedTrackerId = null;
        if (segments.Length > 1)
            extractedTrackerId = segments[1];

        // Whitelist filtering: if list is non-empty, only accept matching tracker IDs
        if (settings.TrackersWhiteList.Count > 0)
        {
            if (extractedTrackerId is null ||
                !settings.TrackersWhiteList.Contains(extractedTrackerId, StringComparer.OrdinalIgnoreCase))
                return;
        }

        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
        ctx.RecordedEvents.Add(new RecordedDataEvent
        {
            ReceivedAt = DateTime.UtcNow,
            Endpoint = topic,
            Payload = payload,
            TrackerId = extractedTrackerId
        });
        await ctx.SaveChangesAsync(ct);
    }
}
