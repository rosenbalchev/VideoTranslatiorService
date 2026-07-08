using Microsoft.EntityFrameworkCore;
using Xunit;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Data.Repositories;

namespace RB.VideoTranslator.Tests.Data;

public sealed class VoicePaceRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly VoicePaceRepository _sut;

    public VoicePaceRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _sut = new VoicePaceRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAllAsync_ReturnsEmpty_WhenNoSamplesRecorded()
    {
        var result = await _sut.GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task RecordSampleAsync_CreatesRowForNewVoice()
    {
        await _sut.RecordSampleAsync("VoiceA", textLength: 40, rate: 200.0, actualMs: 1000);

        var all = await _sut.GetAllAsync();

        Assert.True(all.ContainsKey("VoiceA"));
        Assert.Equal(1, all["VoiceA"].SampleCount);
        Assert.Equal(40, all["VoiceA"].TotalChars);
        Assert.Equal(2000, all["VoiceA"].TotalNaturalMs); // 1000ms at rate=200% → 2000ms at rate=100%
        Assert.Equal(200.0, all["VoiceA"].LastRateUsed);
    }

    [Fact]
    public async Task RecordSampleAsync_AccumulatesAcrossCallsForSameVoice()
    {
        await _sut.RecordSampleAsync("VoiceA", 40, 200.0, 1000);
        await _sut.RecordSampleAsync("VoiceA", 40, 200.0, 1000);

        var all = await _sut.GetAllAsync();

        Assert.Equal(2, all["VoiceA"].SampleCount);
        Assert.Equal(80, all["VoiceA"].TotalChars);
        Assert.Equal(4000, all["VoiceA"].TotalNaturalMs);
    }

    [Fact]
    public async Task RecordSampleAsync_TracksVoicesSeparately()
    {
        await _sut.RecordSampleAsync("VoiceA", 40, 200.0, 1000);
        await _sut.RecordSampleAsync("VoiceB", 60, 150.0, 800);

        var all = await _sut.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(1, all["VoiceA"].SampleCount);
        Assert.Equal(1, all["VoiceB"].SampleCount);
    }

    [Fact]
    public async Task RecordSampleAsync_UpdatesLastRateUsedToMostRecentSample()
    {
        await _sut.RecordSampleAsync("VoiceA", 40, 120.0, 1000);
        await _sut.RecordSampleAsync("VoiceA", 40, 163.0, 900);

        var all = await _sut.GetAllAsync();

        Assert.Equal(163.0, all["VoiceA"].LastRateUsed);
    }

    [Theory]
    [InlineData(0, 100.0, 1000)]
    [InlineData(40, 0.0, 1000)]
    [InlineData(40, 100.0, 0)]
    public async Task RecordSampleAsync_IgnoresInvalidSamples(int textLength, double rate, int actualMs)
    {
        await _sut.RecordSampleAsync("VoiceA", textLength, rate, actualMs);

        var all = await _sut.GetAllAsync();

        Assert.False(all.ContainsKey("VoiceA"));
    }
}
