using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using DataReplayer.Services;
using Microsoft.AspNetCore.SignalR;

namespace DataReplayer.BackgroundJobs;

public class WebSocketRecordingService : BackgroundService
{
    private readonly ILogger<WebSocketRecordingService> _logger;
    private readonly IServiceProvider _sp;

    public WebSocketRecordingService(ILogger<WebSocketRecordingService> logger, IServiceProvider sp)
    {
        _logger = logger;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ReplayerSettings settings;
            string? wsUrl;
            string? apiKey;
            using (var scope = _sp.CreateScope())
            {
                var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                settings = await settingsSvc.GetSettingsAsync(stoppingToken);

                var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
                var rtlsMgr = config.GetSection("RtlsManager");
                wsUrl = rtlsMgr["WebSocketAddress"];
                apiKey = rtlsMgr["ApiKey"];
            }

            if (!settings.IsRtlsRecordingEnabled || string.IsNullOrWhiteSpace(wsUrl))
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }

            try
            {
                // Ensure URL is in valid format for ClientWebSocket
                if (!wsUrl.EndsWith("/")) wsUrl += "/";
                
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wsUrl), stoppingToken);
                _logger.LogInformation("Connected to RTLS WebSocket at {Url}", wsUrl);

                // Send authenticated subscription command
                var subscribeObj = new
                {
                    headers = new { X_ApiKey = apiKey },
                    method = "subscribe",
                    resource = "/feeds/"
                };
                // Sewio expects X-ApiKey (dash), but C# anonymous types use underscores. 
                // We'll use a raw string for exact header name or a custom serializer option.
                // Raw string is safer for specific Sewio requirements.
                var subscribeMsg = "{\"headers\": {\"X-ApiKey\": \"" + apiKey + "\"}, \"method\": \"subscribe\", \"resource\": \"/feeds/\"}";
                
                var subscribeBytes = Encoding.UTF8.GetBytes(subscribeMsg);
                await ws.SendAsync(new ArraySegment<byte>(subscribeBytes), WebSocketMessageType.Text, true, stoppingToken);

                var buffer = new byte[1024 * 64]; // 64KB buffer for large JSON chunks
                var messageBuilder = new StringBuilder();

                while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stoppingToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(chunk);

                    if (result.EndOfMessage)
                    {
                        var fullMessage = messageBuilder.ToString();
                        messageBuilder.Clear();
                        
                        await ProcessWebSocketMessageAsync(fullMessage, stoppingToken);

                        // Check dynamically if recording was disabled
                        using (var scope = _sp.CreateScope())
                        {
                            var settingsSvc = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                            var freshSettings = await settingsSvc.GetSettingsAsync(stoppingToken);
                            if (!freshSettings.IsRtlsRecordingEnabled)
                            {
                                _logger.LogInformation("RTLS Recording disabled in settings. Closing WebSocket.");
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Recording Disabled", stoppingToken);
                                break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket connection error. Retrying in 5 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessWebSocketMessageAsync(string payload, CancellationToken ct)
    {
        try
        {
            // Sewio RTLS sends arrays of objects or single objects.
            // A typical message wraps the event in `body`, e.g. {"body": {"address": "MAC_HERE", ...}}
            var node = JsonNode.Parse(payload);
            if (node == null) return;

            // Sometimes JSON root is an array
            if (node is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item != null) await ExtractAndSaveEventAsync(item, ct);
                }
            }
            else
            {
                await ExtractAndSaveEventAsync(node, ct);
            }
        }
        catch (JsonException)
        {
            // ignore malformed JSON or pings
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message");
        }
    }

    private async Task ExtractAndSaveEventAsync(JsonNode node, CancellationToken ct)
    {
        // Try to find the Mac UWB address. Usually it's in ["body"]["address"] or ["address"]
        var addressNode = node["body"]?["address"] ?? node["address"];
        if (addressNode == null) return;

        var macAddress = addressNode.GetValue<string>();
        if (string.IsNullOrEmpty(macAddress)) return;

        var rawJson = node.ToJsonString();

        using var scope = _sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
        
        var @event = new RecordedRtlsEvent
        {
            ReceivedAt = DateTime.UtcNow,
            UwbMacAddress = macAddress,
            RawPayload = rawJson
        };
        ctx.RecordedRtlsEvents.Add(@event);

        await ctx.SaveChangesAsync(ct);

        var hubCtx = scope.ServiceProvider.GetRequiredService<IHubContext<LiveEventsHub>>();
        await hubCtx.Clients.All.SendAsync("ReceiveRtlsEvent", new
        {
            receivedAt = @event.ReceivedAt,
            macAddress = @event.UwbMacAddress,
            payload = @event.RawPayload
        }, ct);
    }
}
