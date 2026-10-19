using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Api.Services;

public sealed record StoredAttachment(AttachmentDto Info, string PhysicalPath);

/// <summary>
/// Streams uploaded configuration files, deployment photos and hardware logs to disk
/// with a pooled buffer while hashing them (SHA-256), then records them in a JSON index.
/// </summary>
public sealed class AttachmentStorage
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    private const int BufferSize = 81_920;

    private static readonly Dictionary<string, string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".xml"] = "application/xml",
        [".cfg"] = "text/plain",
        [".ini"] = "text/plain",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
        [".bin"] = "application/octet-stream"
    };

    private static readonly HashSet<string> Kinds = new(AttachmentKinds.All, StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ConcurrentDictionary<Guid, StoredAttachment> _byId = new();
    private readonly object _indexGate = new();
    private readonly string _root;
    private readonly string _indexPath;
    private readonly ILogger<AttachmentStorage> _logger;

    public AttachmentStorage(IWebHostEnvironment environment, IConfiguration configuration, ILogger<AttachmentStorage> logger)
    {
        _logger = logger;
        string configured = configuration["Storage:UploadsPath"] ?? "App_Data/uploads";
        _root = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        Directory.CreateDirectory(_root);
        _indexPath = Path.Combine(_root, "index.json");
        LoadIndex();
    }

    public static IReadOnlyCollection<string> AllowedExtensionList => AllowedExtensions.Keys;

    public int CountFor(string mac) =>
        _byId.Values.Count(a => string.Equals(a.Info.DeviceMac, mac, StringComparison.OrdinalIgnoreCase));

    public AttachmentDto[] ListFor(string mac) =>
        _byId.Values
            .Where(a => string.Equals(a.Info.DeviceMac, mac, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Info)
            .OrderByDescending(a => a.UploadedAt)
            .ToArray();

    public bool TryGet(Guid id, [NotNullWhen(true)] out StoredAttachment? attachment) =>
        _byId.TryGetValue(id, out attachment);

    public async Task<GatewayOutcome<AttachmentDto>> SaveAsync(
        SensorDevice device,
        IFormFile? file,
        string? kind,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return GatewayOutcome.Invalid<AttachmentDto>("file", "Choose a non-empty file to upload.");
        }

        if (file.Length > MaxFileBytes)
        {
            return GatewayOutcome.Invalid<AttachmentDto>("file", $"Files are limited to {MaxFileBytes / (1024 * 1024)} MB.");
        }

        string originalName = Path.GetFileName(file.FileName ?? string.Empty);
        string extension = Path.GetExtension(originalName);

        if (!AllowedExtensions.TryGetValue(extension, out string? contentType))
        {
            return GatewayOutcome.Invalid<AttachmentDto>(
                "file",
                $"'{extension}' files are not accepted. Allowed: {string.Join(", ", AllowedExtensions.Keys)}.");
        }

        string requestedKind = string.IsNullOrWhiteSpace(kind) ? GuessKind(extension) : kind.Trim();
        if (!Kinds.TryGetValue(requestedKind, out string? canonicalKind))
        {
            return GatewayOutcome.Invalid<AttachmentDto>("kind", $"Kind must be one of: {string.Join(", ", AttachmentKinds.All)}.");
        }

        Guid id = Guid.NewGuid();
        string folder = Path.Combine(_root, MacAddress.ToSlug(device.MacAddress));
        Directory.CreateDirectory(folder);
        string physicalPath = Path.Combine(folder, id.ToString("N") + extension.ToLowerInvariant());

        long written = 0;
        string sha256;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream input = file.OpenReadStream())
            await using (var output = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    written += read;
                    if (written > MaxFileBytes)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            TryDelete(physicalPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (written > MaxFileBytes)
        {
            TryDelete(physicalPath);
            return GatewayOutcome.Invalid<AttachmentDto>("file", "The file is larger than the upload limit.");
        }

        var info = new AttachmentDto(
            id,
            device.MacAddress,
            SafeDisplayName(originalName),
            contentType,
            written,
            canonicalKind,
            sha256,
            DateTimeOffset.UtcNow);

        _byId[id] = new StoredAttachment(info, physicalPath);
        SaveIndex();
        _logger.LogInformation("Stored {Kind} '{File}' ({Bytes} bytes) for {Mac}", canonicalKind, info.FileName, written, device.MacAddress);

        return GatewayOutcome.Ok(info);
    }

    private static string GuessKind(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" => AttachmentKinds.DeploymentPhoto,
        ".log" or ".txt" or ".csv" => AttachmentKinds.HardwareLog,
        ".json" or ".yaml" or ".yml" or ".xml" or ".cfg" or ".ini" => AttachmentKinds.Configuration,
        _ => AttachmentKinds.Other
    };

    private static string SafeDisplayName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (!char.IsControl(c) && c != '/' && c != '\\')
            {
                builder.Append(c);
            }
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "attachment";
        }

        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort clean-up.
        }
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return;
            }

            List<StoredAttachment>? items = JsonSerializer.Deserialize<List<StoredAttachment>>(File.ReadAllText(_indexPath), IndexJson);
            foreach (StoredAttachment item in items ?? new List<StoredAttachment>())
            {
                if (File.Exists(item.PhysicalPath))
                {
                    _byId[item.Info.Id] = item;
                }
            }

            _logger.LogInformation("Loaded {Count} attachment records from {Path}", _byId.Count, _indexPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the attachment index at {Path}; starting empty.", _indexPath);
        }
    }

    private void SaveIndex()
    {
        lock (_indexGate)
        {
            try
            {
                string json = JsonSerializer.Serialize(_byId.Values.ToList(), IndexJson);
                File.WriteAllText(_indexPath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write the attachment index.");
            }
        }
    }
}
