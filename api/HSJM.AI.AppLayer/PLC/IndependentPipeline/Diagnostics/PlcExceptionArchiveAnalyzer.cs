#nullable enable
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Original353;
using System.IO;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Diagnostics;

public sealed record PlcExceptionArchiveResult(string SourceFingerprint,string ArchiveHash,
    IReadOnlyList<PlcSourceExceptionEntry> Entries,int MatchedDbFiles,int ReplacedProgramBlocks,IReadOnlyList<string> Issues);

public static class PlcExceptionArchiveAnalyzer
{
    public static PlcExceptionArchiveResult Analyze(PipelineSnapshot source,Stream archive) {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(archive);
        if(!archive.CanRead||archive.CanSeek&&archive.Length>128_000_000)throw new PipelineException("ArchiveSizeExceeded");
        var projectFolders=source.Units.Select(u=>u.SourceFile.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries))
            .Select(parts=>{var index=Array.FindIndex(parts,p=>p.Equals("blocks",StringComparison.OrdinalIgnoreCase));return index>0?parts[index-1]:"";})
            .Where(s=>s.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(projectFolders.Length!=1)throw new PipelineException("ArchiveProjectFolderAmbiguous");
        using var bytes=new MemoryStream();
        var buffer=new byte[81920];int read;
        while((read=archive.Read(buffer,0,buffer.Length))>0) {
            if(bytes.Length+read>128_000_000)throw new PipelineException("ArchiveSizeExceeded");
            bytes.Write(buffer,0,read);
        }
        var archiveHash=Convert.ToHexString(SHA256.HashData(bytes.ToArray()));bytes.Position=0;
        using var zip=new ZipArchive(bytes,ZipArchiveMode.Read,true);
        if(zip.Entries.Count is 0 or >20000)throw new PipelineException("InvalidArchiveEntries");
        var declarations=new List<PlcDbDeclaration>();var documents=new List<OriginalXmlDocument>();var issues=new List<string>();
        var matchedDb=0;long expanded=0;
        foreach(var entry in zip.Entries) {
            var path=entry.FullName.Replace('\\','/');
            var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
            if(path.StartsWith('/')||path.Contains(':')||segments.Any(s=>s is "." or "..")||segments.Length>32)
                throw new PipelineException("ArchiveEntryPathInvalid");
            if(!path.EndsWith(".xml",StringComparison.OrdinalIgnoreCase))continue;
            if(entry.Length>32_000_000||entry.Length<0||expanded+entry.Length>512_000_000)
                throw new PipelineException("ArchiveExpandedSizeExceeded");
            expanded+=entry.Length;
            var folderIndex=Array.FindIndex(segments,s=>s.Equals(projectFolders[0],StringComparison.OrdinalIgnoreCase));
            if(folderIndex<0)continue;
            var blocksIndex=Array.FindIndex(segments,folderIndex+1,s=>s.Equals("blocks",StringComparison.OrdinalIgnoreCase));
            var isDb=blocksIndex>=0&&blocksIndex+1<segments.Length&&segments[blocksIndex+1].Equals("DB",StringComparison.OrdinalIgnoreCase);
            var isProgram=blocksIndex>=0&&blocksIndex+1<segments.Length&&segments[blocksIndex+1] is not("DB" or "db");
            var isUdt=Array.FindIndex(segments,folderIndex+1,s=>s.Equals("udt",StringComparison.OrdinalIgnoreCase))>=0;
            if(!isDb&&!isProgram&&!isUdt)continue;
            var xml=ReadXml(entry);
            if(isDb) {
                try {
                    var parsed=PlcDbDeclaration.Parse(xml);
                    if(parsed.Count==0)issues.Add("DbDeclarationUnavailable:"+path);
                    else {declarations.AddRange(parsed);matchedDb++;}
                }catch(PipelineException error) when(error.Code=="GlobalDbDeclarationRequired") {
                    // Instance DBs need their FB interface and exact instance binding. They are
                    // retained as unresolved evidence, never misread as global Bool declarations.
                    issues.Add("InstanceDbNeedsInterfaceBinding:"+path);
                }
            } else if(isUdt) {
                // The source is read, but an unbound UDT member does not identify a concrete
                // DB instance or an observable point. It cannot be promoted into an alarm.
                issues.Add("UdtNeedsInstanceBinding:"+path);
            } else {
                try {
                    var block=PipelineExportReader.Read(xml,source.ProjectId,path);
                    if(block.Kind is "FB" or "FC" or "OB")documents.Add(new(path,xml));
                }catch(PipelineException e){issues.Add("ProgramXmlUnavailable:"+path+":"+e.Code);}
            }
        }
        if(matchedDb==0)issues.Add("NoGlobalDbDeclarationsForProject");
        var current=documents.Count>0?OriginalConstraintWorkflow.Reimport(source,documents):source;
        var metadata=declarations.GroupBy(d=>d.SymbolPath,StringComparer.OrdinalIgnoreCase).ToArray();
        if(metadata.Any(g=>g.Select(d=>(d.DataType,d.ExternalAccessible,d.SourceHash)).Distinct().Count()>1))
            throw new PipelineException("ConflictingDbDeclarations");
        var unique=metadata.Select(g=>g.First()).ToArray();
        var entries=PlcSourceExceptionCatalog.Discover(current,unique);
        return new(PlcSourceExceptionCatalog.CompositeFingerprint(current,unique),archiveHash,entries,matchedDb,documents.Count,issues);
    }
    static string ReadXml(ZipArchiveEntry entry) {
        using var input=entry.Open();using var bytes=new MemoryStream();
        var buffer=new byte[81920];int read;
        while((read=input.Read(buffer,0,buffer.Length))>0) {
            if(bytes.Length+read>32_000_000)throw new PipelineException("ArchiveXmlSizeExceeded");
            bytes.Write(buffer,0,read);
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimStart('\uFEFF');
    }
}
