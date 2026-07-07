using RB.VideoTranslator.Domain.Dbo;
using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Interfaces;

public interface ISpeakerSampleExtractorService
{
    /// <summary>
    /// Extracts one clean voice sample per speaker from <see cref="VideoJob.VocalsAudioPath"/>
    /// (the isolated vocals track produced by Demucs), using the start/end timestamps of each
    /// speaker's longest segment recorded in the VTT's speaker/gender summary NOTE block.
    /// Writes samples into a <c>speaker_samples</c> subfolder of the job's processing folder,
    /// populates <see cref="VideoJob.SpeakerSamplePathsJson"/>, and transitions state to
    /// <see cref="JobState.SpeakerSamplesExtracted"/>.
    /// </summary>
    Task ExtractAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default);
}
