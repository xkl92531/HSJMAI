#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;

/// <summary>Builds an immutable recognition snapshot from the uploaded XML archive before
/// the legacy database importer drops block interface/body details.</summary>
public static class Original353ArchiveSource
{
    public static PipelineSnapshot Read(string archivePath, long projectId, IReadOnlyList<PipelineTag> tags)
    {
        if (projectId <= 0 || string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new PipelineException("UploadedArchiveUnavailable");
        using var archive = ZipFile.OpenRead(archivePath);
        var xmlEntries = archive.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (xmlEntries.Length is 0 or > 1024 || xmlEntries.Sum(entry => entry.Length) > 512_000_000)
            throw new PipelineException("UploadedXmlBudgetExceeded");
        var blocks = new List<PipelineImportBlock>();
        var dbInterfaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in xmlEntries)
        {
            if (entry.Length > 32_000_000) throw new PipelineException("UploadedXmlBudgetExceeded");
            using var input = new StreamReader(entry.Open());
            var content = input.ReadToEnd();
            try { blocks.Add(PipelineExportReader.Read(content, projectId, entry.FullName)); }
            catch (PipelineException ex) when (ex.Code == "UnsupportedExportBlock")
            {
                // DB/UDT 声明不是可执行块；全局 DB 的接口（起始值）留给期望状态报警的配置输入解析用（重名不收，只会少解析）
                if (PipelineExportReader.TryReadGlobalDbInterface(content, out var dbName, out var dbInterface) && !dbInterfaces.TryAdd(dbName, dbInterface))
                    dbInterfaces[dbName] = "";
            }
        }
        if (blocks.Count == 0) throw new PipelineException("NoExecutableBlockXml");
        if (blocks.GroupBy(block => block.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new PipelineException("DuplicateUploadedBlock");
        var units = blocks.SelectMany(block => block.Units).ToArray();
        if (units.GroupBy(unit => unit.Id).Any(group => group.Count() != 1))
            throw new PipelineException("AmbiguousImportedUnitIdentity");
        return new(projectId, units, tags) { DbInterfaces = dbInterfaces.Where(x => x.Value.Length > 0).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase) };
    }
}
