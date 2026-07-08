namespace RB.VideoTranslator.Domain.Dbo;

// Accumulated Azure TTS speaking-pace statistics for one voice, learned across synthesis
// sessions (see VttToAzureTtsService). TotalChars / TotalNaturalMs together give the
// voice's observed chars/sec pace at rate=100%, back-derived from whatever rate was
// actually used for each sample — lets later jobs start closer to the correct prosody
// rate for a voice instead of relying on overrun retries every time.
public class VoicePaceStat
{
    public required string Voice { get; set; }
    public double TotalChars { get; set; }
    public double TotalNaturalMs { get; set; }
    public int SampleCount { get; set; }

    // The prosody rate (%) actually used for the most recent sample — a diagnostic/audit
    // field distinct from CharsPerSecondAt100 (the derived estimate), so the DB row shows
    // what was really sent to Azure last, not just the accumulated natural-pace estimate.
    public double LastRateUsed { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
