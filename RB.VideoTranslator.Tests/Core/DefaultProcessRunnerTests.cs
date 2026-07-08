using Xunit;
using RB.VideoTranslator.Core.Services;

namespace RB.VideoTranslator.Tests.Core;

public sealed class DefaultProcessRunnerTests
{
    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("/usr/bin/ffmpeg")]
    [InlineData("/home/user/videos/rb.video.translator/bin/pythonw")]
    public void BuildLinuxCudaLibraryPath_ReturnsNull_ForNonPythonExecutables(string executable)
    {
        Assert.Null(DefaultProcessRunner.BuildLinuxCudaLibraryPath(executable));
    }

    [Fact]
    public void BuildLinuxCudaLibraryPath_ReturnsNull_WhenVenvLibDirMissing()
    {
        var venvRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(venvRoot, "bin"));
        try
        {
            var executable = Path.Combine(venvRoot, "bin", "python");
            Assert.Null(DefaultProcessRunner.BuildLinuxCudaLibraryPath(executable));
        }
        finally
        {
            Directory.Delete(venvRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildLinuxCudaLibraryPath_ReturnsNull_WhenNoNvidiaPackagesInstalled()
    {
        var venvRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(venvRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(venvRoot, "lib", "python3.12", "site-packages"));
        try
        {
            var executable = Path.Combine(venvRoot, "bin", "python");
            Assert.Null(DefaultProcessRunner.BuildLinuxCudaLibraryPath(executable));
        }
        finally
        {
            Directory.Delete(venvRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildLinuxCudaLibraryPath_JoinsLibDirsOfEveryInstalledNvidiaPackage()
    {
        // Mirrors the real layout: <venv>/lib/python3.XX/site-packages/nvidia/<pkg>/lib/*.so
        var venvRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var nvidiaDir = Path.Combine(venvRoot, "lib", "python3.12", "site-packages", "nvidia");
        var cudnnLib = Path.Combine(nvidiaDir, "cudnn", "lib");
        var cublasLib = Path.Combine(nvidiaDir, "cublas", "lib");
        Directory.CreateDirectory(Path.Combine(venvRoot, "bin"));
        Directory.CreateDirectory(cudnnLib);
        Directory.CreateDirectory(cublasLib);
        // A package dir with no "lib" subfolder must be skipped, not throw.
        Directory.CreateDirectory(Path.Combine(nvidiaDir, "nvtx"));

        try
        {
            var executable = Path.Combine(venvRoot, "bin", "python");
            var result = DefaultProcessRunner.BuildLinuxCudaLibraryPath(executable);

            Assert.NotNull(result);
            var parts = result!.Split(Path.PathSeparator);
            Assert.Contains(cudnnLib, parts);
            Assert.Contains(cublasLib, parts);
            Assert.Equal(2, parts.Length);
        }
        finally
        {
            Directory.Delete(venvRoot, recursive: true);
        }
    }
}
