using System;
using System.IO;
using System.Text.Json;
using HSJM.AI.Core;
using Microsoft.Extensions.Configuration;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// 保存包的版本元数据（2026-10-09 上传约束后台执行）：记录包对应的库内程序指纹，
    /// 以及该版本是否已被约束任务成功发布过。约束任务据此做版本校验（防止 A 上传的约束任务
    /// 在 B 上传入库之后发布出与库不符的规则），并避免同版本重复发布。
    /// </summary>
    public sealed record PlcArchiveMeta(
        string Fingerprint,
        string TaskId,
        DateTime SavedAtUtc,
        string PublishedFingerprint = null,
        DateTime? PublishedAtUtc = null);

    /// <summary>
    /// 每个项目最近一次 XMLV2 发布成功的上传包（审查 P2/P3，2026-10-02）：约束重置（ResetRule）与单 LU / 单块重跑都用它重新发布，
    /// 不再走老方法。每项目只留一份（{root}/{projectId}.zip），先写同目录临时文件再原子替换。
    /// 根目录取 <see cref="ConfigKey"/>，默认 {当前目录}/Uploads/plc-archives——容器里即 /app/Uploads（只读容器唯一可写的业务卷）。
    /// 2026-10-09 上传约束后台执行：保存包在入库成功后即写入（不再等发布成功），并附带 <see cref="PlcArchiveMeta"/>；
    /// 约束任务（PLC_UploadConstraintJob）读包发布。发布失败不再 Discard（库与包仍同版本，重试可用）。
    /// </summary>
    public sealed class PlcArchiveStore : ISelfSingletonAutoInject
    {
        public const string ConfigKey = "WebConfig:PlcArchive:Root";

        private static readonly JsonSerializerOptions MetaJsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public PlcArchiveStore(IConfiguration configuration) : this(ResolveRoot(configuration))
        {
        }

        internal PlcArchiveStore(string root)
        {
            Root = Path.GetFullPath(root);
        }

        public string Root { get; }

        public static string ResolveRoot(IConfiguration configuration)
        {
            var configured = configuration?[ConfigKey]?.Trim();
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Directory.GetCurrentDirectory(), "Uploads", "plc-archives")
                : configured);
        }

        public string PathFor(long projectId)
        {
            if (projectId <= 0) throw new ArgumentOutOfRangeException(nameof(projectId));
            return Path.Combine(Root, projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".zip");
        }

        /// <summary>保存包的版本元数据文件路径（与包并排，{projectId}.meta.json）。</summary>
        public string MetaPathFor(long projectId)
        {
            if (projectId <= 0) throw new ArgumentOutOfRangeException(nameof(projectId));
            return Path.Combine(Root, projectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".meta.json");
        }

        public bool Exists(long projectId) => projectId > 0 && File.Exists(PathFor(projectId));

        /// <summary>读保存包的版本元数据；包存在但 meta 缺失（2026-10-09 之前的存量包）返回 null。</summary>
        public PlcArchiveMeta ReadMeta(long projectId)
        {
            if (projectId <= 0 || !File.Exists(MetaPathFor(projectId))) return null;
            try
            {
                return JsonSerializer.Deserialize<PlcArchiveMeta>(File.ReadAllText(MetaPathFor(projectId)), MetaJsonOptions);
            }
            catch
            {
                return null; // meta 损坏按缺失处理（调用方按版本不明跳过，不会拿错版本发布）
            }
        }

        /// <summary>复制到临时文件后原子替换；复制失败时旧包不动、临时文件清掉。</summary>
        public void Save(long projectId, string sourceZip)
        {
            var target = PathFor(projectId);
            Directory.CreateDirectory(Root);
            var temp = Path.Combine(Root, $".{projectId}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(sourceZip, temp, overwrite: false);
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }

        /// <summary>
        /// 保存包 + 版本元数据一起落盘（2026-10-09 上传约束后台执行）。
        /// 原子性：zip 与 meta 各写临时文件，两个都写成功后<b>先换 zip 再换 meta</b>——
        /// 中间崩溃的窗口是「zip 新、meta 旧」：约束任务按 meta 旧指纹校验会跳过（安全，由 ResetRule 兜底）；
        /// 反过来「meta 新、zip 旧」会用旧 zip 的内容配新指纹发布（有害），所以顺序不能反。
        /// </summary>
        public void SaveWithMeta(long projectId, string sourceZip, PlcArchiveMeta meta)
        {
            if (projectId <= 0) throw new ArgumentOutOfRangeException(nameof(projectId));
            if (meta == null) throw new ArgumentNullException(nameof(meta));
            var zipTarget = PathFor(projectId);
            var metaTarget = MetaPathFor(projectId);
            Directory.CreateDirectory(Root);
            var zipTemp = Path.Combine(Root, $".{projectId}.{Guid.NewGuid():N}.zip.tmp");
            var metaTemp = Path.Combine(Root, $".{projectId}.{Guid.NewGuid():N}.meta.tmp");
            try
            {
                File.Copy(sourceZip, zipTemp, overwrite: false);
                File.WriteAllText(metaTemp, JsonSerializer.Serialize(meta, MetaJsonOptions));
                File.Move(zipTemp, zipTarget, overwrite: true);
                File.Move(metaTemp, metaTarget, overwrite: true);
            }
            finally
            {
                foreach (var temp in new[] { zipTemp, metaTemp })
                {
                    if (File.Exists(temp))
                    {
                        try { File.Delete(temp); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// 标记「该版本的约束已成功发布」（约束任务成功后调用，发布锁内单写者）。
        /// meta 缺失/损坏时重建为只含发布指纹的最小记录。
        /// </summary>
        public void MarkPublished(long projectId, string fingerprint, string taskId = null)
        {
            if (projectId <= 0) return;
            var meta = ReadMeta(projectId) ?? new PlcArchiveMeta(fingerprint, taskId, DateTime.UtcNow);
            meta = meta with { PublishedFingerprint = fingerprint, PublishedAtUtc = DateTime.UtcNow };
            Directory.CreateDirectory(Root);
            File.WriteAllText(MetaPathFor(projectId), JsonSerializer.Serialize(meta, MetaJsonOptions));
        }

        /// <summary>删除保存包（库已与它不一致时）。返回是否删掉了文件。</summary>
        public bool Discard(long projectId)
        {
            var target = PathFor(projectId);
            var deleted = false;
            if (File.Exists(target))
            {
                File.Delete(target);
                deleted = true;
            }
            var meta = MetaPathFor(projectId);
            if (File.Exists(meta))
            {
                try { File.Delete(meta); } catch { }
            }
            return deleted;
        }
    }
}
