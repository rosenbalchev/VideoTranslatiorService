namespace RB.VideoTranslator.BLL;

/// <summary>
/// Represents the complete pipeline runner that handles configuration, setup, and execution.
/// This is the main entry point for consumers of the RB.VideoTranslator.BLL NuGet package.
/// </summary>
public interface IPipelineRunner
{
    /// <summary>
    /// Initializes the pipeline with the given configuration and options.
    /// This includes loading configuration from files, merging CLI overrides,
    /// building the service provider, creating necessary folders, and initializing the database.
    /// </summary>
    /// <param name="configFilePath">Path to appsettings.json; if null, default location is used.</param>
    /// <param name="options">Optional pipeline options to override config file values.</param>
    Task InitializeAsync(string? configFilePath = null, Action<PipelineOptions>? options = null);

    /// <summary>
    /// Runs the complete pipeline: discovers new input files, queues them as jobs,
    /// and processes all pending jobs sequentially.
    /// </summary>
    Task RunAsync();
}
