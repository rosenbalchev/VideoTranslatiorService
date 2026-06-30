using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RB.VideoTranslator.BLL;
using RB.VideoTranslator.BLL.Services;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.CLI;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        // All options are optional on the CLI — required values can come from appsettings.json.
        // Runtime validation happens inside SetAction after options are merged.

        var workFolderOpt = new Option<DirectoryInfo?>("--work-folder")
        {
            Description = "Root folder for all pipeline data. Overrides WorkingFolderPath in appsettings.json. " +
                          "Subfolders 'input', 'processing', 'output' are created automatically."
        };

        var ffmpegOpt = new Option<string?>("--ffmpeg")
        {
            Description = "Path to the ffmpeg executable. Overrides FfmpegPath in appsettings.json."
        };

        var pythonOpt = new Option<string?>("--python")
        {
            Description = "Path to the python executable. Overrides PythonPath in appsettings.json."
        };

        var demucsOpt = new Option<string?>("--demucs")
        {
            Description = "Python executable used to run 'python -m demucs'. Overrides DemucsPath in appsettings.json."
        };

        var azureKeyOpt = new Option<string?>("--azure-key")
        {
            Description = "Azure Cognitive Services subscription key. Overrides AzureSubscriptionKey in appsettings.json."
        };

        var azureEndpointOpt = new Option<string?>("--azure-endpoint")
        {
            Description = "Azure Cognitive Services endpoint URL. Overrides AzureEndpointUrl in appsettings.json."
        };

        var openAiEndpointOpt = new Option<string?>("--openai-endpoint")
        {
            Description = "Azure AI Services root endpoint URL for GPT. Overrides AzureOpenAiEndpoint in appsettings.json."
        };

        var openAiDeploymentOpt = new Option<string?>("--openai-deployment")
        {
            Description = "Azure OpenAI deployment name. Overrides AzureOpenAiDeployment in appsettings.json."
        };

        var targetLangOpt = new Option<string?>("--target-lang")
        {
            Description = "Comma-separated target languages, e.g. \"Bulgarian,English\". " +
                          "Overrides TranslationTargetLanguages in appsettings.json."
        };

        var venvOpt = new Option<DirectoryInfo?>("--venv")
        {
            Description = "Path to a Python virtual environment. Overrides VenvPath in appsettings.json."
        };

        var femaleOpt = new Option<bool>("--female")
        {
            Description = "Use a female Azure TTS voice instead of the default male voice."
        };

        var root = new RootCommand(
            "RB.VideoTranslator — picks up video files and advances them through the translation pipeline. " +
            "Configure via appsettings.json (RBVideoTranslator section); CLI arguments override config values.");

        root.Add(workFolderOpt);
        root.Add(ffmpegOpt);
        root.Add(pythonOpt);
        root.Add(demucsOpt);
        root.Add(azureKeyOpt);
        root.Add(azureEndpointOpt);
        root.Add(openAiEndpointOpt);
        root.Add(openAiDeploymentOpt);
        root.Add(targetLangOpt);
        root.Add(venvOpt);
        root.Add(femaleOpt);

        root.SetAction(async parseResult =>
        {
            // ── Load config file ─────────────────────────────────────────────
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            // ── Build service provider with merged options ───────────────────
            var services = BuildServices(configuration, o =>
            {
                // CLI overrides — only applied when the option was explicitly provided.
                if (parseResult.GetResult(workFolderOpt) is not null)
                    o.WorkingFolderPath = parseResult.GetValue(workFolderOpt)!.FullName;

                if (parseResult.GetResult(azureKeyOpt) is not null)
                    o.AzureSubscriptionKey = parseResult.GetValue(azureKeyOpt)!;

                if (parseResult.GetResult(azureEndpointOpt) is not null)
                    o.AzureEndpointUrl = parseResult.GetValue(azureEndpointOpt)!;

                if (parseResult.GetResult(openAiEndpointOpt) is not null)
                    o.AzureOpenAiEndpoint = NormalizeOpenAiEndpoint(parseResult.GetValue(openAiEndpointOpt)!);

                if (parseResult.GetResult(ffmpegOpt) is not null)
                    o.FfmpegPath = parseResult.GetValue(ffmpegOpt)!;

                if (parseResult.GetResult(openAiDeploymentOpt) is not null)
                    o.AzureOpenAiDeployment = parseResult.GetValue(openAiDeploymentOpt)!;

                if (parseResult.GetResult(targetLangOpt) is not null)
                    o.TranslationTargetLanguages = parseResult.GetValue(targetLangOpt)!
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (parseResult.GetResult(femaleOpt) is not null)
                    o.UseFemaleVoice = parseResult.GetValue(femaleOpt);

                // Venv: explicit CLI venv arg > config VenvPath > default <WorkingFolderPath>\rb.video.translator
                if (parseResult.GetResult(venvOpt) is not null)
                {
                    o.VenvPath = parseResult.GetValue(venvOpt)!.FullName;
                }
                else if (string.IsNullOrEmpty(o.VenvPath) && !string.IsNullOrEmpty(o.WorkingFolderPath))
                {
                    var defaultVenv = Path.Combine(o.WorkingFolderPath, "rb.video.translator");
                    if (Directory.Exists(defaultVenv))
                        o.VenvPath = defaultVenv;
                }

                // Python/Demucs: CLI args > config > venv path
                if (!string.IsNullOrEmpty(o.VenvPath))
                {
                    var venvPython = VenvPythonPath(o.VenvPath);
                    // Only override if not explicitly set by user CLI args
                    if (parseResult.GetResult(pythonOpt) is not null)
                        o.PythonPath = parseResult.GetValue(pythonOpt)!;
                    else if (string.IsNullOrEmpty(o.PythonPath) || o.PythonPath == "python")
                        o.PythonPath = venvPython;

                    if (parseResult.GetResult(demucsOpt) is not null)
                        o.DemucsPath = parseResult.GetValue(demucsOpt)!;
                    else if (string.IsNullOrEmpty(o.DemucsPath) || o.DemucsPath == "python")
                        o.DemucsPath = venvPython;
                }
                else
                {
                    if (parseResult.GetResult(pythonOpt) is not null)
                        o.PythonPath = parseResult.GetValue(pythonOpt)!;
                    if (parseResult.GetResult(demucsOpt) is not null)
                        o.DemucsPath = parseResult.GetValue(demucsOpt)!;
                }
            });

            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var opts = sp.GetRequiredService<IOptions<PipelineOptions>>().Value;

            // ── Validate required values ──────────────────────────────────────
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(opts.WorkingFolderPath))
                errors.Add("WorkingFolderPath: set via --work-folder or in appsettings.json → RBVideoTranslator.WorkingFolderPath");
            if (string.IsNullOrWhiteSpace(opts.AzureSubscriptionKey))
                errors.Add("AzureSubscriptionKey: set via --azure-key or in appsettings.json → RBVideoTranslator.AzureSubscriptionKey");
            if (string.IsNullOrWhiteSpace(opts.AzureEndpointUrl))
                errors.Add("AzureEndpointUrl: set via --azure-endpoint or in appsettings.json → RBVideoTranslator.AzureEndpointUrl");
            if (string.IsNullOrWhiteSpace(opts.AzureOpenAiEndpoint))
                errors.Add("AzureOpenAiEndpoint: set via --openai-endpoint or in appsettings.json → RBVideoTranslator.AzureOpenAiEndpoint");

            if (errors.Count > 0)
            {
                Console.Error.WriteLine("ERROR: The following required values are missing:");
                foreach (var e in errors) Console.Error.WriteLine($"  • {e}");
                return;
            }

            // ── Derive folder layout from WorkingFolderPath ───────────────────
            var workPath         = opts.WorkingFolderPath;
            var inputFolder      = new DirectoryInfo(Path.Combine(workPath, "input"));
            var processingFolder = new DirectoryInfo(Path.Combine(workPath, "processing"));
            var outputFolder     = string.IsNullOrEmpty(opts.OutputFolderPath)
                                       ? Path.Combine(workPath, "output")
                                       : opts.OutputFolderPath;

            Directory.SetCurrentDirectory(workPath);
            Directory.CreateDirectory(inputFolder.FullName);
            Directory.CreateDirectory(processingFolder.FullName);
            Directory.CreateDirectory(outputFolder);

            // Normalise OpenAI endpoint once (in case it came from config without normalisation)
            opts.AzureOpenAiEndpoint = NormalizeOpenAiEndpoint(opts.AzureOpenAiEndpoint);
            opts.OutputFolderPath    = outputFolder;

            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CLI");

            await sp.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

            var jobService = sp.GetRequiredService<IJobService>();

            // ── Phase 1: discover and queue new input files ──────────────────
            if (!inputFolder.Exists)
            {
                log.LogWarning("Input folder does not exist: {Path} — skipping file discovery", inputFolder.FullName);
            }
            else
            {
                var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".mp4", ".mkv", ".avi", ".mov", ".webm" };

                var newFiles = inputFolder
                    .GetFiles()
                    .Where(f => videoExtensions.Contains(f.Extension))
                    .ToList();

                if (newFiles.Count == 0)
                {
                    log.LogInformation("No new video files found in {Path}", inputFolder.FullName);
                }
                else
                {
                    log.LogInformation("Found {Count} new video file(s) — queuing...", newFiles.Count);
                    foreach (var file in newFiles)
                    {
                        try
                        {
                            var job = await jobService.CreateJobFromFileAsync(
                                file.FullName, processingFolder.FullName);
                            log.LogInformation("  Queued {File} → job {Id}", file.Name, job.Id);
                        }
                        catch (Exception ex)
                        {
                            log.LogError(ex, "  Failed to queue {File}", file.Name);
                        }
                    }
                }
            }

            // ── Phase 2: resume all jobs that are waiting for their next step ─
            var pending = await jobService.GetResumableJobsAsync();

            if (pending.Count == 0)
            {
                log.LogInformation("No pending jobs found in the database.");
                return;
            }

            log.LogInformation("{Count} job(s) are waiting to advance — starting pipeline...", pending.Count);

            var orchestrator = sp.GetRequiredService<IPipelineOrchestrator>();

            // Process jobs sequentially (one at a time) to ensure files are processed one by one
            foreach (var job in pending)
            {
                log.LogInformation(
                    "=== Job {Id} | {File} | state: {State} — attempting to advance to next step ===",
                    job.Id, job.OriginalFileName, job.State);

                try
                {
                    await orchestrator.AdvanceAsync(job);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Job {Id} failed during advancement", job.Id);
                }
            }
        });

        return await root.Parse(args).InvokeAsync();
    }

    private static string VenvPythonPath(string venvRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvRoot, "Scripts", "python.exe")
            : Path.Combine(venvRoot, "bin", "python");

    // AzureOpenAIClient builds the REST path internally (/openai/deployments/…).
    // Strip any /openai/v1 suffix the user may have copied from AI Foundry docs.
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

    private static ServiceProvider BuildServices(IConfiguration configuration, Action<PipelineOptions> configureOverrides)
    {
        // Eagerly resolve WorkingFolderPath to configure the SQLite connection string.
        var tempOpts = new PipelineOptions();
        configuration.GetSection(PipelineOptionsDefaults.SectionName).Bind(tempOpts);
        configureOverrides(tempOpts);

        var dbPath = string.IsNullOrEmpty(tempOpts.WorkingFolderPath)
            ? "videotranslator.db"
            : Path.Combine(tempOpts.WorkingFolderPath, "videotranslator.db");

        return new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging(b => b
                .AddSimpleConsole(opts => opts.TimestampFormat = "HH:mm:ss ")
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .AddScoped<IVideoJobRepository, VideoJobRepository>()
            .AddRBVideoTranslator(configuration, configureOverrides)
            .BuildServiceProvider();
    }
}
