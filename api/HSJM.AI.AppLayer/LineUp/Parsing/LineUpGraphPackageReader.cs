using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.LineUp.Parsing;

public sealed class LineUpGraphPackageReader
{
    public const long MaxSingleFileBytes = 25L * 1024 * 1024;
    public const long MaxExpandedBytes = 200L * 1024 * 1024;
    public const int MaxArchiveEntries = 2000;

    private const int CopyBufferBytes = 64 * 1024;

    static LineUpGraphPackageReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<IReadOnlyList<LineUpGraphSourceFile>> ReadAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        var logicalFileName = SafeDisplayName(fileName);
        try
        {
            if (HasExtension(logicalFileName, ".xml") || HasExtension(logicalFileName, ".ml"))
            {
                var bytes = await ReadSinglePayloadAsync(
                    content,
                    logicalFileName,
                    MaxSingleFileBytes,
                    cancellationToken);
                var xmlContent = await ReadXmlContentAsync(bytes, logicalFileName, cancellationToken);
                return new[]
                {
                    CreateSourceFile(logicalFileName, logicalFileName, bytes, xmlContent, cancellationToken)
                };
            }

            if (HasExtension(logicalFileName, ".zip"))
            {
                return await ReadArchiveAsync(content, logicalFileName, cancellationToken);
            }

            throw Error(
                "UnsupportedFileExtension",
                logicalFileName,
                "Only XML, ML, and ZIP graph packages are supported.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LineUpImportException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Error("InputReadFailure", logicalFileName, "The uploaded content could not be read safely.");
        }
        catch (NotSupportedException)
        {
            throw Error("InputReadFailure", logicalFileName, "The uploaded content could not be read safely.");
        }
    }

    private static async Task<IReadOnlyList<LineUpGraphSourceFile>> ReadArchiveAsync(
        Stream content,
        string archiveName,
        CancellationToken cancellationToken)
    {
        await using var archiveBuffer = await BufferArchiveAsync(content, archiveName, cancellationToken);

        try
        {
            using var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0)
            {
                throw Error("EmptyArchive", archiveName, "The archive contains no entries.");
            }

            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw Error(
                    "ArchiveEntryLimitExceeded",
                    archiveName,
                    "The archive contains too many entries.");
            }

            var entries = ValidateArchiveEntries(archive, archiveName);
            var sourceEntries = entries.Where(item => !item.IsDirectory).ToList();
            if (sourceEntries.Count == 0)
            {
                throw Error("NoGraphSources", archiveName, "The archive contains no graph source files.");
            }

            var result = new List<LineUpGraphSourceFile>(sourceEntries.Count);
            var expandedCounter = new ExpandedCounter();
            foreach (var sourceEntry in sourceEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var entryStream = sourceEntry.Entry.Open();
                var bytes = await ReadArchiveEntryAsync(
                    entryStream,
                    archiveName,
                    expandedCounter,
                    cancellationToken);
                var xmlContent = await ReadXmlContentAsync(bytes, sourceEntry.FileName, cancellationToken);
                result.Add(CreateSourceFile(
                    sourceEntry.FileName,
                    sourceEntry.RelativePath,
                    bytes,
                    xmlContent,
                    cancellationToken));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LineUpImportException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw Error("InvalidArchive", archiveName, "The ZIP package is invalid or corrupted.");
        }
        catch (IOException)
        {
            throw Error("InvalidArchive", archiveName, "The ZIP package could not be read safely.");
        }
        catch (NotSupportedException)
        {
            throw Error("InvalidArchive", archiveName, "The ZIP package uses an unsupported structure.");
        }
    }

    private static List<ValidatedArchiveEntry> ValidateArchiveEntries(ZipArchive archive, string archiveName)
    {
        var canonicalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ValidatedArchiveEntry>(archive.Entries.Count);

        foreach (var entry in archive.Entries)
        {
            var relativePath = CanonicalizeArchivePath(entry.FullName, archiveName);
            if (!canonicalPaths.Add(relativePath.Normalize(NormalizationForm.FormC)))
            {
                throw Error(
                    "DuplicateArchivePath",
                    archiveName,
                    "The archive contains colliding logical paths.");
            }

            var normalizedFullName = entry.FullName.Replace('\\', '/');
            var isDirectory = normalizedFullName.EndsWith("/", StringComparison.Ordinal);
            var fileName = SafeDisplayName(relativePath);
            if (!isDirectory && !HasExtension(fileName, ".xml") && !HasExtension(fileName, ".ml"))
            {
                throw Error(
                    "UnsupportedArchiveEntry",
                    archiveName,
                    "The archive contains a file that is not XML or ML.");
            }

            result.Add(new ValidatedArchiveEntry(entry, fileName, relativePath, isDirectory));
        }

        return result;
    }

    private static string CanonicalizeArchivePath(string entryName, string archiveName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            throw Error("ArchivePathTraversal", archiveName, "The archive contains an unsafe path.");
        }

        string normalized;
        try
        {
            normalized = entryName.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            throw Error("ArchivePathTraversal", archiveName, "The archive contains an unsafe path.");
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && IsAsciiLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.Any(character => character == ':' || char.IsControl(character)))
        {
            throw Error("ArchivePathTraversal", archiveName, "The archive contains an unsafe path.");
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/'))
        {
            if (segment == "..")
            {
                throw Error("ArchivePathTraversal", archiveName, "The archive contains an unsafe path.");
            }

            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw Error("ArchivePathTraversal", archiveName, "The archive contains an unsafe path.");
        }

        return string.Join('/', segments).Normalize(NormalizationForm.FormC);
    }

    private static async Task<MemoryStream> BufferArchiveAsync(
        Stream source,
        string archiveName,
        CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long totalBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > MaxSingleFileBytes - totalBytes)
                {
                    throw Error(
                        "SingleFileSizeLimitExceeded",
                        archiveName,
                        "The uploaded ZIP file exceeds the allowed size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalBytes += read;
            }

            destination.Position = 0;
            return destination;
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadSinglePayloadAsync(
        Stream source,
        string logicalFileName,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long totalBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > maxBytes - totalBytes)
                {
                    throw Error(
                        "SingleFileSizeLimitExceeded",
                        logicalFileName,
                        "The graph source exceeds the allowed size.");
                }

                destination.Write(buffer, 0, read);
                totalBytes += read;
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadArchiveEntryAsync(
        Stream source,
        string archiveName,
        ExpandedCounter expandedCounter,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long fileBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > MaxSingleFileBytes - fileBytes)
                {
                    throw Error(
                        "SingleFileSizeLimitExceeded",
                        archiveName,
                        "An archive entry exceeds the allowed size.");
                }

                if (read > MaxExpandedBytes - expandedCounter.Bytes)
                {
                    throw Error(
                        "ExpandedSizeLimitExceeded",
                        archiveName,
                        "The archive expands beyond the allowed size.");
                }

                destination.Write(buffer, 0, read);
                fileBytes += read;
                expandedCounter.Bytes += read;
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ReadXmlContentAsync(
        byte[] bytes,
        string logicalFileName,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxSingleFileBytes,
            MaxCharactersFromEntities = 0
        };

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            cancellationToken.ThrowIfCancellationRequested();
            var document = await XDocument.LoadAsync(
                reader,
                LoadOptions.PreserveWhitespace,
                cancellationToken);
            if (document.Root == null || document.Root.Name.LocalName != "Document")
            {
                throw Error(
                    "UnexpectedXmlRoot",
                    logicalFileName,
                    "The graph source root element must be Document.");
            }

            // Keep advancing the hardened reader so malformed trailing data cannot pass
            // merely because a valid Document element was loaded first.
            while (await reader.ReadAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return document.ToString(SaveOptions.DisableFormatting);
        }
        catch (XmlException)
        {
            throw Error("InvalidXml", logicalFileName, "The graph source is not a valid XML document.");
        }
    }

    private static LineUpGraphSourceFile CreateSourceFile(
        string fileName,
        string relativePath,
        byte[] bytes,
        string xmlContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();

        return new LineUpGraphSourceFile
        {
            FileName = fileName,
            RelativePath = relativePath,
            Content = xmlContent,
            Sha256 = sha256
        };
    }

    private static bool HasExtension(string fileName, string extension) =>
        fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string SafeDisplayName(string fileName)
    {
        var normalized = (fileName ?? string.Empty).Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        var leaf = separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;

        try
        {
            leaf = leaf.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            leaf = string.Empty;
        }

        var safe = new string(leaf
            .Where(character => !char.IsControl(character))
            .Take(128)
            .ToArray());
        return string.IsNullOrEmpty(safe) ? "upload" : safe;
    }

    private static LineUpImportException Error(string code, string logicalFileName, string summary) =>
        new(code, SafeDisplayName(logicalFileName), summary);

    private sealed record ValidatedArchiveEntry(
        ZipArchiveEntry Entry,
        string FileName,
        string RelativePath,
        bool IsDirectory);

    private sealed class ExpandedCounter
    {
        public long Bytes { get; set; }
    }
}

public sealed class LineUpGraphSourceFile
{
    public string FileName { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class LineUpImportException : Exception
{
    public LineUpImportException(string code, string logicalFileName, string safeSummary)
        : base($"{code}: {logicalFileName}: {safeSummary}")
    {
        Code = code;
        LogicalFileName = logicalFileName;
        SafeSummary = safeSummary;
    }

    public string Code { get; }
    public string LogicalFileName { get; }
    public string SafeSummary { get; }
}
