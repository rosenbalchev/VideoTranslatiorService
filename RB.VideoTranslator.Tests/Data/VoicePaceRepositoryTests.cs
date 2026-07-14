using Microsoft.EntityFrameworkCore;
using Xunit;
using RB.VideoTranslator.Data.Context;
using RB.VideoTranslator.Data.Repositories;
using RB.VideoTranslator.Domain.Models;

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

    private static readonly TextFeatures SampleFeatures = new(CharCount: 40, WordCount: 6, SentenceCount: 1, CommaCount: 2);

    [Fact]
    public async Task GetAllSamplesAsync_ReturnsEmpty_WhenNoSamplesRecorded()
    {
        var result = await _sut.GetAllSamplesAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task RecordSampleAsync_AppendsRowForVoice()
    {
        await _sut.RecordSampleAsync("VoiceA", SampleFeatures, rate: 200.0, expectedMs: 500, actualMs: 1000);

        var all = await _sut.GetAllSamplesAsync();

        var sample = Assert.Single(all);
        Assert.Equal("VoiceA", sample.Voice);
        Assert.Equal(40, sample.CharCount);
        Assert.Equal(6, sample.WordCount);
        Assert.Equal(1, sample.SentenceCount);
        Assert.Equal(2, sample.CommaCount);
        Assert.Equal(200.0, sample.RateUsed);
        Assert.Equal(500, sample.ExpectedMs);
        Assert.Equal(1000, sample.ActualMs);
        Assert.Equal(2000, sample.NaturalMs); // 1000ms at rate=200% → 2000ms at rate=100%
    }

    [Fact]
    public async Task RecordSampleAsync_AppendsSeparateRowPerCall_DoesNotAccumulate()
    {
        await _sut.RecordSampleAsync("VoiceA", SampleFeatures, 200.0, 500, 1000);
        await _sut.RecordSampleAsync("VoiceA", SampleFeatures, 200.0, 500, 1000);

        var all = await _sut.GetAllSamplesAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, s => Assert.Equal("VoiceA", s.Voice));
    }

    [Fact]
    public async Task RecordSampleAsync_TracksVoicesSeparately()
    {
        await _sut.RecordSampleAsync("VoiceA", SampleFeatures, 200.0, 500, 1000);
        await _sut.RecordSampleAsync("VoiceB", new TextFeatures(60, 8, 2, 3), 150.0, 700, 800);

        var all = await _sut.GetAllSamplesAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Voice == "VoiceA");
        Assert.Contains(all, s => s.Voice == "VoiceB");
    }

    [Theory]
    [InlineData(0, 100.0, 1000)]
    [InlineData(40, 0.0, 1000)]
    [InlineData(40, 100.0, 0)]
    public async Task RecordSampleAsync_IgnoresInvalidSamples(int charCount, double rate, int actualMs)
    {
        var features = SampleFeatures with { CharCount = charCount };
        await _sut.RecordSampleAsync("VoiceA", features, rate, expectedMs: 500, actualMs: actualMs);

        var all = await _sut.GetAllSamplesAsync();

        Assert.Empty(all);
    }
}
