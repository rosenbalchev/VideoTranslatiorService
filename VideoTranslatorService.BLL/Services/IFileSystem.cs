namespace VideoTranslatorService.BLL.Services;

public interface IFileSystem
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    Task<string> ReadAllTextAsync(string path, CancellationToken ct = default);
    Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);
    Stream Create(string path);
}
