namespace Loomi.Services;
public interface IImageStorage
{
    Task<string> SaveAsync(Guid project, Guid generation, byte[] bytes, CancellationToken ct);
    string Resolve(string path);
    void DeleteProject(Guid id);
}
public class ImageStorage(IConfiguration config) : IImageStorage
{
    private readonly string root = Path.GetFullPath(Path.Combine(config["Storage:Root"] ?? "Storage", "images"));
    public async Task<string> SaveAsync(Guid project, Guid generation, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length < 12 || bytes.Length > 40 * 1024 * 1024) throw new InvalidOperationException("InvalidImage");
        var ext = bytes.AsSpan(0, 8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) ? "png"
            : bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? "jpg"
            : System.Text.Encoding.ASCII.GetString(bytes,0,4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes,8,4) == "WEBP" ? "webp" : throw new InvalidOperationException("InvalidImage");
        var relative = $"projects/{project}/{generation}/image.{ext}";
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
    public void DeleteProject(Guid id)
    {
        var folder = Resolve($"projects/{id}");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}
