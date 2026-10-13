using Hangfire;
using Hangfire.States;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    /// <summary>
    /// 上传后 PLC 标签增量注册（2026-09-11 并入上传链路；用户拍板：增量 + 与约束生成/报警识别并行，不链式等待）。
    /// 与「注册PLC标签」按钮（PLC_UploadApp.ExecuteBatchRegistrationTag，全项目）完全同规则：
    /// DB 接口行（有 Offset 的标量成员）拼绝对地址 DB{块号}.DBX{偏移} 发老网关 {StationId}_S7 设备（2000/批），
    /// 每块另发 AddGroupTagItems 到 {StationId}_Plus；IOTag（I/Q/M 绝对地址）仅在本次上传带标签 CSV（全表刷新）时注册。
    /// 增量语义：只注册本次上传实际变更（清理+重插）的块——与工站分组无关，无需等「批量绑定工站」。
    /// 失败语义（2026-09-11 用户拍板）：异常抛回 Hangfire，任务标记 Failed（面板可见异常详情），
    /// 自动重试已关闭（[AutomaticRetry(Attempts=0)]），由用户手工重试（面板 Retry 同参重跑 / 按钮全量兜底）；
    /// 注册失败不阻断上传，也不影响同批入队的其它后处理任务。
    /// </summary>
    public class PLC_TagRegistrationJob : ISelfScopedAutoInject
    {
        /// <summary>同项目防重复入队（本进程内执行中则跳过；跨进程由本机队列隔离）。</summary>
        private static readonly ConcurrentDictionary<long, byte> _inFlightProjects = new();

        /// <summary>上传进度后处理槽的 key / 展示名（PLC_UploadProgressService.SetPostProcess 消费）。</summary>
        public const string PostProcessKey = "tag-registration";
        public const string PostProcessTitle = "标签增量注册";

        private readonly IPLC_ProjectService _proj;
        private readonly IPLC_BlockService _blockService;
        private readonly IPLC_BlockInterfaceService _blockInterfaceService;
        private readonly IPLC_IOTagService _ioTagService;
        private readonly IIotService _iotService;
        private readonly PLC_UploadProgressService _progressService;
        private readonly ILogger<PLC_TagRegistrationJob> _logger;

        public PLC_TagRegistrationJob(
            IPLC_ProjectService proj,
            IPLC_BlockService blockService,
            IPLC_BlockInterfaceService blockInterfaceService,
            IPLC_IOTagService ioTagService,
            IIotService iotService,
            PLC_UploadProgressService progressService,
            ILogger<PLC_TagRegistrationJob> logger)
        {
            _proj = proj;
            _blockService = blockService;
            _blockInterfaceService = blockInterfaceService;
            _ioTagService = ioTagService;
            _iotService = iotService;
            _progressService = progressService;
            _logger = logger;
        }

        /// <summary>
        /// 触发增量注册任务（防重复）。上传事务提交完成后调用，与约束/报警任务并行入队。
        /// taskId 非空时任务执行全程向该上传进度对象上报后处理状态（前端完成态继续轮询可见）。
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="changedBlockNames">本次上传实际变更（清理+重插）的块名集合</param>
        /// <param name="ioTagsRefreshed">本次上传带标签 CSV（IOTag 全表刷新）时为 true</param>
        /// <param name="taskId">上传任务ID（用于进度上报；null=不上报）</param>
        /// <returns>是否成功入队（false = 同项目任务执行中，可用「注册PLC标签」按钮全量兜底）</returns>
        public static bool EnqueueIfNotRunning(long projectId, string[] changedBlockNames, bool ioTagsRefreshed, string taskId = null)
        {
            if (_inFlightProjects.ContainsKey(projectId))
            {
                return false;
            }

            // 本机专用队列 local_<机器名>：任务语义与本机 dll/网关连通性强相关（见 MachineQueue 说明）
            var client = new BackgroundJobClient();
            client.Create<PLC_TagRegistrationJob>(
                svc => svc.ExecuteAsync(projectId, changedBlockNames, ioTagsRefreshed, taskId),
                new EnqueuedState(MachineQueue.BuildCurrent()));
            return true;
        }

        /// <summary>
        /// Hangfire 后台执行入口。失败时把异常抛回 Hangfire：任务标记 Failed，
        /// 异常详情在 Hangfire 面板可见，用户可在面板上手工重试（同参数重跑本次增量范围）。
        /// [AutomaticRetry(Attempts=0)]：关闭 Hangfire 默认的 10 次自动重试——重试完全由用户手工决定
        /// （面板 Retry 或「注册PLC标签」按钮全量兜底）。
        /// </summary>
        [AutomaticRetry(Attempts = 0)]
        public async Task ExecuteAsync(long projectId, string[] changedBlockNames, bool ioTagsRefreshed, string taskId = null)
        {
            // 进度上报（taskId 为空 = 非 upload 链路调用，不上报）
            void Report(string status, string detail)
            {
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    _progressService.SetPostProcess(taskId, PostProcessKey, PostProcessTitle, status, detail);
                }
            }

            _inFlightProjects.TryAdd(projectId, 0);
            try
            {
                Report("Running", "后台任务执行中");
                var changed = (changedBlockNames ?? [])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct()
                    .ToList();
                if (changed.Count == 0 && !ioTagsRefreshed)
                {
                    _logger.LogInformation("[标签注册-增量] 无变更块且未刷新 IOTag，跳过: ProjectId={ProjectId}", projectId);
                    Report("Skipped", "无变更块且未刷新 IOTag");
                    return;
                }

                var projectEntity = await _proj.GetByIdAsync(projectId).ConfigureAwait(false);
                if (projectEntity == null)
                {
                    _logger.LogWarning("[标签注册-增量] 项目不存在，跳过: ProjectId={ProjectId}", projectId);
                    Report("Skipped", "项目不存在");
                    return;
                }
                if (string.IsNullOrWhiteSpace(projectEntity.IotUrl))
                {
                    _logger.LogWarning("[标签注册-增量] 项目未配置网关地址（IotUrl 为空），跳过: ProjectId={ProjectId}", projectId);
                    Report("Skipped", "项目未配置网关地址（IotUrl 为空）");
                    return;
                }

                var s7 = projectEntity.StationId + "_S7";
                var s7Plus = projectEntity.StationId + "_Plus";
                var iotUrl = projectEntity.IotUrl;

                // 变更块的接口行（与全量按钮同过滤口径：有 Offset 的标量/字符串成员）
                List<PLC_BlockEntity> blockLs = [];
                if (changed.Count > 0)
                {
                    blockLs = await _blockService.GetListAsync(it => it.IsDeleted == false
                        && it.ProjectId == projectId
                        && it.ProgrammingLanguage == "DB"
                        && changed.Contains(it.BlockName)).ConfigureAwait(false);
                }
                var ids = blockLs.Select(it => it.Id).ToList();

                // ===== IGW 项目已改由 PLC_IgwRegistrationJob 批量注册（sub-project B），本任务只服务老网关 =====
                // 上传链路 / 「注册PLC标签」按钮已按 URL 分派，IGW 项目正常不会进入本任务；兜底场景（Hangfire 面板
                // Retry 历史任务、升级前已入队的旧任务）执行时非老网关 URL 一律跳过。按 URL 判定而不探测：
                // IGW 暂时不可达时探测会退化成「老网关」，从而把老网关协议请求发给 IGW。
                if (!DcsIgwConsts.IsLegacyIotGatewayUrl(iotUrl))
                {
                    _logger.LogWarning("[标签注册-增量] 项目网关为 IGW，已改由 IGW 注册任务处理，本任务跳过: ProjectId={ProjectId}", projectId);
                    Report("Skipped", "IGW 项目已改由「IGW 注册」任务处理（可用「注册PLC标签」按钮触发 IGW 全量注册）");
                    return;
                }

                // ===== 老网关路径（原有行为，零改动）=====
                List<PLC_BlockInterfaceEntity> interLs = [];
                if (ids.Count > 0)
                {
                    interLs = await _blockInterfaceService.GetListAsync(it => it.IsDeleted == false
                        && ids.Contains(it.BlockId.Value)
                        && !string.IsNullOrEmpty(it.Offset)
                        && (GlobalStaticVariable.PlcDataTypeList.Any(x => x == it.DataType)
                            || it.DataType.StartsWith("String")
                            || it.DataType.StartsWith("WString"))).ConfigureAwait(false);
                }

                var list = new List<IQTagItem>();
                foreach (var item in blockLs)
                {
                    var ls = interLs.Where(it => it.BlockId == item.Id).ToList();
                    foreach (var v in ls)
                    {
                        var tag = BuildDbTagItem(item, v);
                        if (tag == null) continue;
                        list.Add(tag);
                        if (list.Count == 2000)
                        {
                            await _iotService.ManualAddTagItems(iotUrl, s7, list).ConfigureAwait(false);
                            list.Clear();
                        }
                    }
                    await _iotService.AddGroupTagItems(iotUrl, s7Plus, item.BlockName).ConfigureAwait(false);
                }

                // IOTag 只在本次上传刷新过标签表时注册（全表刷新 → 全量注册 I/Q/M 点）
                if (ioTagsRefreshed)
                {
                    var tagLs = await _ioTagService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId).ConfigureAwait(false);
                    foreach (var item in tagLs)
                    {
                        list.Add(new IQTagItem
                        {
                            ByteSize = 1,
                            DataType = item.DataType,
                            Address = item.Address.Replace("%", ""),
                            TagPathNames = new List<string> { item.TableName, item.TagName }
                        });
                    }
                }

                if (list.Count > 0)
                {
                    var res = await _iotService.ManualAddTagItems(iotUrl, s7, list).ConfigureAwait(false);
                    var gatewayOk = res?.IsSuccess == true;
                    _logger.LogInformation(
                        "[标签注册-增量] 完成: ProjectId={ProjectId}, 变更块 {BlockCount} 个, 接口行 {InterfaceCount} 条, IOTag刷新={IoTagRefreshed}, 网关返回 {GatewayResult}",
                        projectId, blockLs.Count, interLs.Count, ioTagsRefreshed, gatewayOk ? "成功" : "失败:" + res?.ErrorMessage);
                    await WriteLogHelper.WriteLogsAsync(
                        $"[标签注册-增量] 变更块 {blockLs.Count} 个，接口行 {interLs.Count} 条，IOTag刷新={ioTagsRefreshed}，网关返回 {(gatewayOk ? "成功" : "失败:" + res?.ErrorMessage)}",
                        "PLC上传").ConfigureAwait(false);
                    if (!gatewayOk)
                    {
                        // 网关明确返回失败：按失败语义抛出（Hangfire Failed + 前端进度槽 Failed，可手工重试）
                        throw new InvalidOperationException($"网关返回失败: {res?.ErrorMessage ?? "网关无返回"}（变更块 {blockLs.Count} 个，接口行 {interLs.Count} 条）");
                    }
                    Report("Succeeded", $"变更块 {blockLs.Count} 个，接口行 {interLs.Count} 条，IOTag刷新={(ioTagsRefreshed ? "是" : "否")}");
                }
                else
                {
                    _logger.LogInformation(
                        "[标签注册-增量] 无可注册条目（变更块均无带 Offset 的标量接口行）: ProjectId={ProjectId}, 变更块 {BlockCount} 个",
                        projectId, blockLs.Count);
                    Report("Succeeded", "无可注册条目（变更块均无带 Offset 的标量接口行）");
                }
            }
            catch (Exception ex)
            {
                // 2026-09-11 用户拍板：异常抛回 Hangfire——任务标记 Failed（面板可见异常详情），
                // 用户手工重试（Hangfire 面板 Retry 同参重跑，或「注册PLC标签」按钮全量兜底）。
                // 自动重试已由 [AutomaticRetry(Attempts=0)] 关闭。
                _logger.LogError(ex, "[标签注册-增量] 执行失败（任务将标记 Failed，可在 Hangfire 面板手工重试）: ProjectId={ProjectId}", projectId);
                Report("Failed", $"{ex.GetBaseException().Message}（可在 Hangfire 面板重试，或用「注册PLC标签」按钮全量兜底）");
                try
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[标签注册-增量] 执行失败（Hangfire 任务标记 Failed，可手工重试）: {ex.Message}\n根因: {ex.GetBaseException().Message}",
                        "PLC上传").ConfigureAwait(false);
                }
                catch
                {
                    // 日志写入失败不能掩盖原始异常
                }
                throw;
            }
            finally
            {
                _inFlightProjects.TryRemove(projectId, out _);
            }
        }

        /// <summary>
        /// 构造 DB 接口行 → 网关 tag（与 ExecuteBatchRegistrationTag 完全同规则）：
        /// Bool 用完整偏移（DB{n}.DBX字节.位），非 Bool 截到字节（DB{n}.DBW/DBD 字节），
        /// String/WString 按 declared 长度带 ByteSize。返回 null 表示该行无法构造（缺 OffsetByte 等）。
        /// </summary>
        private static IQTagItem? BuildDbTagItem(PLC_BlockEntity item, PLC_BlockInterfaceEntity v)
        {
            var dataType = MapDataType(v.DataType);
            var tag = new IQTagItem
            {
                ByteSize = 0,
                DataType = dataType,
                Address = item.ProgrammingLanguage + item.BlockNumber + "." + v.Offset
            };
            if (tag.DataType != "Bool")
            {
                tag.Address = item.ProgrammingLanguage + item.BlockNumber + "." + v.Offset.Split('.')[0];
                if (tag.DataType is "String" or "WString")
                {
                    if (v.OffsetByte == null) return null;
                    tag.ByteSize = v.OffsetByte.Value;
                    var match = Regex.Match(v.DataType, tag.DataType + @"\[(\d+)\]");
                    tag.ByteSize = match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : 255;
                }
            }
            tag.TagPathNames = new List<string> { item.BlockName, v.VariablePath };
            return tag;
        }

        /// <summary>数据类型映射（与 ExecuteBatchRegistrationTag.getDataType 同规则）。</summary>
        private static string MapDataType(string dataType)
        {
            var lowerStr = dataType.ToLower();
            if (lowerStr.StartsWith("string"))
            {
                return "String";
            }
            if (lowerStr.StartsWith("wstring"))
            {
                return "WString";
            }
            return dataType switch
            {
                "Word" or "UInt" => "UInt16",
                "USInt" => "Byte",
                "DWord" => "UInt32",
                "Real" => "Float",
                "LReal" => "Duoble",
                _ => dataType
            };
        }
    }
}
