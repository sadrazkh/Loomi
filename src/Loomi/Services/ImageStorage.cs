namespace Loomi.Services;
public interface IImageStorage
{
    Task<string> SaveAsync(Guid project, Guid generation, byte[] bytes, CancellationToken ct);
    /// <summary>Keeps a user's reference image under uploads/{user}/, judged by its bytes like everything else here.</summary>
    Task<string> SaveUploadAsync(Guid user, Guid upload, byte[] bytes, CancellationToken ct);
    string Resolve(string path);
    void Delete(string path);
    void DeleteProject(Guid id);
}
public class ImageStorage(IConfiguration config) : IImageStorage
{
    public const int MaxBytes = 40 * 1024 * 1024;
    private readonly string root = Path.GetFullPath(Path.Combine(config["Storage:Root"] ?? "Storage", "images"));
    /// <summary>The extension the bytes earn, or null: png, jpg or webp by signature, never by the name a file arrived with.</summary>
    public static string? ExtensionFor(ReadOnlySpan<byte> bytes) =>
        bytes.Length < 12 ? null
        : bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]) ? "png"
        : bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "jpg"
        : bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? "webp"
        : null;
    public static string ContentTypeFor(string path) => Path.GetExtension(path) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
    public Task<string> SaveAsync(Guid project, Guid generation, byte[] bytes, CancellationToken ct) => WriteAsync($"projects/{project}/{generation}/image", bytes, ct);
    public Task<string> SaveUploadAsync(Guid user, Guid upload, byte[] bytes, CancellationToken ct) => WriteAsync($"uploads/{user}/{upload}", bytes, ct);
    private async Task<string> WriteAsync(string stem, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("InvalidImage");
        var relative = $"{stem}.{ExtensionFor(bytes) ?? throw new InvalidOperationException("InvalidImage")}";
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full + ".tmp", bytes, ct);
        File.Move(full + ".tmp", full, true);
        return relative;
    }
    public string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("InvalidPath");
        return full;
    }
    public void Delete(string path)
    {
        var full = Resolve(path);
        if (File.Exists(full)) File.Delete(full);
    }
    public void DeleteProject(Guid id)
    {
        var folder = Resolve($"projects/{id}");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
