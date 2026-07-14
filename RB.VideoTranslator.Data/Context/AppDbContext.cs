using Microsoft.EntityFrameworkCore;
using RB.VideoTranslator.Domain.Dbo;

namespace RB.VideoTranslator.Data.Context;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VideoJob> VideoJobs => Set<VideoJob>();
    public DbSet<VoicePaceSample> VoicePaceSamples => Set<VoicePaceSample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VideoJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasConversion<string>();
        });

        modelBuilder.Entity<VoicePaceSample>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });
    }
}
