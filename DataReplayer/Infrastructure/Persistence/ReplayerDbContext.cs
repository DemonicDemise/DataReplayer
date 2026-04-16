using DataReplayer.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataReplayer.Infrastructure.Persistence;

public class ReplayerDbContext : DbContext
{
    public ReplayerDbContext(DbContextOptions<ReplayerDbContext> options) : base(options) { }

    public DbSet<RecordedDataEvent> RecordedEvents { get; set; } = null!;
    public DbSet<RecordedRtlsEvent> RecordedRtlsEvents { get; set; } = null!;
    public DbSet<ReplayerSettings> Settings { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RecordedDataEvent>(builder =>
        {
            builder.ToTable("RecordedEvents");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.ReceivedAt).IsRequired();
            builder.Property(x => x.Payload).HasColumnType("jsonb");
            builder.HasIndex(x => x.ReceivedAt);
            builder.HasIndex(x => x.TrackerId);
        });

        modelBuilder.Entity<ReplayerSettings>(builder =>
        {
            builder.ToTable("Settings");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.TrackersWhiteList)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v == "" ? new List<string>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                );
            builder.Property(x => x.SubscribedTopics)
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v == "" ? new List<string>() : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
                );
        });

        modelBuilder.Entity<RecordedRtlsEvent>(builder =>
        {
            builder.HasIndex(x => x.ReceivedAt);
            builder.HasIndex(x => x.UwbMacAddress);
        });
    }
}
