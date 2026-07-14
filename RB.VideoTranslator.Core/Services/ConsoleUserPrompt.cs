using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class ConsoleUserPrompt : IUserPrompt
{
    public Task WaitForKeyPressAsync(string message, CancellationToken ct = default)
    {
        Console.WriteLine(message);
        Console.ReadKey(intercept: true);
        return Task.CompletedTask;
    }
}
