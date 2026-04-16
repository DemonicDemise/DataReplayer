using DataReplayer.BackgroundJobs;
using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using DataReplayer.Services;

using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ─── Services ───────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// Database – swap UseInMemoryDatabase for UseNpgsql for production
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<ReplayerDbContext>(opts =>
        opts.UseNpgsql(connectionString));
}
else
{
    builder.Services.AddDbContext<ReplayerDbContext>(opts =>
        opts.UseInMemoryDatabase("DataReplayer"));
}

builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<IReplayService>(sp => sp.GetRequiredService<ReplayService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReplayService>());

builder.Services.AddSingleton<RtlsReplayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtlsReplayService>());
builder.Services.AddHostedService<MqttRecordingService>();
builder.Services.AddHostedService<DataCleanupService>();
builder.Services.AddHostedService<WebSocketRecordingService>();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var rabbit = builder.Configuration.GetSection("RabbitMq");
        var portStr = rabbit["Port"];
        ushort port = ushort.TryParse(portStr, out var p) ? p : (ushort)5672;

        cfg.Host(
            rabbit["Host"] ?? "localhost",
            port,
            rabbit["VirtualHost"] ?? "/",
            h =>
            {
                h.Username(rabbit["Username"] ?? "guest");
                h.Password(rabbit["Password"] ?? "guest");
            });
    });
});


builder.Services.AddSignalR();

var app = builder.Build();

// ─── Middleware ──────────────────────────────────────────────────────────
// Built-in .NET 10 OpenAPI endpoint (replaces Swashbuckle)
app.MapOpenApi();
app.UseDefaultFiles();
app.UseStaticFiles();

// ─── Settings endpoints ──────────────────────────────────────────────────
app.MapGet("/api/settings", async (ISettingsService svc) =>
    Results.Ok(await svc.GetSettingsAsync()))
    .WithName("GetSettings").WithTags("Settings");

app.MapPost("/api/settings", async (ReplayerSettings input, ISettingsService svc) =>
{
    await svc.UpdateSettingsAsync(input);
    return Results.Ok();
}).WithName("UpdateSettings").WithTags("Settings");

// ─── Data endpoints ──────────────────────────────────────────────────────
app.MapGet("/api/events", async (
    ReplayerDbContext ctx,
    DateTime? from, DateTime? to, string? trackerId,
    int page = 1, int pageSize = 100) =>
{
    var query = ctx.RecordedEvents.AsQueryable();
    if (from.HasValue) query = query.Where(e => e.ReceivedAt >= from.Value);
    if (to.HasValue) query = query.Where(e => e.ReceivedAt <= to.Value);
    if (!string.IsNullOrEmpty(trackerId)) query = query.Where(e => e.TrackerId == trackerId);

    var total = await query.CountAsync();
    var items = await query.OrderByDescending(e => e.ReceivedAt)
        .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
    return Results.Ok(new { total, items });
}).WithName("GetEvents").WithTags("Events");

app.MapGet("/api/events/trackers", async (ReplayerDbContext ctx) =>
{
    var trackers = await ctx.RecordedEvents
        .Where(e => e.TrackerId != null)
        .Select(e => e.TrackerId)
        .Distinct()
        .ToListAsync();
    return Results.Ok(trackers);
}).WithName("GetTrackers").WithTags("Events");

app.MapDelete("/api/events", async (ReplayerDbContext ctx) =>
{
    var count = await ctx.RecordedEvents.ExecuteDeleteAsync();
    return Results.Ok(new { deleted = count });
}).WithName("ClearEvents").WithTags("Events");

// ─── Replay endpoints ────────────────────────────────────────────────────
app.MapPost("/api/replay/start", async (ReplaySessionCommand cmd, IReplayService replay) =>
{
    await replay.StartSessionAsync(cmd);
    return Results.Ok();
}).WithName("StartReplay").WithTags("Replay");

app.MapPost("/api/replay/stop", (IReplayService replay) =>
{
    replay.StopSession();
    return Results.Ok();
}).WithName("StopReplay").WithTags("Replay");

app.MapGet("/api/replay/status", (IReplayService replay) =>
    Results.Ok(new
    {
        isPlaying = replay.IsPlaying,
        progress = replay.Progress
    })
).WithName("GetReplayStatus").WithTags("Replay");

// ─── RTLS Replay endpoints ───────────────────────────────────────────────
app.MapPost("/api/rtls-replay/start", async (RtlsReplaySessionCommand cmd, RtlsReplayService replay) =>
{
    await replay.StartSessionAsync(cmd);
    return Results.Ok();
}).WithName("StartRtlsReplay").WithTags("RtlsReplay");

app.MapPost("/api/rtls-replay/stop", (RtlsReplayService replay) =>
{
    replay.StopSessionAsync();
    return Results.Ok();
}).WithName("StopRtlsReplay").WithTags("RtlsReplay");

app.MapGet("/api/rtls-replay/status", (RtlsReplayService replay) =>
    Results.Ok(new
    {
        isPlaying = replay.IsPlaying,
        processedCount = replay.ProcessedCount,
        totalSessionEvents = replay.TotalSessionEvents
    })
).WithName("GetRtlsReplayStatus").WithTags("RtlsReplay");

app.MapGet("/api/rtls-events/macs", async (ReplayerDbContext ctx) =>
{
    var macs = await ctx.RecordedRtlsEvents
        .Select(e => e.UwbMacAddress)
        .Distinct()
        .ToListAsync();
    return Results.Ok(macs);
}).WithName("GetRtlsMacs").WithTags("RtlsEvents");

app.MapHub<LiveEventsHub>("/api/live-events");

// ─── Debug/Test endpoints ────────────────────────────────────────────────
app.MapPost("/api/debug/rtls-test", async (string? mac, IHubContext<LiveEventsHub> hub) =>
{
    var m = mac ?? "DE:AD:BE:EF:00:01";
    await hub.Clients.All.SendAsync("ReceiveRtlsEvent", new
    {
        receivedAt = DateTime.UtcNow,
        macAddress = m,
        payload = "{\"debug\": true, \"address\": \"" + m + "\"}"
    });
    return Results.Ok(new { message = "Test RTLS event sent to SignalR clients", mac = m });
}).WithName("TestRtlsEvent").WithTags("Debug");

// ─── Run ─────────────────────────────────────────────────────────────────
// Ensure DB is created and seed default settings BEFORE hosted services start
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
    await ctx.Database.EnsureCreatedAsync();

    // Apply schema changes that EnsureCreatedAsync won't pick up on existing DBs
    await ctx.Database.ExecuteSqlRawAsync("""
        ALTER TABLE "Settings"
            ADD COLUMN IF NOT EXISTS "RtlsWebSocketUrl"          TEXT    NOT NULL DEFAULT 'ws://localhost:8080/feeds/',
            ADD COLUMN IF NOT EXISTS "IsRtlsRecordingEnabled"    BOOLEAN NOT NULL DEFAULT FALSE;
        """);

    // Seed default settings row so background services don't race to create it
    if (!await ctx.Settings.AnyAsync())
    {
        var mqttCfg  = app.Configuration.GetSection("Mqtt");
        var rtlsCfg  = app.Configuration.GetSection("Rtls");

        ctx.Settings.Add(new ReplayerSettings());
        await ctx.SaveChangesAsync();
    }
}

app.Run();