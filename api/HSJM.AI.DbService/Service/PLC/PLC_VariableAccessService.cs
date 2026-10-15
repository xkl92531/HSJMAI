
using HSJM.AI.Core;
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4_SqlSugar 添加时间:2026/2/7 15:05:56
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Hosting;
using SqlSugar;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;


namespace HSJM.AI.DbService
{
    public class PLC_VariableAccessService : AbstractBaseService<PLC_VariableAccessEntity>, IPLC_VariableAccessService, IAutoInject
    {
        private readonly IPLC_ProjectService _projectService;
        private readonly RequestToHttpHelper _requestToHttpHelper;
        readonly IIotService _iotService;
        readonly IPLC_BlockService _blockService;
        readonly IPLC_IOTagService _tagService;
        readonly IPLC_BlockInterfaceService _blockInterfaceService;
        /// <summary>IGW 注册台账（task-8 读链按台账拼别名；测试构造可为 null → 恒用空台账回退）</summary>
        private readonly IPLC_IgwRegistrationService? _igwRegistrationService;
        private readonly Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken,
            Task<IReadOnlyList<PLC_IOTagEntity>>> _exactTagCandidateLoader;
        /// <summary>D-1 诊断读取的 IO 点加载器（生产读库；测试构造注入）</summary>
        private readonly Func<long, CancellationToken, Task<IReadOnlyList<PLC_IOTagEntity>>> _diagnosticTagLoader;
        /// <summary>
        /// D-1 fix round 2：项目全量未删除 IO 点加载器（不按 PlcDataTypeList/ConstraintStatus 过滤，
        /// 与注册规划器 <c>PLC_IgwRegistrationJob.cs</c> ~285-288 判定重名时用的集合口径一致）。
        /// 台账不可用时靠它判定诊断点是否与"未参与诊断的同表点"重名，避免只在诊断子集内查漏检碰撞。
        /// </summary>
        private readonly Func<long, CancellationToken, Task<IReadOnlyList<PLC_IOTagEntity>>> _fullIoTagLoader;
        /// <summary>
        /// hardening D14：项目 DB 块名加载器（台账不可用时判定块根组改名风险；生产读库，测试构造注入）。
        /// 生产实现含软删块名（台账会保留已删块的根组行，规划器沿用根组时它们仍可能占用名字），只取名字，大小写重复由判定函数合并。
        /// </summary>
        private readonly Func<long, CancellationToken, Task<IReadOnlyList<string>>> _liveDbBlockLoader;
        /// <summary>hardening D14：指定块的接口变量路径加载器（台账不可用时判定块内点名重名；生产读库，测试构造注入）</summary>
        private readonly Func<long, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<(string Block, string VariablePath)>>> _blockPathLoader;

        /// <summary>台账不可用时因改名风险拒读的错误码（按 Unknown 质量返回，上层状态机保持上一状态）</summary>
        internal const string LedgerOutageRefusedCode = "IgwLedgerUnavailableAmbiguous";
        /// <summary>D-1 IGW 诊断读取器</summary>
        private readonly IgwDiagnosticReader _igwDiagnosticReader;
        /// <summary>GRAPH 工站回退表加载器（默认查 AI_PLC_GraphStepTag；测试可注入，避免无库时 GetDb() 空引用）。</summary>
        private readonly Func<long, CancellationToken, Task<IReadOnlyDictionary<string, string>>> _graphStationLoader;

        public PLC_VariableAccessService(IPLC_ProjectService projectService
            , RequestToHttpHelper requestToHttpHelper
            , IIotService iotService
            , IPLC_IOTagService tagService
            , IPLC_BlockService blockService
            , IPLC_BlockInterfaceService blockInterfaceService
            , IPLC_IgwRegistrationService igwRegistrationService)
        {
            _projectService = projectService;
            _requestToHttpHelper = requestToHttpHelper;
            _iotService = iotService;
            _tagService = tagService;
            _blockService = blockService;
            _blockInterfaceService = blockInterfaceService;
            _igwRegistrationService = igwRegistrationService;
            _exactTagCandidateLoader = LoadExactTagCandidatesAsync;
            _diagnosticTagLoader = LoadDiagnosticTagsAsync;
            _fullIoTagLoader = LoadFullIoTagsAsync;
            _igwDiagnosticReader = new IgwDiagnosticReader(iotService);
            _graphStationLoader = LoadGraphStationFallbackAsync;
            _liveDbBlockLoader = LoadGuardDbBlockNamesAsync;
            _blockPathLoader = LoadBlockVariablePathsAsync;
        }

        internal PLC_VariableAccessService(
            IPLC_ProjectService projectService,
            IIotService iotService,
            Func<long, IReadOnlyCollection<PLC_ExactReadRequest>, CancellationToken,
                Task<IReadOnlyList<PLC_IOTagEntity>>> exactTagCandidateLoader,
            IPLC_IgwRegistrationService? igwRegistrationService = null,
            Func<long, CancellationToken, Task<IReadOnlyList<PLC_IOTagEntity>>>? diagnosticTagLoader = null,
            IgwDiagnosticReader? igwDiagnosticReader = null,
            Func<long, CancellationToken, Task<IReadOnlyList<PLC_IOTagEntity>>>? fullIoTagLoader = null,
            Func<long, CancellationToken, Task<IReadOnlyDictionary<string, string>>>? graphStationLoader = null,
            Func<long, CancellationToken, Task<IReadOnlyList<string>>>? liveDbBlockLoader = null,
            Func<long, IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<(string Block, string VariablePath)>>>? blockPathLoader = null)
        {
            _projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
            _iotService = iotService ?? throw new ArgumentNullException(nameof(iotService));
            _exactTagCandidateLoader = exactTagCandidateLoader
                ?? throw new ArgumentNullException(nameof(exactTagCandidateLoader));
            _requestToHttpHelper = null!;
            _tagService = null!;
            _blockService = null!;
            _blockInterfaceService = null!;
            _igwRegistrationService = igwRegistrationService;
            _diagnosticTagLoader = diagnosticTagLoader
                ?? ((_, _) => Task.FromResult<IReadOnlyList<PLC_IOTagEntity>>(Array.Empty<PLC_IOTagEntity>()));
            _fullIoTagLoader = fullIoTagLoader
                ?? ((_, _) => Task.FromResult<IReadOnlyList<PLC_IOTagEntity>>(Array.Empty<PLC_IOTagEntity>()));
            _igwDiagnosticReader = igwDiagnosticReader ?? new IgwDiagnosticReader(iotService);
            _graphStationLoader = graphStationLoader ?? LoadGraphStationFallbackAsync;
            _liveDbBlockLoader = liveDbBlockLoader
                ?? ((_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
            _blockPathLoader = blockPathLoader
                ?? ((_, _, _) => Task.FromResult<IReadOnlyList<(string Block, string VariablePath)>>(Array.Empty<(string, string)>()));
        }
        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteAsync(Expression<Func<PLC_VariableAccessEntity, bool>> whereExpression)
        {
            var result = false;
            var delDatas = await GetListAsync(whereExpression);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }

        /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdAsync(object id)
        {
            var result = false;
            var delData = GetById(id);
            if (delData != null)
            {
                delData.IsDeleted = true;
                result = await UpdateAsync(delData);
            }
            return result;
        }

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        public async Task<bool> SoftDeleteByIdsAsync(List<long> listId)
        {
            var result = false;
            var delDatas = await GetListByIdsAsync(listId);
            if (delDatas.IsListNullOrEmpty() == false)
            {
                delDatas.ForEach(x => x.IsDeleted = true);
                int rows = await UpdateRangeAsync(delDatas);
                result = rows > 0;
            }
            return result;
        }

        /// <summary>
        /// 根据PLC项目读取实时数据
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public async Task<List<PLC_ReadTagResult>> GetReadData(long projectId)
        {
            return await GetReadData(projectId, null);
        }

        /// <summary>
        /// 根据PLC项目读取指定点位的实时数据
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="tagNames"></param>
        /// <returns></returns>
        public async Task<List<PLC_ReadTagResult>> GetReadData(long projectId, List<string> tagNames)
        {
            var projectEntity = _projectService.GetById(projectId);
            if (projectEntity == null)
            {
                return new List<PLC_ReadTagResult>();
            }

            List<string> tagLs = new List<string>();
            var normalizedTagNames = (tagNames ?? new List<string>())
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Select(it => it.Trim().Trim('"'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var query = _tagService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false
                    && it.ProjectId == projectId
                    && GlobalStaticVariable.PlcDataTypeList.Contains(it.DataType)
                    && it.ConstraintStatus != 6);

            if (normalizedTagNames.Count > 0)
            {
                query = query.Where(it => normalizedTagNames.Contains(it.TagName));
            }

            var ls = await query.ToListAsync();
            if (ls.Count > 0)
            {
                var tagList = ls.Select(it => it.TableName + "." + it.TagName).ToList();
                tagLs.AddRange(tagList);
            }
            var s7 = projectEntity.StationId + "_S7";
            var s7Plus = projectEntity.StationId + "_Plus";

            // P2（2026-10-09 检查清单 #8）：IGW 项目没有老网关 /DriverInfo/ReadTag 接口，原先无条件
            // 走 ReadTags 的结果是被转成空列表（HTTP 失败 → null → 空返回），调用方
            //（LineUpAgentQueryApp.GetPointValuesAsync / AlarmMonitorWorker / 实时读取 API）
            // 持续拿空数据。非老网关改走 D-1 诊断读链的 IGW 分支（注册台账别名 +
            // ReadTagsWithQuality 逐点质量 + 旧 IGW 定位式坏点隔离），映射回本方法
            // PLC_ReadTagResult 契约（行级 IsGood/ErrorMessage，好行带值）；老网关项目保持原路径不变。
            if (!IsLegacyIotGateway(projectEntity.IotUrl))
            {
                return await GetReadDataViaIgwAsync(projectEntity, ls).ConfigureAwait(false);
            }

            if (tagLs.Count > 0)
            {
                var result = await ReadTags(projectEntity.IotUrl, s7, tagLs);
                if (result != null)
                {
                    var goodLs = result.Where(it => it.IsGood == true).ToList();

                    if (result.Exists(it => it.IsGood == false))
                    {
                        var plusTagsLs = result.Where(it => it.IsGood == false).Select(it => it.TagName).ToList();
                        var res = await ReadTags(projectEntity.IotUrl, s7Plus, plusTagsLs);
                        if (res != null)
                        {
                            goodLs.AddRange(res);
                        }
                    }

                    foreach (var item in goodLs)
                    {
                        var ss = ls.FirstOrDefault(x => (x.TableName + "." + x.TagName) == item.TagName);
                        if (ss != null)
                        {
                            item.OldTagName = ss.TagName;
                        }
                        else
                        {
                            item.OldTagName = item.TagName;
                        } 
                    }

                    return goodLs;
                }
            }

            return new List<PLC_ReadTagResult>();
        }

        /// <summary>
        /// GetReadData 的 IGW 分支（2026-10-09 检查清单 #8）：复用 D-1 诊断读链
        /// <see cref="ReadDiagnosticViaIgwAsync"/>（注册台账别名 + 逐点质量 + 坏点隔离），
        /// 把 <see cref="PLC_DiagnosticSignal"/> 映射回 GetReadData 的 PLC_ReadTagResult 契约
        ///（TagName＝TableName.TagName 去 IQ.、OldTagName＝纯点名、IsGood＝Status==Good）。
        /// 全部行（好+坏）都返回，调用方按行级 IsGood/ErrorMessage 区分读取失败与没有数据——
        /// 比原先静默转空列表的信息量大；读取链内部异常才返回空（与老网关路径同容错语义）。
        /// </summary>
        private async Task<List<PLC_ReadTagResult>> GetReadDataViaIgwAsync(
            PLC_ProjectEntity project, List<PLC_IOTagEntity> tags)
        {
            var points = tags
                .Select(t => new DiagnosticPoint(t, DiagnosticTagKey(t.TableName, t.TagName), DiagnosticRequestName(t.TableName, t.TagName)))
                .GroupBy(p => p.Key, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (points.Count == 0)
            {
                return new List<PLC_ReadTagResult>();
            }
            if (string.IsNullOrWhiteSpace(project.PlcIp))
            {
                return points.Select(p => new PLC_ReadTagResult
                {
                    TagName = p.Key,
                    OldTagName = p.Tag.TagName,
                    IsGood = false,
                    ErrorMessage = "项目未配置 PLC IP，无法生成 IGW 读别名",
                }).ToList();
            }
            try
            {
                var signals = await ReadDiagnosticViaIgwAsync(project, points, CancellationToken.None)
                    .ConfigureAwait(false);
                return signals.Select(s => new PLC_ReadTagResult
                {
                    TagName = s.TagName,
                    OldTagName = string.IsNullOrEmpty(s.OldTagName) ? s.TagName : s.OldTagName,
                    Value = s.Status == PLC_SignalReadStatus.Good ? s.Value : null,
                    IsGood = s.Status == PLC_SignalReadStatus.Good,
                    ErrorMessage = s.Status == PLC_SignalReadStatus.Good ? null : s.Detail,
                }).ToList();
            }
            catch (Exception ex)
            {
                // 与老网关路径同样的容错语义：读取链异常按空结果处理（行级坏值已由上面的映射给出）
                return points.Select(p => new PLC_ReadTagResult
                {
                    TagName = p.Key,
                    OldTagName = p.Tag.TagName,
                    IsGood = false,
                    ErrorMessage = "IGW 读取异常: " + ex.GetType().Name + ": " + ex.Message,
                }).ToList();
            }
        }

        // ---------- D-1：约束诊断专用读取（逐点质量；不改 GetReadData 契约） ----------

        private sealed record DiagnosticPoint(PLC_IOTagEntity Tag, string Key, string RequestName);

        private sealed record LegacyDeviceRead(bool IsSuccess, string? Error, Dictionary<string, PLC_DiagnosticSignal> Rows);

        /// <summary>诊断点键：TableName.TagName 去掉 "IQ."（与 GetReadData / 私有 ReadTags 的 TagName 回填逐字一致）。</summary>
        internal static string DiagnosticTagKey(string? tableName, string tagName) =>
            DiagnosticRequestName(tableName, tagName).Replace("IQ.", "");

        /// <summary>老网关请求名：TableName.TagName（同 GetReadData）。</summary>
        private static string DiagnosticRequestName(string? tableName, string tagName) => tableName + "." + tagName;

        public async Task<PLC_DiagnosticReadResult> ReadDiagnosticSignalsAsync(long projectId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PLC_ProjectEntity? project;
            try
            {
                project = _projectService.GetById(projectId);
            }
            catch (Exception ex)
            {
                return DiagnosticUnavailable(projectId, "读取 PLC 项目失败: " + ex.Message);
            }
            if (project is null || project.IsDeleted)
            {
                return DiagnosticUnavailable(projectId, "PLC 项目不存在或已删除");
            }
            if (string.IsNullOrWhiteSpace(project.IotUrl))
            {
                return DiagnosticUnavailable(projectId, "PLC 项目未配置网关地址（IotUrl）");
            }

            IReadOnlyList<PLC_IOTagEntity> tags;
            try
            {
                tags = await _diagnosticTagLoader(projectId, cancellationToken).WaitAsync(cancellationToken)
                    ?? Array.Empty<PLC_IOTagEntity>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return DiagnosticUnavailable(projectId, "读取 IO 点表失败: " + ex.Message);
            }

            var points = tags
                .Where(tag => tag is not null && !string.IsNullOrWhiteSpace(tag.TagName))
                .Select(tag => new DiagnosticPoint(tag, DiagnosticTagKey(tag.TableName, tag.TagName),
                    DiagnosticRequestName(tag.TableName, tag.TagName)))
                .GroupBy(point => point.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (points.Count == 0)
            {
                return new PLC_DiagnosticReadResult { ProjectId = projectId, ProjectStatus = PLC_DiagnosticProjectStatus.Ok };
            }

            var legacy = IsLegacyIotGateway(project.IotUrl);
            if (legacy && string.IsNullOrWhiteSpace(project.StationId))
            {
                return DiagnosticUnavailable(projectId, "老网关项目未配置 StationId，无法确定设备名",
                    points, PLC_SignalReadStatus.CommFailure);
            }
            if (!legacy && string.IsNullOrWhiteSpace(project.PlcIp))
            {
                return DiagnosticUnavailable(projectId, "项目未配置 PLC IP，无法生成 IGW 读别名",
                    points, PLC_SignalReadStatus.NotRegistered);
            }

            try
            {
                var signals = legacy
                    ? await ReadDiagnosticViaLegacyAsync(project, points, cancellationToken)
                    : await ReadDiagnosticViaIgwAsync(project, points, cancellationToken);
                return BuildDiagnosticResult(projectId, signals);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = "实时信号读取异常: " + ex.GetType().Name + ": " + ex.Message;
                return new PLC_DiagnosticReadResult
                {
                    ProjectId = projectId,
                    ProjectStatus = PLC_DiagnosticProjectStatus.CommFailure,
                    ProjectError = error,
                    Signals = points.Select(p => DiagnosticBad(p, PLC_SignalReadStatus.CommFailure, error)).ToList(),
                };
            }
        }

        private async Task<IReadOnlyList<PLC_IOTagEntity>> LoadDiagnosticTagsAsync(long projectId, CancellationToken cancellationToken)
        {
            return await _tagService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false
                    && it.ProjectId == projectId
                    && GlobalStaticVariable.PlcDataTypeList.Contains(it.DataType)
                    && it.ConstraintStatus != 6)
                .OrderBy(it => it.Id)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// D-1 fix round 2：项目全量未删除 IO 点（不按 PlcDataTypeList/ConstraintStatus 过滤），
        /// 与 <c>PLC_IgwRegistrationJob.cs</c> ~285-288 规划器判定重名时读取的集合口径逐字一致——
        /// 只在这个集合上找重名，才不会漏掉"诊断点与未参与诊断的同表点重名"的情形。
        /// </summary>
        /// <remarks>只投影判定所需的 Id / TableName / TagName（行集合不变；D-1 诊断读链与精确读链的重名判定只用这三列）。</remarks>
        private async Task<IReadOnlyList<PLC_IOTagEntity>> LoadFullIoTagsAsync(long projectId, CancellationToken cancellationToken)
        {
            return await _tagService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && it.ProjectId == projectId)
                .OrderBy(it => it.Id)
                .Select(it => new PLC_IOTagEntity { Id = it.Id, TableName = it.TableName, TagName = it.TagName })
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// hardening D14：项目 DB 块名（块口径与注册任务 DbStore.LoadDataAsync 一致：ProgrammingLanguage == "DB"），
        /// 含软删块（台账可能仍记着已删块的根组，规划器沿用时会占用名字，使存活块被改名为 _n），只取名字、去重。
        /// </summary>
        private async Task<IReadOnlyList<string>> LoadGuardDbBlockNamesAsync(long projectId, CancellationToken cancellationToken)
        {
            long? projectIdNullable = projectId;
            var names = await _blockService.GetDb().AsQueryable()
                .Where(blk => blk.ProjectId == projectIdNullable && blk.ProgrammingLanguage == "DB")
                .Select(blk => blk.BlockName)
                .Distinct()
                .ToListAsync(cancellationToken);
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }

        /// <summary>hardening D14：指定块（按名）的未删除接口变量路径（只投影两列）</summary>
        private async Task<IReadOnlyList<(string Block, string VariablePath)>> LoadBlockVariablePathsAsync(
            long projectId, IReadOnlyCollection<string> blockNames, CancellationToken cancellationToken)
        {
            long? projectIdNullable = projectId;
            var names = blockNames.ToList();
            var blocks = await _blockService.GetDb().AsQueryable()
                .Where(blk => blk.IsDeleted == false && blk.ProjectId == projectIdNullable && names.Contains(blk.BlockName))
                .ToListAsync(cancellationToken);
            var nameById = blocks.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().BlockName);
            if (nameById.Count == 0)
            {
                return Array.Empty<(string, string)>();
            }
            var ids = nameById.Keys.ToList();
            var rows = await _blockInterfaceService.GetDb().AsQueryable()
                .Where(it => it.IsDeleted == false && ids.Contains(it.BlockId!.Value) && !string.IsNullOrEmpty(it.VariablePath))
                .Select(it => new PLC_BlockInterfaceEntity { BlockId = it.BlockId, VariablePath = it.VariablePath })
                .ToListAsync(cancellationToken);
            return rows
                .Where(r => r.BlockId.HasValue && nameById.ContainsKey(r.BlockId.Value))
                .Select(r => (nameById[r.BlockId!.Value], r.VariablePath))
                .ToList();
        }

        /// <summary>IGW：IO 点按台账别名（IgwReadAliasResolver.ResolveIo，含改名映射）逐点带质量读取。</summary>
        private async Task<List<PLC_DiagnosticSignal>> ReadDiagnosticViaIgwAsync(
            PLC_ProjectEntity project, List<DiagnosticPoint> points, CancellationToken cancellationToken)
        {
            var ip = project.PlcIp!.Trim();
            var context = await GetIgwReadContextAsync(project.Id, cancellationToken);

            // D-1 fix round：台账加载失败时用空台账回退，没有改名映射。若同表内存在清洗后点名冲突
            // （真实注册时规划器会把后出现者加后缀改名，如 flag → flag_2），空台账下两个点会解析到
            // 同一个 IGW 别名——读到的是其中一个点的真值，却可能被当成另一个点报告 Good，是张冠李戴。
            // 这类点无法确定应读哪个别名，直接拒读，不产出可能读错点位的 Good。
            //
            // D-1 fix round 2：重名判定不能只在"本次诊断要读的点"里找——规划器是在项目全量 IO 点
            // （见 LoadFullIoTagsAsync）上判定重名的，诊断点可能与一个未参与本次诊断的同表点重名
            // （如 Word 点 I1.0 与 Bool 点 I1_0 清洗后同名，只有前者在诊断子集里）。台账不可用时改
            // 为加载全量集合来判定；全量集合本身也加载不到时不猜——把本次全部诊断点直接拒读。
            Dictionary<string, List<PLC_IOTagEntity>>? collidingGroups = null;
            if (context.LedgerUnavailable)
            {
                IReadOnlyList<PLC_IOTagEntity>? allTags;
                try
                {
                    allTags = await _fullIoTagLoader(project.Id, cancellationToken).WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = "注册台账暂不可用，且项目 IO 点全量校验失败，无法确认是否存在重名: "
                        + ex.GetType().Name + ": " + ex.Message;
                    return points.Select(p => DiagnosticBad(p, PLC_SignalReadStatus.DeviceError, error)).ToList();
                }
                collidingGroups = FindCollidingIoTagGroups(allTags ?? Array.Empty<PLC_IOTagEntity>());
            }

            var aliasByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var collisionDetailByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var point in points)
            {
                if (collidingGroups != null)
                {
                    var sanitizedTable = IgwTagNaming.SanitizeName(point.Tag.TableName);
                    var sanitizedName = IgwTagNaming.SanitizeName(point.Tag.TagName);
                    if (sanitizedTable != null && sanitizedName != null
                        && collidingGroups.TryGetValue(sanitizedTable + IgwTagNaming.KeySeparator + sanitizedName, out var group))
                    {
                        var others = group.Where(t => t.Id != point.Tag.Id).ToList();
                        var detail = "注册台账暂不可用，重名点位无法确定读取名";
                        if (others.Count > 0)
                        {
                            detail += "，与 " + string.Join("、", others.Select(FormatIoTagRef)) + " 冲突";
                        }
                        collisionDetailByKey[point.Key] = detail;
                        continue;
                    }
                }
                if (string.IsNullOrWhiteSpace(point.Tag.TableName))
                {
                    continue;
                }
                aliasByKey[point.Key] = IgwReadAliasResolver
                    .ResolveIo(ip, context.Snapshot, point.Tag.TableName, point.Tag.TagName)
                    .Trim();
            }

            // D-1 最终评审 I-2：台账可用但已过期（如注册后新增了一个仅大小写不同的同表点，台账里没有它的改名映射）
            // 时，两个 IO 点仍可能解析到同一个 IGW 别名（IGW 别名查找忽略大小写）——共用一次读数就是张冠李戴。
            // 这类点全部不读，标 DeviceError 提示重新注册。
            foreach (var shared in aliasByKey
                .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList())
            {
                foreach (var kv in shared)
                {
                    aliasByKey.Remove(kv.Key);
                    collisionDetailByKey[kv.Key] = SharedIgwAliasDetail;
                }
            }

            var readings = await _igwDiagnosticReader.ReadAsync(project.IotUrl.Trim(),
                aliasByKey.Values.Distinct(StringComparer.Ordinal).ToList(), cancellationToken);

            var signals = new List<PLC_DiagnosticSignal>(points.Count);
            foreach (var point in points)
            {
                if (collisionDetailByKey.TryGetValue(point.Key, out var collisionDetail))
                {
                    signals.Add(DiagnosticBad(point, PLC_SignalReadStatus.DeviceError, collisionDetail));
                    continue;
                }
                if (!aliasByKey.TryGetValue(point.Key, out var alias))
                {
                    signals.Add(DiagnosticBad(point, PLC_SignalReadStatus.NotRegistered, "IO 点缺少变量表名，无法生成 IGW 读别名"));
                    continue;
                }
                if (!readings.TryGetValue(alias, out var reading))
                {
                    signals.Add(DiagnosticBad(point, PLC_SignalReadStatus.Missing, "IGW 未返回该点（别名 " + alias + "）"));
                    continue;
                }
                signals.Add(reading.Status == PLC_SignalReadStatus.Good
                    ? DiagnosticGood(point, reading.Value, reading.TimestampUtc)
                    : new PLC_DiagnosticSignal
                    {
                        TagName = point.Key,
                        OldTagName = point.Tag.TagName,
                        Status = reading.Status,
                        Detail = reading.Detail + "（别名 " + alias + "）",
                        TimestampUtc = reading.TimestampUtc,
                    });
            }
            return signals;
        }

        /// <summary>D-1 最终评审 I-2：多个 IO 点解析到同一 IGW 别名时的点级原因。</summary>
        internal const string SharedIgwAliasDetail = "多个 IO 点解析到同一 IGW 别名（台账可能过期），请重新注册PLC标签";

        /// <summary>
        /// D-1 fix round 2：按注册规划器同一套清洗规则（<see cref="IgwTagNaming.SanitizeName"/>）与同一分组
        /// 口径（IgwRegistrationPlanner.BuildIoItem 的 GroupPath=[IO 根组, 清洗后表名]）在给定 IO 点集合上
        /// 找出重名分组：清洗后表名相同 + 清洗后点名相同（大小写不敏感）的 ≥2 个点。返回值按
        /// "清洗后表名 + 分隔符 + 清洗后点名"（大小写不敏感比较）分组，值是该分组内的原始 IO 点（含点自身，供调用方
        /// 排除后报出"与谁冲突"）。真实注册时这些点会被规划器加后缀改名（DedupeNames），但当前没有台账给出
        /// 改名映射，两个点会解析到同一个别名，无法确定读取名。清洗不出合法名字的点（本身就不可注册）不参与判定。
        /// </summary>
        private static Dictionary<string, List<PLC_IOTagEntity>> FindCollidingIoTagGroups(IEnumerable<PLC_IOTagEntity> allTags)
        {
            return IgwLedgerOutageGuard.FindCollidingIoGroups(allTags, t => t.TableName, t => t.TagName);
        }

        /// <summary>诊断冲突信息里引用另一个 IO 点用的展示名：TableName.TagName（未清洗，供运维核对原始点名）。</summary>
        private static string FormatIoTagRef(PLC_IOTagEntity tag) => (tag.TableName ?? string.Empty) + "." + tag.TagName;

        /// <summary>老网关：_S7 → _Plus 两段读取（链路同 GetReadData），坏行不带值。</summary>
        private async Task<List<PLC_DiagnosticSignal>> ReadDiagnosticViaLegacyAsync(
            PLC_ProjectEntity project, List<DiagnosticPoint> points, CancellationToken cancellationToken)
        {
            var iotUrl = project.IotUrl.Trim();
            var station = project.StationId.Trim();
            var s7 = await ReadLegacyDiagnosticDeviceAsync(iotUrl, station + "_S7", points, cancellationToken);
            if (!s7.IsSuccess)
            {
                var error = "老网关读取失败（" + station + "_S7）: " + s7.Error;
                return points.Select(p => DiagnosticBad(p, PLC_SignalReadStatus.CommFailure, error)).ToList();
            }

            var merged = new Dictionary<string, PLC_DiagnosticSignal>(s7.Rows, StringComparer.Ordinal);
            var retry = points.Where(p => merged[p.Key].Status != PLC_SignalReadStatus.Good).ToList();
            if (retry.Count > 0)
            {
                var plus = await ReadLegacyDiagnosticDeviceAsync(iotUrl, station + "_Plus", retry, cancellationToken);
                if (plus.IsSuccess)
                {
                    foreach (var point in retry)
                    {
                        merged[point.Key] = PickLegacyDiagnostic(merged[point.Key], plus.Rows[point.Key]);
                    }
                }
                else
                {
                    // D-1 fix round：_Plus 请求本身在 HTTP 层失败（连接失败/异常/网关无返回）时，_S7 已有的
                    // 坏行状态原样保留（不因 _Plus 兜底也没跑起来而升级/降级状态），但把失败原因追加到 Detail，
                    // 避免运维只看到 _S7 报文、以为已经用 _Plus 复核过却其实没有。
                    var note = "; " + station + "_Plus 读取失败: " + plus.Error;
                    foreach (var point in retry)
                    {
                        merged[point.Key].Detail += note;
                    }
                }
            }
            return points.Select(p => merged[p.Key]).ToList();
        }

        private async Task<LegacyDeviceRead> ReadLegacyDiagnosticDeviceAsync(
            string iotUrl, string deviceName, List<DiagnosticPoint> points, CancellationToken cancellationToken)
        {
            HttpResponseResultModel<List<PLC_ReadTagResult>>? response;
            try
            {
                response = await _iotService.ReadTags(iotUrl, new PLC_ReadTagParame
                {
                    DeviceName = deviceName,
                    TagList = points.Select(p => p.RequestName).ToList(),
                }).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new LegacyDeviceRead(false, ex.GetType().Name + ": " + ex.Message,
                    new Dictionary<string, PLC_DiagnosticSignal>(StringComparer.Ordinal));
            }
            if (response is not { IsSuccess: true } || response.BackResult is null)
            {
                return new LegacyDeviceRead(false, response?.ErrorMessage ?? "网关无返回",
                    new Dictionary<string, PLC_DiagnosticSignal>(StringComparer.Ordinal));
            }

            var rows = new Dictionary<string, PLC_DiagnosticSignal>(StringComparer.Ordinal);
            for (var i = 0; i < points.Count; i++)
            {
                // 老网关不回显 tagName：按请求索引对位（与 GetReadData 的私有 ReadTags 同口径）
                var row = i < response.BackResult.Count ? response.BackResult[i] : null;
                rows[points[i].Key] = row is null
                    ? DiagnosticBad(points[i], PLC_SignalReadStatus.Missing, "老网关 " + deviceName + " 未返回该点")
                    : row.IsGood
                        ? DiagnosticGood(points[i], row.Value, null)
                        : DiagnosticBad(points[i], PLC_SignalReadStatus.BadQuality,
                            deviceName + ": " + (string.IsNullOrWhiteSpace(row.ErrorMessage) ? "质量异常" : row.ErrorMessage));
            }
            return new LegacyDeviceRead(true, null, rows);
        }

        /// <summary>两段取舍：任一段 Good 用 Good；否则 BadQuality 优先于 Missing（先 _S7 后 _Plus）。</summary>
        private static PLC_DiagnosticSignal PickLegacyDiagnostic(PLC_DiagnosticSignal s7, PLC_DiagnosticSignal plus)
        {
            if (s7.Status == PLC_SignalReadStatus.Good) return s7;
            if (plus.Status == PLC_SignalReadStatus.Good) return plus;
            if (s7.Status == PLC_SignalReadStatus.BadQuality) return s7;
            return plus.Status == PLC_SignalReadStatus.BadQuality ? plus : s7;
        }

        private static PLC_DiagnosticReadResult BuildDiagnosticResult(long projectId, List<PLC_DiagnosticSignal> signals)
        {
            var result = new PLC_DiagnosticReadResult
            {
                ProjectId = projectId,
                ProjectStatus = PLC_DiagnosticProjectStatus.Ok,
                Signals = signals,
            };
            if (signals.Count > 0 && signals.All(s => s.Status is PLC_SignalReadStatus.CommFailure or PLC_SignalReadStatus.Timeout))
            {
                result.ProjectStatus = PLC_DiagnosticProjectStatus.CommFailure;
                result.ProjectError = signals[0].Detail;
            }
            return result;
        }

        private static PLC_DiagnosticReadResult DiagnosticUnavailable(long projectId, string error,
            IEnumerable<DiagnosticPoint>? points = null, PLC_SignalReadStatus pointStatus = PLC_SignalReadStatus.NotRegistered) =>
            new()
            {
                ProjectId = projectId,
                ProjectStatus = PLC_DiagnosticProjectStatus.ProjectUnavailable,
                ProjectError = error,
                Signals = (points ?? Enumerable.Empty<DiagnosticPoint>()).Select(p => DiagnosticBad(p, pointStatus, error)).ToList(),
            };

        private static PLC_DiagnosticSignal DiagnosticBad(DiagnosticPoint point, PLC_SignalReadStatus status, string? detail) =>
            new() { TagName = point.Key, OldTagName = point.Tag.TagName, Status = status, Detail = detail };

        private static PLC_DiagnosticSignal DiagnosticGood(DiagnosticPoint point, object? value, DateTime? timestampUtc) =>
            new()
            {
                TagName = point.Key,
                OldTagName = point.Tag.TagName,
                Value = value,
                Status = PLC_SignalReadStatus.Good,
                TimestampUtc = timestampUtc,
            };

        /// <summary>
        /// 按完整 BlockName.VariablePath 精确读取实时数据。
        /// </summary>
        /// <summary>PLC 工程重传后失效候选回退缓存（块/接口表已重写；另有 10 分钟 TTL 兜底）。</summary>
        public void InvalidateExactCandidateCache(long projectId)
        {
            if (projectId > 0)
            {
                ExactCandidateCaches.TryRemove(projectId, out _);
            }
        }

        public async Task<IReadOnlyList<PLC_ExactReadResult>> ReadExactDataAsync(
            long projectId,
            IReadOnlyCollection<PLC_ExactReadRequest> requests,
            CancellationToken cancellationToken,
            bool keepPolling = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slots = NormalizeExactRequests(requests);
            if (slots.Count == 0)
            {
                return [];
            }

            var results = new Dictionary<string, PLC_ExactReadResult>(StringComparer.Ordinal);
            var validRequests = new List<NormalizedExactRequest>();
            foreach (var slot in slots)
            {
                if (slot.ErrorCode is not null)
                {
                    results[slot.RequestKey] = ExactResult(slot, null,
                        PLC_ExactReadQuality.Unknown, slot.ErrorCode);
                }
                else
                {
                    validRequests.Add(slot);
                }
            }

            if (validRequests.Count == 0)
            {
                return OrderedExactResults(slots, results);
            }

            PLC_ProjectEntity? project;
            try
            {
                project = _projectService.GetById(projectId);
            }
            catch
            {
                FillExactFailures(validRequests, results, "PlcExactProjectReadFailed");
                return OrderedExactResults(slots, results);
            }

            if (project is null || project.IsDeleted || project.Id != projectId)
            {
                FillExactFailures(validRequests, results, "PlcExactProjectMissing");
                return OrderedExactResults(slots, results);
            }

            if (string.IsNullOrWhiteSpace(project.StationId) ||
                string.IsNullOrWhiteSpace(project.IotUrl))
            {
                FillExactFailures(validRequests, results, "PlcExactProjectUnavailable");
                return OrderedExactResults(slots, results);
            }

            IReadOnlyList<PLC_IOTagEntity> candidates;
            try
            {
                candidates = await _exactTagCandidateLoader(
                        projectId,
                        validRequests.Select(x => x.Request).ToArray(),
                        cancellationToken)
                    .WaitAsync(cancellationToken) ?? [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ExactCandidates] 候选加载失败 project=" + projectId + " -> " + ex.GetType().Name + ": " + ex.Message);
                FillExactFailures(validRequests, results, "PlcExactMetadataReadFailed");
                return OrderedExactResults(slots, results);
            }

            var activeCandidates = candidates
                .Where(candidate => IsExactTagCandidate(candidate, projectId))
                .ToArray();
            var readable = new List<NormalizedExactRequest>();
            // task-8：规范路径 → 读目标（块/表名、变量路径、是否 IO 表点位），供 IGW 读链按台账拼别名
            var readTargets = new Dictionary<string, IgwExactReadTarget>(StringComparer.Ordinal);
            foreach (var request in validRequests)
            {
                var matches = activeCandidates.Where(candidate =>
                        string.Equals(candidate.TableName, request.BlockName, StringComparison.Ordinal) &&
                        string.Equals(candidate.TagName, request.VariablePath, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length == 0)
                {
                    results[request.RequestKey] = ExactResult(request, null,
                        PLC_ExactReadQuality.Unknown, "PlcExactTagMissing");
                }
                else if (matches.Length > 1)
                {
                    results[request.RequestKey] = ExactResult(request, null,
                        PLC_ExactReadQuality.Unknown, "PlcExactTagAmbiguous");
                }
                else
                {
                    readable.Add(request);
                    readTargets.TryAdd(request.CanonicalPath, new IgwExactReadTarget(
                        request.BlockName,
                        request.VariablePath,
                        IsIoTag: !IsSynthesizedCandidate(matches[0])));
                }
            }

            if (readable.Count == 0)
            {
                return OrderedExactResults(slots, results);
            }

            var paths = readable
                .Select(x => x.CanonicalPath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            IReadOnlyDictionary<string, ExactTransportValue> values;
            try
            {
                values = await ReadExactTransportAsync(
                    projectId,
                    project.IotUrl.Trim(),
                    project.StationId.Trim(),
                    project.PlcIp?.Trim(),
                    paths,
                    readTargets,
                    cancellationToken,
                    keepPolling);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                values = paths.ToDictionary(
                    path => path,
                    _ => new ExactTransportValue(null, PLC_ExactReadQuality.Unknown,
                        "PlcExactReadFailed"),
                    StringComparer.Ordinal);
            }

            foreach (var request in readable)
            {
                var value = values[request.CanonicalPath];
                results[request.RequestKey] = ExactResult(
                    request,
                    value.Value,
                    value.Quality,
                    value.ErrorCode,
                    value.SourceTimestamp);
            }

            return OrderedExactResults(slots, results);
        }

        private async Task<IReadOnlyList<PLC_IOTagEntity>> LoadExactTagCandidatesAsync(
            long projectId,
            IReadOnlyCollection<PLC_ExactReadRequest> requests,
            CancellationToken cancellationToken)
        {
            if (requests.Count == 0)
            {
                return [];
            }

            // 2026-09-23：不再把每个 (表名, 点名) 拼成一长串 OR 交给 SqlSugar——它翻译 OR 链的耗时约按点数
            // 三次方增长（实测不连库只生成 SQL：200 点 0.9 s / 400 点 10.6 s / 800 点 103 s），停机诊断一个工位
            // 读几百个点，GraphActiveCheck 因此要 30~40 s。改为两个 IN 列表取超集，再在内存按配对精确过滤。
            var pairFilter = BuildExactTagPairFilter(requests);
            var candidates = (await _tagService.GetDb().AsQueryable()
                    .Where(tag => tag.IsDeleted == false &&
                        tag.ProjectId == projectId &&
                        (tag.ConstraintStatus == null || tag.ConstraintStatus != 6) &&
                        GlobalStaticVariable.PlcDataTypeList.Contains(tag.DataType))
                    .Where(tag => pairFilter.Blocks.Contains(tag.TableName) && pairFilter.Paths.Contains(tag.TagName))
                    .ToListAsync(cancellationToken))
                .Where(pairFilter.Matches)
                .ToList();

            // 候选回退（2026-09-10 监控开启修复）：GRAPH 实例 DB 的运行时符号（如
            // DB161_HomeGraph.RT_DATA.EXEC_BITS.EXEC_BITS[0]）只登记在程序块接口表
            // （AI_PLC_BlockInterface），不在 IO tag 表——iotag 未命中的请求对回退查块接口，
            // 命中即视为可读符号（transport 按 BlockName.VariablePath 符号式路径读值，不消费候选地址）。
            var missing = requests
                .Select(request => new
                {
                    Block = request?.BlockName?.Trim() ?? string.Empty,
                    Path = request?.VariablePath?.Trim() ?? string.Empty,
                })
                .Where(pair => pair.Block.Length > 0 && pair.Path.Length > 0)
                .Where(pair => !candidates.Any(tag =>
                    string.Equals(tag.TableName, pair.Block, StringComparison.Ordinal) &&
                    string.Equals(tag.TagName, pair.Path, StringComparison.Ordinal)))
                // 2026-09-11 修复：plan 中同一 (Block, Path) 被多个转移条件重复引用时（FB261 的
                // CYL06/CYL16 各被引用 2 次），missing 未去重会把同一合成候选 Add 两遍 →
                // 下游 matches.Length>1 误判 PlcExactTagAmbiguous → 值 Unknown → 快照 Degraded
                // → 监控树上整绑定"异常"。匿名类型自带值相等语义，Distinct 即可去重。
                .Distinct()
                .ToArray();
            if (missing.Length == 0)
            {
                return candidates;
            }

            // 2026-09-14 回退缓存 + 路径过滤：LineUp 监控每秒读值使回退每轮重跑，旧实现按块 ID
            // 整块拉取接口行（实测单绑定可达 11.3 万行/秒，Sync 类大块单块 2.6 万行；24 绑定合计
            // ~36 万行/秒）。现 (块,路径)→可读性/DataType 按项目缓存（含负缓存：接口表无此行或
            // 类型不支持），未缓存的部分用 blockid + variablepath 双 IN 精确查询——全库 variablepath
            // 无空白填充（实测 padded=0/7,271,839），与原 Trim 内存比对等价。
            // 接口表仅在 PLC 工程重传时变化：上传完成钩子调 InvalidateExactCandidateCache 主动失效，
            // 10 分钟 TTL 兜底。
            var projectCache = GetExactCandidateProjectCache(projectId);
            var unresolved = new List<(string Block, string Path)>();
            foreach (var pair in missing)
            {
                if (projectCache.Resolutions.TryGetValue(CacheKey(pair.Block, pair.Path), out var dataType))
                {
                    if (dataType is not null)
                    {
                        candidates.Add(SynthesizeCandidate(projectId, pair.Block, pair.Path, dataType));
                    }
                    continue;
                }
                unresolved.Add((pair.Block, pair.Path));
            }
            if (unresolved.Count == 0)
            {
                return candidates;
            }

            var blockIdByName = await LoadExactCandidateBlockIdsAsync(
                projectId, unresolved, projectCache, cancellationToken);
            // 注：不用 InnerJoin——bi.BlockId(long?) == blk.Id(long) 会触发 op_Implicit 隐式转换，
            // SqlSugar 拉姆达解析不支持（实测 SqlSugarException）。拆两步查询后在内存配对。
            var allBlockIds = blockIdByName.Values
                .SelectMany(ids => ids)
                .Distinct()
                .Select(id => (long?)id)
                .ToList();
            var unresolvedPaths = unresolved
                .Select(pair => pair.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var interfaceRows = allBlockIds.Count > 0
                ? await _blockInterfaceService.GetDb().AsQueryable()
                    .Where(bi => bi.IsDeleted == false &&
                        allBlockIds.Contains(bi.BlockId) &&
                        unresolvedPaths.Contains(bi.VariablePath))
                    .ToListAsync(cancellationToken)
                : [];
            var idToName = blockIdByName
                .SelectMany(entry => entry.Value.Select(id => (Id: id, Name: entry.Key)))
                .ToDictionary(item => item.Id, item => item.Name);
            foreach (var pair in unresolved)
            {
                var matched = interfaceRows.FirstOrDefault(row =>
                    row.BlockId is long blockId &&
                    idToName.TryGetValue(blockId, out var bn) &&
                    string.Equals(bn, pair.Block, StringComparison.Ordinal) &&
                    string.Equals(row.VariablePath?.Trim(), pair.Path, StringComparison.Ordinal) &&
                    GlobalStaticVariable.PlcDataTypeList.Contains(row.DataType));
                projectCache.Resolutions[CacheKey(pair.Block, pair.Path)] = matched?.DataType;
                if (matched is null)
                {
                    continue;
                }

                candidates.Add(SynthesizeCandidate(projectId, pair.Block, pair.Path, matched.DataType));
            }

            return candidates;
        }

        /// <summary>
        /// 候选回退缓存（静态跨实例共享，本服务为注入实例——与 IotService 网关探测缓存同模式）。
        /// Resolutions：key=块名+\n+路径，value=DataType（可读）或 null（负缓存：无接口行/类型不支持）；
        /// BlockIdsByName：块名→块 ID 集合（空集=该项目无此块名的块，同样属负缓存）。
        /// </summary>
        private static readonly ConcurrentDictionary<long, ExactCandidateProjectCache> ExactCandidateCaches = new();

        private static readonly TimeSpan ExactCandidateCacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// 2026-09-15 IGW 工步数组「最后 Good」缓存（静态跨实例共享）：IGW 对 PLC2 的批读有 ~2%
        /// 行级「点位质量异常」抖动（压测 28 DB×30s：840 请求失败 15 次，全部集中在 10.11.38.20，
        /// 均匀散布各 DB——S7 连接瞬态 busy）。任一轮抖动即令快照 Degraded → 前端告警条闪现
        /// （isStaleOrOffline 对 offline/degraded 均触发）。抖动轮复用 5s 内最近一次 Good 数组值，
        /// 抹平瞬态；PLC 真断链超 TTL 后缓存过期 → Unknown → 照常 Degraded/SignalBad，不掩盖故障。
        /// key=IGW 地址 + 换行 + 数组别名（含 ip+db；task-8 起不同网关地址分开缓存），value=(取值时刻, 展开后的位数组)。
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IgwLastGoodBitsEntry> IgwLastGoodBits = new(StringComparer.Ordinal);

        private static readonly TimeSpan IgwLastGoodBitsTtl = TimeSpan.FromSeconds(5);

        private sealed record IgwLastGoodBitsEntry(DateTimeOffset FetchedAtUtc, List<object> Bits);

        private sealed class ExactCandidateProjectCache
        {
            public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

            public ConcurrentDictionary<string, string?> Resolutions { get; } = new(StringComparer.Ordinal);

            public ConcurrentDictionary<string, List<long>> BlockIdsByName { get; } = new(StringComparer.Ordinal);
        }

        private static ExactCandidateProjectCache GetExactCandidateProjectCache(long projectId)
        {
            while (true)
            {
                var cache = ExactCandidateCaches.GetOrAdd(projectId, _ => new ExactCandidateProjectCache());
                if (DateTimeOffset.UtcNow - cache.CreatedAtUtc <= ExactCandidateCacheTtl)
                {
                    return cache;
                }
                // TTL 到期：按 (key, value) 原子移除，避免误删并发方刚重建的新条目
                ExactCandidateCaches.TryRemove(new KeyValuePair<long, ExactCandidateProjectCache>(projectId, cache));
            }
        }

        private static string CacheKey(string block, string path) => block + "\n" + path;

        internal static PLC_IOTagEntity SynthesizeCandidate(long projectId, string block, string path, string dataType)
        {
            var candidate = new PLC_IOTagEntity
            {
                ProjectId = projectId,
                TableName = block,
                TagName = path,
                DataType = dataType,
                IsDeleted = false,
            };
            // task-8：回退合成的候选（来自块接口表 = DB 变量）打标，读链据此区分「IO 表点位」与「DB 变量」
            SynthesizedCandidateMarks.AddOrUpdate(candidate, SynthesizedMark);
            return candidate;
        }

        /// <summary>合成候选标记（弱引用表，不延长候选对象生命周期）</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PLC_IOTagEntity, object> SynthesizedCandidateMarks = new();

        private static readonly object SynthesizedMark = new();

        /// <summary>是否由块接口表回退合成的候选（DB 变量）；否则为 ai_plc_iotag 真实行（IO 表点位）</summary>
        internal static bool IsSynthesizedCandidate(PLC_IOTagEntity candidate) =>
            SynthesizedCandidateMarks.TryGetValue(candidate, out _);

        /// <summary>未缓存块名的块 ID 查库回填（block_new 表仅 4 千行级；查过的块名连同空结果一起缓存）。</summary>
        private async Task<Dictionary<string, List<long>>> LoadExactCandidateBlockIdsAsync(
            long projectId,
            List<(string Block, string Path)> unresolved,
            ExactCandidateProjectCache projectCache,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var toQuery = new List<string>();
            foreach (var name in unresolved.Select(pair => pair.Block).Distinct(StringComparer.Ordinal))
            {
                if (projectCache.BlockIdsByName.TryGetValue(name, out var ids))
                {
                    result[name] = ids;
                }
                else
                {
                    toQuery.Add(name);
                }
            }
            if (toQuery.Count > 0)
            {
                // SqlSugar 拉姆达解析不支持 long?==long 的 op_Implicit 隐式转换，projectId 预转 long? 再比较；
                // 数组 Contains 也不翻译，统一用 List（与 PlcDataTypeList.Contains 同型）
                long? projectIdNullable = projectId;
                var rows = await _blockService.GetDb().AsQueryable()
                    .Where(blk => blk.IsDeleted == false && blk.ProjectId == projectIdNullable &&
                        toQuery.Contains(blk.BlockName))
                    .ToListAsync(cancellationToken);
                foreach (var name in toQuery)
                {
                    projectCache.BlockIdsByName[name] = rows
                        .Where(row => string.Equals(row.BlockName, name, StringComparison.Ordinal))
                        .Select(row => row.Id)
                        .ToList();
                    result[name] = projectCache.BlockIdsByName[name];
                }
            }
            return result;
        }

        /// <summary>精确读候选查询条件：两个 IN 列表（取超集）+ 内存里的精确配对（与数据库一样区分大小写）。</summary>
        internal sealed record ExactTagPairFilter(List<string> Blocks, List<string> Paths, HashSet<(string Block, string Path)> Pairs)
        {
            public bool Matches(PLC_IOTagEntity tag) => Pairs.Contains((tag.TableName ?? string.Empty, tag.TagName ?? string.Empty));
        }

        internal static ExactTagPairFilter BuildExactTagPairFilter(IReadOnlyCollection<PLC_ExactReadRequest> requests)
        {
            var pairs = requests
                .Select(request => (Block: request.BlockName ?? string.Empty, Path: request.VariablePath ?? string.Empty))
                .ToHashSet();
            return new ExactTagPairFilter(
                pairs.Select(pair => pair.Block).Distinct(StringComparer.Ordinal).ToList(),
                pairs.Select(pair => pair.Path).Distinct(StringComparer.Ordinal).ToList(),
                pairs);
        }

        private static bool IsExactTagCandidate(PLC_IOTagEntity? candidate, long projectId) =>
            candidate is not null &&
            candidate.IsDeleted == false &&
            candidate.ConstraintStatus != 6 &&
            candidate.ProjectId == projectId &&
            GlobalStaticVariable.PlcDataTypeList.Contains(candidate.DataType);

        /// <summary>
        /// 2026-09-12 GRAPH 工步变量读取规则（用户拍板，仅限老 IoT 网关）：
        /// 背景——老网关(2500) 的 {Station}_S7 设备符号表（8/24 静态导入 IDC）把 GRAPH 优化块符号
        /// 记成绝对偏移，读取命中常 0 区域且报 Good（实测 PLC1：S_NO 读 0、EXEC_BITS 全 false，
        /// 而 PLC 真值 S_NO=43、EXEC_BITS 多位 true）——假 Good 令上层永不重试；{Station}_Plus 设备
        /// 符号表又可能整组缺失（PLC1_Plus 无 DB160_MainGraph，AddGroupTagItems 在线枚举报 9A0013
        /// 无法补录）。规则：
        /// ①仅当项目 IotUrl 为 2500 老网关时启用；②仅 GRAPH 工步变量（.RT_DATA.EXEC_BITS. 特征路径）
        /// 跳过 _S7 直读 _Plus（S7 绝对通道对优化块符号语义不可信）；③_Plus 非 Good 的经新 IGW
        /// （DcsIgwConsts.FallbackIgwBaseUrl）数组别名整组读回本地展开；④非工步变量与其他网关
        /// 配置维持原「_S7 优先、Bad 重试 _Plus」逻辑不动。
        /// 2026-09-14 增补（用户拍板，最终简化规则——仅作用于 GRAPH 工步变量，其他变量流程不变）：
        /// ⑤工步变量直读 IGW，跳过 _Plus 每秒无效一跳（45/45 轮实测 PLC1_Plus 对 DB160/DB461/DB760
        /// 的 EXEC_BITS 全 Bad——符号组缺失是持续态而非间歇态；稳定 DB 的 _Plus 虽可读，统一切 IGW）；
        /// ⑥IGW 批读失败（实测 ~10% BadCommFailure 且整批一损俱损）立即重试一次；
        /// ⑦重试后仍未取到值的路径按批失败同码填 Unknown（PlcExactReadFailed）——
        /// 状态机保持上一状态（画面不闪）并记 SignalBad 事件。
        /// </summary>
        private async Task<IReadOnlyDictionary<string, ExactTransportValue>> ReadExactTransportAsync(
            long projectId,
            string iotUrl,
            string stationId,
            string? plcIp,
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, IgwExactReadTarget> readTargets,
            CancellationToken cancellationToken,
            bool keepPolling)
        {
            var values = new Dictionary<string, ExactTransportValue>(StringComparer.Ordinal);

            // 2026-09-16 全 IGW 方向：ioturl 指向新 IGW（非 2500 老网关）时整链走 IGW——
            // 工步位数组别名整组读 + 其余符号路径按 通用.S1500_{ip}.{path} 别名 ReadTagsAsList。
            // 不再向下打老网关 /DriverInfo（IGW 上无此路由必 405，实测监控全异常的根因）。
            if (!IsLegacyIotGateway(iotUrl))
            {
                await ReadExactViaIgwAsync(projectId, iotUrl, plcIp, paths, readTargets, values, cancellationToken, keepPolling);
                return values;
            }

            IReadOnlyList<string> normalPaths = paths;
            if (paths.Count > 0 && IsLegacyIotGateway(iotUrl))
            {
                var stepPaths = new List<string>();
                foreach (var path in paths)
                {
                    if (IsGraphStepExecutionPath(path))
                    {
                        stepPaths.Add(path);
                    }
                }

                if (stepPaths.Count > 0)
                {
                    normalPaths = paths.Where(path => !IsGraphStepExecutionPath(path)).ToArray();

                    // 2026-09-14（用户拍板）：GRAPH 工步变量直读 IGW（内含批读失败重试一次）。
                    // 重试后仍未取到值的路径按批失败同码填 Unknown——上层状态机保持上一状态。
                    // 老网关分支保持原样：旧扁平别名 通用.S7Plus_{ip}.{db}.RT_DATA.EXEC_BITS + 固定兜底 IGW 地址
                    await ReadGraphStepsViaIgwAsync(
                        plcIp,
                        stepPaths,
                        values,
                        dbName => DcsIgwConsts.BuildGraphPlusReadAlias(plcIp!, dbName + ".RT_DATA.EXEC_BITS"),
                        host: null,
                        cancellationToken,
                        keepPolling);
                    foreach (var path in stepPaths)
                    {
                        if (!values.ContainsKey(path))
                        {
                            values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                        }
                    }
                }
            }

            if (normalPaths.Count == 0)
            {
                return values;
            }

            var s7 = await ReadExactBatchAsync(
                iotUrl,
                stationId + "_S7",
                normalPaths,
                cancellationToken);
            if (!s7.IsSuccess)
            {
                foreach (var path in normalPaths)
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                }
                return values;
            }

            var retryPaths = new List<string>();
            MapExactBatch(normalPaths, s7.Rows, values, retryPaths);
            if (retryPaths.Count == 0)
            {
                return values;
            }

            var plus2 = await ReadExactBatchAsync(
                iotUrl,
                stationId + "_Plus",
                retryPaths,
                cancellationToken);
            if (!plus2.IsSuccess)
            {
                foreach (var path in retryPaths)
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                }
                return values;
            }

            MapExactBatch(retryPaths, plus2.Rows, values, retryPaths: null);
            return values;
        }

        /// <summary>老 IoT 网关判定：委托 DcsIgwConsts.IsLegacyIotGatewayUrl（2026-09-18 共享版，
        /// AppLayer 快照服务同用一份判定）。</summary>
        internal static bool IsLegacyIotGateway(string iotUrl) =>
            DcsIgwConsts.IsLegacyIotGatewayUrl(iotUrl);

        /// <summary>GRAPH 工步变量特征：CanonicalPath 含 ".RT_DATA.EXEC_BITS."（西门子 GRAPH
        /// 运行数据步位数组元素，如 DB160_MainGraph.RT_DATA.EXEC_BITS.EXEC_BITS[42]）。</summary>
        internal static bool IsGraphStepExecutionPath(string path)
        {
            return !string.IsNullOrEmpty(path) &&
                   path.IndexOf(".RT_DATA.EXEC_BITS.", StringComparison.Ordinal) >= 0;
        }

        /// <summary>解析工步变量路径 {db}.RT_DATA.EXEC_BITS.EXEC_BITS[n] → (db, n)；不匹配返回 false。</summary>
        internal static bool TryParseGraphStepExecutionPath(
            string path, out string dbName, out int bitIndex)
        {
            dbName = string.Empty;
            bitIndex = -1;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var open = path.LastIndexOf('[');
            if (open <= 0 || !path.EndsWith("]", StringComparison.Ordinal))
            {
                return false;
            }

            var suffix = path.Substring(0, open);
            const string tail = ".RT_DATA.EXEC_BITS.EXEC_BITS";
            if (!suffix.EndsWith(tail, StringComparison.Ordinal) ||
                !int.TryParse(path.Substring(open + 1, path.Length - open - 2), out bitIndex) ||
                bitIndex < 0)
            {
                return false;
            }

            dbName = suffix.Substring(0, suffix.Length - tail.Length);
            return dbName.Length > 0;
        }

        /// <summary>新 IGW 兜底：按 {db} 聚合成数组别名（通用.S1500_{ip}.{db}.RT_DATA.EXEC_BITS，
        /// 与 GRAPH 步位注册形态一致）整组读回，NormalizeDcsArray 展开后按 bitIndex 取位。
        /// 任一环节失败仅保持原值（上层按 Unknown 处理），不抛出。
        /// 2026-09-14 返回值：批调用本身是否成功（含一次失败重试）——供熔断直读分支决定是否回落 _Plus；
        /// 返回 true 不代表每个路径都取到 Good（个别别名行 Bad/越界时对应路径不写值）。</summary>
        /// <param name="aliasForDb">DB 名 → EXEC_BITS 数组读别名（task-8：IGW 分支按台账工站拼
        /// <see cref="IgwTagNaming.GraphReadAlias"/>；老网关分支沿用旧扁平别名）</param>
        /// <param name="host">IGW 地址；null → 固定兜底 <see cref="DcsIgwConsts.FallbackIgwBaseUrl"/>（老网关分支）</param>
        private async Task<bool> ReadGraphStepsViaIgwAsync(
            string? plcIp,
            IReadOnlyList<string> paths,
            IDictionary<string, ExactTransportValue> values,
            Func<string, string> aliasForDb,
            string? host,
            CancellationToken cancellationToken,
            bool keepPolling)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(plcIp))
                {
                    return false;
                }

                var requestsByDb = new Dictionary<string, List<(string Path, int BitIndex)>>(StringComparer.Ordinal);
                foreach (var path in paths)
                {
                    if (!TryParseGraphStepExecutionPath(path, out var dbName, out var bitIndex))
                    {
                        continue;
                    }
                    if (requestsByDb.TryGetValue(dbName, out var list))
                    {
                        list.Add((path, bitIndex));
                    }
                    else
                    {
                        requestsByDb[dbName] = new List<(string, int)> { (path, bitIndex) };
                    }
                }

                if (requestsByDb.Count == 0)
                {
                    return false;
                }

                var aliases = new List<string>(requestsByDb.Count);
                var aliasByDb = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var dbName in requestsByDb.Keys)
                {
                    // GRAPH 容器在独立逻辑设备 S7Plus_{ip} 上：别名由调用方按分支给出
                    // （IGW：通用.S7Plus_{ip}.{工站|_未分组}.{db}.RT_DATA.EXEC_BITS；老网关：通用.S7Plus_{ip}.{db}.RT_DATA.EXEC_BITS）
                    var alias = aliasForDb(dbName);
                    aliasByDb[dbName] = alias;
                    if (!aliases.Contains(alias))
                    {
                        aliases.Add(alias);
                    }
                }

                // 2026-09-14 批读失败立即重试一次：实测 IGW 对同批别名 ~10% 概率 BadCommFailure
                // 且整批一损俱损（一个别名通信抖动=全部行作废），多为孤立瞬态——一次重试可挽回大部分。
                var response = host is null
                    ? await ReadIgwTagsWithOneRetryAsync(aliases, cancellationToken, keepPolling)
                    : await ReadIgwTagsAtHostWithOneRetryAsync(host, aliases, cancellationToken, keepPolling);
                var rowsByAlias = (response is { IsSuccess: true, BackResult: not null })
                    ? response.BackResult
                        .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.TagName))
                        .GroupBy(row => row.TagName, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                    : new Dictionary<string, PLC_ReadTagResult>(StringComparer.Ordinal);

                // 2026-09-15：行缺失/行 Bad/批失败的别名，5s 内复用最近一次 Good 数组（抖动抹平）。
                // task-8：缓存键带 IGW 地址（同一别名在不同网关上是不同点位）
                var lastGoodKeyPrefix = (host ?? DcsIgwConsts.FallbackIgwBaseUrl) + "\n";
                var now = DateTimeOffset.UtcNow;
                foreach (var (dbName, requests) in requestsByDb)
                {
                    var alias = aliasByDb[dbName];
                    List<object>? bits = null;
                    if (rowsByAlias.TryGetValue(alias, out var row) && row.IsGood)
                    {
                        bits = DcsIgwMapper.NormalizeDcsArray(row.Value);
                        if (bits is not null)
                        {
                            IgwLastGoodBits[lastGoodKeyPrefix + alias] = new IgwLastGoodBitsEntry(now, bits);
                        }
                    }

                    if (bits is null &&
                        IgwLastGoodBits.TryGetValue(lastGoodKeyPrefix + alias, out var cached) &&
                        now - cached.FetchedAtUtc <= IgwLastGoodBitsTtl)
                    {
                        bits = cached.Bits;
                    }

                    if (bits is null)
                    {
                        continue;
                    }

                    foreach (var (path, bitIndex) in requests)
                    {
                        if (bitIndex >= bits.Count)
                        {
                            continue;
                        }
                        if (TryIgwBit(bits[bitIndex], out var flag))
                        {
                            values[path] = new(flag, PLC_ExactReadQuality.Good, null);
                        }
                    }
                }
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 兜底通道异常不改变已有结果（保持 _Plus 的 Unknown/Bad），不阻断主流程
                return false;
            }
        }

        /// <summary>2026-09-16 全 IGW 方向：ioturl 指向新 IGW 时的整链读。
        /// 2026-09-19 task-8（spec §4.6）：别名改按 IGW 注册台账解析（<see cref="IgwReadAliasResolver"/>，与注册侧同一套命名规则）：
        /// - 候选校验命中 IO 表点位（ai_plc_iotag 真实行）→ 通用.S7_{ip}.IO.{表}.{点}（台账 IO 重名映射）；
        /// - GRAPH 工步位（{db}.RT_DATA.EXEC_BITS.EXEC_BITS[n]）→ <see cref="ReadGraphStepsViaIgwAsync"/> 数组别名整组读（5s 抖动缓存），
        ///   GRAPH 容器（{db}.S_NO / {db}.RT_DATA.EXEC_BITS）→ 通用.S7Plus_{ip}.{工站|_未分组}.{db}.{member}；
        ///   工站取台账 → AI_PLC_GraphStepTag.StationCode → _未分组；
        /// - 其余 DB 变量 → 台账 DeviceKind（S7_{ip} / S1500_{ip}）+ 台账根组 + 还原路径（台账点重名映射）；
        ///   台账无记录 → S1500_{ip} + 清洗后块名（兼容旧注册）。
        /// 台账按项目缓存 60 秒（注册完成经 IgwAliasCacheInvalidation 失效）；台账读取失败按空台账回退，不阻断读值。
        /// （hardening D14：此时可能被改名的 DB/IO 点按 Unknown / IgwLedgerUnavailableAmbiguous 拒读，见 IgwLedgerOutageGuard。）
        /// 批读按项目 ioturl（失败重试一次、3s 超时）；未注册/行 Bad 的路径按 (null, Unknown, "PlcExactReadFailed") 填充——
        /// 上层状态机保持上一状态，不抛出。</summary>
        private async Task ReadExactViaIgwAsync(
            long projectId,
            string iotUrl,
            string? plcIp,
            IReadOnlyList<string> paths,
            IReadOnlyDictionary<string, IgwExactReadTarget> readTargets,
            Dictionary<string, ExactTransportValue> values,
            CancellationToken cancellationToken,
            bool keepPolling)
        {
            if (paths.Count == 0)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(plcIp))
            {
                foreach (var path in paths)
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                }
                return;
            }

            var context = await GetIgwReadContextAsync(projectId, cancellationToken);
            var ip = plcIp.Trim();
            // hardening D14：台账读取失败（空快照回退，没有改名映射）时，可能被规划器改名的点无法确定该读哪个别名——拒读而不是猜，
            // 避免读到同名的另一个点还报 Good。GRAPH 工站回退与注册侧同源，别名错了只会「点位不存在」，不加防护。
            IgwOutageGuardView? outageGuard = null;
            var refuseAllDbAndIo = false;
            if (context.LedgerUnavailable)
            {
                var dbBlocks = new List<string>();
                var hasIoPoints = false;
                foreach (var path in paths)
                {
                    var candidateTarget = readTargets.TryGetValue(path, out var knownTarget) ? knownTarget : null;
                    if (candidateTarget is { IsIoTag: true })
                    {
                        hasIoPoints = true;
                        continue;
                    }
                    if (IsGraphStepExecutionPath(path) || TrySplitGraphContainer(path, candidateTarget, out _, out _))
                    {
                        continue;
                    }
                    dbBlocks.Add(candidateTarget is not null ? candidateTarget.BlockName : SplitCanonicalPath(path).Block);
                }
                outageGuard = await GetOutageGuardAsync(projectId, context, hasIoPoints, dbBlocks, cancellationToken);
                refuseAllDbAndIo = outageGuard is null;
            }

            var stepPaths = new List<string>();
            var aliasByPath = new Dictionary<string, string>(paths.Count, StringComparer.Ordinal);
            var aliases = new List<string>(paths.Count);
            foreach (var path in paths)
            {
                var target = readTargets.TryGetValue(path, out var known) ? known : null;
                string alias;
                if (target is { IsIoTag: true })
                {
                    if (context.LedgerUnavailable && (refuseAllDbAndIo || outageGuard!.IsIoAmbiguous(target.BlockName, target.VariablePath)))
                    {
                        values[path] = new(null, PLC_ExactReadQuality.Unknown, LedgerOutageRefusedCode);
                        continue;
                    }
                    alias = IgwReadAliasResolver.ResolveIo(ip, context.Snapshot, target.BlockName, target.VariablePath);
                }
                else if (IsGraphStepExecutionPath(path))
                {
                    stepPaths.Add(path);
                    continue;
                }
                else if (TrySplitGraphContainer(path, target, out var graphDb, out var graphMember))
                {
                    alias = IgwReadAliasResolver.ResolveGraph(ip, context.Snapshot, graphDb, graphMember,
                        context.FallbackGraphStation(graphDb));
                }
                else
                {
                    var (block, variablePath) = target is not null
                        ? (target.BlockName, target.VariablePath)
                        : SplitCanonicalPath(path);
                    if (context.LedgerUnavailable && (refuseAllDbAndIo || outageGuard!.IsDbAmbiguous(block, variablePath)))
                    {
                        values[path] = new(null, PLC_ExactReadQuality.Unknown, LedgerOutageRefusedCode);
                        continue;
                    }
                    alias = IgwReadAliasResolver.ResolveDb(ip, context.Snapshot, block, variablePath);
                }

                aliasByPath[path] = alias;
                if (!aliases.Contains(alias))
                {
                    aliases.Add(alias); // 同一符号被多个请求引用时按别名去重
                }
            }

            if (stepPaths.Count > 0)
            {
                await ReadGraphStepsViaIgwAsync(
                    ip,
                    stepPaths,
                    values,
                    dbName => IgwReadAliasResolver.ResolveGraph(ip, context.Snapshot, dbName,
                        IgwReadAliasResolver.GraphExecBitsMember, context.FallbackGraphStation(dbName)),
                    iotUrl,
                    cancellationToken,
                    keepPolling);
                foreach (var path in stepPaths)
                {
                    if (!values.ContainsKey(path))
                    {
                        values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                    }
                }
            }

            var symbolPaths = aliasByPath.Keys.ToList();
            if (symbolPaths.Count == 0)
            {
                return;
            }

            if (!keepPolling)
            {
                // Diagnosis needs the PLC sample time, including for values which have not changed.
                // The quality reader preserves per-point quality and timestamps; older IGW versions
                // retain their bounded list fallback, whose missing timestamp stays unknown to consumers.
                var readings = await new IgwDiagnosticReader(_iotService, requestTimeout: IgwExactReadTimeout)
                    .ReadAsync(iotUrl, aliases, cancellationToken);
                foreach (var path in symbolPaths)
                {
                    if (!readings.TryGetValue(aliasByPath[path], out var reading))
                    {
                        values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactResponseMissing");
                        continue;
                    }
                    DateTimeOffset? timestamp = reading.TimestampUtc is { } time
                        ? new DateTimeOffset(time.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time).ToUniversalTime()
                        : null;
                    values[path] = reading.Status == PLC_SignalReadStatus.Good
                        ? new(reading.Value, PLC_ExactReadQuality.Good, null, timestamp)
                        : new(null, PLC_ExactReadQuality.Unknown, reading.Detail ?? reading.Status.ToString(), timestamp);
                }
                return;
            }

            var response = await ReadIgwTagsAtHostWithOneRetryAsync(iotUrl, aliases, cancellationToken, keepPolling);
            var rowsByAlias = (response is { IsSuccess: true, BackResult: not null })
                ? response.BackResult
                    .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.TagName))
                    .GroupBy(row => row.TagName, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)
                : new Dictionary<string, PLC_ReadTagResult>(StringComparer.Ordinal);

            foreach (var path in symbolPaths)
            {
                if (rowsByAlias.TryGetValue(aliasByPath[path], out var row) && row.IsGood)
                {
                    // ReadTagsAsList 返回值已过 NormalizeDcsValue 宽松解析（bool/数值），直接透传
                    values[path] = new(row.Value, PLC_ExactReadQuality.Good, null);
                }
                else
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown, "PlcExactReadFailed");
                }
            }
        }

        /// <summary>
        /// GRAPH 容器判定：有读目标时按其变量路径（S_NO / RT_DATA.EXEC_BITS，大小写不敏感）判定；
        /// 无读目标时退回 <see cref="DcsIgwConsts.IsGraphContainerPath"/> 规范路径判定。member 统一成注册侧的规范大小写。
        /// </summary>
        internal static bool TrySplitGraphContainer(string path, IgwExactReadTarget? target, out string dbName, out string member)
        {
            dbName = string.Empty;
            member = string.Empty;
            string? variablePath;
            if (target is not null)
            {
                dbName = target.BlockName;
                variablePath = target.VariablePath;
            }
            else if (DcsIgwConsts.IsGraphContainerPath(path))
            {
                (dbName, variablePath) = SplitCanonicalPath(path);
            }
            else
            {
                return false;
            }

            if (!IgwReadAliasResolver.IsGraphContainerMember(variablePath))
            {
                return false;
            }
            member = string.Equals(variablePath, IgwReadAliasResolver.GraphStepNoMember, StringComparison.OrdinalIgnoreCase)
                ? IgwReadAliasResolver.GraphStepNoMember
                : IgwReadAliasResolver.GraphExecBitsMember;
            return dbName.Length > 0;
        }

        /// <summary>
        /// 规范路径按第一个 '.' 拆为 (块名, 变量路径)；无 '.' → (路径, "")。
        /// 可达性：ReadExactDataAsync 为每个可读请求都登记了读目标，正常情况下 ReadExactViaIgwAsync 总能取到目标，
        /// 本函数只是防御性兜底（读目标缺失时按规范路径拆分；块名含 '.' 时会拆错，但该情形上游不会出现）。
        /// </summary>
        internal static (string Block, string VariablePath) SplitCanonicalPath(string path)
        {
            var dot = path.IndexOf('.');
            return dot <= 0 ? (path, string.Empty) : (path.Substring(0, dot), path.Substring(dot + 1));
        }

        /// <summary>精确读目标（task-8）：块名/变量表名、变量路径、是否 IO 表点位（ai_plc_iotag 真实行命中）。</summary>
        internal sealed record IgwExactReadTarget(string BlockName, string VariablePath, bool IsIoTag);

        // ---------- task-8：IGW 读别名项目级台账缓存 ----------

        /// <summary>项目读链上下文：台账快照 + GRAPH 工站回退表（AI_PLC_GraphStepTag：DB 名 → StationCode）。</summary>
        internal sealed class IgwReadContext
        {
            public IgwReadContext(IgwLedgerSnapshot snapshot, IReadOnlyDictionary<string, string> graphStationByDb,
                DateTimeOffset expiresAtUtc, bool ledgerUnavailable = false)
            {
                Snapshot = snapshot;
                GraphStationByDb = graphStationByDb;
                ExpiresAtUtc = expiresAtUtc;
                LedgerUnavailable = ledgerUnavailable;
            }

            public IgwLedgerSnapshot Snapshot { get; }

            public IReadOnlyDictionary<string, string> GraphStationByDb { get; }

            public DateTimeOffset ExpiresAtUtc { get; }

            /// <summary>D-1 fix round：本次快照是台账读取失败后的空快照回退（而非真实台账），没有改名映射可用。
            /// IO 诊断读链据此对同表内清洗后点名冲突的点做防御，避免张冠李戴报出错误点位的 Good 值。</summary>
            public bool LedgerUnavailable { get; }

            /// <summary>hardening D14：台账不可用时的改名风险判定数据（随上下文缓存，失败上下文 10 秒后随之重建）</summary>
            public IgwOutageGuardCache OutageGuard { get; } = new();

            public string? FallbackGraphStation(string dbName) =>
                dbName is not null && GraphStationByDb.TryGetValue(dbName.Trim(), out var station) ? station : null;
        }

        /// <summary>hardening D14：改名风险判定数据缓存（按读上下文；并发读由信号量串行加载；只加载本次读取涉及的点类型）</summary>
        internal sealed class IgwOutageGuardCache : IDisposable
        {
            private readonly SemaphoreSlim _lock = new(1, 1);
            private volatile bool _retired;

            /// <summary>任一判定数据加载失败过：本上下文存续期间（失败上下文 10 秒）不再重试加载，直接全部拒读</summary>
            public bool LoadFailed;
            public IReadOnlySet<string>? AmbiguousBlocks;
            public IReadOnlySet<string>? CollidingIoKeys;
            public readonly Dictionary<string, IReadOnlySet<string>> CollidingPathsByBlock = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// 取串行加载锁；上下文已被替换 / 失效（缓存已释放）→ false，调用方不再复用本缓存（改用一次性缓存直接加载，
            /// 正确性不变，只是少一次并发去重）。
            /// </summary>
            public async Task<bool> TryEnterAsync(CancellationToken cancellationToken)
            {
                if (_retired)
                {
                    return false;
                }
                try
                {
                    await _lock.WaitAsync(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                return true;
            }

            /// <summary>释放加载锁（信号量已随上下文释放时忽略）</summary>
            public void Exit()
            {
                try
                {
                    _lock.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 上下文被替换时释放的旧信号量，忽略
                }
            }

            /// <summary>
            /// 上下文被替换 / 失效时调用：标记退休并释放信号量。只在当前无人持有（也就不会有等待者）时才真正 Dispose，
            /// 避免把正在等待的读取永久挂住；正持有的并发读取最多在 <see cref="Exit"/> 里吃一个 ObjectDisposedException（已忽略）。
            /// </summary>
            public void Dispose()
            {
                if (_retired)
                {
                    return;
                }
                _retired = true;
                if (_lock.CurrentCount == 1)
                {
                    try
                    {
                        _lock.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        /// <summary>hardening D14：一次读取用的判定快照（未加载的判定数据为 null，按有风险处理）</summary>
        internal sealed record IgwOutageGuardView(
            IReadOnlySet<string>? AmbiguousBlocks,
            IReadOnlySet<string>? CollidingIoKeys,
            IReadOnlyDictionary<string, IReadOnlySet<string>> CollidingPathsByBlock)
        {
            public bool IsIoAmbiguous(string tableName, string tagName)
            {
                var key = IgwLedgerOutageGuard.IoKey(tableName, tagName);
                return key is null || CollidingIoKeys is null || CollidingIoKeys.Contains(key);
            }

            public bool IsDbAmbiguous(string blockName, string variablePath) =>
                string.IsNullOrEmpty(blockName)
                || AmbiguousBlocks is null
                || AmbiguousBlocks.Contains(blockName)
                || (CollidingPathsByBlock.TryGetValue(blockName, out var paths) && paths.Contains(variablePath ?? string.Empty));
        }

        /// <summary>
        /// hardening D14：取改名风险判定快照（按读上下文缓存；本次涉及的块按需补加载；没有 IO 点 / DB 点时不加载对应数据）。
        /// 任一加载失败 → null（调用方拒读全部 DB/IO 点），且失败记在上下文里，本上下文存续期间不再重试加载。
        /// </summary>
        private async Task<IgwOutageGuardView?> GetOutageGuardAsync(long projectId, IgwReadContext context,
            bool hasIoPoints, IReadOnlyCollection<string> dbBlocks, CancellationToken cancellationToken)
        {
            var cache = context.OutageGuard;
            var entered = await cache.TryEnterAsync(cancellationToken);
            if (!entered)
            {
                // 上下文已被替换（缓存已释放）：本次用一次性缓存直接加载，不复用已退休的上下文缓存
                cache = new IgwOutageGuardCache();
            }
            try
            {
                if (cache.LoadFailed)
                {
                    return null;
                }
                var hasDbPoints = dbBlocks.Count > 0;
                if (hasDbPoints && cache.AmbiguousBlocks is null)
                {
                    var live = await _liveDbBlockLoader(projectId, cancellationToken).WaitAsync(cancellationToken);
                    cache.AmbiguousBlocks = IgwLedgerOutageGuard.FindAmbiguousBlocks(live ?? Array.Empty<string>());
                }
                if (hasIoPoints && cache.CollidingIoKeys is null)
                {
                    var tags = await _fullIoTagLoader(projectId, cancellationToken).WaitAsync(cancellationToken);
                    cache.CollidingIoKeys = new HashSet<string>(
                        IgwLedgerOutageGuard.FindCollidingIoGroups(tags ?? Array.Empty<PLC_IOTagEntity>(), t => t.TableName, t => t.TagName).Keys,
                        StringComparer.OrdinalIgnoreCase);
                }
                var missing = dbBlocks
                    .Where(b => !string.IsNullOrEmpty(b) && !cache.CollidingPathsByBlock.ContainsKey(b))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (missing.Count > 0)
                {
                    var rows = await _blockPathLoader(projectId, missing, cancellationToken).WaitAsync(cancellationToken)
                        ?? Array.Empty<(string, string)>();
                    foreach (var block in missing)
                    {
                        cache.CollidingPathsByBlock[block] = IgwLedgerOutageGuard.FindCollidingPaths(
                            rows.Where(r => string.Equals(r.Block, block, StringComparison.OrdinalIgnoreCase)).Select(r => r.VariablePath));
                    }
                }
                return new IgwOutageGuardView(cache.AmbiguousBlocks, cache.CollidingIoKeys,
                    new Dictionary<string, IReadOnlySet<string>>(cache.CollidingPathsByBlock, StringComparer.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                cache.LoadFailed = true;
                Console.WriteLine("[IgwReadAlias] 警告：台账不可用且改名风险判定数据加载失败，DB/IO 点全部拒读（失败上下文过期前不再重试） project=" + projectId
                    + " -> " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (entered)
                {
                    cache.Exit();
                }
            }
        }

        /// <summary>项目 → 读链上下文（静态跨实例共享，本服务为注入实例）</summary>
        private static readonly ConcurrentDictionary<long, IgwReadContext> IgwReadContexts = new();

        /// <summary>项目缓存代次：失效时自增，加载前后代次不一致的结果不回写缓存（防止失效与加载并发时写回旧台账）</summary>
        private static readonly ConcurrentDictionary<long, long> IgwReadContextGenerations = new();

        private static readonly TimeSpan IgwReadContextTtl = TimeSpan.FromSeconds(60);

        /// <summary>台账读取失败时空上下文的缓存时长（短于正常 TTL：尽快重试，又避免每秒打库）</summary>
        private static readonly TimeSpan IgwReadContextFailureTtl = TimeSpan.FromSeconds(10);

        static PLC_VariableAccessService()
        {
            // 注册任务（AppLayer）写完台账后经 IDbService 的静态事件通知，DbService 无需引用 AppLayer
            IgwAliasCacheInvalidation.Invalidated += InvalidateIgwAliasCache;
        }

        /// <summary>失效指定项目的 IGW 读别名台账缓存（注册完成时经 <see cref="IgwAliasCacheInvalidation"/> 调用）。</summary>
        public static void InvalidateIgwAliasCache(long projectId)
        {
            IgwReadContextGenerations.AddOrUpdate(projectId, 1, (_, generation) => generation + 1);
            if (IgwReadContexts.TryRemove(projectId, out var evicted))
            {
                evicted.OutageGuard.Dispose();
            }
        }

        /// <summary>项目级加载锁：同一项目并发读只有一个请求加载台账（single-flight），其余等待后直接命中缓存</summary>
        private static readonly ConcurrentDictionary<long, SemaphoreSlim> IgwReadContextLocks = new();

        /// <summary>取项目读链上下文（60 秒缓存，项目内 single-flight 加载）；台账/工站读取失败记告警并按空台账回退，不抛出（取消除外）。</summary>
        internal async Task<IgwReadContext> GetIgwReadContextAsync(long projectId, CancellationToken cancellationToken)
        {
            if (TryGetFreshIgwReadContext(projectId, out var cached))
            {
                return cached;
            }

            var gate = IgwReadContextLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (TryGetFreshIgwReadContext(projectId, out cached))
                {
                    return cached;
                }

                var generation = IgwReadContextGenerations.GetOrAdd(projectId, 0);
                var (snapshot, stations, failed, ledgerFailed) = await LoadIgwReadContextAsync(projectId, cancellationToken);
                var context = new IgwReadContext(snapshot, stations,
                    DateTimeOffset.UtcNow + (failed ? IgwReadContextFailureTtl : IgwReadContextTtl), ledgerFailed);

                // 代次检查：加载期间发生过失效 → 本次结果只用于本次读，不写缓存。
                // 写入后再复核一次代次：若 Invalidate 恰在「检查」与「写入」之间发生，按 (key, value) 原子撤回本次写入，
                // 保证失效不会被旧台账覆盖（Invalidate 先自增代次再移除缓存，二者任一先后都能被这里的复核覆盖到）。
                if (CurrentIgwReadContextGeneration(projectId) == generation)
                {
                    IgwReadContexts.TryGetValue(projectId, out var replaced);
                    IgwReadContexts[projectId] = context;
                    if (CurrentIgwReadContextGeneration(projectId) != generation)
                    {
                        IgwReadContexts.TryRemove(new KeyValuePair<long, IgwReadContext>(projectId, context));
                    }
                    // 被顶掉的过期上下文：释放其判定数据缓存的信号量（本次返回的 context 仍在用，不能释放）
                    if (replaced != null && !ReferenceEquals(replaced, context))
                    {
                        replaced.OutageGuard.Dispose();
                    }
                }
                return context;
            }
            finally
            {
                gate.Release();
            }
        }

        private static bool TryGetFreshIgwReadContext(long projectId, out IgwReadContext context) =>
            IgwReadContexts.TryGetValue(projectId, out context!) && context.ExpiresAtUtc > DateTimeOffset.UtcNow;

        private static long CurrentIgwReadContextGeneration(long projectId) =>
            IgwReadContextGenerations.TryGetValue(projectId, out var generation) ? generation : 0;

        /// <summary>加载台账快照（不跑建表 DDL）+ GRAPH 工站回退表；任一失败 → 告警 + 该部分用空值，failed=true。
        /// ledgerFailed 单独标记台账快照本身是否读取失败（不含 GRAPH 工站部分）——D-1 fix round：诊断 IO 读链
        /// 据此判断本次是否用的是"假装没有重名"的空台账，需要对同表内清洗后点名冲突的点额外防御。</summary>
        private async Task<(IgwLedgerSnapshot Snapshot, IReadOnlyDictionary<string, string> Stations, bool Failed, bool LedgerFailed)>
            LoadIgwReadContextAsync(long projectId, CancellationToken cancellationToken)
        {
            IgwLedgerSnapshot snapshot = IgwLedgerSnapshot.Empty;
            IReadOnlyDictionary<string, string> stations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var failed = false;
            var ledgerFailed = false;
            if (_igwRegistrationService is null)
            {
                return (snapshot, stations, failed, ledgerFailed);
            }

            try
            {
                // 读链热路径不跑建表 DDL（GetLedgerForReadAsync）：建表由启动初始化器与注册任务负责
                snapshot = await _igwRegistrationService.GetLedgerForReadAsync(projectId).WaitAsync(cancellationToken)
                    ?? IgwLedgerSnapshot.Empty;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                ledgerFailed = true;
                Console.WriteLine("[IgwReadAlias] 警告：台账读取失败，按空台账回退别名 project=" + projectId + " -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                stations = await _graphStationLoader(projectId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                Console.WriteLine("[IgwReadAlias] 警告：GRAPH 工站读取失败，按 _未分组 回退 project=" + projectId + " -> "
                    + ex.GetType().Name + ": " + ex.Message);
            }
            return (snapshot, stations, failed, ledgerFailed);
        }

        /// <summary>AI_PLC_GraphStepTag 未删除行 → DB 名 → 工站代码（取值口径见 <see cref="IgwReadAliasResolver.BuildGraphStationFallback"/>）。</summary>
        private async Task<IReadOnlyDictionary<string, string>> LoadGraphStationFallbackAsync(
            long projectId,
            CancellationToken cancellationToken)
        {
            // SqlSugar 拉姆达解析不支持 long?==long 的 op_Implicit 隐式转换，projectId 预转 long? 再比较
            long? projectIdNullable = projectId;
            var rows = await GetDb().Context.Queryable<PLC_GraphStepTagEntity>()
                .Where(x => x.IsDeleted == false && x.ProjectId == projectIdNullable)
                .Select(x => new { x.Id, x.DbName, x.StationCode })
                .ToListAsync(cancellationToken);
            return IgwReadAliasResolver.BuildGraphStationFallback(
                rows.Select(r => (r.Id, (string?)r.DbName, (string?)r.StationCode)));
        }

        /// <summary>IGW 精确读单次 HTTP 请求超时（每 1000 点一批各自计时）。</summary>
        internal static readonly TimeSpan IgwExactReadTimeout = TimeSpan.FromSeconds(3);

        /// <summary>通讯故障补读前的等待：给 IGW 重连失效会话留一点时间（现场实测重读即成功，约多 0.3 s）。</summary>
        internal static readonly TimeSpan IgwCommFailureRetryDelay = TimeSpan.FromMilliseconds(200);

        /// <summary>按指定 IGW 地址批读——host 取项目 ioturl（全 IGW 方向下即 48020），不固定 FallbackIgwBaseUrl。</summary>
        private Task<HttpResponseResultModel<List<PLC_ReadTagResult>>?> ReadIgwTagsAtHostWithOneRetryAsync(
            string host,
            List<string> aliases,
            CancellationToken cancellationToken,
            bool keepPolling = false) =>
            ReadIgwTagsWithRetryCoreAsync(_iotService, host, aliases, IgwExactReadTimeout, cancellationToken, keepPolling);

        /// <summary>按兜底 IGW 地址批读。</summary>
        private Task<HttpResponseResultModel<List<PLC_ReadTagResult>>?> ReadIgwTagsWithOneRetryAsync(
            List<string> aliases,
            CancellationToken cancellationToken,
            bool keepPolling = false) =>
            ReadIgwTagsWithRetryCoreAsync(_iotService, DcsIgwConsts.FallbackIgwBaseUrl, aliases, IgwExactReadTimeout,
                cancellationToken, keepPolling);

        /// <summary>
        /// IGW 批读：成功即返；失败（网关不可达等快速失败）重试一次；两次均失败返回最后一次结果或 null。
        /// 2026-09-23 超时不再重发：旧实现只在外面包 WaitAsync(3s)，放弃等待后 HTTP 请求本体仍跑到 HttpClient
        /// 默认 100 s，紧接着又原样重发一次——IGW 同一台 PLC 的请求串行执行（MaxInFlight=1），每次超时都会在
        /// 设备队列里多堆一个没人等结果的现场读（单个最长 10 s），后面所有读取排在它后面，停机诊断一次排查被拖到
        /// 100 多秒。现在超时下传到 HttpClient（请求真正结束），且超时（408 / 外层兜底超时）不再重发。
        /// <para>
        /// 2026-09-23 通讯故障补读：IGW 的 S7Plus 会话会周期性失效（现场 GRAPH 设备约 60 s 一次），碰上时整批返回
        /// 「点位质量异常:… BadCommFailure」。ReadTagsAsList 把它展开成逐行坏值、整体仍算成功，所以"失败重试"
        /// 不会触发，停机诊断拿不到 EXEC_BITS 就退去查信号历史表（慢）。现在批内失败原因是通讯故障/超时时，
        /// 等 <see cref="IgwCommFailureRetryDelay"/> 整批再读一次（只一次）；「点位不存在」等内容性失败不补读。
        /// keepPolling（设备监控持续轮询）不补读：它 100 ms 后本来就会再读。
        /// </para>
        /// </summary>
        internal static async Task<HttpResponseResultModel<List<PLC_ReadTagResult>>?> ReadIgwTagsWithRetryCoreAsync(
            IIotService iotService,
            string host,
            List<string> aliases,
            TimeSpan requestTimeout,
            CancellationToken cancellationToken,
            bool keepPolling = false)
        {
            // 外层兜底只防 HttpClient 超时没生效的极端情况：按批数放宽，并留出 503 热重载重试的余量
            var batches = Math.Max(1, (aliases.Count + DcsIgwConsts.ReadBatchLimit - 1) / DcsIgwConsts.ReadBatchLimit);
            var guard = TimeSpan.FromTicks(requestTimeout.Ticks * batches) + TimeSpan.FromSeconds(2);

            HttpResponseResultModel<List<PLC_ReadTagResult>>? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    // 快速失败：让出极短间隔，避开同一次抖动窗口的尾巴；通讯故障：等 IGW 重连会话
                    var delay = last is { IsSuccess: true } ? IgwCommFailureRetryDelay : TimeSpan.FromMilliseconds(50);
                    await Task.Delay(delay, cancellationToken);
                }
                try
                {
                    last = await iotService
                        .ReadTagsAsList(host, aliases, keepPolling, requestTimeout)
                        .WaitAsync(guard, cancellationToken);
                    if (last is { IsSuccess: true, BackResult: not null })
                    {
                        if (attempt == 0 && !keepPolling && HasTransientCommFailure(last.BackResult, aliases))
                        {
                            continue;
                        }
                        return last;
                    }
                    if (last?.HttpStatusCode == HttpStatusCode.RequestTimeout)
                    {
                        return last;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    return null;
                }
                catch
                {
                    last = null;
                }
            }
            return last;
        }

        /// <summary>
        /// 批内有行因通讯故障 / 超时没读到值（IGW 报文「点位质量异常:{别名} BadCommFailure|BadNotConnected|BadTimeout(…)」）。
        /// 报文只点名第一个坏点，所以按行上的原因逐个解析，任一命中即算。
        /// </summary>
        internal static bool HasTransientCommFailure(IReadOnlyList<PLC_ReadTagResult> rows, IReadOnlyList<string> aliases)
        {
            foreach (var message in rows.Where(r => r is { IsGood: false } && !string.IsNullOrWhiteSpace(r.ErrorMessage))
                         .Select(r => r.ErrorMessage)
                         .Distinct(StringComparer.Ordinal))
            {
                if (IgwLegacyReadErrorParser.TryLocate(message, aliases, out _, out var status)
                    && status is PLC_SignalReadStatus.CommFailure or PLC_SignalReadStatus.Timeout)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>IGW 数组元素位值归一化：CLR bool 直接用；其余按 "1"/"0"/"true"/"false"（不区分大小写）。</summary>
        private static bool TryIgwBit(object raw, out bool value)
        {
            switch (raw)
            {
                case bool flag:
                    value = flag;
                    return true;
                default:
                    var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)?
                        .Trim();
                    if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        value = true;
                        return true;
                    }
                    if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                    {
                        value = false;
                        return true;
                    }
                    value = false;
                    return false;
            }
        }

        private async Task<ExactTransportBatch> ReadExactBatchAsync(
            string iotUrl,
            string deviceName,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 2026-09-11 毒批隔离：老网关对含 < > = 等特殊字符的符号（跨 PLC 通信块如 DB182_PLC1<->PLC2）
            // 会把整批读拖死——实测 8 tag 批含 1 个毒符号时整个 backResult 反序列化为 null（全 Bad 假象），
            // 剔除后同批其余符号全部 Good。可疑路径单独逐个成批：单发失败只影响自身（该路径自然落到
            // PlcExactResponseMissing → 重试/Unknown），不再污染主批。
            List<string> safePaths = new(paths.Count);
            List<string> suspectPaths = new();
            foreach (var path in paths)
            {
                if (IsSafeGatewayTagPath(path)) safePaths.Add(path);
                else suspectPaths.Add(path);
            }

            List<PLC_ReadTagResult> rows = new(paths.Count);
            if (safePaths.Count > 0)
            {
                var main = await ReadExactBatchCoreAsync(iotUrl, deviceName, safePaths, cancellationToken);
                if (!main.IsSuccess)
                {
                    return new(false, []);
                }

                rows.AddRange(main.Rows);
            }

            foreach (var path in suspectPaths)
            {
                var single = await ReadExactBatchCoreAsync(iotUrl, deviceName, new[] { path }, cancellationToken);
                if (single.IsSuccess)
                {
                    rows.AddRange(single.Rows);
                }
            }

            return new(true, rows);
        }

        private static bool IsSafeGatewayTagPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true; // 空串交给原有链路处理
            foreach (var ch in path)
            {
                var safe = ch is ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                           or '_' or '.' or '[' or ']' or '-';
                if (!safe) return false;
            }

            return true;
        }

        private async Task<ExactTransportBatch> ReadExactBatchCoreAsync(
            string iotUrl,
            string deviceName,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _iotService.ReadTags(iotUrl, new PLC_ReadTagParame
                {
                    DeviceName = deviceName,
                    TagList = paths.ToList()
                }).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (response is { IsSuccess: true, BackResult: not null })
                {
                    // 2026-09-11 修复：老网关（/DriverInfo/ReadTag）返回体不回显 tagName（反序列化为 null），
                    // MapExactBatch 按 row.TagName 分组对位会把全部行滤成 PlcExactResponseMissing，
                    // retryPaths 恒空 → _Plus 设备重试永远不触发 → EXEC_BITS 符号在 S7 设备全 Bad 时整批判死
                    // （实测 FB261 验证 0/57，同批标签手发 PLC2_Plus 设备 54/54 Good）。
                    // 按请求索引回填 TagName（与下方私有 ReadTags 的对位口径一致），保证 rowsByPath 能命中、重试链路生效。
                    var rows = response.BackResult;
                    for (var i = 0; i < rows.Count && i < paths.Count; i++)
                    {
                        if (rows[i] != null && string.IsNullOrWhiteSpace(rows[i].TagName))
                        {
                            rows[i].TagName = paths[i];
                        }
                    }
                    return new(true, rows);
                }
                return new(false, []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new(false, []);
            }
        }

        private static void MapExactBatch(
            IReadOnlyList<string> paths,
            IReadOnlyList<PLC_ReadTagResult> rows,
            IDictionary<string, ExactTransportValue> values,
            ICollection<string>? retryPaths)
        {
            var rowsByPath = rows
                .Where(row => row is not null && !string.IsNullOrWhiteSpace(row.TagName))
                .GroupBy(row => row.TagName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var path in paths)
            {
                if (!rowsByPath.TryGetValue(path, out var matches))
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown,
                        "PlcExactResponseMissing");
                }
                else if (matches.Length != 1)
                {
                    values[path] = new(null, PLC_ExactReadQuality.Unknown,
                        "PlcExactResponseDuplicate");
                }
                else if (!matches[0].IsGood)
                {
                    values[path] = new(null, PLC_ExactReadQuality.Bad,
                        "PlcExactTagBadQuality");
                    retryPaths?.Add(path);
                }
                else
                {
                    values[path] = new(matches[0].Value, PLC_ExactReadQuality.Good, null);
                }
            }
        }

        private static List<NormalizedExactRequest> NormalizeExactRequests(
            IReadOnlyCollection<PLC_ExactReadRequest>? requests)
        {
            if (requests is null || requests.Count == 0)
            {
                return [];
            }

            var normalized = requests.Select((request, order) =>
            {
                var requestKey = request?.RequestKey?.Trim() ?? string.Empty;
                var blockName = request?.BlockName?.Trim() ?? string.Empty;
                var variablePath = request?.VariablePath?.Trim() ?? string.Empty;
                var canonicalPath = blockName.Length > 0 && variablePath.Length > 0
                    ? blockName + "." + variablePath
                    : string.Empty;
                var errorCode = requestKey.Length == 0 || canonicalPath.Length == 0
                    ? "PlcExactRequestInvalid"
                    : null;
                return new NormalizedExactRequest(
                    order,
                    new PLC_ExactReadRequest(requestKey, blockName, variablePath),
                    canonicalPath,
                    errorCode);
            }).ToArray();

            return normalized
                .GroupBy(x => x.RequestKey, StringComparer.Ordinal)
                .Select(group => group.Count() == 1
                    ? group.Single()
                    : group.First() with { ErrorCode = "PlcExactRequestKeyDuplicate" })
                .OrderBy(x => x.Order)
                .ToList();
        }

        private static void FillExactFailures(
            IEnumerable<NormalizedExactRequest> requests,
            IDictionary<string, PLC_ExactReadResult> results,
            string errorCode)
        {
            foreach (var request in requests)
            {
                results[request.RequestKey] = ExactResult(
                    request,
                    null,
                    PLC_ExactReadQuality.Unknown,
                    errorCode);
            }
        }

        private static IReadOnlyList<PLC_ExactReadResult> OrderedExactResults(
            IEnumerable<NormalizedExactRequest> requests,
            IReadOnlyDictionary<string, PLC_ExactReadResult> results) =>
            requests.OrderBy(x => x.Order).Select(x => results[x.RequestKey]).ToArray();

        private static PLC_ExactReadResult ExactResult(
            NormalizedExactRequest request,
            object? value,
            PLC_ExactReadQuality quality,
            string? errorCode,
            DateTimeOffset? sourceTimestamp = null) =>
            new(
                request.RequestKey,
                request.BlockName,
                request.VariablePath,
                request.CanonicalPath,
                value,
                quality,
                SourceTimestamp: sourceTimestamp,
                errorCode);

        private sealed record NormalizedExactRequest(
            int Order,
            PLC_ExactReadRequest Request,
            string CanonicalPath,
            string? ErrorCode)
        {
            public string RequestKey => Request.RequestKey;
            public string BlockName => Request.BlockName;
            public string VariablePath => Request.VariablePath;
        }

        private sealed record ExactTransportBatch(
            bool IsSuccess,
            IReadOnlyList<PLC_ReadTagResult> Rows);

        private sealed record ExactTransportValue(
            object? Value,
            PLC_ExactReadQuality Quality,
            string? ErrorCode,
            DateTimeOffset? SourceTimestamp = null);

        // 注：GetReadData 契约保持老网关链路原样（LineUp 等调用方按 IsGood 处理）；约束诊断改用
        // ReadDiagnosticSignalsAsync（D-1，2026-09-19：IGW 按台账别名逐点带质量读取，缺值不当通过）。

        public async Task<List<PLC_ReadTagResult>> ReadTagsByFixedDeviceAsync(long projectId, List<string> tagNames)
        {
            var projectEntity = _projectService.GetById(projectId);
            if (projectEntity == null)
            {
                return new List<PLC_ReadTagResult>();
            }

            var normalizedTagNames = (tagNames ?? new List<string>())
                .Where(it => string.IsNullOrWhiteSpace(it) == false)
                .Select(it => it.Trim().Trim('"'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedTagNames.Count == 0)
            {
                return new List<PLC_ReadTagResult>();
            }

            // 2026-09-18 全 IGW 迁移：非 2500 老网关改走精确读——"PLC1IO_S7" 是老网关 deviceName 模型，
            // IGW 无此路由（曾静默返回空表，气缸信号采集在 IGW 项目上采不到任何值）。
            // 入参短名（如 OP030_CYL01_W）先按 iotag.TagName 解析出 {TableName}.{TagName} 全路径再精确读；
            // IQ 表（点名带地址小数点，如 I106.2_SG）同样按 iotag 换成「IQ.点名」走 IO 别名读（2026-10-15 修正：
            // 以前先按小数点拆块段，IQ 表的点一律读不到，并非 IGW 不支持）；既不在 iotag、又无块段的才降级 IsGood=false。
            if (!DcsIgwConsts.IsLegacyIotGatewayUrl(projectEntity.IotUrl))
            {
                return await ReadNamedTagsViaIgwAsync(projectId, normalizedTagNames).ConfigureAwait(false);
            }

            var result = await ReadTags(projectEntity.IotUrl, "PLC1IO_S7", normalizedTagNames);
            return result ?? new List<PLC_ReadTagResult>();
        }

        /// <summary>
        /// 2026-09-18 全 IGW 迁移：ReadTagsByFixedDeviceAsync 的 IGW 分支。
        /// 名称解析优先级（2026-10-15 调整）：①iotag.TagName 命中 → 换成全路径（TableName.TagName）精确读
        /// （含点名带小数点的 IQ 表点）；②本身含块段（DB6000_StdSignal.xxx 形态）直接精确读；
        /// ③无块段且 iotag 无此名 → IsGood=false（IgwNoBlockSegment）。见 PlanNamedIgwReads。
        /// 返回全部行（含坏行），与老网关 ReadTags 契约一致，由调用方按 IsGood 过滤。
        /// </summary>
        /// <summary>
        /// 把调用方的点名拆成精确读请求（RequestKey = 表名.点名 / 块名.成员，requestNameByKey 回填原始名）。
        /// 2026-10-15：先查 iotag 短名映射、再看有没有块段——IQ 表点名常带地址里的小数点（I106.2_SG），
        /// 以前先按小数点拆成「块 I106 + 成员 2_SG」，IQ 表的点在 IGW 上一律读不到。
        /// 既不在 iotag 里、又没有块段的点名按行降级 IsGood=false（IgwNoBlockSegment）。
        /// </summary>
        internal static List<PLC_ExactReadRequest> PlanNamedIgwReads(
            IReadOnlyList<string> tagNames,
            IReadOnlyDictionary<string, string> byShortName,
            out List<PLC_ReadTagResult> fallbackRows,
            out Dictionary<string, string> requestNameByKey)
        {
            var requests = new List<PLC_ExactReadRequest>();
            requestNameByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // RequestKey → 调用方原始名
            fallbackRows = new List<PLC_ReadTagResult>();
            foreach (var name in tagNames ?? Array.Empty<string>())
            {
                var fullName = byShortName != null && byShortName.TryGetValue(name, out var resolved) ? resolved : name;
                var firstDot = fullName.IndexOf('.');
                if (firstDot <= 0 || firstDot == fullName.Length - 1)
                {
                    fallbackRows.Add(new PLC_ReadTagResult
                    {
                        TagName = name,
                        IsGood = false,
                        ErrorMessage = "IgwNoBlockSegment",
                    });
                    continue;
                }

                var requestKey = fullName;
                if (!requestNameByKey.ContainsKey(requestKey))
                {
                    requestNameByKey[requestKey] = name;
                    requests.Add(new PLC_ExactReadRequest(
                        requestKey,
                        fullName.Substring(0, firstDot),
                        fullName.Substring(firstDot + 1)));
                }
            }
            return requests;
        }

        private async Task<List<PLC_ReadTagResult>> ReadNamedTagsViaIgwAsync(
            long projectId,
            List<string> tagNames)
        {
            var byShortName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 短名 → TableName.TagName
            try
            {
                var iotagRows = await _tagService.GetDb().AsQueryable()
                    .Where(it => it.IsDeleted == false && it.ProjectId == projectId)
                    .Where(it => tagNames.Contains(it.TagName))
                    .ToListAsync(CancellationToken.None);
                foreach (var row in iotagRows)
                {
                    var shortName = (row.TagName ?? string.Empty).Trim();
                    if (shortName.Length > 0 && !byShortName.ContainsKey(shortName))
                    {
                        byShortName[shortName] = row.TableName + "." + row.TagName;
                    }
                }
            }
            catch
            {
                // 元数据查询失败不阻断：退化为直接按入参形态精确读
            }

            var requests = PlanNamedIgwReads(tagNames, byShortName, out var fallbackRows, out var requestNameByKey);

            var results = new List<PLC_ReadTagResult>(fallbackRows);
            if (requests.Count > 0)
            {
                var exact = await ReadExactDataAsync(projectId, requests, CancellationToken.None).ConfigureAwait(false);
                foreach (var item in exact)
                {
                    requestNameByKey.TryGetValue(item.RequestKey, out var originalName);
                    results.Add(new PLC_ReadTagResult
                    {
                        TagName = originalName ?? item.RequestKey, // 回填调用方原始短名，与老网关对位口径一致
                        OldTagName = item.RequestKey,
                        Value = item.Value,
                        IsGood = item.Quality == PLC_ExactReadQuality.Good,
                        ErrorMessage = item.ErrorCode,
                    });
                }
            }

            return results
                .GroupBy(it => it.TagName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(it => it.IsGood).First())
                .ToList();
        }

        private async Task<List<PLC_ReadTagResult>> ReadTags(string url, string deviceName, List<string> tagLs)
        {
            var result = await _iotService.ReadTags(url, new PLC_ReadTagParame
            {
                DeviceName = deviceName,
                TagList = tagLs
            });
            if (result.IsSuccess)
            {
                foreach (var item in result.BackResult)
                {
                    int index = result.BackResult.IndexOf(item);

                    if ((index + 1) > tagLs.Count)
                    {
                        break;
                    }

                    var tag = tagLs[index];
                    if (tag != null)
                    {
                        item.TagName = tag.Replace("IQ.", "");
                    }
                }
                return result.BackResult;
            }
            return null;
        }

        /// <summary>
        /// 获取属于 DB 块且数据类型为标量的变量名集合。
        /// 过滤逻辑：ProgrammingLanguage == "DB" 且 DataType 在 PlcDataTypeList 内。
        /// 用于 Q 点发现时排除结构体类型和非 DB 块变量。
        /// </summary>
        public async Task<HashSet<string>> GetScalarDbVarNamesAsync(long projectId, List<string> candidateVarNames)
        {
            if (candidateVarNames == null || candidateVarNames.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var matched = await _blockInterfaceService.GetDb().AsQueryable()
                .InnerJoin<PLC_BlockEntity>((t, b) => t.BlockId == b.Id)
                .Where((t, b) =>
                    t.IsDeleted == false &&
                    b.IsDeleted == false &&
                    b.ProjectId == projectId &&
                    b.ProgrammingLanguage == "DB" &&
                    GlobalStaticVariable.PlcDataTypeList.Contains(t.DataType) &&
                    candidateVarNames.Contains(b.BlockName + "." + t.VariablePath))
                .Select((t, b) => (b.BlockName + "." + t.VariablePath))
                .ToListAsync();

            return new HashSet<string>(matched, StringComparer.OrdinalIgnoreCase);
        }

    }
}
