using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Data.Repositories;
using RB.VideoTranslator.Domain.Consts;
using RB.VideoTranslator.Domain.Interfaces;
using RB.VideoTranslator.Domain.Models;

namespace RB.VideoTranslator.Core;

/// <summary>
/// Handles the bootstrap process for the video translation pipeline.
/// Manages configuration loading, service provider creation, folder setup, and database initialization.
/// </summary>
public sealed class PipelineBootstrapper : IPipelineRunner
{
    private IServiceProvider? _serviceProvider;
    private ILogger? _logger;
    private PipelineOptions? _options;

    /// <summary>
    /// Initializes the pipeline with configuration from files and optional programmatic overrides.
    /// </summary>
    public async Task InitializeAsync(string? configFilePath = null, Action<PipelineOptions>? optionsOverride = null)
    {
        // Load configuration from file
        var configuration = BuildConfiguration(configFilePath);

        // Build service provider with merged options
        _serviceProvider = BuildServiceProvider(configuration, optionsOverride);

        var scope = _serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        _logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PipelineBootstrapper");
        _options = sp.GetRequiredService<IOptions<PipelineOptions>>().Value;

        // Initialize database
        var db = sp.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await MigrateSchemaAsync(db);

        // Setup folder structure and normalize configuration
        SetupFolders();

        _logger.LogInformation("Pipeline initialized successfully");
    }

    // Adds new columns to the VideoJobs table if they don't exist yet.
    // EnsureCreatedAsync creates the table on first run but never alters existing schemas,
    // so we apply idempotent ALTER TABLE statements for any column added post-initial-creation.
    private static async Task MigrateSchemaAsync(AppDbContext db)
    {
        foreach (var sql in new[]
        {
            "ALTER TABLE VideoJobs ADD COLUMN AudioChannels INTEGER NOT NULL DEFAULT 2",
            "ALTER TABLE VideoJobs ADD COLUMN AudioSampleRate INTEGER NOT NULL DEFAULT 44100",
        })
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // Column already exists on subsequent runs — expected, not an error.
            }
        }
    }

    /// <summary>
    /// Runs the complete pipeline: discovers, queues, and processes video files.
    /// </summary>
    public async Task RunAsync()
    {
        if (_serviceProvider == null)
            throw new InvalidOperationException("Pipeline must be initialized before running. Call InitializeAsync first.");

        if (_options == null)
            throw new InvalidOperationException("Pipeline options not loaded.");

        if (_logger == null)
            _logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("PipelineBootstrapper");

        using var scope = _serviceProvider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var workPath = _options.WorkingFolderPath;
        var inputFolder = new DirectoryInfo(Path.Combine(workPath, "input"));
        var processingFolder = new DirectoryInfo(Path.Combine(workPath, "processing"));

        var jobService = sp.GetRequiredService<IJobService>();
        var orchestrator = sp.GetRequiredService<IPipelineOrchestrator>();

        // Phase 1: Discover and queue new input files
        DiscoverAndQueueNewFiles(sp, inputFolder, processingFolder, jobService);

        // Phase 2: Resume all jobs that are waiting for their next step
        var pending = await jobService.GetResumableJobsAsync();

        if (pending.Count == 0)
        {
            _logger.LogInformation("No pending jobs found in the database.");
            return;
        }

        _logger.LogInformation("{Count} job(s) are waiting to advance — starting pipeline...", pending.Count);

        // Process jobs sequentially (one at a time) to ensure files are processed one by one
        foreach (var job in pending)
        {
            _logger.LogInformation(
                "=== Job {Id} | {File} | state: {State} — attempting to advance to next step ===",
                job.Id, job.OriginalFileName, job.State);

            try
            {
                await orchestrator.AdvanceAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {Id} failed during advancement", job.Id);
            }
        }
    }

    /// <summary>
    /// Builds configuration from appsettings.json and environment variables.
    /// </summary>
    private static IConfiguration BuildConfiguration(string? configFilePath)
    {
        var builder = new ConfigurationBuilder();

        if (!string.IsNullOrEmpty(configFilePath))
            builder.AddJsonFile(configFilePath, optional: true);
        else
            builder.AddJsonFile("appsettings.json", optional: true);

        builder.AddEnvironmentVariables();
        return builder.Build();
    }

    /// <summary>
    /// Builds the service provider with all required services.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        IConfiguration configuration,
        Action<PipelineOptions>? configureOverrides)
    {
        // Eagerly resolve WorkingFolderPath to configure the SQLite connection string.
        var tempOpts = new PipelineOptions();
        configuration.GetSection(PipelineOptionsDefaults.SectionName).Bind(tempOpts);
        configureOverrides?.Invoke(tempOpts);

        var dbPath = string.IsNullOrEmpty(tempOpts.WorkingFolderPath)
            ? "videotranslator.db"
            : Path.Combine(tempOpts.WorkingFolderPath, "videotranslator.db");

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(b => b
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .AddScoped<IVideoJobRepository, VideoJobRepository>()
            .AddRBVideoTranslator(configuration, configureOverrides)
            .BuildServiceProvider();
    }

    /// <summary>
    /// Creates necessary folder structure and normalizes configuration values.
    /// </summary>
    private void SetupFolders()
    {
        if (_options == null)
            throw new InvalidOperationException("Pipeline options not loaded.");

        var workPath = _options.WorkingFolderPath;
        var inputFolder = Path.Combine(workPath, "input");
        var processingFolder = Path.Combine(workPath, "processing");
        var outputFolder = string.IsNullOrEmpty(_options.OutputFolderPath)
            ? Path.Combine(workPath, "output")
            : _options.OutputFolderPath;

        Directory.SetCurrentDirectory(workPath);
        Directory.CreateDirectory(inputFolder);
        Directory.CreateDirectory(processingFolder);
        Directory.CreateDirectory(outputFolder);

        // Normalize OpenAI endpoint
        _options.AzureOpenAiEndpoint = NormalizeOpenAiEndpoint(_options.AzureOpenAiEndpoint);
        _options.OutputFolderPath = outputFolder;

        ValidateRequiredOptions();
    }

    /// <summary>
    /// Validates that all required configuration options are set.
    /// </summary>
    private void ValidateRequiredOptions()
    {
        if (_options == null)
            throw new InvalidOperationException("Pipeline options not loaded.");

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_options.WorkingFolderPath))
            errors.Add("WorkingFolderPath: set via configuration or InitializeAsync options");
        if (string.IsNullOrWhiteSpace(_options.AzureSubscriptionKey))
            errors.Add("AzureSubscriptionKey: set via configuration or InitializeAsync options");
        if (string.IsNullOrWhiteSpace(_options.AzureEndpointUrl))
            errors.Add("AzureEndpointUrl: set via configuration or InitializeAsync options");
        if (string.IsNullOrWhiteSpace(_options.AzureOpenAiEndpoint))
            errors.Add("AzureOpenAiEndpoint: set via configuration or InitializeAsync options");

        if (errors.Count > 0)
        {
            var message = "ERROR: The following required values are missing:\n  • " + string.Join("\n  • ", errors);
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// AzureOpenAIClient builds the REST path internally (/openai/deployments/…).
    /// Strip any /openai/v1 suffix the user may have copied from AI Foundry docs.
    /// </summary>
    private static string NormalizeOpenAiEndpoint(string raw)
    {
        var url = raw.TrimEnd('/');
        foreach (var suffix in new[] { "/openai/v1", "/openai" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return url[..^suffix.Length] + "/";
        }
        return url + "/";
    }

    /// <summary>
    /// Discovers new video files in the input folder and queues them as jobs.
    /// </summary>
    private void DiscoverAndQueueNewFiles(
        IServiceProvider sp,
        DirectoryInfo inputFolder,
        DirectoryInfo processingFolder,
        IJobService jobService)
    {
        if (!inputFolder.Exists)
        {
            _logger?.LogWarning("Input folder does not exist: {Path} — skipping file discovery", inputFolder.FullName);
            return;
        }

        var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".webm" };

        var newFiles = inputFolder
            .GetFiles()
            .Where(f => videoExtensions.Contains(f.Extension))
            .ToList();

        if (newFiles.Count == 0)
        {
            _logger?.LogInformation("No new video files found in {Path}", inputFolder.FullName);
            return;
        }

        _logger?.LogInformation("Found {Count} new video file(s) — queuing...", newFiles.Count);
        foreach (var file in newFiles)
        {
            try
            {
                var job = jobService.CreateJobFromFileAsync(
                    file.FullName, processingFolder.FullName).GetAwaiter().GetResult();
                _logger?.LogInformation("  Queued {File} → job {Id}", file.Name, job.Id);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "  Failed to queue {File}", file.Name);
            }
        }
    }
}
