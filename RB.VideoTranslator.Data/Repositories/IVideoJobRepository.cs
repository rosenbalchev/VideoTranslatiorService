using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.Data.Repositories;

public interface IVideoJobRepository
{
    Task<VideoJob> CreateAsync(VideoJob job, CancellationToken ct = default);
    Task<VideoJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<VideoJob>> GetByStateAsync(JobState state, CancellationToken ct = default);
    Task<IReadOnlyList<VideoJob>> GetResumableAsync(CancellationToken ct = default);
    Task UpdateAsync(VideoJob job, CancellationToken ct = default);
}
