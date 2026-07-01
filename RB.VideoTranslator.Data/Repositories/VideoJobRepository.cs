using Microsoft.EntityFrameworkCore;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Data.Repositories;

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

    public async Task<IReadOnlyList<VideoJob>> GetResumableAsync(CancellationToken ct = default)
    {
        // Stable states: ready for the next step.
        // In-progress states: the process was interrupted (crash/restart) and must be retried.
        // The orchestrator detects in-progress states and resets them to their stable
        // predecessor before retrying, so it is safe to include both here.
        JobState[] resumable =
        [
            // stable — ready for the next step
            JobState.Queued,
            JobState.AudioExtracted,
            JobState.SrtExtracted,
            JobState.VoiceRemoved,
            JobState.MixedNoVoiceWithSyntheticVoice,
            // in-progress (orchestrator resets these to their stable predecessor and retries)
            JobState.SeparatingMedia,
            JobState.ExtractingSrt,
            JobState.RemovingVoice,
            // Inside the multi-language loop — all reset to VoiceRemoved
            JobState.TranslatingSrt,
            JobState.SrtTranslated,
            JobState.SynthesisingAzureTts,
            JobState.AzureTtsSynthesised,
            JobState.MixingAudio,
            JobState.AddingToVideo,
        ];

        return await _db.VideoJobs
            .AsNoTracking()
            .Where(j => resumable.Contains(j.State))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(VideoJob job, CancellationToken ct = default)
    {
        job.UpdatedAt = DateTime.UtcNow;

        // If the DbContext already tracks an instance with this key (e.g. from a prior
        // CreateAsync in the same scope), copy values into it instead of attaching a
        // second instance — which would throw an identity-conflict exception.
        var tracked = _db.ChangeTracker.Entries<VideoJob>()
            .FirstOrDefault(e => e.Entity.Id == job.Id);

        if (tracked != null)
            tracked.CurrentValues.SetValues(job);
        else
            _db.VideoJobs.Update(job);

        await _db.SaveChangesAsync(ct);
    }
}
