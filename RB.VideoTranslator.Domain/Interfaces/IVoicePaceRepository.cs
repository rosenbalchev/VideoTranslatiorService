using RB.VideoTranslator.Domain.Dbo;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVoicePaceRepository
{
    /// <summary>Loads accumulated pace stats for every voice recorded so far, keyed by voice name.</summary>
    Task<IReadOnlyDictionary<string, VoicePaceStat>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Accumulates one synthesis sample into <paramref name="voice"/>'s running stats,
    /// creating the row if this is the first sample recorded for that voice.
    /// </summary>
    Task RecordSampleAsync(string voice, int textLength, double rate, int actualMs, CancellationToken ct = default);
}
