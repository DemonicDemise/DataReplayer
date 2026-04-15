using DataReplayer.BackgroundJobs;
using DataReplayer.Domain.Entities;
using DataReplayer.Infrastructure.Persistence;
using DataReplayer.Services;
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
builder.Services.AddHostedService<MqttRecordingService>();
builder.Services.AddHostedService<DataCleanupService>();

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

// ─── Run ─────────────────────────────────────────────────────────────────
// Ensure DB is created and seed default settings BEFORE hosted services start
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<ReplayerDbContext>();
    await ctx.Database.EnsureCreatedAsync();

    // Seed default settings row so background services don't race to create it
    if (!await ctx.Settings.AnyAsync())
    {
        ctx.Settings.Add(new ReplayerSettings());
        await ctx.SaveChangesAsync();
    }
}

app.Run();