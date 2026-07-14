namespace RB.VideoTranslator.Domain.Dbo;

// One raw observation of a single synthesised subtitle paragraph, logged for one voice.
// Never mutated after insert — every synthesis attempt (including overrun retries) gets
// its own row, so the full history survives to be refit against as the feature set or
// model changes, instead of being collapsed into a running sum immediately (see
// VttToAzureTtsService.VoicePaceModel, which loads and refits from these rows per job).
public class VoicePaceSample
{
    public int Id { get; set; }
    public required string Voice { get; set; }
    public int CharCount { get; set; }
    public int WordCount { get; set; }
    public int SentenceCount { get; set; }
    public int CommaCount { get; set; }

    // Prosody rate (%) actually sent to Azure for this sample.
    public double RateUsed { get; set; }

    // Subtitle window duration this paragraph was synthesised against.
    public int ExpectedMs { get; set; }

    // Measured synthesised audio duration.
    public int ActualMs { get; set; }

    // ActualMs back-derived to what it would have been at rate=100%
    // (ActualMs * RateUsed / 100) — the regression target.
    public double NaturalMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
