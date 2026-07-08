using Xunit;
using RB.VideoTranslator.Core.Services;

namespace RB.VideoTranslator.Tests.Core;

public sealed class SpeakerVoiceAssignerTests
{
    [Fact]
    public void Assign_GivesEachSpeakerADistinctGenderMatchedVoice()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "Female"), ("Speaker2", "Male") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1", "Male2"], ["Female1", "Female2"], defaultToFemale: false);

        Assert.Equal("Female1", result["Speaker1"]);
        Assert.Equal("Male1", result["Speaker2"]);
    }

    [Fact]
    public void Assign_MultipleSameGenderSpeakersGetDifferentVoices()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "Male"), ("Speaker2", "Male"), ("Speaker3", "Male") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["MaleA", "MaleB", "MaleC"], ["FemaleA"], defaultToFemale: false);

        Assert.Equal("MaleA", result["Speaker1"]);
        Assert.Equal("MaleB", result["Speaker2"]);
        Assert.Equal("MaleC", result["Speaker3"]);
    }

    [Fact]
    public void Assign_CyclesBackWhenMoreSameGenderSpeakersThanVoices()
    {
        // 3 male speakers, only 2 male voices available — must cycle, never cross genders.
        var speakers = new List<(string, string)> { ("Speaker1", "Male"), ("Speaker2", "Male"), ("Speaker3", "Male") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["MaleA", "MaleB"], ["FemaleA", "FemaleB"], defaultToFemale: false);

        Assert.Equal("MaleA", result["Speaker1"]);
        Assert.Equal("MaleB", result["Speaker2"]);
        Assert.Equal("MaleA", result["Speaker3"]); // cycled back — reused, not crossed to female
    }

    [Fact]
    public void Assign_GenderMatchIsCaseInsensitive()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "female") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1"], ["Female1"], defaultToFemale: false);

        Assert.Equal("Female1", result["Speaker1"]);
    }

    [Fact]
    public void Assign_UnknownGenderDefaultsToMaleWhenDefaultToFemaleIsFalse()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "unknown") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1"], ["Female1"], defaultToFemale: false);

        Assert.Equal("Male1", result["Speaker1"]);
    }

    [Fact]
    public void Assign_UnknownGenderDefaultsToFemaleWhenDefaultToFemaleIsTrue()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "unknown") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1"], ["Female1"], defaultToFemale: true);

        Assert.Equal("Female1", result["Speaker1"]);
    }

    [Fact]
    public void Assign_ExplicitMaleGenderIsNotOverriddenByDefaultToFemale()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "Male") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1"], ["Female1"], defaultToFemale: true);

        Assert.Equal("Male1", result["Speaker1"]);
    }

    [Fact]
    public void Assign_FallsBackToMalePoolWhenFemalePoolIsEmpty()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "Female") };
        var result = SpeakerVoiceAssigner.Assign(speakers, ["Male1"], [], defaultToFemale: false);

        Assert.Equal("Male1", result["Speaker1"]);
    }

    [Fact]
    public void Assign_LeavesSpeakerUnassignedWhenBothPoolsEmpty()
    {
        var speakers = new List<(string, string)> { ("Speaker1", "Female") };
        var result = SpeakerVoiceAssigner.Assign(speakers, [], [], defaultToFemale: false);

        Assert.Empty(result);
    }

    [Fact]
    public void Assign_ReturnsEmptyForNoSpeakers()
    {
        var result = SpeakerVoiceAssigner.Assign([], ["Male1"], ["Female1"], defaultToFemale: false);
        Assert.Empty(result);
    }
}
