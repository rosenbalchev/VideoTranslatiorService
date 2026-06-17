namespace VideoTranslatorService.BLL.Services;

public interface IProcessRunner
{
    Task RunAsync(string executable, string arguments, CancellationToken ct = default);
}
