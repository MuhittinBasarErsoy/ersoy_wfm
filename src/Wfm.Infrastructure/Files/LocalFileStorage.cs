using Wfm.Application;

namespace Wfm.Infrastructure.Files;

public class LocalFileStorage(string rootPath) : IFileStorage
{
    public async Task<string> SaveAsync(Stream content, string relativePath, CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return relativePath;
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        return Task.FromResult<Stream?>(File.Exists(full) ? File.OpenRead(full) : null);
    }

    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        var full = Resolve(relativePath);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    private string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Geçersiz dosya yolu.");
        return full;
    }
}
