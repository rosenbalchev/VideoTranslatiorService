using Microsoft.EntityFrameworkCore;
using Xunit;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Data.Entities;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.Tests.Data;

public sealed class VideoJobRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly VideoJobRepository _sut;

    public VideoJobRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _sut = new VideoJobRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private static VideoJob NewJob(JobState state = JobState.Queued) => new()
    {
        OriginalFileName = "test.mp4",
        InputFilePath = "/input/test.mp4",
        ProcessingFolderPath = "/proc",
        State = state
    };

    [Fact]
    public async Task CreateAsync_PersistsJobToDatabase()
    {
        var job = NewJob();

        await _sut.CreateAsync(job);

        Assert.NotNull(await _db.VideoJobs.FindAsync(job.Id));
    }

    [Fact]
    public async Task CreateAsync_ReturnsCreatedJob()
    {
        var job = NewJob();

        var result = await _sut.CreateAsync(job);

        Assert.Equal(job.Id, result.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsJobWhenExists()
    {
        var job = NewJob();
        await _sut.CreateAsync(job);

        var result = await _sut.GetByIdAsync(job.Id);

        Assert.NotNull(result);
        Assert.Equal(job.Id, result!.Id);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNullWhenNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByStateAsync_ReturnsOnlyMatchingJobs()
    {
        await _sut.CreateAsync(NewJob(JobState.Queued));
        await _sut.CreateAsync(NewJob(JobState.Queued));
        await _sut.CreateAsync(NewJob(JobState.Failed));

        var queued = await _sut.GetByStateAsync(JobState.Queued);

        Assert.Equal(2, queued.Count);
        Assert.All(queued, j => Assert.Equal(JobState.Queued, j.State));
    }

    [Fact]
    public async Task GetByStateAsync_ReturnsEmptyWhenNoMatches()
    {
        await _sut.CreateAsync(NewJob(JobState.Queued));

        var results = await _sut.GetByStateAsync(JobState.Completed);

        Assert.Empty(results);
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges()
    {
        var job = NewJob();
        await _sut.CreateAsync(job);
        job.State = JobState.Failed;
        job.ErrorMessage = "oops";

        await _sut.UpdateAsync(job);

        var fromDb = await _db.VideoJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.Equal(JobState.Failed, fromDb.State);
        Assert.Equal("oops", fromDb.ErrorMessage);
    }

    [Fact]
    public async Task UpdateAsync_SetsUpdatedAt()
    {
        var job = NewJob();
        var originalTime = job.UpdatedAt;
        await _sut.CreateAsync(job);
        await Task.Delay(15);

        await _sut.UpdateAsync(job);

        var fromDb = await _db.VideoJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        Assert.True(fromDb.UpdatedAt > originalTime);
    }
}
