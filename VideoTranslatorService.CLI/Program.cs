using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Context;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

var inputFolderOpt = new Option<DirectoryInfo>(
    "--input-folder",
    "Folder to scan for video files")
{ IsRequired = true };

var processingFolderOpt = new Option<DirectoryInfo>(
    "--processing-folder",
    "Working folder where jobs are staged during processing")
{ IsRequired = true };

var dbOpt = new Option<FileInfo>(
    "--db",
    () => new FileInfo("videotranslator.db"),
    "Path to the SQLite database file");

var ffmpegOpt = new Option<string>(
    "--ffmpeg",
    () => "ffmpeg",
    "Path to the ffmpeg executable (must be on PATH or supply full path)");

var root = new RootCommand(
    "AI Video Translator — picks up video files, separates audio/video, and (future) translates audio via Azure AI Foundry");

root.AddOption(inputFolderOpt);
root.AddOption(processingFolderOpt);
root.AddOption(dbOpt);
root.AddOption(ffmpegOpt);

root.SetHandler(async (inputFolder, processingFolder, dbFile, ffmpegPath) =>
{
    var services = BuildServices(dbFile.FullName);
    await using var scope = services.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CLI");

    // Ensure schema exists
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
    var separator = sp.GetRequiredService<IMediaSeparatorService>();

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

            // Reload so separator works with the latest persisted state
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
}, inputFolderOpt, processingFolderOpt, dbOpt, ffmpegOpt);

return await root.InvokeAsync(args);

static ServiceProvider BuildServices(string dbPath) =>
    new ServiceCollection()
        .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information))
        .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"))
        .AddScoped<IVideoJobRepository, VideoJobRepository>()
        .AddScoped<IJobService, JobService>()
        .AddScoped<IMediaSeparatorService, MediaSeparatorService>()
        .BuildServiceProvider();
