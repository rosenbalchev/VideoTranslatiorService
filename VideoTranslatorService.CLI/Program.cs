using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Context;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.CLI;

internal sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        var inputFolderOpt = new Option<DirectoryInfo>("--input-folder")
        {
            Required = true,
            Description = "Folder to scan for video files"
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

        var root = new RootCommand(
            "AI Video Translator — picks up video files, separates audio/video, and (future) translates audio via Azure AI Foundry");

        root.Add(inputFolderOpt);
        root.Add(processingFolderOpt);
        root.Add(dbOpt);
        root.Add(ffmpegOpt);

        root.SetAction(async parseResult =>
        {
            var inputFolder      = parseResult.GetValue(inputFolderOpt)!;
            var processingFolder = parseResult.GetValue(processingFolderOpt)!;
            var dbFile           = parseResult.GetValue(dbOpt)!;
            var ffmpegPath       = parseResult.GetValue(ffmpegOpt)!;

            var services = BuildServices(dbFile.FullName);
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CLI");

            await sp.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

            if (!inputFolder.Exists)
            {
                log.LogError("Input folder does not exist: {Path}", inputFolder.FullName);
                return;
            }

            var videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".mkv", ".avi", ".mov", ".webm" };

            var files = inputFolder
                .GetFiles()
                .Where(f => videoExtensions.Contains(f.Extension))
                .ToList();

            if (files.Count == 0)
            {
                log.LogInformation("No video files found in {Path}", inputFolder.FullName);
                return;
            }

            log.LogInformation("Found {Count} video file(s) in {Path}", files.Count, inputFolder.FullName);

            var jobService = sp.GetRequiredService<IJobService>();
            var separator  = sp.GetRequiredService<IMediaSeparatorService>();

            foreach (var file in files)
            {
                log.LogInformation("--- Processing {File} ---", file.Name);

                VideoJob job;
                try
                {
                    job = await jobService.CreateJobFromFileAsync(
                        file.FullName,
                        processingFolder.FullName);

                    log.LogInformation("Job {Id} created — state: {State}", job.Id, job.State);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to queue {File}", file.Name);
                    continue;
                }

                try
                {
                    await jobService.TransitionStateAsync(job.Id, JobState.SeparatingMedia);
                    log.LogInformation("Job {Id} → {State}", job.Id, JobState.SeparatingMedia);

                    job = (await jobService.GetJobAsync(job.Id))!;

                    var (audioPath, silentVideoPath) = await separator.SeparateAsync(job, ffmpegPath);

                    log.LogInformation(
                        "Job {Id} → {State}  |  audio: {Audio}  |  silent video: {Video}",
                        job.Id, JobState.AudioExtracted, audioPath, silentVideoPath);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to separate media for job {Id}", job.Id);
                    await jobService.TransitionStateAsync(job.Id, JobState.Failed, ex.Message);
                }
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
            .BuildServiceProvider();
}
