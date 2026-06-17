using NSubstitute;
using Xunit;
using VideoTranslatorService.BLL.Services;
using VideoTranslatorService.Data.Entities;
using VideoTranslatorService.Data.Repositories;

namespace VideoTranslatorService.Tests.BLL;

public sealed class JobServiceTests : IDisposable
{
    private readonly IVideoJobRepository _repo;
    private readonly JobService _sut;
    private readonly string _tempDir;

    public JobServiceTests()
    {
        _repo = Substitute.For<IVideoJobRepository>();
        _sut = new JobService(_repo);
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<string> CreateTempFileAsync(string name = "video.mp4")
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task CreateJobFromFileAsync_MovesFileToProcessingFolder()
    {
        var source = await CreateTempFileAsync();
        var processingDir = Path.Combine(_tempDir, "processing");
        _repo.CreateAsync(Arg.Any<VideoJob>()).Returns(c => c.Arg<VideoJob>());

        await _sut.CreateJobFromFileAsync(source, processingDir);

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(processingDir, "video.mp4")));
    }

    [Fact]
    public async Task CreateJobFromFileAsync_CreatesJobWithQueuedState()
    {
        var source = await CreateTempFileAsync();
        VideoJob? captured = null;
        _repo.CreateAsync(Arg.Do<VideoJob>(j => captured = j)).Returns(c => c.Arg<VideoJob>());

        await _sut.CreateJobFromFileAsync(source, Path.Combine(_tempDir, "processing"));

        Assert.NotNull(captured);
        Assert.Equal(JobState.Queued, captured!.State);
        Assert.Equal("video.mp4", captured.OriginalFileName);
    }

    [Fact]
    public async Task CreateJobFromFileAsync_SetsProcessingVideoPath()
    {
        var source = await CreateTempFileAsync();
        var processingDir = Path.Combine(_tempDir, "processing");
        VideoJob? captured = null;
        _repo.CreateAsync(Arg.Do<VideoJob>(j => captured = j)).Returns(c => c.Arg<VideoJob>());

        await _sut.CreateJobFromFileAsync(source, processingDir);

        Assert.Equal(Path.Combine(processingDir, "video.mp4"), captured!.ProcessingVideoPath);
    }

    [Fact]
    public async Task TransitionStateAsync_UpdatesJobState()
    {
        var job = new VideoJob
        {
            OriginalFileName = "v.mp4",
            InputFilePath = "/input/v.mp4",
            ProcessingFolderPath = "/proc",
            State = JobState.Queued
        };
        _repo.GetByIdAsync(job.Id).Returns(job);

        await _sut.TransitionStateAsync(job.Id, JobState.SeparatingMedia);

        await _repo.Received(1).UpdateAsync(Arg.Is<VideoJob>(j => j.State == JobState.SeparatingMedia));
    }

    [Fact]
    public async Task TransitionStateAsync_SetsErrorMessage()
    {
        var job = new VideoJob
        {
            OriginalFileName = "v.mp4",
            InputFilePath = "/input/v.mp4",
            ProcessingFolderPath = "/proc"
        };
        _repo.GetByIdAsync(job.Id).Returns(job);

        await _sut.TransitionStateAsync(job.Id, JobState.Failed, "something broke");

        await _repo.Received(1).UpdateAsync(Arg.Is<VideoJob>(j => j.ErrorMessage == "something broke"));
    }

    [Fact]
    public async Task TransitionStateAsync_ThrowsWhenJobNotFound()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>()).Returns((VideoJob?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.TransitionStateAsync(Guid.NewGuid(), JobState.Failed));
    }

    [Fact]
    public async Task GetJobAsync_DelegatesToRepository()
    {
        var job = new VideoJob
        {
            OriginalFileName = "v.mp4",
            InputFilePath = "/input/v.mp4",
            ProcessingFolderPath = "/proc"
        };
        _repo.GetByIdAsync(job.Id).Returns(job);

        var result = await _sut.GetJobAsync(job.Id);

        Assert.Same(job, result);
    }

    [Fact]
    public async Task GetJobAsync_ReturnsNullWhenNotFound()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>()).Returns((VideoJob?)null);

        var result = await _sut.GetJobAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}
