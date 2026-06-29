using Microsoft.EntityFrameworkCore;
using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.Data.Context;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<VideoJob> VideoJobs => Set<VideoJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VideoJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.State).HasConversion<string>();
        });
    }
}
