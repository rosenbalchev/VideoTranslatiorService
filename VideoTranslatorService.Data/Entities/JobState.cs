namespace VideoTranslatorService.Data.Entities;

public enum JobState
{
    Queued,
    SeparatingMedia,
    AudioExtracted,
    Translating,
    Merging,
    Completed,
    Failed
}
