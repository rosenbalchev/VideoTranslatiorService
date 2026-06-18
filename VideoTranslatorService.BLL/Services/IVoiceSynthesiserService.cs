using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IVoiceSynthesiserService
{
    /// <summary>
    /// Synthesises a voice audio track from the translated .srt file.
    /// Populates <see cref="VideoJob.SynthesisedVoicePath"/> and transitions state to
    /// <see cref="JobState.VoiceSynthesised"/>.
    /// </summary>
    Task SynthesiseAsync(VideoJob job, CancellationToken ct = default);
}
