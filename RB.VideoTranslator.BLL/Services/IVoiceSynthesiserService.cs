using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public interface IVoiceSynthesiserService
{
    /// <summary>
    /// Synthesises a voice audio track from the translated .srt file.
    /// Populates <see cref="VideoJob.SynthesisedVoicePath"/> and transitions state to
    /// <see cref="JobState.VoiceSynthesised"/>.
    /// </summary>
    Task SynthesiseAsync(VideoJob job, CancellationToken ct = default);
}
