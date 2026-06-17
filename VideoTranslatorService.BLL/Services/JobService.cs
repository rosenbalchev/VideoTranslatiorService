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
        var destPath = Path.Combine(processingFolder, fileName);

        Directory.CreateDirectory(processingFolder);
        File.Move(inputFilePath, destPath, overwrite: false);

        var job = new VideoJob
        {
            OriginalFileName = fileName,
            InputFilePath = inputFilePath,
            ProcessingFolderPath = processingFolder,
            ProcessingVideoPath = destPath,
            State = JobState.Queued
        };

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
}
