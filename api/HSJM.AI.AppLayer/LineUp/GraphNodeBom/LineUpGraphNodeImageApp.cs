using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.LineUp.GraphNodeBom;

public sealed class LineUpGraphNodeImageException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>单张节点成品图载荷（前端 canvas 渲染的 PNG，Base64 传输）。</summary>
public sealed record LineUpGraphNodeImagePayload(
    string SourceImageId,
    int PixelWidth,
    int PixelHeight,
    string PngBase64);

/// <summary>保存一个节点的全部成品图快照（同批次同节点整组替换，幂等）。</summary>
public sealed record LineUpGraphNodeImageSaveRequest(
    string BatchId,
    string LineId,
    string DeviceId,
    string BlockName,
    string SequenceSourceKey,
    string NodeSourceKey,
    string NodeKind,
    string SubStationId,
    IReadOnlyList<LineUpGraphNodeImagePayload> Images);

/// <summary>节点成品图行（Id 字符串化，避免 JS 超 2^53 精度丢失）。</summary>
public sealed record LineUpGraphNodeImageDto(
    string BatchId,
    string LineId,
    string DeviceId,
    string BlockName,
    string SequenceSourceKey,
    string NodeSourceKey,
    string NodeKind,
    string SubStationId,
    string SourceImageId,
    string StorageKey,
    int PixelWidth,
    int PixelHeight,
    string ContentUrl);

public sealed record LineUpGraphNodeImageSaveDto(
    string BatchId,
    string NodeSourceKey,
    int ImageCount);

/// <summary>
/// GRAPH 节点成品图快照：保存 graph 结构时前端按节点渲染"成品图+匹配 BOM 红点"并上传；
/// 查看流程图直接读本表 + 内容文件，不做运行时匹配。
/// 文件与 Lup_StationImage 同根（WebConfig:LineUp:StorageRoot，缺省 App_Data/LineUp），子目录 graph-node-images。
/// </summary>
public sealed class LineUpGraphNodeImageApp(
    ILineUpGraphNodeImageRepository repository,
    IConfiguration configuration) : ISelfScopedAutoInject
{
    private static readonly Regex StorageKeyPattern = new(@"^[0-9a-f]{64}\.png$", RegexOptions.Compiled);

    private readonly string imagesRoot = ResolveImagesRoot(configuration);

    private static string ResolveImagesRoot(IConfiguration config)
    {
        var configured = config["WebConfig:LineUp:StorageRoot"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Path.Combine(AppContext.BaseDirectory, "App_Data", "LineUp");
        }

        var root = Path.Combine(Path.GetFullPath(configured), "graph-node-images");
        Directory.CreateDirectory(root);
        return root;
    }

    public async Task<LineUpGraphNodeImageSaveDto> SaveNodeImagesAsync(
        LineUpGraphNodeImageSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.NodeSourceKey) || request.Images.Count == 0)
        {
            throw new LineUpGraphNodeImageException("GraphNodeImagePayloadInvalid");
        }

        if (!long.TryParse(request.BatchId, out var batchId)
            || !long.TryParse(request.LineId, out var lineId)
            || !long.TryParse(request.DeviceId, out var deviceId)
            || !long.TryParse(request.SubStationId, out var subStationId))
        {
            throw new LineUpGraphNodeImageException("GraphNodeImageIdentityInvalid");
        }

        var rows = new List<LineUpGraphNodeImageEntity>();
        foreach (var image in request.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.PngBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new LineUpGraphNodeImageException("GraphNodeImageContentInvalid");
            }

            if (bytes.Length == 0)
            {
                throw new LineUpGraphNodeImageException("GraphNodeImageContentInvalid");
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var storageKey = $"{sha}.png";
            var path = Path.Combine(imagesRoot, storageKey);
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            }

            if (!long.TryParse(image.SourceImageId, out var sourceImageId))
            {
                throw new LineUpGraphNodeImageException("GraphNodeImageIdentityInvalid");
            }

            rows.Add(new LineUpGraphNodeImageEntity
            {
                BatchId = batchId,
                LineId = lineId,
                DeviceId = deviceId,
                BlockName = request.BlockName ?? string.Empty,
                SequenceSourceKey = request.SequenceSourceKey ?? string.Empty,
                NodeSourceKey = request.NodeSourceKey,
                NodeKind = request.NodeKind ?? string.Empty,
                SubStationId = subStationId,
                SourceImageId = sourceImageId,
                StorageKey = storageKey,
                PixelWidth = image.PixelWidth,
                PixelHeight = image.PixelHeight,
            });
        }

        await repository.ReplaceNodeRowsAsync(batchId, request.NodeSourceKey, rows, cancellationToken);
        return new LineUpGraphNodeImageSaveDto(request.BatchId, request.NodeSourceKey, rows.Count);
    }

    public async Task<IReadOnlyList<LineUpGraphNodeImageDto>> ListByNodeAsync(
        long lineId,
        string blockName,
        string nodeSourceKey,
        CancellationToken cancellationToken)
    {
        var rows = await repository.ListByNodeAsync(lineId, blockName, nodeSourceKey, cancellationToken);
        var result = new List<LineUpGraphNodeImageDto>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(ToDto(row));
        }

        return result;
    }

    public Task<(string Path, string ContentType, string DownloadName)> ResolveContentAsync(
        string storageKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StorageKeyPattern.IsMatch(storageKey ?? string.Empty))
        {
            throw new LineUpGraphNodeImageException("GraphNodeImageContentMissing");
        }

        var path = Path.Combine(imagesRoot, storageKey);
        if (!File.Exists(path))
        {
            throw new LineUpGraphNodeImageException("GraphNodeImageContentMissing");
        }

        return Task.FromResult((path, "image/png", storageKey));
    }

    private static LineUpGraphNodeImageDto ToDto(LineUpGraphNodeImageEntity row) => new(
        row.BatchId.ToString(),
        row.LineId.ToString(),
        row.DeviceId.ToString(),
        row.BlockName,
        row.SequenceSourceKey,
        row.NodeSourceKey,
        row.NodeKind,
        row.SubStationId.ToString(),
        row.SourceImageId.ToString(),
        row.StorageKey,
        row.PixelWidth,
        row.PixelHeight,
        $"/LineUp/GraphNodeImage/Content?key={row.StorageKey}");
}