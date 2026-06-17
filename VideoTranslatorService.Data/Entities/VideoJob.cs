namespace VideoTranslatorService.Data.Entities;

public class VideoJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string OriginalFileName { get; set; }
    public required string InputFilePath { get; set; }
    public required string ProcessingFolderPath { get; set; }

    // Populated after the file is moved to the processing folder
    public string? ProcessingVideoPath { get; set; }

    // Populated after media separation (Step 1)
    public string? ExtractedAudioPath { get; set; }
    public string? SilentVideoPath { get; set; }

    // Populated after translation (Step 2 — future)
    public string? TranslatedAudioPath { get; set; }

    // Populated after merge (Step 3 — future)
    public string? OutputFilePath { get; set; }

    public JobState State { get; set; } = JobState.Queued;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
