using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface IAudioMixerService
{
    /// <summary>
    /// Mixes the voice-removed audio bed with the synthesised voice track.
    /// Populates <see cref="VideoJob.MixedAudioPath"/> and transitions state to
    /// <see cref="JobState.MixedNoVoiceWithSyntheticVoice"/>.
    /// </summary>
    Task MixAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default);
}
