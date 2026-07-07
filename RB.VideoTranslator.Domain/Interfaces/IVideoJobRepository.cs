using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVideoJobRepository
{
    Task<VideoJob> CreateAsync(VideoJob job, CancellationToken ct = default);
    Task<VideoJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<VideoJob>> GetByStateAsync(JobState state, CancellationToken ct = default);
    Task<IReadOnlyList<VideoJob>> GetResumableAsync(CancellationToken ct = default);
    Task UpdateAsync(VideoJob job, CancellationToken ct = default);
}
