using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.BLL.Services;

public sealed class JobService : IJobService
{
    private readonly IVideoJobRepository _repo;

    public JobService(IVideoJobRepository repo) => _repo = repo;

    public async Task<VideoJob> CreateJobFromFileAsync(
        string inputFilePath,
        string processingFolder,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(inputFilePath);

        // Each job gets its own subfolder so files from different jobs never collide.
        var job = new VideoJob
        {
            OriginalFileName = fileName,
            InputFilePath = inputFilePath,
            ProcessingFolderPath = processingFolder, // replaced below once the ID is known
            State = JobState.Queued
        };

        var jobFolder = Path.Combine(processingFolder, job.Id.ToString("N")[..8]);
        Directory.CreateDirectory(jobFolder);

        var destPath = Path.Combine(jobFolder, fileName);
        File.Move(inputFilePath, destPath, overwrite: false);

        job.ProcessingFolderPath = jobFolder;
        job.ProcessingVideoPath = destPath;

        return await _repo.CreateAsync(job, ct);
    }

    public async Task TransitionStateAsync(
        Guid jobId,
        JobState newState,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        var job = await _repo.GetByIdAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");

        job.State = newState;
        job.ErrorMessage = errorMessage;
        await _repo.UpdateAsync(job, ct);
    }

    public Task<VideoJob?> GetJobAsync(Guid jobId, CancellationToken ct = default) =>
        _repo.GetByIdAsync(jobId, ct);

    public Task<IReadOnlyList<VideoJob>> GetResumableJobsAsync(CancellationToken ct = default) =>
        _repo.GetResumableAsync(ct);
}
