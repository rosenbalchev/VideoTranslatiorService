namespace RB.VideoTranslator.Domain.Interfaces;

public interface IAzureChatEngine
{
    Task<string> CompleteChatAsync(string systemPrompt, string userContent, CancellationToken ct = default);
}
