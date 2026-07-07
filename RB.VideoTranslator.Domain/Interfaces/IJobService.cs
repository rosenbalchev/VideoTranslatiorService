using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IJobService
{
    /// <summary>
    /// Moves <paramref name="inputFilePath"/> into <paramref name="processingFolder"/>,
    /// creates a DB record in Queued state, and returns the new job.
    /// </summary>
    Task<VideoJob> CreateJobFromFileAsync(
        string inputFilePath,
        string processingFolder,
        CancellationToken ct = default);

    Task TransitionStateAsync(
        Guid jobId,
        JobState newState,
        string? errorMessage = null,
        CancellationToken ct = default);

    Task<VideoJob?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Returns all jobs that are at a stable intermediate state and can be advanced
    /// to the next pipeline step, ordered oldest-first.
    /// </summary>
    Task<IReadOnlyList<VideoJob>> GetResumableJobsAsync(CancellationToken ct = default);
}
