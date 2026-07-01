using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IVoiceSynthesiserService
{
    /// <summary>
    /// Synthesises a voice audio track from the translated .srt file.
    /// Populates <see cref="VideoJob.SynthesisedVoicePath"/> and transitions state to
    /// <see cref="JobState.VoiceSynthesised"/>.
    /// </summary>
    Task SynthesiseAsync(VideoJob job, CancellationToken ct = default);
}
