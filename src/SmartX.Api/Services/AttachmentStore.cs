using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartX.Api.Security;
using SmartX.Core.Contracts;
using SmartX.Core.Deployment;
using SmartX.Core.Devices;

namespace SmartX.Api.Services;

/// <summary>
/// Stores configuration files, deployment photos and hardware logs against a sensor profile.
/// Files are streamed straight from the multipart request through <see cref="ChunkedAesGcm"/> to disk -
/// never fully buffered in memory - and are encrypted at rest with AES-256-GCM.
/// </summary>
public sealed class AttachmentStore
{
    private static readonly Dictionary<string, (AttachmentKind Kind, string ContentType)> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = (AttachmentKind.Config, "application/json"),
        [".yaml"] = (AttachmentKind.Config, "text/yaml"),
        [".yml"] = (AttachmentKind.Config, "text/yaml"),
        [".cfg"] = (AttachmentKind.Config, "text/plain"),
        [".ini"] = (AttachmentKind.Config, "text/plain"),
        [".bin"] = (AttachmentKind.Config, "application/octet-stream"),
        [".jpg"] = (AttachmentKind.Photo, "image/jpeg"),
        [".jpeg"] = (AttachmentKind.Photo, "image/jpeg"),
        [".png"] = (AttachmentKind.Photo, "image/png"),
        [".webp"] = (AttachmentKind.Photo, "image/webp"),
        [".log"] = (AttachmentKind.Log, "text/plain"),
        [".txt"] = (AttachmentKind.Log, "text/plain"),
        [".csv"] = (AttachmentKind.Log, "text/csv"),
    };

    private readonly Dictionary<string, List<AttachmentDto>> _index = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly string _root;
    private readonly byte[] _key;
    private readonly long _maxBytes;
    private readonly ILogger<AttachmentStore> _logger;

    public AttachmentStore(IOptions<AttachmentOptions> options, IWebHostEnvironment env, ILogger<AttachmentStore> logger)
    {
        _logger = logger;
        _maxBytes = options.Value.MaxFileBytes;
        _root = Path.IsPathRooted(options.Value.StoragePath)
            ? options.Value.StoragePath
            : Path.Combine(env.ContentRootPath, options.Value.StoragePath);
        Directory.CreateDirectory(_root);
        _key = LoadOrCreateKey(options.Value.EncryptionKeyBase64);
        LoadIndex();
    }

    public long MaxBytes => _maxBytes;

    public static string AllowedExtensions => string.Join(", ", Allowed.Keys);

    public static bool TryClassify(string fileName, out AttachmentKind kind, out string contentType)
    {
        if (Allowed.TryGetValue(Path.GetExtension(fileName), out var info))
        {
            (kind, contentType) = info;
            return true;
        }
        (kind, contentType) = (AttachmentKind.Log, "");
        return false;
    }

    public IReadOnlyList<AttachmentDto> List(string mac)
    {
        lock (_gate) return _index.TryGetValue(mac, out var l) ? l.OrderByDescending(a => a.UploadedUtc).ToList() : [];
    }

    public int Count(string mac)
    {
        lock (_gate) return _index.TryGetValue(mac, out var l) ? l.Count : 0;
    }

    /// <summary>Encrypts <paramref name="content"/> to disk while computing its SHA-256 in the same pass.</summary>
    public async Task<AttachmentDto> SaveAsync(string mac, string originalName, Stream content, CancellationToken ct)
    {
        var safeName = Path.GetFileName(originalName).Trim();
        if (string.IsNullOrEmpty(safeName)) throw new InvalidDataException("File name is missing.");
        if (!TryClassify(safeName, out var kind, out var contentType))
            throw new InvalidDataException($"File type not allowed. Allowed: {AllowedExtensions}");

        ConfigProfileValidator.Result? configResult = null;
        Stream source = content;
        MemoryStream? buffered = null;

        // Small JSON configs are validated (recursively) before they are accepted.
        if (safeName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            buffered = new MemoryStream();
            await CopyLimitedAsync(content, buffered, 1024 * 1024, ct);
            configResult = ConfigProfileValidator.Validate(Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length));
            buffered.Position = 0;
            source = buffered;
        }

        var id = Guid.NewGuid();
        var dir = Path.Combine(_root, Folder(mac));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id:N}.sxe");

        try
        {
            long size;
            string sha;
            await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                (size, sha) = await ChunkedAesGcm.EncryptAsync(source, fs, _key, _maxBytes, ct);
            }

            var dto = new AttachmentDto(id, mac, safeName, kind, contentType, size, sha, DateTime.UtcNow, "AES-256-GCM (64 KB chunks)", configResult);
            lock (_gate)
            {
                if (!_index.TryGetValue(mac, out var list)) _index[mac] = list = [];
                list.Add(dto);
                SaveIndexLocked();
            }
            return dto;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        finally
        {
            buffered?.Dispose();
        }
    }

    public AttachmentDto? Find(string mac, Guid id)
    {
        lock (_gate) return _index.TryGetValue(mac, out var l) ? l.Find(a => a.Id == id) : null;
    }

    public async Task DecryptToAsync(string mac, Guid id, Stream output, CancellationToken ct)
    {
        var path = Path.Combine(_root, Folder(mac), $"{id:N}.sxe");
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await ChunkedAesGcm.DecryptAsync(fs, output, _key, ct);
    }

    public bool Delete(string mac, Guid id)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(mac, out var l) || l.RemoveAll(a => a.Id == id) == 0) return false;
            SaveIndexLocked();
        }
        TryDelete(Path.Combine(_root, Folder(mac), $"{id:N}.sxe"));
        return true;
    }

    private static string Folder(string mac) => mac.Replace(":", "");

    private static async Task CopyLimitedAsync(Stream src, Stream dst, long limit, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            if (total > limit) throw new ChunkedAesGcm.FileTooLargeException(limit);
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    private byte[] LoadOrCreateKey(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var k = Convert.FromBase64String(configured);
            if (k.Length != 32) throw new InvalidOperationException("Attachments:EncryptionKeyBase64 must decode to 32 bytes.");
            return k;
        }
        var keyFile = Path.Combine(_root, ".attachment.key");
        if (File.Exists(keyFile)) return Convert.FromBase64String(File.ReadAllText(keyFile));
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyFile, Convert.ToBase64String(key));
        _logger.LogWarning("Generated a new attachment encryption key at {Path}. Set Attachments:EncryptionKeyBase64 in production.", keyFile);
        return key;
    }

    private string IndexPath => Path.Combine(_root, "index.json");

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var items = JsonSerializer.Deserialize<List<AttachmentDto>>(File.ReadAllText(IndexPath), SmartXJson.Options) ?? [];
            foreach (var g in items.GroupBy(i => i.Mac)) _index[g.Key] = g.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read attachment index - starting empty.");
        }
    }

    private void SaveIndexLocked()
    {
        var all = _index.Values.SelectMany(v => v).ToList();
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(all, SmartXJson.Options));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
