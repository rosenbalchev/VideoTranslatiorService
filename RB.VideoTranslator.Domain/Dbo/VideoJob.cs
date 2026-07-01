using RB.VideoTranslator.Domain.Enums;

namespace RB.VideoTranslator.Domain.Dbo;

public class VideoJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OriginalFileName { get; set; }
    public required string InputFilePath { get; set; }
    public required string ProcessingFolderPath { get; set; }

    // Step 1 — media separation
    public string? ProcessingVideoPath { get; set; }
    public string? ExtractedAudioPath { get; set; }
    public string? SilentVideoPath { get; set; }

    // Step 2 — SRT subtitle extraction
    public string? SrtFilePath { get; set; }

    // Step 3 — Azure TTS synthesis
    public string? AzureTtsAudioPath { get; set; }

    // Step 4 — voice removal
    public string? VoiceRemovedAudioPath { get; set; }

    // Step 4 — SRT translation
    public string? TranslatedSrtFilePath { get; set; }

    // Step 5 — voice synthesis
    public string? SynthesisedVoicePath { get; set; }

    // Step 6 — audio mix
    public string? MixedAudioPath { get; set; }

    // JSON array of LanguageResult — set after all languages are translated/synthesised/mixed
    public string? LanguageResultsJson { get; set; }

    // Step 7 — final video
    public string? OutputFilePath { get; set; }

    // Input audio format — probed from the extracted WAV during media separation.
    // Used to match the dubbed track's channel count and sample rate to the original.
    public int AudioChannels   { get; set; } = 2;
    public int AudioSampleRate { get; set; } = 44100;

    public JobState State { get; set; } = JobState.Queued;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
