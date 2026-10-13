#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Business;

/// <summary>Lossless internal storage format, not HTML. Arrays retain order; dictionary keys are canonical.</summary>
public static class BusinessPayloadCodec
{
    // Large GRAPH contexts can contain repeated whole-block evidence. Never silently truncate them.
    public const int DefaultMaxDecodedBytes = 256 * 1024 * 1024;
    public const string MaxDecodedBytesEnvironmentVariable = "PLC_BUSINESS_MAX_DESCRIPTION_BYTES";
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public sealed record Encoded(byte[] Gzip,string Hash,int Utf8Length);

    public static Encoded Encode(BusinessDescription description,int? maxDecodedBytes = null) {
        var bytes = CanonicalBytes(description,Limit(maxDecodedBytes));
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output,CompressionLevel.Fastest,leaveOpen:true)) gzip.Write(bytes);
        return new(output.ToArray(),Digest(bytes),bytes.Length);
    }

    public static string Hash(BusinessDescription description,int? maxDecodedBytes = null)
        => Digest(CanonicalBytes(description,Limit(maxDecodedBytes)));

    public static BusinessDescription Decode(byte[] compressed,string? expectedHash,int? maxDecodedBytes = null) {
        var limit = Limit(maxDecodedBytes);
        if (compressed.Length < 18 || string.IsNullOrWhiteSpace(expectedHash))
            throw new PipelineException("InvalidBusinessDescriptionPayload");
        try {
            using var input = new MemoryStream(compressed,writable:false);
            using var gzip = new GZipStream(input,CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = gzip.Read(buffer,0,buffer.Length)) != 0) {
                if (output.Length + count > limit) throw new PipelineException("BusinessDescriptionTooLarge");
                output.Write(buffer,0,count);
            }
            // GZipStream can accept missing trailer bytes; explicitly reject incomplete gzip members.
            if (BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(compressed.Length-4)) != output.Length)
                throw new PipelineException("InvalidBusinessDescriptionPayload");
            var bytes = output.ToArray();
            if (!string.Equals(Digest(bytes),expectedHash,StringComparison.Ordinal))
                throw new PipelineException("BusinessDescriptionDigestMismatch");
            return Deserialize(bytes);
        }
        catch (InvalidDataException) { throw new PipelineException("InvalidBusinessDescriptionPayload"); }
        catch (JsonException) { throw new PipelineException("InvalidBusinessDescriptionPayload"); }
    }

    public static BusinessDescription DecodeLegacy(string json,int? maxDecodedBytes = null) {
        if (Encoding.UTF8.GetByteCount(json) > Limit(maxDecodedBytes)) throw new PipelineException("BusinessDescriptionTooLarge");
        try { return Deserialize(Encoding.UTF8.GetBytes(json)); }
        catch (JsonException) { throw new PipelineException("InvalidBusinessDescriptionPayload"); }
    }

    private static BusinessDescription Deserialize(byte[] json) {
        var description = JsonSerializer.Deserialize<BusinessDescription>(json);
        Validate(description); return description!;
    }
    private static void Validate(BusinessDescription? description) {
        if (description is null || description.BlockName is null || description.Fingerprint is null || description.SourceHash is null ||
            description.Status is null || description.Markdown is null || description.ModelText is null || description.SignalAliases is null ||
            description.Evidence is null || description.Limitations is null || description.Callees is null)
            throw new PipelineException("InvalidBusinessDescriptionPayload");
    }
    private static byte[] CanonicalBytes(BusinessDescription description,int limit) {
        Validate(description);
        var aliases = new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach (var pair in description.SignalAliases) aliases.Add(pair.Key,pair.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(description with { SignalAliases = aliases },Options);
        if (bytes.Length > limit) throw new PipelineException("BusinessDescriptionTooLarge");
        return bytes;
    }
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static int Limit(int? explicitLimit) {
        if (explicitLimit.HasValue) {
            if (explicitLimit.Value <= 0) throw new PipelineException("InvalidBusinessDescriptionSizeLimit");
            return explicitLimit.Value;
        }
        var setting = Environment.GetEnvironmentVariable(MaxDecodedBytesEnvironmentVariable);
        if (string.IsNullOrEmpty(setting)) return DefaultMaxDecodedBytes;
        if (!int.TryParse(setting,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed) || parsed <= 0)
            throw new PipelineException("InvalidBusinessDescriptionSizeLimit");
        return parsed;
    }
}
