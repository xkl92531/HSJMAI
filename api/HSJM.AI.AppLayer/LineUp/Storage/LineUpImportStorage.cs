using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.LineUp.Storage;

public sealed class LineUpImportStorage(IConfiguration configuration) : ISelfScopedAutoInject
{
    public const long MaximumSourceBytes = 25L * 1024 * 1024;

    private readonly string root = ResolveRoot(configuration);

    public async Task<LineUpStoredImportFile> SaveAsync(
        long batchId,
        string sourceFileName,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var safeName = SafeLeafName(sourceFileName);
        var extension = ValidateExtension(safeName);
        if (batchId <= 0)
        {
            throw ImportError("InvalidBatch", safeName, "The import batch identity is invalid.");
        }

        var storageKey = $"{batchId}/source{extension}";
        var targetPath = ResolveStorageKey(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var created = false;

        try
        {
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumSourceBytes)
                {
                    throw ImportError("SourceTooLarge", safeName, "The uploaded source exceeds the 25 MiB limit.");
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await target.FlushAsync(cancellationToken);
            return new(
                storageKey,
                safeName,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                total);
        }
        catch (LineUpImportException)
        {
            DeletePartial(targetPath, created);
            throw;
        }
        catch (IOException) when (!created && File.Exists(targetPath))
        {
            throw ImportError("SourceAlreadyExists", safeName, "This import batch already has a stored source.");
        }
        catch (OperationCanceledException)
        {
            DeletePartial(targetPath, created);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DeletePartial(targetPath, created);
            throw ImportError("StorageWriteFailed", safeName, "The uploaded source could not be stored.");
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveStorageKey(storageKey);
        if (!File.Exists(path))
        {
            throw ImportError("StoredSourceMissing", "source", "The stored import source is unavailable.");
        }

        Stream result = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveStorageKey(storageKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolveStorageKey(string storageKey)
    {
        var normalized = storageKey?.Replace('\\', '/').Trim() ?? string.Empty;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var extension = segments.Length == 2 ? Path.GetExtension(segments[1]) : string.Empty;
        if (segments.Length != 2 ||
            !long.TryParse(segments[0], out var batchId) || batchId <= 0 ||
            !AllowedExtension(extension) ||
            !string.Equals(segments[1], $"source{extension.ToLowerInvariant()}", StringComparison.Ordinal) ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw ImportError("InvalidStorageKey", "source", "The stored import source key is invalid.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, segments[0], segments[1]));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw ImportError("InvalidStorageKey", "source", "The stored import source key is invalid.");
        }

        return fullPath;
    }

    private static string SafeLeafName(string sourceFileName)
    {
        var normalized = sourceFileName?.Replace('\\', '/') ?? string.Empty;
        var leaf = Path.GetFileName(normalized).Trim();
        if (string.IsNullOrWhiteSpace(leaf))
        {
            throw ImportError("InvalidSourceName", "source", "The uploaded source filename is invalid.");
        }

        return leaf;
    }

    private static string ValidateExtension(string safeName)
    {
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (!AllowedExtension(extension))
        {
            throw ImportError("UnsupportedExtension", safeName, "Only XML, ML, or ZIP sources are accepted.");
        }

        return extension;
    }

    private static bool AllowedExtension(string extension) =>
        string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".ml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRoot(IConfiguration configuration)
    {
        var configured = configuration["WebConfig:LineUp:ImportRoot"];
        var value = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "App_Data", "lineup-imports")
            : configured.Trim();
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void DeletePartial(string path, bool created)
    {
        if (created && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static LineUpImportException ImportError(string code, string fileName, string summary) =>
        new(code, fileName, summary);
}

public sealed record LineUpStoredImportFile(
    string StorageKey,
    string SourceFileName,
    string SourceFileHash,
    long Length);
