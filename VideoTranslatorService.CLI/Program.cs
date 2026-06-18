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

        var root = new RootCommand(
            "AI Video Translator — picks up video files and advances them through the translation pipeline");

        root.Add(inputFolderOpt);
        root.Add(processingFolderOpt);
        root.Add(dbOpt);
        root.Add(ffmpegOpt);
        root.Add(pythonOpt);

        root.SetAction(async parseResult =>
        {
            var inputFolder      = parseResult.GetValue(inputFolderOpt)!;
            var processingFolder = parseResult.GetValue(processingFolderOpt)!;
            var dbFile           = parseResult.GetValue(dbOpt)!;
            var pipelineOptions  = new PipelineOptions
            {
                FfmpegPath = parseResult.GetValue(ffmpegOpt)!,
                PythonPath = parseResult.GetValue(pythonOpt)!
            };

            var services = BuildServices(dbFile.FullName);
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

    private static ServiceProvider BuildServices(string dbPath) =>
        new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
            .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
            .AddScoped<IVideoJobRepository, VideoJobRepository>()
            .AddScoped<IJobService, JobService>()
            .AddScoped<IProcessRunner, DefaultProcessRunner>()
            .AddScoped<IMediaSeparatorService, MediaSeparatorService>()
            .AddScoped<ISrtExtractorService, SrtExtractorService>()
            .AddScoped<IVoiceRemoverService, VoiceRemoverService>()
            .AddScoped<ISrtTranslatorService, SrtTranslatorService>()
            .AddScoped<IVoiceSynthesiserService, VoiceSynthesiserService>()
            .AddScoped<IAudioMixerService, AudioMixerService>()
            .AddScoped<IVideoMuxerService, VideoMuxerService>()
            .AddScoped<IPipelineOrchestrator, PipelineOrchestrator>()
            .BuildServiceProvider();
}
