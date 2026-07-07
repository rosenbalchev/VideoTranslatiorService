using System.Text;
using RB.VideoTranslator.Domain.Interfaces;

namespace RB.VideoTranslator.Core.Services;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct = default) =>
        File.ReadAllTextAsync(path, Encoding.UTF8, ct);

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct = default) =>
        File.WriteAllTextAsync(path, content, Encoding.UTF8, ct);

    public Stream Create(string path) => File.Create(path);

    public Stream OpenRead(string path) => File.OpenRead(path);
}
