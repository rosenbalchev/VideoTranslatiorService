namespace RB.VideoTranslator.Domain.Interfaces;

// Abstraction over blocking console interaction, so callers that need to pause for human
// input (e.g. PipelineOrchestrator's gender-review pause) can be unit tested without a real
// attended terminal.
public interface IUserPrompt
{
    /// <summary>Writes <paramref name="message"/> and blocks until a key is pressed.</summary>
    Task WaitForKeyPressAsync(string message, CancellationToken ct = default);
}
