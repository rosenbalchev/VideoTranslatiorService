using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVoicePaceRepository
{
    /// <summary>Loads every recorded sample for every voice, raw — no aggregation.</summary>
    Task<IReadOnlyList<VoicePaceSample>> GetAllSamplesAsync(CancellationToken ct = default);

    /// <summary>Appends one raw synthesis sample for <paramref name="voice"/>.</summary>
    Task RecordSampleAsync(
        string voice,
        TextFeatures features,
        double rate,
        int expectedMs,
        int actualMs,
        CancellationToken ct = default);
}
