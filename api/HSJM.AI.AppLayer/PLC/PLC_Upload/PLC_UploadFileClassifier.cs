using System;
using System.IO;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload;

public static class PLC_UploadFileClassifier
{
    public static bool IsGeneralPlcSource(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        // 2026-09-21 去掉 .json：解析器（plc-analyzer/scripts/parser.py）只认 .scl/.awl 与 XML，
        // .json 走 XML 分支必然识别失败，却仍会在 PLC_UploadApp 里留下一行 ParseStatus=1 的
        // 孤儿 PLC_SourceFileEntity（SourceFile 在 blocks 判定之前就构建）。导出包里的
        // manifest.json / udt_index.json 因此每次上传各多一行；manifest.json 内容每次都变，
        // 文件级 hash 跳过永不命中，孤儿行只增不减。
        return extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
    }
}
