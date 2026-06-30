namespace RB.VideoTranslator.BLL.Services;

public interface IAzureChatEngine
{
    Task<string> CompleteChatAsync(string systemPrompt, string userContent, CancellationToken ct = default);
}
