using Azure.AI.OpenAI;
using Azure.Identity;
using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.BLL;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Context;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.CLI;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        var inputFolderOpt = new Option<DirectoryInfo>("--input-folder")
        {
            Required = true,
            Description = "Folder to scan for new video files"
        };

        var processingFolderOpt = new Option<DirectoryInfo>("--processing-folder")
        {
            Required = true,
            Description = "Working folder where jobs are staged during processing"
        };

        var dbOpt = new Option<FileInfo>("--db")
        {
            Description = "Path to the SQLite database file",
            DefaultValueFactory = _ => new FileInfo("videotranslator.db")
        };

        var ffmpegOpt = new Option<string>("--ffmpeg")
        {
            Description = "Path to the ffmpeg executable (must be on PATH or supply full path)",
            DefaultValueFactory = _ => "ffmpeg"
        };

        var pythonOpt = new Option<string>("--python")
        {
            Description = "Path to the python executable (must be on PATH or supply full path)",
            DefaultValueFactory = _ => "python"
        };

        var demucsOpt = new Option<string>("--demucs")
        {
            Description = "Python executable used to run 'python -m demucs' (must be on PATH or supply full path)",
            DefaultValueFactory = _ => "python"
        };

        var azureKeyOpt = new Option<string>("--azure-key")
        {
            Required = true,
            Description = "Azure Cognitive Services subscription key"
        };

        var azureEndpointOpt = new Option<string>("--azure-endpoint")
        {
            Required = true,
            Description = "Azure Cognitive Services endpoint URL (e.g. https://my-resource.cognitiveservices.azure.com/)"
        };


        var openAiEndpointOpt = new Option<string>("--openai-endpoint")
        {
            Required = true,
            Description = "Azure AI Services root endpoint URL for GPT (e.g. https://my-resource.services.ai.azure.com/)"
        };

        var openAiDeploymentOpt = new Option<string>("--openai-deployment")
        {
            Description = "Azure OpenAI deployment name used for SRT translation",
            DefaultValueFactory = _ => "gpt-4o-mini"
        };

        var targetLangOpt = new Option<string>("--target-lang")
        {
            Description = "Comma-separated target languages for SRT translation (e.g. \"Bulgarian,English\")",
            DefaultValueFactory = _ => "Bulgarian"
        };

        var outputFolderOpt = new Option<string>("--output-folder")
        {
            Description = "Folder where the finished dubbed video files are written",
            DefaultValueFactory = _ => "output"
        };

        var venvOpt = new Option<DirectoryInfo?>("--venv")
        {
            Description = "Path to a Python virtual environment whose interpreter is used for ALL Python " +
                          "operations (Whisper, Demucs). Overrides --python and --demucs when provided. " +
                          "Example: --venv bgtts-env"
        };

        var femaleOpt = new Option<bool>("--female")
        {
            Description = "Use a female Azure TTS voice instead of the default male voice"
        };

        var root = new RootCommand(
            "AI Video Translator — picks up video files and advances them through the translation pipeline");

        root.Add(inputFolderOpt);
        root.Add(processingFolderOpt);
        root.Add(dbOpt);
        root.Add(ffmpegOpt);
        root.Add(pythonOpt);
        root.Add(demucsOpt);
        root.Add(azureKeyOpt);
        root.Add(azureEndpointOpt);
        root.Add(openAiEndpointOpt);
        root.Add(openAiDeploymentOpt);
        root.Add(targetLangOpt);
        root.Add(outputFolderOpt);
        root.Add(venvOpt);
        root.Add(femaleOpt);

        root.SetAction(async parseResult =>
        {
            var inputFolder      = parseResult.GetValue(inputFolderOpt)!;
            var processingFolder = parseResult.GetValue(processingFolderOpt)!;
            var dbFile           = parseResult.GetValue(dbOpt)!;

            var venv = parseResult.GetValue(venvOpt);
            var venvPython = venv is not null ? VenvPythonPath(venv.FullName) : null;

            var pipelineOptions = new PipelineOptions
            {
                FfmpegPath                = parseResult.GetValue(ffmpegOpt)!,
                PythonPath                = venvPython ?? parseResult.GetValue(pythonOpt)!,
                DemucsPath                = venvPython ?? parseResult.GetValue(demucsOpt)!,
                AzureSubscriptionKey      = parseResult.GetValue(azureKeyOpt)!,
                AzureEndpointUrl          = parseResult.GetValue(azureEndpointOpt)!,
                AzureOpenAiEndpoint       = parseResult.GetValue(openAiEndpointOpt)!,
                AzureOpenAiDeployment     = parseResult.GetValue(openAiDeploymentOpt)!,
                TranslationTargetLanguages = parseResult.GetValue(targetLangOpt)!
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                OutputFolderPath          = parseResult.GetValue(outputFolderOpt)!,
                UseFemaleVoice            = parseResult.GetValue(femaleOpt),
            };

            var services = BuildServices(dbFile.FullName, pipelineOptions);
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;

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

            foreach (var job in pending)
            {
                log.LogInformation(
                    "=== Job {Id} | {File} | state: {State} — attempting to advance to next step ===",
                    job.Id, job.OriginalFileName, job.State);

                await orchestrator.AdvanceAsync(job, pipelineOptions);
            }
        });

        return await root.Parse(args).InvokeAsync();
    }

    /// <summary>
    /// Returns the path to the Python executable inside a virtual environment,
    /// without requiring the venv to be activated first.
    /// </summary>
    private static string VenvPythonPath(string venvRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(venvRoot, "Scripts", "python.exe")
            : Path.Combine(venvRoot, "bin", "python");

    private static ServiceProvider BuildServices(string dbPath, PipelineOptions opts) =>
        new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .AddScoped<IVideoJobRepository, VideoJobRepository>()
            .AddScoped<IJobService, JobService>()
            .AddScoped<IProcessRunner, DefaultProcessRunner>()
            .AddScoped<IFileSystem, PhysicalFileSystem>()
            .AddScoped<IMediaSeparatorService, MediaSeparatorService>()
            .AddScoped<ISrtExtractorService, SrtExtractorService>()
            .AddSingleton<IAzureChatEngine>(_ =>
            {
                var openAiClient = new AzureOpenAIClient(
                    new Uri(opts.AzureOpenAiEndpoint),
                    new DefaultAzureCredential());
                return new AzureChatEngine(openAiClient.GetChatClient(opts.AzureOpenAiDeployment));
            })
            .AddScoped<ISrtTranslatorService, SrtTranslatorService>()
            .AddSingleton<IAzureSpeechEngine, AzureSpeechEngine>()
            .AddScoped<ISrtToAzureTtsService, SrtToAzureTtsService>()
            .AddScoped<IVoiceRemoverService, VoiceRemoverService>()
            .AddScoped<IAudioMixerService, AudioMixerService>()
            .AddScoped<IVideoMuxerService, VideoMuxerService>()
            .AddScoped<IPipelineOrchestrator, PipelineOrchestrator>()  // IVideoJobRepository injected alongside IJobService
            .BuildServiceProvider();
}
