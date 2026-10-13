using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.AppLayer.LineUp.Parsing;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.LineUp.ImageMarks;

public sealed class LineUpImageStorage : ILineUpImageStorage, IAutoInject, ISelfScopedAutoInject
{
    public const long DefaultMaxBytes = 25L * 1024 * 1024;
    public const long DefaultMaxPixels = 100_000_000;
    private const int HeaderLimit = 1024 * 1024;
    private readonly string root;
    private readonly string stagingRoot;
    private readonly long maxBytes;
    private readonly long maxPixels;

    public LineUpImageStorage(IConfiguration configuration)
        : this(configuration, DefaultMaxBytes, DefaultMaxPixels)
    {
    }

    internal LineUpImageStorage(IConfiguration configuration, long maxBytes, long maxPixels)
    {
        if (maxBytes <= 0 || maxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var configured = configuration["WebConfig:LineUp:StorageRoot"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Path.Combine(AppContext.BaseDirectory, "App_Data", "LineUp");
        }

        root = Path.GetFullPath(configured);
        stagingRoot = Path.Combine(root, ".staging");
        this.maxBytes = maxBytes;
        this.maxPixels = maxPixels;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(stagingRoot);
    }

    public async Task<LineUpStoredImage> StoreAsync(
        Stream source,
        LineUpImageDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (descriptor.ImageId <= 0)
        {
            throw Error("InvalidImageIdentity", "image", "The image identity is invalid.");
        }

        var fileName = descriptor.OriginalFileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName != Path.GetFileName(fileName) ||
            fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw Error("InvalidImageFileName", "image", "The image file name is invalid.");
        }

        var requestedExtension = Path.GetExtension(fileName).ToLowerInvariant();
        if (requestedExtension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
        {
            throw Error("UnsupportedImageType", fileName, "Only PNG, JPEG, and WebP images are supported.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stagePath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.upload");
        try
        {
            var (length, sha256) = await CopyToStageAsync(source, stagePath, fileName, cancellationToken);
            var metadata = await IdentifyAsync(stagePath, length, fileName, cancellationToken);
            var expectedExtension = metadata.Extension;
            if (!ExtensionMatches(requestedExtension, expectedExtension))
            {
                throw Error("ImageExtensionMismatch", fileName, "The image extension does not match its content.");
            }

            var pixels = checked((long)metadata.Width * metadata.Height);
            if (pixels > maxPixels)
            {
                throw Error("ImagePixelLimitExceeded", fileName, "The image dimensions exceed the allowed limit.");
            }

            var storageKey = $"images/{descriptor.ImageId}/{sha256}{expectedExtension}";
            var targetPath = ResolveStorageKey(storageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            if (File.Exists(targetPath))
            {
                if (new FileInfo(targetPath).Length != length)
                {
                    throw Error("ImageStorageCollision", fileName, "An image storage collision was detected.");
                }

                File.Delete(stagePath);
            }
            else
            {
                try
                {
                    File.Move(stagePath, targetPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(targetPath) && new FileInfo(targetPath).Length == length)
                {
                    File.Delete(stagePath);
                }
            }

            return new(
                storageKey,
                sha256,
                length,
                metadata.Width,
                metadata.Height,
                metadata.MimeType);
        }
        catch (OperationCanceledException)
        {
            DeleteStage(stagePath);
            throw;
        }
        catch (LineUpImportException)
        {
            DeleteStage(stagePath);
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException)
        {
            DeleteStage(stagePath);
            throw Error("ImageStorageFailed", fileName, "The image could not be stored safely.");
        }
    }

    public Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(storageKey) ||
            !storageKey.StartsWith("images/", StringComparison.Ordinal) ||
            storageKey.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            throw Error("InvalidStorageKey", "image", "The image storage key is invalid.");
        }

        var path = ResolveStorageKey(storageKey);
        if (!File.Exists(path))
        {
            // 404 高频根因：库里有行（Lup_StationImage.CurrentStorageKey）但磁盘缺文件——
            // 库行与文件两段式，换机部署/本地开发没拷存储目录即命中。带上 key 便于直接定位缺哪个文件。
            throw Error("ImageContentMissing", "image",
                $"The image content is missing from storage (key: {storageKey}).");
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private async Task<(long Length, string Sha256)> CopyToStageAsync(
        Stream source,
        string stagePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            stagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long length = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > maxBytes - length)
                {
                    throw Error("ImageSizeLimitExceeded", fileName, "The image exceeds the allowed size.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                length += read;
            }

            await destination.FlushAsync(cancellationToken);
            if (length == 0)
            {
                throw Error("InvalidImageContent", fileName, "The image content is empty.");
            }

            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<ImageMetadata> IdentifyAsync(
        string path,
        long length,
        string fileName,
        CancellationToken cancellationToken)
    {
        var headerLength = (int)Math.Min(length, HeaderLimit);
        var header = new byte[headerLength];
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(header.AsMemory(), cancellationToken);

        ImageMetadata? metadata = null;
        if (header.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (header.Length >= 24 &&
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4)) == 13 &&
                header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                metadata = new(
                    BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)),
                    "image/png",
                    ".png");
            }
        }
        else if (header.Length >= 4 && header[0] == 0xff && header[1] == 0xd8)
        {
            metadata = IdentifyJpeg(header);
        }
        else if (header.Length >= 30 &&
                 header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                 header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            metadata = IdentifyWebP(header, length);
        }

        if (metadata is null || metadata.Width <= 0 || metadata.Height <= 0)
        {
            throw Error("InvalidImageContent", fileName, "The image header is invalid or unsupported.");
        }

        return metadata;
    }

    private static ImageMetadata? IdentifyJpeg(ReadOnlySpan<byte> bytes)
    {
        var offset = 2;
        while (offset + 8 < bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] != 0xff) offset++;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9) continue;
            if (offset + 2 > bytes.Length) break;
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or
                0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                if (segmentLength < 7) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                return new(width, height, "image/jpeg", ".jpg");
            }

            offset += segmentLength;
        }

        return null;
    }

    private static ImageMetadata? IdentifyWebP(ReadOnlySpan<byte> bytes, long length)
    {
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) + 8L;
        if (declaredLength > length)
        {
            return null;
        }

        if (bytes.Slice(12, 4).SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            var width = ReadUInt24LittleEndian(bytes.Slice(24, 3)) + 1;
            var height = ReadUInt24LittleEndian(bytes.Slice(27, 3)) + 1;
            return new(width, height, "image/webp", ".webp");
        }

        if (bytes.Slice(12, 4).SequenceEqual("VP8L"u8) &&
            bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            var width = 1 + bytes[21] + ((bytes[22] & 0x3f) << 8);
            var height = 1 + (bytes[22] >> 6) + (bytes[23] << 2) + ((bytes[24] & 0x0f) << 10);
            return new(width, height, "image/webp", ".webp");
        }

        if (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) &&
            bytes.Length >= 30 && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(26, 2)) & 0x3fff;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(28, 2)) & 0x3fff;
            return new(width, height, "image/webp", ".webp");
        }

        return null;
    }

    private string ResolveStorageKey(string storageKey)
    {
        var relative = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Error("InvalidStorageKey", "image", "The generated image path is invalid.");
        }

        return fullPath;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | value[1] << 8 | value[2] << 16;

    private static bool ExtensionMatches(string requested, string detected) =>
        requested == detected || detected == ".jpg" && requested == ".jpeg";

    private static void DeleteStage(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static LineUpImportException Error(string code, string fileName, string summary) =>
        new(code, Path.GetFileName(fileName), summary);

    private sealed record ImageMetadata(int Width, int Height, string MimeType, string Extension);
}
