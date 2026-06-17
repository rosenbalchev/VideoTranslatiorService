using Microsoft.EntityFrameworkCore;
using VideoTranslatorService.Data.Context;
using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.Data.Repositories;

public sealed class VideoJobRepository : IVideoJobRepository
{
    private readonly AppDbContext _db;

    public VideoJobRepository(AppDbContext db) => _db = db;

    public async Task<VideoJob> CreateAsync(VideoJob job, CancellationToken ct = default)
    {
        _db.VideoJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return job;
    }

    public Task<VideoJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.VideoJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<VideoJob>> GetByStateAsync(JobState state, CancellationToken ct = default) =>
        await _db.VideoJobs.AsNoTracking().Where(j => j.State == state).ToListAsync(ct);

    public async Task UpdateAsync(VideoJob job, CancellationToken ct = default)
    {
        job.UpdatedAt = DateTime.UtcNow;
        _db.VideoJobs.Update(job);
        await _db.SaveChangesAsync(ct);
    }
}
