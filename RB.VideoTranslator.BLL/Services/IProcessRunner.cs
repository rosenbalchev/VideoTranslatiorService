namespace RB.VideoTranslator.BLL.Services;

public interface IProcessRunner
{
    Task RunAsync(string executable, string arguments, CancellationToken ct = default);

    /// <summary>Runs the process and returns its standard-output as a string.</summary>
    Task<string> RunAndCaptureAsync(string executable, string arguments, CancellationToken ct = default);
}
