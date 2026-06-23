namespace VideoTranslatorService.BLL.Services;

public interface IAzureChatEngine
{
    Task<string> CompleteChatAsync(string systemPrompt, string userContent, CancellationToken ct = default);
}
