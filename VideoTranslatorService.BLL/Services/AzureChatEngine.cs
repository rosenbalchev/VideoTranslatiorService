using OpenAI.Chat;

namespace VideoTranslatorService.BLL.Services;

public sealed class AzureChatEngine : IAzureChatEngine
{
    private readonly ChatClient _client;

    public AzureChatEngine(ChatClient client) => _client = client;

    public async Task<string> CompleteChatAsync(
        string systemPrompt,
        string userContent,
        CancellationToken ct = default)
    {
        ChatMessage[] messages =
        [
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userContent),
        ];

        var result = await _client.CompleteChatAsync(messages, cancellationToken: ct);
        return result.Value.Content[0].Text;
    }
}
