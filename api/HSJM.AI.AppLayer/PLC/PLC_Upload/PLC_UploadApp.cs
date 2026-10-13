using Hangfire;
using Hangfire.States;
using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.Agent.Services.Plc_diagnosis;
using HSJM.AI.AppLayer.PLC.DTOS;
using HSJM.AI.AppLayer.PLC.PLC_Upload.Igw;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SqlSugar;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload
{
    public class PLC_UploadApp : ISelfScopedAutoInject
    {
        private readonly IPLC_ProjectService _proj;
        private readonly ISqlSugarClient _db;
        readonly IPLC_BlockService _blockService;
        readonly IPLC_SourceFileService _sourceFileService;
        readonly IPLC_BlockInterfaceService _blockInterfaceService;
        readonly IPLC_VariableAccessService _variableAccessService;
        readonly IPLC_LogicUnitService _logicUnitService;
        readonly IPLC_IOTagService _ioTagService;
        readonly IPLC_BlockCallService _blockCallService;
        readonly IPLC_DbFbBindingService _dbFbBindingService;
        private readonly S7OffsetCalculator _offsetCalculator;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PLC_UploadApp> _logger;
        private readonly IOConstraintPreprocessService _constraintPreprocessService;
        private readonly IIotService _iotService;
        private readonly IPLC_IOConstraintRuleService _constraintRuleService;
        private readonly IPLC_IOConstraintSignalService _constraintSignalService;
        private readonly IEnumerable<IPlcProjectMetadataChangedHandler> _metadataChangedHandlers;

        public PLC_UploadApp(ISqlSugarClient db,
            IPLC_SourceFileService sourceFileService,
             IPLC_LogicUnitService logicUnitService,
            IPLC_BlockInterfaceService blockInterfaceService,
            IPLC_BlockService PLC_BlockService,
            IPLC_VariableAccessService variableAccessService,
            IPLC_IOTagService ioTagService,
            IPLC_BlockCallService blockCallService,
            IPLC_DbFbBindingService dbFbBindingService,
            S7OffsetCalculator offsetCalculator,
            IPLC_ProjectService proj, IWebHostEnvironment env, IConfiguration config, IHttpContextAccessor httpContextAccessor,
            IServiceProvider serviceProvider,
            ILogger<PLC_UploadApp> logger, IOConstraintPreprocessService constraintPreprocessService, IIotService iotService,
            IPLC_IOConstraintRuleService constraintRuleService, IPLC_IOConstraintSignalService constraintSignalService,
            IEnumerable<IPlcProjectMetadataChangedHandler> metadataChangedHandlers)
        {
            _logicUnitService = logicUnitService;
            _db = db;
            _sourceFileService = sourceFileService;
            _blockInterfaceService = blockInterfaceService;
            _proj = proj;
            _variableAccessService = variableAccessService;
            _blockService = PLC_BlockService;
            _ioTagService = ioTagService;
            _blockCallService = blockCallService;
            _dbFbBindingService = dbFbBindingService;
            _offsetCalculator = offsetCalculator;
            _env = env;
            _config = config;
            _httpContextAccessor = httpContextAccessor;
            _serviceProvider = serviceProvider;
            _logger = logger;
            _constraintPreprocessService = constraintPreprocessService;
            _iotService = iotService;
            _constraintRuleService = constraintRuleService;
            _constraintSignalService = constraintSignalService;
            _metadataChangedHandlers = metadataChangedHandlers ?? Enumerable.Empty<IPlcProjectMetadataChangedHandler>();
        }

        private static int CountLogicUnitReads(PLC_LogicUnitDTO unit)
        {
            if (unit?.variable_access == null)
            {
                return 0;
            }

            return unit.variable_access.Count(va =>
                string.Equals(va.access_type, "READ", StringComparison.OrdinalIgnoreCase)
                || string.Equals(va.access_type, "READWRITE", StringComparison.OrdinalIgnoreCase));
        }

        private static int CountLogicUnitWrites(PLC_LogicUnitDTO unit)
        {
            if (unit?.variable_access == null)
            {
                return 0;
            }

            return unit.variable_access.Count(va =>
                string.Equals(va.access_type, "WRITE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(va.access_type, "READWRITE", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<HttpResponseResultModel<string>> UploadAndParseAsync(IFormFile file, long projectId)
        {
            var result = new HttpResponseResultModel<string>();
            // 同步路径没有 Hangfire taskId，生成 sync 版 parseRunId 供 Block 溯源
            var syncRunId = $"plc_upload_sync_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}";
            string tempFolder = Path.Combine(_env.WebRootPath, "temp_plc_upload", Guid.NewGuid().ToString());

            try
            {
                // 1. Save and Extract
                if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);
                string zipPath = Path.Combine(tempFolder, file.FileName);

                using (var stream = new FileStream(zipPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                string extractPath = Path.Combine(tempFolder, "extracted");
                ExtractZipToWorkingDirectory(zipPath, extractPath);

                var blockPathMap = LoadBlockPathMappings(extractPath);

                // 2. Find all XML/JSON/ML files (.scl/.awl 通过 XML 自动关联处理，无需单独解析)
                var files = Directory.GetFiles(extractPath, "*.*", SearchOption.AllDirectories)
                    .Where(PLC_UploadFileClassifier.IsGeneralPlcSource)
                    .ToList();

                if (files.Count == 0)
                {
                    result.ErrorMessage = "No valid PLC files found in the uploaded zip.";
                    return result;
                }

                // 3. Process each file
                string scriptPath = Path.Combine(_env.WebRootPath, "skills", "plc-analyzer", "scripts", "engine.py");
                string pythonCmd = _config["PythonPath"] ?? "python";

                // Verify Project Exists
                var projectEntity = await _proj.GetByIdAsync(projectId);
                if (projectEntity == null)
                {
                    result.ErrorMessage = "Project not found.";
                    return result;
                }

                var blocksToInsert = new List<PLC_BlockEntity>();
                var sourceFilesToInsert = new List<PLC_SourceFileEntity>();
                var logicUnitsToInsert = new List<PLC_LogicUnitEntity>();
                var interfacesToInsert = new List<PLC_BlockInterfaceEntity>();
                var varAccessToInsert = new List<PLC_VariableAccessEntity>();
                var blockCallsToInsert = new List<PLC_BlockCallEntity>();
                var tagToToInsert = new List<PLC_IOTagEntity>();
                var dbFbBindingsToInsert = new List<PLC_DbFbBindingEntity>();

                // 收集需要清理的块名（用于删除旧数据）
                var blockNamesToClean = new HashSet<string>();

                bool isFirstFile = true;

                foreach (var filePath in files)
                {

                    var ext = Path.GetExtension(filePath);
                    if (ext.ToLower().Contains("csv"))
                    {
                        var res = await ParseTagCSV(filePath, projectEntity.Id);
                        if (res.Count > 0)
                        {
                            tagToToInsert.AddRange(res);
                        }
                    }
                    else
                    {

                        string outputJsonPath = Path.ChangeExtension(filePath, ".parsed.json");

                        var processInfo = new ProcessStartInfo
                        {
                            FileName = pythonCmd,
                            Arguments = $"\"{scriptPath}\" --file \"{filePath}\" --format json-v2 --output \"{outputJsonPath}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(scriptPath)
                        };

                        using (var process = Process.Start(processInfo))
                        {
                            string stderr = await process.StandardError.ReadToEndAsync();
                            process.WaitForExit();

                            if (process.ExitCode != 0)
                            {
                                Console.WriteLine($"Error parsing {filePath}: {stderr}");
                                continue;
                            }
                        }

                        if (File.Exists(outputJsonPath))
                        {
                            string jsonContent = await File.ReadAllTextAsync(outputJsonPath);
                            PLC_ParseResultDTO parseResult = null;
                            try
                            {
                                parseResult = JsonSerializer.Deserialize<PLC_ParseResultDTO>(jsonContent);
                            }
                            catch
                            {
                                // Ignore json error
                                continue;
                            }

                            if (parseResult != null)
                            {
                                if (isFirstFile && parseResult.metadata != null)
                                {
                                    // Optional: Update project info if needed, or just rely on manual input
                                    // projectEntity.Brand = parseResult.metadata.brand; 
                                    isFirstFile = false;
                                }

                                // Source File
                                var sourceId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                                var sourceFile = new PLC_SourceFileEntity
                                {
                                    Id = sourceId,
                                    ProjectId = projectId,
                                    FileName = Path.GetFileName(filePath),
                                    FilePath = filePath, // Note: This is temp path, might want to save relative or permanent path if we keep files
                                    FileType = Path.GetExtension(filePath).TrimStart('.'),
                                    FileSize = new FileInfo(filePath).Length,
                                    // 2026-10-09 检查清单 #3：存「解析输入指纹」（主文件＋伴随源码），
                                    // 跳过判定（上方 hash 比对处）用同一口径计算。
                                    FileHash = ComputeSourceFingerprint(filePath),
                                    FileContent = TryReadGraphSourceContent(filePath, parseResult), // GRAPH 块原文归档（供 LineUp 预览复用）
                                    ParseStatus = 1,
                                    ParseTime = DateTime.Now,
                                    IsDeleted = false
                                };
                                SetBaseField(sourceFile);
                                sourceFilesToInsert.Add(sourceFile);

                                // Process each block in the blocks array
                                if (parseResult.blocks != null && parseResult.blocks.Count > 0)
                                {
                                    // For now, we'll process the first block and associate logic_units/interface with it
                                    // In the future, this could be refactored to handle multiple blocks per file
                                    var blockInfo = parseResult.blocks[0];
                                    var blockId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                                    // 构建元数据 JSON（包含解析器信息、项目元数据等）
                                    string metadataJson = null;
                                    if (parseResult.metadata != null)
                                    {
                                        var metadataObj = new
                                        {
                                            brand = parseResult.metadata.brand,
                                            product = parseResult.metadata.product,
                                            version = parseResult.metadata.version,
                                            station_id = parseResult.metadata.station_id,
                                            parse_config = parseResult.metadata.parse_config,
                                            parse_timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                            source_file = Path.GetFileName(filePath),
                                            // 语义提示信息
                                            semantic_hints = parseResult.semantic_hints != null ? new
                                            {
                                                station_type = parseResult.semantic_hints.station_type,
                                                function_category = parseResult.semantic_hints.function_category,
                                                keywords_detected = parseResult.semantic_hints.keywords_detected,
                                                confidence = parseResult.semantic_hints.confidence,
                                                inferred_purpose = parseResult.semantic_hints.inferred_purpose
                                            } : null,
                                            // 解析错误信息
                                            errors = parseResult.errors
                                        };
                                        metadataJson = JsonSerializer.Serialize(metadataObj, new JsonSerializerOptions { WriteIndented = false });
                                    }

                                    var block = new PLC_BlockEntity
                                    {
                                        Id = blockId,
                                        SourceFileId = sourceId,
                                        ProjectId = projectId,
                                        BlockName = blockInfo.name,
                                        BlockType = blockInfo.type,
                                        BlockNumber = blockInfo.number ?? 0,
                                        ProgrammingLanguage = blockInfo.language,
                                        Description = ResolveBlockDescription(blockInfo.name, blockInfo.description, blockPathMap),
                                        IsPotentialEntry = blockInfo.is_potential_entry, // 先写入解析器的判定，后续会根据全局调用关系重新计算
                                        MetadataJson = metadataJson,
                                        ParseRunId = syncRunId,
                                        SourceHash = ComputeGraphSourceHash(parseResult),
                                        IsDeleted = false
                                    };
                                    SetBaseField(block);
                                    blocksToInsert.Add(block);

                                    // DB→FB 绑定关系（InstanceDB 专有）：随 DB 块一同入库 ai_plc_dbfbinding
                                    if (!string.IsNullOrWhiteSpace(blockInfo.instance_of_name))
                                    {
                                        var dbFbBinding = new PLC_DbFbBindingEntity
                                        {
                                            ProjectId = projectId,
                                            BlockId = blockId,
                                            DbName = blockInfo.name,
                                            InstanceOfName = blockInfo.instance_of_name,
                                            InstanceOfType = blockInfo.instance_of_type,
                                            IsDeleted = false
                                        };
                                        SetBaseField(dbFbBinding);
                                        dbFbBindingsToInsert.Add(dbFbBinding);
                                    }

                                    // 记录块名用于后续清理旧数据
                                    if (!string.IsNullOrEmpty(blockInfo.name))
                                    {
                                        blockNamesToClean.Add(blockInfo.name);
                                    }

                                    // Logic Units
                                    // 构建 compile_unit_id -> LogicUnitId 映射，用于关联 BlockCall
                                    var compileUnitToLogicUnitMap = new Dictionary<string, long>();

                                    if (parseResult.logic_units != null)
                                    {
                                        foreach (var unit in parseResult.logic_units)
                                        {
                                            var unitId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                                            // 统计该 LogicUnit 的读写变量数量
                                            var readCount = CountLogicUnitReads(unit);
                                            var writeCount = CountLogicUnitWrites(unit);

                                            // 统计该 LogicUnit 中的函数调用数量
                                            int callsInUnit = 0;
                                            if (parseResult.calls != null && !string.IsNullOrEmpty(unit.compile_unit_id))
                                            {
                                                callsInUnit = parseResult.calls.Count(c => c.network_id == unit.compile_unit_id);
                                            }

                                            var logicUnit = new PLC_LogicUnitEntity
                                            {
                                                Id = unitId,
                                                BlockId = blockId,
                                                UnitType = unit.unit_type,
                                                NetworkId = unit.network_id,
                                                Title = unit.title,
                                                Comment = unit.comment,
                                                RawContent = unit.raw_content,
                                                UidSymbolMapJson = unit.uid_symbol_map != null ? JsonSerializer.Serialize(unit.uid_symbol_map) : null,
                                                ProgrammingLanguage = unit.programming_language,
                                                ReadVarsCount = readCount,
                                                WriteVarsCount = writeCount,
                                                CallsCount = callsInUnit,
                                                CallPinBindingsJson = (unit.call_pin_bindings != null && unit.call_pin_bindings.Count > 0)
                                                    ? JsonSerializer.Serialize(unit.call_pin_bindings)
                                                    : null,
                                                GraphOutgoingTransitionsJson = (unit.outgoing_transition_numbers != null && unit.outgoing_transition_numbers.Count > 0)
                                                    ? JsonSerializer.Serialize(unit.outgoing_transition_numbers)
                                                    : null,
                                                IsDeleted = false
                                            };
                                            SetBaseField(logicUnit);
                                            logicUnitsToInsert.Add(logicUnit);

                                            // 记录 compile_unit_id -> LogicUnitId 映射
                                            if (!string.IsNullOrEmpty(unit.compile_unit_id))
                                            {
                                                compileUnitToLogicUnitMap[unit.compile_unit_id] = unitId;
                                            }

                                            if (unit.variable_access != null)
                                            {
                                                foreach (var access in unit.variable_access)
                                                {
                                                    var accessId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                                                    var varAccess = new PLC_VariableAccessEntity
                                                    {
                                                        Id = accessId,
                                                        LogicUnitId = unitId,
                                                        BlockId = blockId,
                                                        ProjectId = projectId,
                                                        VariableName = access.variable,
                                                        AccessType = access.access_type,
                                                        Uid = access.uid,
                                                        Comment = access.comment,
                                                        Instruction = access.instruction,
                                                        AddressMode = access.address_mode,
                                                        VariableScope = access.variable_scope,
                                                        IsDeleted = false
                                                    };
                                                    SetBaseField(varAccess);
                                                    varAccessToInsert.Add(varAccess);
                                                }
                                            }
                                        }
                                    }

                                    // Interface
                                    // Block Calls (调用关系)
                                    if (parseResult.calls != null)
                                    {
                                        foreach (var call in parseResult.calls)
                                        {
                                            // 跳过无效的调用关系（caller为空的情况）
                                            if (string.IsNullOrEmpty(call.caller))
                                                continue;

                                            // 通过 compile_unit_id (network_id) 查找对应的 LogicUnitId
                                            long? logicUnitId = null;
                                            if (!string.IsNullOrEmpty(call.network_id) &&
                                                compileUnitToLogicUnitMap.TryGetValue(call.network_id, out var mappedId))
                                            {
                                                logicUnitId = mappedId;
                                            }

                                            var blockCallEntity = new PLC_BlockCallEntity
                                            {
                                                ProjectId = projectId,
                                                CallerBlockId = blockId,
                                                CallerBlockName = call.caller,
                                                CalleeBlockName = call.callee,
                                                CalleeBlockType = call.callee_type,
                                                LogicUnitId = logicUnitId,  // 关联 LogicUnit
                                                NetworkId = int.TryParse(call.network_id, System.Globalization.NumberStyles.HexNumber, null, out var nid) ? nid : null,  // 十六进制转整数
                                                IsDeleted = false
                                            };
                                            SetBaseField(blockCallEntity);
                                            blockCallsToInsert.Add(blockCallEntity);
                                        }
                                    }

                                    // Interface
                                    if (parseResult.@interface != null)
                                    {
                                        foreach (var item in parseResult.@interface)
                                        {
                                            AddInterfaceRecursively(item, blockId, null, 0, interfacesToInsert);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                // DB Transaction
                try
                {
                    using (var tran = _proj.BeginTran())
                    {

                        // ========== 清理旧数据 ==========
                        if (blockNamesToClean.Count > 0)
                        {
                            // 1. 查找该项目下同名的旧Block
                            var oldBlocks = await _blockService.GetListAsync(x =>
                                x.ProjectId == projectId &&
                                blockNamesToClean.Contains(x.BlockName) &&
                                !x.IsDeleted);

                            if (oldBlocks.Count > 0)
                            {
                                var oldBlockIds = oldBlocks.Select(b => b.Id).ToList();
                                var oldSourceFileIds = oldBlocks.Where(b => b.SourceFileId.HasValue).Select(b => b.SourceFileId.Value).Distinct().ToList();

                                // 2. 查找旧Block关联的LogicUnit
                                var oldLogicUnits = await _logicUnitService.GetListAsync(x =>
                                    x.BlockId.HasValue && oldBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted);
                                var oldLogicUnitIds = oldLogicUnits.Select(l => l.Id).ToList();

                                // 方案 D（增量约束）：删旧 LU 之前，软删这些 LU 生成的约束，
                                // 让后台增量任务能重生最新约束。SourceLuId 为 NULL 的旧约束 / 手动录入约束保留不动。
                                // P1（2026-10-09 检查清单 #5）：必须显式排除 Source=Manual——UpdateConstraintRuleAsync
                                // 把 Source 改为 Manual 后保留原 SourceLuId，不排除会把用户手工编辑过的历史自动
                                // 规则一并软删（XMLV2 发布器对 Manual 的保护在更晚的发布阶段，来不及救）。
                                // Source 为 NULL 的历史自动规则仍要清理（SQL 三值逻辑需显式 IS NULL OR）。
                                if (oldLogicUnitIds.Count > 0)
                                {
                                    var staleRuleIds = await _db.Queryable<PLC_IOConstraintRuleEntity>()
                                        .Where(r => r.SourceLuId.HasValue && oldLogicUnitIds.Contains(r.SourceLuId.Value) && !r.IsDeleted
                                            && (r.Source == null || r.Source != "Manual"))
                                        .Select(r => r.Id)
                                        .ToListAsync();

                                    if (staleRuleIds.Count > 0)
                                    {
                                        await _db.Updateable<PLC_IOConstraintSignalEntity>()
                                            .SetColumns(s => new PLC_IOConstraintSignalEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                            .Where(s => staleRuleIds.Contains(s.RuleId) && !s.IsDeleted)
                                            .ExecuteCommandAsync();

                                        await _db.Updateable<PLC_IOConstraintRuleEntity>()
                                            .SetColumns(r => new PLC_IOConstraintRuleEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                            .Where(r => staleRuleIds.Contains(r.Id) && !r.IsDeleted)
                                            .ExecuteCommandAsync();

                                        // 涉及的 IO Tag 状态归零，重新进入约束生成队列
                                        await _ioTagService.UpdateAsync(
                                            t => new PLC_IOTagEntity { ConstraintStatus = 0 },
                                            t => t.ProjectId == projectId && !t.IsDeleted && t.ConstraintStatus != 0);

                                        await WriteLogHelper.WriteLogsAsync(
                                            $"[增量约束] 软删 {staleRuleIds.Count} 条与本次上传 block 关联的旧约束（按 SourceLuId）",
                                            "PLC上传");
                                    }
                                }

                                if (oldLogicUnitIds.Count > 0)
                                {
                                    await _db.Deleteable<PLC_VariableAccessEntity>()
                                        .Where(x => x.LogicUnitId.HasValue && oldLogicUnitIds.Contains(x.LogicUnitId.Value) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                if (oldLogicUnitIds.Count > 0)
                                {
                                    await _db.Deleteable<PLC_LogicUnitEntity>()
                                        .Where(x => x.BlockId.HasValue && oldBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                await _db.Deleteable<PLC_BlockInterfaceEntity>()
                                    .Where(x => x.BlockId.HasValue && oldBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted)
                                    .ExecuteCommandAsync();

                                // 6. 删除 BlockCall
                                await _db.Deleteable<PLC_BlockCallEntity>()
                                    .Where(x => oldBlockIds.Contains(x.CallerBlockId.Value) && !x.IsDeleted)
                                    .ExecuteCommandAsync();

                                // 6.1 删除 DB→FB 绑定关系（按项目+DB块名，与 Block 同生命周期）
                                if (blockNamesToClean.Count > 0)
                                {
                                    await _db.Deleteable<PLC_DbFbBindingEntity>()
                                        .Where(x => x.ProjectId == projectId && blockNamesToClean.Contains(x.DbName) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                // 6.1.1 GRAPH 步位台账与绑定表同生命周期软删（本次清理的 DB 块），
                                // 事务提交后 GraphStepTagRegistrationJob 会按最新绑定重新生成
                                if (blockNamesToClean.Count > 0)
                                {
                                    await _db.Updateable<PLC_GraphStepTagEntity>()
                                        .SetColumns(x => new PLC_GraphStepTagEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                        .Where(x => x.ProjectId == projectId && blockNamesToClean.Contains(x.DbName) && !x.IsDeleted)
                                        .ExecuteCommandAsync();

                                    // GRAPH EXEC_BITS 候补信号同生命周期软删（下次播种按最新绑定幂等补齐）
                                    await SoftDeleteGraphExecBitSignalsForBlocksAsync(projectId, blockNamesToClean);
                                }

                                // 7. 删除 Block
                                await _db.Deleteable<PLC_BlockEntity>()
                                    .Where(x => oldBlockIds.Contains(x.Id) && !x.IsDeleted)
                                    .ExecuteCommandAsync();

                                // 8. 删除 SourceFile
                                if (oldSourceFileIds.Count > 0)
                                {
                                    await _db.Deleteable<PLC_SourceFileEntity>()
                                        .Where(x => oldSourceFileIds.Contains(x.Id) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                            }
                        }
                        // P3: IO Tag 清空移到 oldBlocks 条件外——与后台路径（ExecuteBackgroundUploadAsync）对齐：
                        // 「纯 CSV zip」场景先清后插，避免旧 tag 不清、新 tag 照插造成重复堆积
                        if (tagToToInsert.Count > 0)
                        {
                            await _db.Deleteable<PLC_IOTagEntity>()
                               .Where(x => x.ProjectId == projectId && !x.IsDeleted)
                               .ExecuteCommandAsync();
                        }
                        // ========== 清理完毕，插入新数据 ==========

                        // No need to insert project, just update if we changed it
                        // await _proj.UpdateAsync(projectEntity); 

                        if (sourceFilesToInsert.Count > 0) await _sourceFileService.BulkCopyAsync(sourceFilesToInsert);
                        if (blocksToInsert.Count > 0) await _blockService.BulkCopyAsync(blocksToInsert);
                        if (logicUnitsToInsert.Count > 0) await _logicUnitService.BulkCopyAsync(logicUnitsToInsert);
                        if (interfacesToInsert.Count > 0) await _blockInterfaceService.BulkCopyAsync(interfacesToInsert);
                        if (varAccessToInsert.Count > 0) await _variableAccessService.BulkCopyAsync(varAccessToInsert);
                        if (blockCallsToInsert.Count > 0) await _blockCallService.BulkCopyAsync(blockCallsToInsert);
                        if (dbFbBindingsToInsert.Count > 0) await _dbFbBindingService.BulkCopyAsync(dbFbBindingsToInsert);
                        if (tagToToInsert.Count > 0) await _ioTagService.BulkCopyAsync(tagToToInsert);

                        // ========== 后处理：统计并更新 Block 的聚合字段 ==========
                        await PostProcessBlockStatisticsAsync(projectId, blocksToInsert, logicUnitsToInsert, blockCallsToInsert);

                        // ========== 后处理：计算 DB 块的 S7 偏移量 ==========
                        // 2026-08-26 注释：偏移统一由 Python 解析器预计算（含 taint 机制——尺寸未解析成员起不保存偏移，
                        // OffsetByte=NULL 语义=不可信）。此处 C# S7OffsetCalculator 会把全 NULL 块当"未计算"重算，
                        // 且其 TypeMap 过时（TON 按 12B）+ 未知类型按 2 字节编造，会把刻意清空的脏偏移回填。
                        // 与后台路径 ExecuteBackgroundUploadAsync 保持一致：不再调用。
                        // await _offsetCalculator.CalculateOffsetsForProjectAsync(projectId);


                        tran.Complete();
                        // DB→FB 绑定可能已换绑，清空工步前缀缓存
                        _dbFbBindingService.InvalidateStepPrefixCache();
                    }
                }
                catch (Exception dbEx)
                {
                    _proj.RollbackTran();
                    throw new Exception("Database error: " + dbEx.Message);
                }

                // 2026-09-04 PLC 自包含改造：移除上传后自动写 LineUp GRAPH 草稿的联动
                //（原 _graphDraftCoordinator.CreateDraftsAsync；LineUp 需要流程图走导入工作台，
                //  PreviewFromBlock 免上传复用 ai_plc_sourcefile.filecontent 的能力保留）。

                result.IsSuccess = true;
                result.BackResult = projectId.ToString(); // Return Project ID

                // 触发 IO 约束预处理后台任务
                try
                {
                   // Agent.Services.Plc_diagnosis.IOConstraintPreprocessService.EnqueueIfNotRunning(projectId);
                }
                catch (Exception constraintEx)
                {
                    _logger.LogWarning(constraintEx, "[PLC上传] 触发 IO 约束预处理失败（不影响上传结果）");
                }

            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Error: {ex.Message}";
            }
            finally
            {
                if (Directory.Exists(tempFolder))
                {
                    try { Directory.Delete(tempFolder, true); } catch { }
                }
            }

            return result;
        }

        /// <summary>
        /// 后处理：根据已插入的数据统计并更新 Block 的聚合字段
        /// - NetworksCount: 该块包含的 LogicUnit(Network) 数量
        /// - CallsOutCount: 该块调用其他块的数量（出度）
        /// - CalledByCount: 该块被其他块调用的数量（入度）
        /// - IsPotentialEntry: 是否为潜在入口块（没有被任何其他块调用）
        /// </summary>
        private async Task PostProcessBlockStatisticsAsync(
            long projectId,
            List<PLC_BlockEntity> blocksToInsert,
            List<PLC_LogicUnitEntity> logicUnitsToInsert,
            List<PLC_BlockCallEntity> blockCallsToInsert)
        {
            if (blocksToInsert.Count == 0) return;

            // 同时查询该项目下所有已有的 Block 和 BlockCall（包含之前导入的其他文件）
            var allProjectBlocks = await _blockService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);
            var allProjectCalls = await _blockCallService.GetListAsync(x =>
                x.ProjectId == projectId && !x.IsDeleted);
            // 仅统计该项目 LU 的 BlockId 计数（2026-08-27 修复：原实现 GetListAsync 无项目过滤，
            // 加载全库所有项目的 LU 实体——含 RawContent/UidSymbolMapJson 大字段，多项目库时
            // 一次拉几十万行大文本，PostProcess 阶段卡几分钟；而统计只用到 BlockId 计数，
            // 改为按项目块 id 过滤 + 仅取 BlockId 列，数据量从 GB 级降到 3488 个 long）
            var projectBlockIds = new HashSet<long>(allProjectBlocks.Select(b => b.Id));
            var luBlockIdCounts = (await _db.Queryable<PLC_LogicUnitEntity>()
                    .Where(x => x.BlockId.HasValue && projectBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted)
                    .Select(x => x.BlockId.Value)
                    .ToListAsync())
                .GroupBy(id => id)
                .ToDictionary(g => g.Key, g => g.Count());

            // 构建 BlockName -> BlockId 映射（用于通过 CalleeBlockName 匹配入度）
            var blockNameToIds = allProjectBlocks
                .Where(b => !string.IsNullOrEmpty(b.BlockName))
                .GroupBy(b => b.BlockName)
                .ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());

            // 收集所有被调用的块名（用于判定 IsPotentialEntry）
            var allCalledBlockNames = new HashSet<string>(
                allProjectCalls
                    .Where(c => !string.IsNullOrEmpty(c.CalleeBlockName))
                    .Select(c => c.CalleeBlockName),
                StringComparer.OrdinalIgnoreCase);

            var blocksToUpdate = new List<PLC_BlockEntity>();

            foreach (var block in allProjectBlocks)
            {
                bool changed = false;

                // 1. NetworksCount - 该块包含的 Network/LogicUnit 数量
                var networksCount = luBlockIdCounts.TryGetValue(block.Id, out var luCount) ? luCount : 0;
                if (block.NetworksCount != networksCount)
                {
                    block.NetworksCount = networksCount;
                    changed = true;
                }

                // 2. CallsOutCount - 该块作为 Caller 调用其他块的数量（出度）
                var callsOutCount = allProjectCalls.Count(c => c.CallerBlockId == block.Id);
                if (block.CallsOutCount != callsOutCount)
                {
                    block.CallsOutCount = callsOutCount;
                    changed = true;
                }

                // 3. CalledByCount - 该块被其他块调用的数量（入度）
                // 通过匹配 CalleeBlockName == block.BlockName
                var calledByCount = allProjectCalls.Count(c =>
                    string.Equals(c.CalleeBlockName, block.BlockName, StringComparison.OrdinalIgnoreCase));
                if (block.CalledByCount != calledByCount)
                {
                    block.CalledByCount = calledByCount;
                    changed = true;
                }

                // 4. IsPotentialEntry - 没有被任何其他块调用的就是潜在入口
                var isPotentialEntry = !allCalledBlockNames.Contains(block.BlockName ?? "");
                if (block.IsPotentialEntry != isPotentialEntry)
                {
                    block.IsPotentialEntry = isPotentialEntry;
                    changed = true;
                }

                if (changed)
                {
                    block.ModifyTime = DateTime.Now;
                    blocksToUpdate.Add(block);
                }
            }

            // 批量更新
            if (blocksToUpdate.Count > 0)
            {
                await _blockService.BulkUpdateAsync(blocksToUpdate);
            }
        }

        private void AddInterfaceRecursively(PLC_InterfaceDTO item, long blockId, long? parentId, int level, List<PLC_BlockInterfaceEntity> list)
        {
            var id = GeneratePrimaryKeyIdHelper.GetLockPrimaryKeyId();

            // 计算 SizeBits: Python 提供的 size_in_bytes 转为 bits
            int? sizeBits = null;
            if (item.size_in_bytes > 0)
            {
                if (item.size_in_bytes < 1) // Bool: 0.125 bytes = 1 bit
                    sizeBits = 1;
                else
                    sizeBits = (int)(item.size_in_bytes * 8);
            }

            // 判断是否为UDT/FB引用类型（datatype带引号的是UDT引用）
            string referencedType = null;
            if (!string.IsNullOrEmpty(item.datatype))
            {
                var dt = item.datatype.Trim();
                if (dt.StartsWith("\"") && dt.EndsWith("\""))
                    referencedType = dt.Trim('"');
                else if (item.is_structure && dt != "Struct")
                    referencedType = dt; // 非Struct的结构体类型（如 G7_RTDataPlus_V6）
            }

            var entity = new PLC_BlockInterfaceEntity
            {
                Id = id,
                BlockId = blockId,
                VariableName = item.name,
                VariablePath = item.path,
                DataType = item.datatype,
                Scope = item.scope,
                Comment = item.comment,
                InitialValue = item.initial_value,
                Remanence = item.remanence,
                ParentId = parentId,
                NestingLevel = level,
                ArrayInfo = item.array_info,
                // ===== Python解析器预计算的偏移量 =====
                OffsetByte = item.byte_offset >= 0 ? item.byte_offset : null,
                OffsetBit = item.byte_offset >= 0 ? item.bit_offset : null,
                SizeBits = sizeBits,
                Offset = item.offset,
                ReferencedTypeName = referencedType,
                IsDeleted = false
            };
            SetBaseField(entity);
            list.Add(entity);

            if (item.members != null)
            {
                foreach (var member in item.members)
                {
                    AddInterfaceRecursively(member, blockId, id, level + 1, list);
                }
            }
        }

        private void SetBaseField(object entity)
        {
            // Reflection to set BaseFields if types are known, or manual set if we know the type
            // BaseField properties: CreaterId, CreaterName, CreateTime, IsDeleted
            // Assuming entity inherits from BaseField or has these properties
            var currentUser = CurrentUserInfo.Instance; // Assuming static access
            var baseField = entity as BaseField;
            if (baseField != null)
            {
                baseField.CreateTime = DateTime.Now;
                baseField.IsDeleted = false;
                baseField.ModifierId = currentUser.UserID;
                baseField.ModifyTime = baseField.CreateTime;
                baseField.CreaterName = currentUser.UserName;
                baseField.ModifierName = currentUser.UserName;
                try
                {
                    // Access CurrentUserInfo via HttpContext or similar if Helper is singleton/static usually
                    // If CurrentUserInfo is a specific class with static members:
                    // baseField.CreaterId = CurrentUserInfo.UserId;
                    // baseField.CreaterName = CurrentUserInfo.UserName;

                    // Since I don't reference CurrentUserInfo static class here directly to avoid compile error if namespace mismatch options, 
                    // I'll leave it simple for now or use the helper if I find it.
                    // The imports include HSJM.AI.Utility, assuming CurrentUserInfo is static there.

                    // baseField.CreaterId = CurrentUserInfo.Current?.Id ?? 0;
                    // But CurrentUserInfo.cs I saw earlier was a class file, might be a Model.
                    // Often it's pulled from Claims.
                }
                catch { }
            }
        }


        /// <summary>
        /// 上传并解析IO标签CSV文件
        /// </summary>
        public async Task<List<PLC_IOTagEntity>> ParseTagCSV(string filePath, long projectId)
        {
            try
            {

                var tagsToInsert = new List<PLC_IOTagEntity>();

                // Try to detect encoding - read first 3 bytes for BOM check
                System.Text.Encoding encoding;
                using (var fs = new FileStream(filePath, FileMode.Open, access: FileAccess.Read))
                {
                    var bom = new byte[3];
                    int bytesRead = fs.Read(bom, 0, 3);

                    // UTF-8 BOM: 0xEF 0xBB 0xBF
                    if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    {
                        encoding = System.Text.Encoding.UTF8;
                    }
                    else
                    {
                        // Try GB2312/GBK for Chinese CSV files from Windows
                        try
                        {
                            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                            encoding = System.Text.Encoding.GetEncoding("GB2312");
                        }
                        catch
                        {
                            encoding = System.Text.Encoding.UTF8;
                        }
                    }
                }

                // Re-open stream with detected encoding
                using (var reader = new StreamReader(filePath, encoding: encoding, detectEncodingFromByteOrderMarks: true, options: new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read }))
                {
                    string headerLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        return tagsToInsert;
                    }

                    // Parse header to get column indexes
                    var headers = headerLine.Split(',').Select(h => h.Trim()).ToList();
                    int pathIdx = headers.IndexOf("Path");
                    int tableNameIdx = headers.IndexOf("TableName");
                    int tagNameIdx = headers.IndexOf("TagName");
                    int addressIdx = headers.IndexOf("Address");
                    int dataTypeIdx = headers.IndexOf("DataType");
                    int isSafetyIdx = headers.IndexOf("IsSafety");
                    int retentiveIdx = headers.IndexOf("Retentive");
                    int hmiVisibleIdx = headers.IndexOf("HmiVisible");
                    int hmiWritableIdx = headers.IndexOf("HmiWritable");
                    int commentIdx = headers.IndexOf("Comment");

                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var values = ParseCsvLine(line);
                        if (values.Count == 0) continue;

                        var tagName = GetValueSafe(values, tagNameIdx);

                        if (tagName == null) continue;

                        var tag = new PLC_IOTagEntity
                        {
                            ProjectId = projectId,
                            Path = GetValueSafe(values, pathIdx),
                            TableName = GetValueSafe(values, tableNameIdx),
                            TagName = tagName,
                            Address = GetValueSafe(values, addressIdx),
                            DataType = GetValueSafe(values, dataTypeIdx),
                            IsSafety = ParseBool(GetValueSafe(values, isSafetyIdx)),
                            IsRetentive = ParseBool(GetValueSafe(values, retentiveIdx)),
                            HmiVisible = ParseBool(GetValueSafe(values, hmiVisibleIdx)),
                            HmiWritable = ParseBool(GetValueSafe(values, hmiWritableIdx)),
                            Comment = GetValueSafe(values, commentIdx),
                            IsDeleted = false
                        };
                        SetBaseField(tag);
                        tagsToInsert.Add(tag);
                    }
                }

                return tagsToInsert;
            }
            catch (Exception ex)
            {
                return new List<PLC_IOTagEntity>();
            }
        }



        /// <summary>
        /// 上传并解析IO标签CSV文件
        /// </summary>
        public async Task<HttpResponseResultModel<int>> UploadIOTagsCsvAsync(IFormFile file, long projectId)
        {
            var result = new HttpResponseResultModel<int>();

            try
            {
                // Verify project exists
                var project = await _proj.GetByIdAsync(projectId);
                if (project == null)
                {
                    result.ErrorMessage = "项目不存在";
                    return result;
                }

                var tagsToInsert = new List<PLC_IOTagEntity>();

                // Try to detect encoding - first read as bytes
                System.Text.Encoding encoding;
                using (var memStream = new MemoryStream())
                {
                    await file.CopyToAsync(memStream);
                    memStream.Position = 0;
                    var bytes = memStream.ToArray();

                    // Simple heuristic: if valid UTF-8 and contains UTF-8 BOM, use UTF-8
                    // Otherwise try GB2312 for Chinese Windows files
                    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    {
                        encoding = System.Text.Encoding.UTF8;
                    }
                    else
                    {
                        // Try GB2312/GBK for Chinese CSV files from Windows
                        try
                        {
                            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                            encoding = System.Text.Encoding.GetEncoding("GB2312");
                        }
                        catch
                        {
                            encoding = System.Text.Encoding.UTF8;
                        }
                    }
                }

                // Re-open stream with detected encoding
                using (var reader = new StreamReader(file.OpenReadStream(), encoding))
                {
                    string headerLine = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        result.ErrorMessage = "CSV文件为空";
                        return result;
                    }

                    // Parse header to get column indexes
                    var headers = headerLine.Split(',').Select(h => h.Trim()).ToList();
                    int pathIdx = headers.IndexOf("Path");
                    int tableNameIdx = headers.IndexOf("TableName");
                    int tagNameIdx = headers.IndexOf("TagName");
                    int addressIdx = headers.IndexOf("Address");
                    int dataTypeIdx = headers.IndexOf("DataType");
                    int isSafetyIdx = headers.IndexOf("IsSafety");
                    int retentiveIdx = headers.IndexOf("Retentive");
                    int hmiVisibleIdx = headers.IndexOf("HmiVisible");
                    int hmiWritableIdx = headers.IndexOf("HmiWritable");
                    int commentIdx = headers.IndexOf("Comment");

                    string line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var values = ParseCsvLine(line);
                        if (values.Count == 0) continue;

                        var tag = new PLC_IOTagEntity
                        {
                            ProjectId = projectId,
                            Path = GetValueSafe(values, pathIdx),
                            TableName = GetValueSafe(values, tableNameIdx),
                            TagName = GetValueSafe(values, tagNameIdx),
                            Address = GetValueSafe(values, addressIdx),
                            DataType = GetValueSafe(values, dataTypeIdx),
                            IsSafety = ParseBool(GetValueSafe(values, isSafetyIdx)),
                            IsRetentive = ParseBool(GetValueSafe(values, retentiveIdx)),
                            HmiVisible = ParseBool(GetValueSafe(values, hmiVisibleIdx)),
                            HmiWritable = ParseBool(GetValueSafe(values, hmiWritableIdx)),
                            Comment = GetValueSafe(values, commentIdx),
                            IsDeleted = false
                        };
                        SetBaseField(tag);
                        tagsToInsert.Add(tag);
                    }
                }

                if (tagsToInsert.Count == 0)
                {
                    result.ErrorMessage = "CSV文件中没有有效数据";
                    return result;
                }

                // ========== 清理该项目下的旧IOTag ==========
                await _ioTagService.DeleteAsync(x => x.ProjectId == projectId && !x.IsDeleted);

                // Bulk insert
                await _ioTagService.BulkCopyAsync(tagsToInsert);

                //更新访问表IsIoTag标签
                await ExecuteBulkUpdateVariable(projectId, tagsToInsert);

                // M-2（2026-09-20）：单独导入 IO 标签 CSV 后，IGW 项目刷新 IGW 上的 IO 注册（增量，只整体替换 IO 根组）；
                // 老网关项目行为不变。入队失败不影响导入结果（可用「注册PLC标签」按钮全量兜底）
                if (PLC_IgwRegistrationJob.IsIgwProject(project.IotUrl))
                {
                    try
                    {
                        var igwResult = await PLC_IgwRegistrationJob.EnqueueAsync(projectId, Array.Empty<string>(), Array.Empty<string>(),
                            ioTagsRefreshed: true, full: false, taskId: null,
                            _serviceProvider.GetRequiredService<IPLC_IgwRegistrationStateService>(), _logger).ConfigureAwait(false);
                        _logger.LogInformation("[IO标签导入] 项目 {ProjectId} IGW IO 注册刷新{Result}", projectId,
                            igwResult == IgwEnqueueResult.Enqueued ? "已入队" : "已并入执行中任务的补跑");
                    }
                    catch (Exception igwEx)
                    {
                        _logger.LogWarning(igwEx, "[IO标签导入] 项目 {ProjectId} 入队 IGW IO 注册刷新失败（不影响导入，可用「注册PLC标签」按钮全量兜底）", projectId);
                    }
                }

                result.IsSuccess = true;
                result.BackResult = tagsToInsert.Count;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"解析CSV失败: {ex.Message}";
            }

            return result;
        }

        private List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString().Trim());
            return result;
        }

        private string GetValueSafe(List<string> values, int index)
        {
            return index >= 0 && index < values.Count ? values[index] : null;
        }

        private bool? ParseBool(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || value == "1";
        }

        /// <summary>
        /// 尝试读取 GRAPH 块源 xml 原文（仅当解析结果的首块为 GRAPH 语言时）。
        /// 归档到 ai_plc_sourcefile.filecontent，供 LineUp PreviewFromBlock 免上传预览；
        /// 读取失败不阻断上传（返回 null，仅失去预览复用能力）。
        /// 注意：必须在上传解析窗口内调用——temp 目录在上传结束后即被删除。
        /// </summary>
        private static string TryReadGraphSourceContent(string filePath, PLC_ParseResultDTO parseResult)
        {
            try
            {
                var blockInfo = parseResult?.blocks?.FirstOrDefault();
                if (blockInfo == null)
                {
                    return null;
                }
                if (!string.Equals(blockInfo.language, "GRAPH", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return File.ReadAllText(filePath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>计算本机专用 Hangfire 队列名：local_&lt;归一化机器名&gt;。
        /// 与 HSJM.AI/HangfireQueueNames.BuildCurrentServiceQueue、GraphStepTagRegistrationJob.BuildCurrentMachineQueue
        /// 保持等价语义（AppLayer 不能引用 HSJM.AI 顶层项目，故 inline，第三份拷贝）。</summary>
        private static string BuildCurrentMachineQueue()
        {
            var raw = Environment.GetEnvironmentVariable("HSJM_HANGFIRE_LOCAL_QUEUE");
            if (string.IsNullOrWhiteSpace(raw)) raw = Environment.MachineName;
            var builder = new StringBuilder();
            foreach (var ch in raw.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                    builder.Append(ch);
                else if (builder.Length == 0 || builder[builder.Length - 1] != '_')
                    builder.Append('_');
            }
            var normalized = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(normalized)) normalized = "current";
            return $"local_{normalized}";
        }

        private static void ExtractZipToWorkingDirectory(string zipPath, string extractPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath))
            {
                throw new ArgumentException("Zip path cannot be empty.", nameof(zipPath));
            }

            if (string.IsNullOrWhiteSpace(extractPath))
            {
                throw new ArgumentException("Extract path cannot be empty.", nameof(extractPath));
            }

            if (Directory.Exists(extractPath))
            {
                Directory.Delete(extractPath, true);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);
        }

        private static Dictionary<string, string> LoadBlockPathMappings(string extractPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(extractPath) || !Directory.Exists(extractPath))
            {
                return result;
            }

            var mappingFiles = Directory.GetFiles(extractPath, "block_path.txt", SearchOption.AllDirectories);
            foreach (var mappingFile in mappingFiles)
            {
                foreach (var rawLine in File.ReadLines(mappingFile))
                {
                    if (string.IsNullOrWhiteSpace(rawLine))
                    {
                        continue;
                    }

                    var separatorIndex = rawLine.IndexOf(',');
                    if (separatorIndex < 0)
                    {
                        continue;
                    }

                    var blockPath = rawLine.Substring(0, separatorIndex).Trim();
                    var blockName = rawLine.Substring(separatorIndex + 1).Trim();
                    if (string.IsNullOrWhiteSpace(blockName) || result.ContainsKey(blockName))
                    {
                        continue;
                    }

                    result[blockName] = blockPath;
                }
            }

            return result;
        }

        /// <summary>
        /// 比较库内现有 IO 点集合与本次待写集合的语义内容是否一致（P2，2026-10-09 检查清单 #7）。
        /// 比较字段：点名（键）＋地址/类型/注释（约束与说明引用的语义字段）；行序无关。
        /// 相同 CSV 重复上传 → true（零变更跳过）；点名/地址/类型/注释任一变化 → false（触发约束发布）。
        /// </summary>
        private static bool SameTagContent(List<PLC_IOTagEntity> existing, List<PLC_IOTagEntity> incoming)
        {
            if (existing == null) existing = new List<PLC_IOTagEntity>();
            if (incoming == null) incoming = new List<PLC_IOTagEntity>();
            if (existing.Count != incoming.Count) return false;

            static string Key(PLC_IOTagEntity t) => (t.TagName ?? string.Empty).Trim();
            static string Value(PLC_IOTagEntity t) => string.Join("|",
                (t.TagName ?? string.Empty).Trim(),
                (t.Address ?? string.Empty).Trim(),
                (t.DataType ?? string.Empty).Trim(),
                (t.Comment ?? string.Empty).Trim());

            var existingSorted = existing.Select(t => (Key: Key(t), Value: Value(t)))
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            var incomingSorted = incoming.Select(t => (Key: Key(t), Value: Value(t)))
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            for (var i = 0; i < existingSorted.Count; i++)
            {
                if (!string.Equals(existingSorted[i].Key, incomingSorted[i].Key, StringComparison.Ordinal)
                    || !string.Equals(existingSorted[i].Value, incomingSorted[i].Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// GRAPH EXEC_BITS 候补信号（ai_plc_station_shadow_signal，AnchorType=GraphExecBit）与绑定表同生命周期：
        /// DB 块重传/换绑后，旧候补信号按 DbName 前缀软删失效；下次播种
        /// （extract-stations 站码回填钩子 / 一键分析自动重播 / 手动 seed-signals）按最新绑定幂等补齐。
        /// 仅清 EXEC_BITS 候补，锚点信号归一键分析的删三表重建管，不越界。
        /// </summary>
        private async Task SoftDeleteGraphExecBitSignalsForBlocksAsync(long projectId, IEnumerable<string> blockNames)
        {
            foreach (var blockName in blockNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var prefix = blockName.Trim() + ".";
                await _db.Updateable<PLC_StationShadowSignalEntity>()
                    .SetColumns(x => new PLC_StationShadowSignalEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                    .Where(x => x.ProjectId == projectId
                        && x.AnchorType == "GraphExecBit"
                        && x.VariableName.StartsWith(prefix)
                        && !x.IsDeleted)
                    .ExecuteCommandAsync();
            }
        }

        private static string ResolveBlockDescription(
            string blockName,
            string parserDescription,
            IReadOnlyDictionary<string, string> blockPathMap)
        {
            var blockPath = "";
            blockPathMap?.TryGetValue(blockName ?? string.Empty, out  blockPath);
            if (string.IsNullOrWhiteSpace(blockPath))
            {
                return parserDescription;
            }

            if (string.IsNullOrWhiteSpace(parserDescription))
            {
                return blockPath;
            }

            if (parserDescription.IndexOf(blockPath, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return parserDescription;
            }

            return $"{blockPath} | {parserDescription}";
        }

        /// <summary>
        /// 计算原始文件内容 SHA256（流式读取，失败返回 null 不阻断上传）。
        /// 写入 PLC_SourceFileEntity.FileHash。
        /// </summary>
        private static string ComputeFileSha256(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
                using var stream = File.OpenRead(filePath);
                var bytes = System.Security.Cryptography.SHA256.HashData(stream);
                return Convert.ToHexString(bytes);
            }
            catch
            {
                return null; // 哈希失败不阻断上传流程
            }
        }

        /// <summary>
        /// 计算上传源文件的「解析输入指纹」（P1，2026-10-09 检查清单 #3）：主文件 SHA256
        /// ＋ 实际参与解析的伴随源码（.scl/.awl）SHA256。
        /// parser.py 的 _try_load_companion_source 会在同目录/父目录/同级子目录按「块名或
        /// XML 文件名同名」查找 .scl/.awl 填充 XML 缺少的程序体——只按主 XML 计算 hash 时，
        /// 「XML 不变、伴随源码变了」会被误判为内容未变而跳过重解析，数据库继续保留旧逻辑。
        /// 候选口径与 Python 侧对齐（检查清单 #3 复核：只按 XML 文件名找会漏）：
        /// ① 候选名 = XML 文件名 stem ＋ XML 内 SW.Blocks.* 声明的块名（去引号）——导出
        ///    文件名≠块名（export_1.xml 定义 FB_A、程序体来自 FB_A.scl）时，块名候选是
        ///    唯一能感知 FB_A.scl 变化的来源；
        /// ② 块语言 SCL→.scl、STL→.awl（parser 只对 SCL/STL 块加载伴随源码），语言缺失时
        ///    两个扩展名都算（多算只触发偶发的多余重解析，不会漏）；其它已知语言（LAD/FBD/
        ///    GRAPH 等）parser 不会加载伴随，不加候选；CSV 无伴随概念不参与。
        /// 指纹算法变更会使存量 hash 一次性失配 → 部署后首次上传全量重解析一遍，属预期行为。
        /// 指纹用「相对主文件目录的路径」做前缀（解压目录每次上传都不同，绝对路径会使指纹永远不稳定）。
        /// </summary>
        private static string ComputeSourceFingerprint(string filePath)
        {
            var mainHash = ComputeFileSha256(filePath);
            if (mainHash == null) return null;
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return mainHash;
                var dirPath = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(dirPath)) return mainHash;
                var dir = new DirectoryInfo(dirPath);
                var stem = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrWhiteSpace(stem)) return mainHash;

                // 候选（文件名, 扩展名）集合：stem 双扩展名（语言未知，保持历史口径）＋块名按语言映射
                var candidates = new List<(string Name, string Ext)>();
                foreach (var ext in new[] { ".scl", ".awl" }) candidates.Add((stem, ext));
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stem };
                foreach (var pair in ExtractBlockNameLanguagePairs(filePath))
                {
                    var blockName = (pair.Name ?? string.Empty).Trim().Trim('"', '\'').Trim();
                    if (string.IsNullOrWhiteSpace(blockName) || !seenNames.Add(blockName)) continue;
                    switch ((pair.Language ?? string.Empty).Trim().Trim('"', '\'').Trim().ToUpperInvariant())
                    {
                        case "SCL":
                            candidates.Add((blockName, ".scl"));
                            break;
                        case "STL":
                            candidates.Add((blockName, ".awl"));
                            break;
                        case "":
                            // XML 里没写语言（或提取不到）：保守起见两个扩展名都纳入
                            candidates.Add((blockName, ".scl"));
                            candidates.Add((blockName, ".awl"));
                            break;
                        default:
                            // LAD/FBD/GRAPH 等已知语言：parser 不会为它们加载伴随源码
                            break;
                    }
                }

                // 目录范围与 parser.py _try_load_companion_source 一致：同目录 → 父目录 → 同级子目录
                var searchDirs = new List<DirectoryInfo> { dir };
                if (dir.Parent != null) searchDirs.Add(dir.Parent);
                try
                {
                    foreach (var sub in dir.EnumerateDirectories()) searchDirs.Add(sub);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                var parts = new List<string>();
                foreach (var searchDir in searchDirs)
                {
                    string rel;
                    try { rel = Path.GetRelativePath(dir.FullName, searchDir.FullName); }
                    catch { continue; }
                    foreach (var candidate in candidates)
                    {
                        var candidatePath = Path.Combine(searchDir.FullName, candidate.Name + candidate.Ext);
                        var companionHash = ComputeFileSha256(candidatePath);
                        if (companionHash != null)
                        {
                            parts.Add($"{rel}/{candidate.Name}{candidate.Ext}:{companionHash}");
                        }
                    }
                }
                if (parts.Count == 0) return mainHash;

                parts.Sort(StringComparer.Ordinal);
                var combined = mainHash + "|" + string.Join("|", parts);
                var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(combined));
                return Convert.ToHexString(bytes);
            }
            catch
            {
                return mainHash; // 伴随文件探测失败退化为仅主文件指纹，不阻断上传
            }
        }

        /// <summary>
        /// 从 TIA Openness XML 流式提取 (块名, 编程语言) 对，口径对齐 parser.py 的 blocks
        /// 字段映射（./AttributeList/Name、./AttributeList/ProgrammingLanguage，见
        /// brands/siemens_tia_portal.yaml extraction.blocks）。只认每个 SW.Blocks.* 元素的
        /// 直接子级 AttributeList；嵌套在 ObjectList 里的 Member/CompileUnit 自己的
        /// AttributeList/Name（那是成员名/网络名，不是块名）一律按深度挡掉。
        /// SW.Types.PlcStruct（UDT）不含程序体、parser 不会为它加载伴随源码，不在提取范围。
        /// XML 非法 / 非 XML 文件返回空集合（指纹退化为仅按文件名 stem 找伴随，不阻断上传）。
        /// </summary>
        private static List<(string Name, string Language)> ExtractBlockNameLanguagePairs(string xmlFilePath)
        {
            var pairs = new List<(string Name, string Language)>();
            try
            {
                if (string.IsNullOrEmpty(xmlFilePath) || !File.Exists(xmlFilePath)) return pairs;
                var settings = new System.Xml.XmlReaderSettings
                {
                    // DTD 一律忽略（TIA 导出无 DTD；不解析可从根本上规避 DTD 实体展开）
                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                    IgnoreComments = true,
                    XmlResolver = null,
                };
                using var reader = System.Xml.XmlReader.Create(xmlFilePath, settings);
                int blockDepth = -1;   // 当前所在 SW.Blocks.* 元素深度；-1 = 块外
                int attrDepth = -1;    // 当前所在块级 AttributeList 深度；-1 = 不在
                string blockName = null;
                string blockLanguage = null;
                while (reader.Read())
                {
                    if (blockDepth < 0)
                    {
                        // 块外：找 SW.Blocks.* 开始标记（FB/FC/DB/OB/GlobalDB/InstanceDB）
                        if (reader.NodeType == System.Xml.XmlNodeType.Element
                            && reader.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)
                            && !reader.IsEmptyElement)
                        {
                            blockDepth = reader.Depth;
                            attrDepth = -1;
                            blockName = null;
                            blockLanguage = null;
                        }
                        continue;
                    }

                    if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == blockDepth)
                    {
                        // 块结束：有名字才收录（异常结构跳过）
                        if (!string.IsNullOrWhiteSpace(blockName))
                        {
                            pairs.Add((blockName, blockLanguage));
                        }
                        blockDepth = -1;
                        attrDepth = -1;
                        continue;
                    }

                    if (attrDepth >= 0)
                    {
                        // AttributeList 内：只认直接子元素
                        if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == attrDepth)
                        {
                            attrDepth = -1;
                            continue;
                        }
                        if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Depth == attrDepth + 1)
                        {
                            if (reader.LocalName == "Name" && blockName == null)
                            {
                                blockName = reader.ReadElementContentAsString();
                            }
                            else if (reader.LocalName == "ProgrammingLanguage" && blockLanguage == null)
                            {
                                blockLanguage = reader.ReadElementContentAsString();
                            }
                        }
                        continue;
                    }

                    // 块内但还没进 AttributeList：只对「直接子级」的 AttributeList 开哨；
                    // ObjectList 等其它子树自然下潜，深层的 AttributeList/Name 被 depth 判定挡住
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Depth == blockDepth + 1
                        && reader.LocalName == "AttributeList" && !reader.IsEmptyElement)
                    {
                        attrDepth = reader.Depth;
                    }
                }
                if (blockDepth >= 0 && !string.IsNullOrWhiteSpace(blockName))
                {
                    // 文件在块中间截断的极端情况：已拿到的名字仍可用（保守收录）
                    pairs.Add((blockName, blockLanguage));
                }
                return pairs.Take(64).ToList();
            }
            catch
            {
                return pairs.Take(64).ToList(); // XML 非法 → 退化为仅 stem 候选，不阻断
            }
        }

        /// <summary>
        /// 计算 GRAPH 块内容 SHA256。拼接串：graph-v1|块名|每个GRAPH类LogicUnit的
        /// unit_type/network_id/title/raw_content/outgoing_transition_numbers。
        /// raw_content 覆盖 Transition/StepAction/Supervision/Interlock 的原始 XML，
        /// outgoing_transition_numbers 覆盖块级 &lt;Connections&gt; 的 Step 出口拓扑（不在任何 LU 的 raw_content 里）。
        /// 仅 GRAPH 块返回值（语言判定按 blocks[0].language），非 GRAPH 块返回 null。
        /// </summary>
        private static string ComputeGraphSourceHash(PLC_ParseResultDTO parseResult)
        {
            var blockInfo = (parseResult?.blocks != null && parseResult.blocks.Count > 0) ? parseResult.blocks[0] : null;
            if (blockInfo == null) return null;
            if (!string.Equals(blockInfo.language, "GRAPH", StringComparison.OrdinalIgnoreCase)) return null;

            var sb = new StringBuilder("graph-v1|");
            sb.Append(blockInfo.name ?? string.Empty).Append('|');

            if (parseResult.logic_units != null)
            {
                foreach (var unit in parseResult.logic_units)
                {
                    var ut = unit?.unit_type ?? string.Empty;
                    var isGraphUnit = ut.Equals("TRANSITION", StringComparison.OrdinalIgnoreCase)
                        || (ut.StartsWith("STEP_", StringComparison.OrdinalIgnoreCase)
                            && (ut.EndsWith("_ACTION", StringComparison.OrdinalIgnoreCase)
                                || ut.EndsWith("_SUPERVISION", StringComparison.OrdinalIgnoreCase)
                                || ut.EndsWith("_INTERLOCK", StringComparison.OrdinalIgnoreCase)));
                    if (!isGraphUnit) continue;

                    sb.Append(ut).Append('|')
                      .Append(unit.network_id).Append('|')
                      .Append(unit.title ?? string.Empty).Append('|')
                      .Append(unit.raw_content ?? string.Empty).Append('|');
                    if (unit.outgoing_transition_numbers != null && unit.outgoing_transition_numbers.Count > 0)
                    {
                        sb.Append(string.Join(",", unit.outgoing_transition_numbers));
                    }
                    sb.Append('|');
                }
            }

            var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes);
        }

        #region 异步后台上传

        /// <summary>
        /// 启动异步后台上传任务
        /// 立即返回任务ID，后台使用 Hangfire 执行
        /// </summary>
        public async Task<HttpResponseResultModel<UploadTaskStartResult>> StartBackgroundUploadAsync(IFormFile file, long projectId, bool fullProjectUpdate = false)
        {
            var result = new HttpResponseResultModel<UploadTaskStartResult>();

            try
            {
                // Verify project exists
                var projectEntity = await _proj.GetByIdAsync(projectId);
                if (projectEntity == null)
                {
                    result.ErrorMessage = "项目不存在";
                    return result;
                }

                // Generate task ID
                var taskId = $"plc_upload_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                // Save file to temp location (for background job to pick up)
                string tempFolder = Path.Combine(_env.WebRootPath, "temp_plc_upload", taskId);
                if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);

                // P1 安全（2026-10-09 检查清单 #1）：客户端文件名不可信——带 ".." 段或绝对路径的
                // FileName 与任务目录拼接后可逃出到任意进程可写路径覆盖现有文件（且发生在 ZIP
                // 解析之前）。存储文件名由服务端固定生成，原始文件名仅用于进度展示；任务目录本身
                // 全部由服务端值拼出（WebRootPath + 字面量 + 服务端生成的 taskId），无可控输入。
                string zipPath = Path.Combine(tempFolder, "package.zip");
                using (var stream = new FileStream(zipPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Create progress tracking
                var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
                progressService.CreateProgress(taskId, projectId, file.FileName);

                // Enqueue background job（fullProjectUpdate：全项目更新模式，详见 ExecuteBackgroundUploadAsync 4参重载注释）
                // 2026-09-07: 显式入队到本机专用队列 local_<本机机器名>（与 EnqueueIfNotRunning 同模式）。
                // 原因：原 BackgroundJob.Enqueue 受 [Queue("default")] 属性控制进入多机共享的 default 队列。
                // 本机开发后端与其他部署（如 6.75 测试容器）共用 hsjm_ai_new_test 的 Hangfire 库时，
                // 远端 worker 会抢走任务并拿本机绝对路径（E:\...zip）执行失败——表现为任务秒级 Succeeded
                // 但零副作用（无解压目录、无日志、temp 目录未清理），前端进度永远停在"等待处理"。
                var jobId = new BackgroundJobClient().Create<PLC_UploadApp>(app =>
                    app.ExecuteBackgroundUploadAsync(taskId, zipPath, projectId, fullProjectUpdate),
                    new EnqueuedState(BuildCurrentMachineQueue()));

                _logger.LogInformation("[PLC上传] 后台任务已提交: TaskId={TaskId}, JobId={JobId}, ProjectId={ProjectId}",
                    taskId, jobId, projectId);

                result.IsSuccess = true;
                result.BackResult = new UploadTaskStartResult
                {
                    TaskId = taskId,
                    HangfireJobId = jobId,
                    Message = "上传任务已启动，正在后台处理..."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PLC上传] 启动后台任务失败");
                result.ErrorMessage = $"启动上传任务失败: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 后台执行上传解析任务（供 Hangfire 调用）
        /// 使用并发方式解析文件，提升效率
        /// </summary>
        // 2026-09-07: 移除 [Queue("default")]——该属性会把任务压进多机共享的 default 队列被远端 worker
        // 抢走（见 StartBackgroundUploadAsync 内注释）。入队由 StartBackgroundUploadAsync 用
        // EnqueuedState 显式指定本机队列；不经该方法入队的入口落到 Hangfire 缺省队列（仍为 default，行为不变）。
        [AutomaticRetry(Attempts = 0)] // 不自动重试
        [Hangfire.JobDisplayName("PLC上传解析 · 任务{0} 项目{2}")]
        public Task ExecuteBackgroundUploadAsync(string taskId, string zipPath, long projectId)
        {
            // 兼容入口（Hangfire 存量任务按「方法名+参数类型」反序列化，旧签名必须保留）：
            // 默认非全项目模式，与历史行为完全一致——zip 里有什么就更什么，库里多出的块不动。
            return ExecuteBackgroundUploadAsync(taskId, zipPath, projectId, fullProjectUpdate: false);
        }

        /// <summary>
        /// 后台上传解析（Hangfire 调度），fullProjectUpdate=true 时启用「全项目更新模式」：
        /// 数据库中存在、但本次 zip 中缺少的程序块视为已在博图中删除，其全部关联数据
        /// （LU / 接口变量 / 变量访问 / 块调用 / DB-FB绑定 / GRAPH步位台账 / 按SourceLuId关联的约束）
        /// 将随现有块级清理链一并清理。危险操作：若 zip 不是完整项目导出，缺少的块会被误删。
        /// </summary>
        // 2026-09-07: 同上——移除 [Queue("default")]，由 StartBackgroundUploadAsync 显式指定本机队列。
        [AutomaticRetry(Attempts = 0)] // 不自动重试
        [Hangfire.JobDisplayName("PLC上传解析{3} · 任务{0} 项目{2}")]
        public async Task ExecuteBackgroundUploadAsync(string taskId, string zipPath, long projectId, bool fullProjectUpdate)
        {
            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
            string tempFolder = Path.GetDirectoryName(zipPath);

            // 并发配置
            const int MAX_PARALLELISM = 4; // 最大并发数
            var semaphore = new SemaphoreSlim(MAX_PARALLELISM);

            try
            {
                _logger.LogInformation("[PLC上传-后台] 开始执行(并发模式): TaskId={TaskId}", taskId);
                await WriteLogHelper.WriteLogsAsync($"[开始] TaskId={taskId}, ProjectId={projectId}, ZipPath={zipPath}", "PLC上传");

                // 1. Extract zip
                progressService.SetMessage(taskId, "正在解压文件...");
                string extractPath = Path.Combine(tempFolder, "extracted");
                ExtractZipToWorkingDirectory(zipPath, extractPath);

                var blockPathMap = LoadBlockPathMappings(extractPath);

                // 2. Find all XML/JSON/ML files (.scl/.awl 通过 XML 自动关联处理，无需单独解析)
                var files = Directory.GetFiles(extractPath, "*.*", SearchOption.AllDirectories)
                    .Where(PLC_UploadFileClassifier.IsGeneralPlcSource)
                    .ToList();

                if (files.Count == 0)
                {
                    progressService.MarkFailed(taskId, "压缩包中未找到有效的PLC文件");
                    return;
                }

                progressService.SetTotalFiles(taskId, files.Count);
                _logger.LogInformation("[PLC上传-后台] 发现 {Count} 个文件，开始并发解析", files.Count);

                // ========== P1 文件级 hash 比对：加载本项目已有源文件 hash ==========
                // 语义：zip 内内容未变（同 FileName + 同 SHA256）的块文件整文件跳过——
                // 不解析、不进 blockNamesToClean、不清理不重插，其块/约束/PLD 全保留。
                // 存量数据 FileHash=NULL 不在字典内 → 首次照常全量跑并回填 hash，之后增量生效。
                // CSV 不参与（IO Tag 是项目级全清全插语义，且 CSV 无块概念）。
                var blockFilesInZip = files.Count(f => !f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
                var existingFileHashes = new Dictionary<string, (string Hash, List<long> SourceFileIds)>(StringComparer.OrdinalIgnoreCase);
                // FileName → 全部源文件行 Id（含 FileHash 为 NULL 的存量行）：
                // 全项目更新的「解析失败/零块保护」按 SourceFileId 反查块（检查清单 #2 复核）。
                var sourceFileIdsByFileName = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
                // 加载本项目源文件快照（hash 字典 + FileName→Id 映射）。可重复调用：
                // 预判阶段（锁外）与复核阶段（同项目发布锁内）各加载一次，后者拿最新库内状态。
                async Task LoadSourceFileSnapshotAsync()
                {
                    existingFileHashes.Clear();
                    sourceFileIdsByFileName.Clear();
                    // 投影只取三列，避免把 FileContent（GRAPH 原文归档）大字段整行拖回来
                    var rows = await _db.Queryable<PLC_SourceFileEntity>()
                        .Where(s => s.ProjectId == projectId && !s.IsDeleted)
                        .Select(s => new PLC_SourceFileEntity { Id = s.Id, FileName = s.FileName, FileHash = s.FileHash })
                        .ToListAsync();
                    foreach (var g in rows.GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var ids = g.Select(x => x.Id).ToList();
                        sourceFileIdsByFileName[g.Key] = ids;
                        var hashed = g.FirstOrDefault(x => !string.IsNullOrEmpty(x.FileHash));
                        if (hashed != null)
                        {
                            existingFileHashes[g.Key] = (hashed.FileHash, ids);
                        }
                    }
                }
                try
                {
                    await LoadSourceFileSnapshotAsync();
                    if (existingFileHashes.Count > 0)
                    {
                        _logger.LogInformation("[PLC上传-后台] 已加载 {Count} 个历史源文件 hash（内容未变的文件将被跳过）", existingFileHashes.Count);
                    }
                }
                catch (Exception hashLoadEx)
                {
                    // 加载失败不影响正确性：退化为全量解析（与历史行为一致）
                    _logger.LogWarning(hashLoadEx, "[PLC上传-后台] 加载历史文件 hash 失败，本次不跳过任何文件");
                    existingFileHashes.Clear();
                    sourceFileIdsByFileName.Clear();
                }
                // 预判跳过的文件记录（路径+文件名+指纹）：锁内复核直接比对记录里的指纹，不再重算
                var hashSkippedRecords = new ConcurrentBag<(string FilePath, string FileName, string Fingerprint)>();
                int skippedUnchanged = 0;
            

                // 3. 并发解析所有文件
                string scriptPath = Path.Combine(_env.WebRootPath, "skills", "plc-analyzer", "scripts", "engine.py");
                string pythonCmd = _config["PythonPath"] ?? "python";

                // 使用线程安全集合收集结果
                var parseResults = new ConcurrentBag<FileParseResult>();
                int processedCount = 0;

                // 并发执行解析任务
                var parseTasks = files.Select(async filePath =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // P1: 内容未变的块文件整文件跳过（return 走 finally，进度计数不受影响）。
                        // 2026-10-09 检查清单 #3：指纹含伴随源码（.scl/.awl）——XML 不变而伴随
                        // 源码变化时不再被误判为「内容未变」跳过重解析。
                        // 检查清单 #4 复核：这里只是「预判」；指纹随记录保存，拿到同项目发布锁后
                        // 会用最新库内 hash 复核（等锁期间被并发上传改写的会补解析，见锁内复核段）。
                        if (!filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                            && existingFileHashes.TryGetValue(Path.GetFileName(filePath), out var knownHash))
                        {
                            var fingerprint = ComputeSourceFingerprint(filePath);
                            if (string.Equals(knownHash.Hash, fingerprint, StringComparison.OrdinalIgnoreCase))
                            {
                                hashSkippedRecords.Add((filePath, Path.GetFileName(filePath), fingerprint));
                                Interlocked.Increment(ref skippedUnchanged);
                                return;
                            }
                        }

                        var result = await ParseSingleFileAsync(filePath, scriptPath, pythonCmd, projectId, taskId, blockPathMap);
                        if (result != null)
                        {
                            parseResults.Add(result);
                        }
                    }
                    finally
                    {
                        semaphore.Release();

                        // 原子操作更新进度
                        var currentCount = Interlocked.Increment(ref processedCount);
                        progressService.IncrementProcessed(taskId, Path.GetFileName(filePath));

                        if (currentCount % 10 == 0) // 每处理10个文件记录一次日志
                        {
                            _logger.LogInformation("[PLC上传-后台] 解析进度: {Current}/{Total}", currentCount, files.Count);
                        }
                    }
                }).ToList();

                await Task.WhenAll(parseTasks);

                // GRAPH 注册任务是否已挂上链式触发（约束生成由注册任务收尾时自动入队）。
                // 声明在锁/事务作用域外：事务提交后赋值，MarkCompleted 后据此决定是否需要本地兜底直触发约束任务。
                bool graphStepChained = false;
                // 完成摘要统计（事务内赋值，MarkCompleted 时拼给前端展示增量结果）
                int staleConstraintsCleaned = 0;
                int missingBlocksCleaned = 0;
                int changedBlocksSaved = 0;

                // P1（2026-10-09 检查清单 #4）：同项目发布锁原先只盖住最后的规则发布段——两个同项目的
                // 并发上传（StartBackgroundUploadAsync 允许同项目多次入队）可以「A 入库 → B 入库 →
                // B 发布 → A 发布」，最终库内程序是 B、而规则与保存包来自 A（版本错配；归档覆盖率
                // 检查只要求块覆盖完整，拦不住这种错配）。现在锁从「跳过复核」就开始持有（与
                // ResetRule / 重跑共用 PLC_XmlV2RerunJob.AcquirePublishLockAsync 这一把），上传全链路
                //（跳过复核 → 入库 → 保存包 → 入队约束）同项目串行化。解析阶段仍在锁外（各自独立
                // 解压目录）。
                // 检查清单 #4 复核：锁提前只消除了「写库交错」——「读取旧指纹、决定跳过哪些文件」
                // 仍在锁外的话，两个并发上传可以基于同一份旧快照预判跳过同一批文件，等拿到锁时先
                // 提交的一方已把库内程序与 hash 换成新版本，后到者的跳过集合已过期，虽串行写库、
                // 第二个任务用的跳过结果却是旧的，仍会留下「库内程序 ≠ 保存包/发布规则」的错配。
                // 因此拿到锁后先重载最新库内 hash 逐一复核预判跳过（过期者就地补解析，纯文件操作
                // 不写库），再定案 failedFiles / 聚合 / 覆盖率检查，保证本任务「入库内容 == 保存包内容」
                // 同源同版本。
                // Hangfire 本机队列任务等锁不受超时限制（SemaphoreSlim 等待不阻塞 server heartbeat）。
                progressService.SetMessage(taskId, "等待同项目其它上传/发布任务完成（同项目串行保护）...");

                // 4. 聚合容器（锁外声明：锁释放后的注册/摘要代码仍要用；聚合循环在锁内——见下）
                var blocksToInsert = new List<PLC_BlockEntity>();
                var sourceFilesToInsert = new List<PLC_SourceFileEntity>();
                var logicUnitsToInsert = new List<PLC_LogicUnitEntity>();
                var interfacesToInsert = new List<PLC_BlockInterfaceEntity>();
                var varAccessToInsert = new List<PLC_VariableAccessEntity>();
                var blockCallsToInsert = new List<PLC_BlockCallEntity>();
                var blockNamesToClean = new HashSet<string>();
                var tagsToInsert = new List<PLC_IOTagEntity>();
                var dbFbBindingsToInsert = new List<PLC_DbFbBindingEntity>();

                using (await PLC_XmlV2RerunJob.AcquirePublishLockAsync(projectId))
                {
                progressService.SetSavingPhase(taskId);

                // ========== 检查清单 #4 复核：锁内重载库内指纹快照，复核预判跳过 ==========
                try
                {
                    await LoadSourceFileSnapshotAsync();
                }
                catch (Exception freshEx)
                {
                    // 复核依据拿不到就按「全部过期」处理（补解析）：宁可多解析，不可跳过已变更文件
                    _logger.LogWarning(freshEx, "[PLC上传-后台][锁内复核] 重新加载库内 hash 失败，预判跳过全部按过期处理（锁内补解析）");
                    existingFileHashes.Clear();
                    sourceFileIdsByFileName.Clear();
                }
                var validSkips = new List<(string FilePath, string FileName, string Fingerprint)>();
                var staleSkips = new List<(string FilePath, string FileName, string Fingerprint)>();
                foreach (var record in hashSkippedRecords)
                {
                    var stillUnchanged = existingFileHashes.TryGetValue(record.FileName, out var currentHash)
                        && string.Equals(currentHash.Hash, record.Fingerprint, StringComparison.OrdinalIgnoreCase);
                    (stillUnchanged ? validSkips : staleSkips).Add(record);
                }
                if (staleSkips.Count > 0)
                {
                    await WriteLogHelper.WriteLogsAsync(
                        $"[锁内复核] {staleSkips.Count} 个文件的跳过决策已过期（等锁期间库内程序被同项目其它上传更新），锁内补解析: {string.Join(", ", staleSkips.Select(s => s.FileName).Take(20))}{(staleSkips.Count > 20 ? " ...等" + staleSkips.Count + "个" : "")}",
                        "PLC上传");
                    _logger.LogWarning("[PLC上传-后台][锁内复核] ProjectId={ProjectId}: {Count} 个预判跳过已过期，锁内补解析", projectId, staleSkips.Count);
                    foreach (var stale in staleSkips)
                    {
                        // 补解析失败（返回 null）→ 该文件自然落入 failedFiles，走检查清单 #2 的保护
                        var reparsed = await ParseSingleFileAsync(stale.FilePath, scriptPath, pythonCmd, projectId, taskId, blockPathMap);
                        if (reparsed != null)
                        {
                            parseResults.Add(reparsed);
                        }
                    }
                }
                skippedUnchanged = validSkips.Count;
                var hashSkippedNames = new HashSet<string>(validSkips.Select(s => s.FileName), StringComparer.OrdinalIgnoreCase);

                // 记录解析失败的文件（hash 跳过的不算失败）——锁内定案口径（含锁内补解析结果）
                var failedFiles = files
                    .Where(f => !hashSkippedNames.Contains(Path.GetFileName(f)))
                    .Where(f => !parseResults.Any(r => r.SourceFile?.FilePath == f)).ToList();
                _logger.LogInformation("[PLC上传-后台] 解析定案: 成功 {Success}/{Total}，跳过 {Skipped}，失败 {Failed}", parseResults.Count, files.Count, validSkips.Count, failedFiles.Count);
                var failedNames = failedFiles.Take(50).Select(f => Path.GetFileName(f)).ToList();
                await WriteLogHelper.WriteLogsAsync($"[解析完成] 成功={parseResults.Count}/{files.Count}, 跳过={validSkips.Count}, 失败={failedFiles.Count}" + (failedNames.Count > 0 ? $", 失败文件: {string.Join(", ", failedNames)}" : ""), "PLC上传");
                if (failedFiles.Count > 0)
                {
                    foreach (var ff in failedFiles.Take(50))
                        _logger.LogWarning("[PLC上传-后台] 解析失败/跳过的文件: {File}", Path.GetFileName(ff));
                }

                // P1: 内容未变跳过的文件汇总（锁内复核后的定案口径；块/约束/PLD 原样保留）
                if (skippedUnchanged > 0)
                {
                    _logger.LogInformation("[PLC上传-后台] {Skipped}/{Total} 个文件内容未变（hash 命中），已跳过解析与入库", skippedUnchanged, files.Count);
                    await WriteLogHelper.WriteLogsAsync($"[hash跳过] {skippedUnchanged}/{files.Count} 个文件内容未变，跳过解析与入库（块/约束/PLD 原样保留）", "PLC上传");
                }

                foreach (var result in parseResults)
                {
                    sourceFilesToInsert.Add(result.SourceFile);
                    if (result.Block != null)
                    {
                        blocksToInsert.Add(result.Block);
                        if (!string.IsNullOrEmpty(result.Block.BlockName))
                        {
                            blockNamesToClean.Add(result.Block.BlockName);
                        }
                    }
                    if (result.LogicUnits != null && result.LogicUnits.Count > 0)
                    {
                        logicUnitsToInsert.AddRange(result.LogicUnits);
                    }
                    if (result.Interfaces != null && result.Interfaces.Count > 0)
                    {
                        interfacesToInsert.AddRange(result.Interfaces);
                    }
                    if (result.VariableAccesses != null && result.VariableAccesses.Count > 0)
                    {
                        varAccessToInsert.AddRange(result.VariableAccesses);
                    }

                    if (result.BlockCalls != null && result.BlockCalls.Count > 0)
                    {
                        blockCallsToInsert.AddRange(result.BlockCalls);
                    }
                    if (result.DbFbBinding != null)
                    {
                        dbFbBindingsToInsert.Add(result.DbFbBinding);
                    }
                    if (result.Tags != null && result.Tags.Count > 0)
                    {
                        tagsToInsert.AddRange(result.Tags);
                    }

                }

                if (!fullProjectUpdate && blockNamesToClean.Count > 0)
                {
                    // A complete archive is required because the test publisher replaces all rules.
                    // Run before the upload transaction; a partial ZIP cannot invalidate retained rules.
                    var archiveSource = PLC.IndependentPipeline.Original353.Original353ArchiveSource.Read(
                        zipPath, projectId, Array.Empty<PLC.IndependentPipeline.PipelineTag>());
                    var activeBlocks = await _blockService.GetListAsync(block => block.ProjectId == projectId && !block.IsDeleted);
                    PLC.IndependentPipeline.Original353.Original353ArchiveCoverage.RequireComplete(archiveSource, activeBlocks);
                }

                // 5. Save to database（Saving 相位已在锁内开头设置）
                _logger.LogInformation("[PLC上传-后台] 入库准备: Blocks={Blocks}, LogicUnits={LU}, Interfaces={IF}, VarAccess={VA}, BlockCalls={BC}, CleanNames={Clean}",
                    blocksToInsert.Count, logicUnitsToInsert.Count, interfacesToInsert.Count, varAccessToInsert.Count, blockCallsToInsert.Count, blockNamesToClean.Count);
                await WriteLogHelper.WriteLogsAsync($"[入库准备] Blocks={blocksToInsert.Count}, LogicUnits={logicUnitsToInsert.Count}, Interfaces={interfacesToInsert.Count}, VarAccess={varAccessToInsert.Count}, BlockCalls={blockCallsToInsert.Count}, CleanNames={blockNamesToClean.Count}, Tags={tagsToInsert.Count}", "PLC上传");
                progressService.SetSavingDetail(taskId, $"待写入：程序块 {blocksToInsert.Count}、逻辑单元 {logicUnitsToInsert.Count}、接口变量 {interfacesToInsert.Count} 行");

                try
                {
                    using (var tran = _proj.BeginTran())
                    {
                        await WriteLogHelper.WriteLogsAsync("[事务开始] BeginTran", "PLC上传");

                        // ========== 全项目更新模式：文件比对，缺失块并入清理集合 ==========
                        // 语义：fullProjectUpdate=true 时，数据库中存在但本次 zip 未覆盖的块
                        // 视为「已在博图中删除」，并入 blockNamesToClean 后由下方现有块级清理链
                        // （LU/接口/变量访问/块调用/DB-FB绑定/GRAPH台账/约束软删）统一处理。
                        // 默认 fullProjectUpdate=false 不做任何比对清理（与历史行为完全一致）。
                        if (fullProjectUpdate)
                        {
                            // 守卫1（P0）：zip 内没有任何块文件（纯 CSV / 全部非块文件）→ 直接失败中止。
                            // 防止「zip 块覆盖集为空 → 差集=项目全部块」把整个项目清光且零重插。
                            if (blockFilesInZip == 0)
                            {
                                var guardMsg = "全项目更新模式要求压缩包至少包含 1 个程序块文件（xml/ml/json）；本次未发现任何块文件，任务已中止（防止误清全项目）";
                                await WriteLogHelper.WriteLogsAsync($"[全项目更新][守卫] {guardMsg}", "PLC上传");
                                _logger.LogWarning("[PLC上传-后台][全项目更新] ProjectId={ProjectId}: {Msg}", projectId, guardMsg);
                                progressService.MarkFailed(taskId, guardMsg);
                                return;
                            }

                            var allProjectBlocks = await _blockService.GetListAsync(b =>
                                b.ProjectId == projectId && !b.IsDeleted);

                            // zip 覆盖集 = 本次解析出的块（blockNamesToClean）
                            //           ∪ 内容未变被 hash 跳过的文件对应的历史块（按 SourceFileId 反查）。
                            // 关键：hash 跳过的文件不进 blockNamesToClean，但其块仍在 zip 中，绝不能算缺失。
                            var zipBlockNames = new HashSet<string>(blockNamesToClean, StringComparer.Ordinal);
                            var skippedSourceFileIds = new HashSet<long>();
                            foreach (var skippedName in hashSkippedNames)
                            {
                                if (existingFileHashes.TryGetValue(skippedName, out var info))
                                {
                                    foreach (var sid in info.SourceFileIds) skippedSourceFileIds.Add(sid);
                                }
                            }
                            if (skippedSourceFileIds.Count > 0)
                            {
                                foreach (var pb in allProjectBlocks)
                                {
                                    if (pb.SourceFileId.HasValue && skippedSourceFileIds.Contains(pb.SourceFileId.Value)
                                        && !string.IsNullOrEmpty(pb.BlockName))
                                    {
                                        zipBlockNames.Add(pb.BlockName);
                                    }
                                }
                            }

                            // P1（2026-10-09 检查清单 #2 复核）：包内存在但「没解析出块」的文件不能算
                            // 「缺失块」——两种形态都要覆盖：
                            //   ① 解析失败（不在 parseResults 里，即 failedFiles）；
                            //   ② Python 退出码 0、结果 JSON 有效但 blocks 为空（在 parseResults 里、
                            //      Block==null，进不了 failedFiles——原先只保护 ①，② 仍会被当成
                            //      「解析成功」而其块被按缺失清理，新数据又没进来，后面的 XMLV2
                            //      发布失败也不会回滚该事务）。
                            // 匹配两条路（取并集，CSV 无块概念不参与）：
                            //   ① 文件名 stem == 块名（TIA 常规导出；OrdinalIgnoreCase 容忍大小写差异）；
                            //   ② 历史源文件行（同 FileName，含 FileHash 为 NULL 的存量行）→ SourceFileId
                            //      反查块——覆盖「导出文件名≠块名」（export_1.xml 定义 FB_A）时 stem
                            //      匹配不上的场景。命中的块本次保留库内数据，等修复文件后重传。
                            var noOutputFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var failedFile in failedFiles)
                            {
                                var failedName = Path.GetFileName(failedFile);
                                if (!string.IsNullOrWhiteSpace(failedName)) noOutputFileNames.Add(failedName);
                            }
                            foreach (var parsed in parseResults)
                            {
                                if (parsed.Block == null
                                    && parsed.SourceFile != null
                                    && !parsed.SourceFile.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                {
                                    noOutputFileNames.Add(parsed.SourceFile.FileName);
                                }
                            }
                            var noOutputStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var noOutputSourceFileIds = new HashSet<long>();
                            foreach (var noOutputName in noOutputFileNames)
                            {
                                var noOutputStem = Path.GetFileNameWithoutExtension(noOutputName);
                                if (!string.IsNullOrWhiteSpace(noOutputStem)) noOutputStems.Add(noOutputStem);
                                if (sourceFileIdsByFileName.TryGetValue(noOutputName, out var noOutputIds))
                                {
                                    noOutputSourceFileIds.UnionWith(noOutputIds);
                                }
                            }
                            if (noOutputFileNames.Count > 0)
                            {
                                var parseFailedProtected = allProjectBlocks
                                    .Where(b => !string.IsNullOrEmpty(b.BlockName)
                                        && (noOutputStems.Contains(b.BlockName)
                                            || (b.SourceFileId.HasValue && noOutputSourceFileIds.Contains(b.SourceFileId.Value))))
                                    .ToList();
                                if (parseFailedProtected.Count > 0)
                                {
                                    foreach (var pb in parseFailedProtected)
                                    {
                                        zipBlockNames.Add(pb.BlockName);
                                    }
                                    var protectedNames = parseFailedProtected.Select(b => b.BlockName).ToList();
                                    await WriteLogHelper.WriteLogsAsync(
                                        $"[全项目更新][保护] {parseFailedProtected.Count} 个块对应的文件在包内存在但解析失败或未解析出块，本次不按缺失清理（保留库内数据，修复文件后重传）: {string.Join(", ", protectedNames.Take(50))}{(protectedNames.Count > 50 ? " ...等" + protectedNames.Count + "个" : "")}",
                                        "PLC上传");
                                    _logger.LogWarning(
                                        "[PLC上传-后台][全项目更新] ProjectId={ProjectId}: {Count} 个解析失败/零块文件对应的块被排除出缺失清理: {Names}",
                                        projectId, parseFailedProtected.Count, string.Join(", ", protectedNames.Take(50)));
                                    progressService.SetSavingDetail(taskId,
                                        $"全项目更新：{parseFailedProtected.Count} 个块文件解析失败或未解析出块，已保护不清理（{string.Join(", ", protectedNames.Take(5))}{(protectedNames.Count > 5 ? " 等" + protectedNames.Count + "个" : "")}）");
                                }
                            }

                            var missingBlocks = allProjectBlocks
                                .Where(b => !string.IsNullOrEmpty(b.BlockName) && !zipBlockNames.Contains(b.BlockName))
                                .ToList();

                            // 守卫2（P0）：缺失比例可疑 → 拒绝执行（zip 大概率不是完整项目导出）。
                            // 阈值可配 Upload:FullUpdateMaxMissingRatio，默认 0.8；同时要求缺失绝对数 > 10。
                            var maxMissingRatio = 0.8;
                            var ratioRaw = _config["Upload:FullUpdateMaxMissingRatio"];
                            if (!string.IsNullOrWhiteSpace(ratioRaw) && double.TryParse(ratioRaw, out var ratioCfg) && ratioCfg > 0 && ratioCfg <= 1)
                            {
                                maxMissingRatio = ratioCfg;
                            }
                            if (allProjectBlocks.Count > 0 && missingBlocks.Count > 10
                                && missingBlocks.Count / (double)allProjectBlocks.Count > maxMissingRatio)
                            {
                                var guardMsg = $"全项目更新守卫：数据库 {allProjectBlocks.Count} 个块中将缺失清理 {missingBlocks.Count} 个（占比超过 {maxMissingRatio:0%}），疑似压缩包非完整项目导出，任务已中止。请检查 zip 完整性后重试";
                                await WriteLogHelper.WriteLogsAsync($"[全项目更新][守卫] {guardMsg}", "PLC上传");
                                _logger.LogWarning("[PLC上传-后台][全项目更新] ProjectId={ProjectId}: {Msg}", projectId, guardMsg);
                                progressService.MarkFailed(taskId, guardMsg);
                                return;
                            }

                            if (missingBlocks.Count > 0)
                            {
                                missingBlocksCleaned = missingBlocks.Count;
                                var missingNames = missingBlocks.Select(b => b.BlockName).ToList();
                                await WriteLogHelper.WriteLogsAsync(
                                    $"[全项目更新] 检测到 {missingBlocks.Count} 个数据库存在但 zip 中缺失的块，将清理其全部关联数据: {string.Join(", ", missingNames.Take(50))}{(missingNames.Count > 50 ? " ...等" + missingNames.Count + "个" : "")}",
                                    "PLC上传");
                                _logger.LogWarning(
                                    "[PLC上传-后台][全项目更新] ProjectId={ProjectId} 缺失块 {Count} 个将清理: {Names}",
                                    projectId, missingBlocks.Count, string.Join(", ", missingNames.Take(50)));
                                progressService.SetSavingDetail(taskId,
                                    $"全项目更新：检测到 {missingBlocks.Count} 个缺失块，将清理其关联数据（{string.Join(", ", missingNames.Take(5))}{(missingNames.Count > 5 ? " 等" + missingNames.Count + "个" : "")}）");

                                foreach (var name in missingNames)
                                {
                                    blockNamesToClean.Add(name);
                                }
                            }
                            else
                            {
                                await WriteLogHelper.WriteLogsAsync("[全项目更新] zip 包含项目全部块，无缺失块需要清理", "PLC上传");
                            }
                        }

                        // Clean old data
                        if (blockNamesToClean.Count > 0)
                        {
                            var oldBlocks = await _blockService.GetListAsync(x =>
                                x.ProjectId == projectId &&
                                blockNamesToClean.Contains(x.BlockName) &&
                                !x.IsDeleted);

                            changedBlocksSaved = oldBlocks.Count;
                            await WriteLogHelper.WriteLogsAsync($"[清理旧数据] 找到 {oldBlocks.Count} 个旧Block需要清理 (blockNamesToClean共{blockNamesToClean.Count}个)", "PLC上传");
                            progressService.SetSavingDetail(taskId, $"查询到 {oldBlocks.Count} 个旧程序块，开始清理关联数据");

                            if (oldBlocks.Count > 0)
                            {
                                var oldBlockIds = oldBlocks.Select(b => b.Id).ToList();
                                var oldSourceFileIds = oldBlocks.Where(b => b.SourceFileId.HasValue).Select(b => b.SourceFileId.Value).Distinct().ToList();

                                var oldLogicUnits = await _logicUnitService.GetListAsync(x =>
                                    x.BlockId.HasValue && oldBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted);
                                var oldLogicUnitIds = oldLogicUnits.Select(l => l.Id).ToList();

                                // 方案 D（增量约束）：删旧 LU 之前，软删这些 LU 生成的约束，
                                // 让后台增量任务能重生最新约束。SourceLuId 为 NULL 的旧约束 / 手动录入约束保留不动。
                                // P1（2026-10-09 检查清单 #5）：必须显式排除 Source=Manual——UpdateConstraintRuleAsync
                                // 把 Source 改为 Manual 后保留原 SourceLuId，不排除会把用户手工编辑过的历史自动
                                // 规则一并软删（XMLV2 发布器对 Manual 的保护在更晚的发布阶段，来不及救）。
                                // Source 为 NULL 的历史自动规则仍要清理（SQL 三值逻辑需显式 IS NULL OR）。
                                if (oldLogicUnitIds.Count > 0)
                                {
                                    var staleRuleIds = await _db.Queryable<PLC_IOConstraintRuleEntity>()
                                        .Where(r => r.SourceLuId.HasValue && oldLogicUnitIds.Contains(r.SourceLuId.Value) && !r.IsDeleted
                                            && (r.Source == null || r.Source != "Manual"))
                                        .Select(r => r.Id)
                                        .ToListAsync();

                                    if (staleRuleIds.Count > 0)
                                    {
                                        staleConstraintsCleaned = staleRuleIds.Count;
                                        await _db.Updateable<PLC_IOConstraintSignalEntity>()
                                            .SetColumns(s => new PLC_IOConstraintSignalEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                            .Where(s => staleRuleIds.Contains(s.RuleId) && !s.IsDeleted)
                                            .ExecuteCommandAsync();

                                        await _db.Updateable<PLC_IOConstraintRuleEntity>()
                                            .SetColumns(r => new PLC_IOConstraintRuleEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                            .Where(r => staleRuleIds.Contains(r.Id) && !r.IsDeleted)
                                            .ExecuteCommandAsync();

                                        // 涉及的 IO Tag 状态归零，重新进入约束生成队列
                                        await _ioTagService.UpdateAsync(
                                            t => new PLC_IOTagEntity { ConstraintStatus = 0 },
                                            t => t.ProjectId == projectId && !t.IsDeleted && t.ConstraintStatus != 0);

                                        await WriteLogHelper.WriteLogsAsync(
                                            $"[增量约束] 软删 {staleRuleIds.Count} 条与本次上传 block 关联的旧约束（按 SourceLuId）",
                                            "PLC上传");
                                    }
                                }

                                if (oldLogicUnitIds.Count > 0)
                                {
                                    progressService.SetSavingDetail(taskId, $"清理旧数据：删除变量访问记录（{oldLogicUnitIds.Count} 个逻辑单元）");
                                    await _db.Deleteable<PLC_VariableAccessEntity>()
                                        .Where(x => x.LogicUnitId.HasValue && oldLogicUnitIds.Contains(x.LogicUnitId.Value) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                if (oldLogicUnitIds.Count > 0)
                                {
                                    progressService.SetSavingDetail(taskId, $"清理旧数据：删除逻辑单元（{oldLogicUnitIds.Count} 个）");
                                    await _db.Deleteable<PLC_LogicUnitEntity>()
                                        .Where(x => x.BlockId.HasValue && oldBlockIds.Contains(x.BlockId.Value) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                // 接口变量是最大表（可达百万行级），按程序块分批删除并汇报进度
                                const int DELETE_CHUNK_SIZE = 25;
                                for (int delOffset = 0; delOffset < oldBlockIds.Count; delOffset += DELETE_CHUNK_SIZE)
                                {
                                    var delIdChunk = oldBlockIds.GetRange(delOffset, Math.Min(DELETE_CHUNK_SIZE, oldBlockIds.Count - delOffset));
                                    await _db.Deleteable<PLC_BlockInterfaceEntity>()
                                        .Where(x => x.BlockId.HasValue && delIdChunk.Contains(x.BlockId.Value) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                    progressService.SetSavingDetail(taskId,
                                        $"清理旧数据：删除接口变量（{Math.Min(delOffset + DELETE_CHUNK_SIZE, oldBlockIds.Count)}/{oldBlockIds.Count} 批程序块，此表数据量最大）");
                                }

                                await _db.Deleteable<PLC_BlockCallEntity>()
                                    .Where(x => oldBlockIds.Contains(x.CallerBlockId.Value) && !x.IsDeleted)
                                    .ExecuteCommandAsync();

                                // 删除 DB→FB 绑定关系（按项目+DB块名，与 Block 同生命周期）
                                if (blockNamesToClean.Count > 0)
                                {
                                    await _db.Deleteable<PLC_DbFbBindingEntity>()
                                        .Where(x => x.ProjectId == projectId && blockNamesToClean.Contains(x.DbName) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }

                                // GRAPH 步位台账与绑定表同生命周期软删（本次清理的 DB 块），
                                // 事务提交后 GraphStepTagRegistrationJob 会按最新绑定重新生成
                                if (blockNamesToClean.Count > 0)
                                {
                                    await _db.Updateable<PLC_GraphStepTagEntity>()
                                        .SetColumns(x => new PLC_GraphStepTagEntity { IsDeleted = true, ModifyTime = DateTime.Now })
                                        .Where(x => x.ProjectId == projectId && blockNamesToClean.Contains(x.DbName) && !x.IsDeleted)
                                        .ExecuteCommandAsync();

                                    // GRAPH EXEC_BITS 候补信号同生命周期软删（下次播种按最新绑定幂等补齐）
                                    await SoftDeleteGraphExecBitSignalsForBlocksAsync(projectId, blockNamesToClean);
                                }

                                progressService.SetSavingDetail(taskId, "清理旧数据：删除程序块与源文件记录");
                                await _db.Deleteable<PLC_BlockEntity>()
                                    .Where(x => oldBlockIds.Contains(x.Id) && !x.IsDeleted)
                                    .ExecuteCommandAsync();

                                if (oldSourceFileIds.Count > 0)
                                {
                                    await _db.Deleteable<PLC_SourceFileEntity>()
                                        .Where(x => oldSourceFileIds.Contains(x.Id) && !x.IsDeleted)
                                        .ExecuteCommandAsync();
                                }
                            }
                        }
                        // P2（2026-10-09 检查清单 #7）：点表内容变化不能判零变更——只传了修改过的 CSV
                        //（XML 全部未变）时 blockNamesToClean 为空，若不比对点表，graphStepChained=true
                        // 会跳过约束发布，规则及说明继续引用旧点名/地址/注释。用「本次待写集合 vs 库内
                        // 当前集合」的内容比对（不是 tagsToInsert.Count>0——相同 CSV 重复上传不应触发
                        // 无意义全量重跑）。比对在清空重写之前做，字段取约束引用的语义字段。
                        var tagsContentChanged = false;
                        if (tagsToInsert.Count > 0)
                        {
                            var existingIotags = await _ioTagService.GetListAsync(t => t.ProjectId == projectId && !t.IsDeleted);
                            tagsContentChanged = !SameTagContent(existingIotags, tagsToInsert);
                            if (tagsContentChanged)
                            {
                                _logger.LogInformation("[PLC上传-后台] IO 点表内容有变化（{Existing} 行 → {Incoming} 行），本次上传不按零变更跳过约束发布: ProjectId={ProjectId}",
                                    existingIotags.Count, tagsToInsert.Count, projectId);
                                await WriteLogHelper.WriteLogsAsync($"[点表变化] IO 点表内容有变化（{existingIotags.Count} → {tagsToInsert.Count} 行），将照常触发约束发布", "PLC上传");
                            }
                        }
                        if (tagsToInsert.Count > 0)
                        {
                            progressService.SetSavingDetail(taskId, "清理旧数据：清空 IO 标签");
                            await _db.Deleteable<PLC_IOTagEntity>()
                                        .Where(x => x.ProjectId == projectId && !x.IsDeleted)
                                        .ExecuteCommandAsync();

                        }

                        // Insert new data
                        await WriteLogHelper.WriteLogsAsync("[旧数据清理完成] 开始BulkCopy插入新数据", "PLC上传");

                        // 行数加权子进度：接口变量可达百万行，分块插入（5万/块）逐块汇报，避免90%长时间停留。
                        // 注意：保存全程在同一事务（单连接）内，无法真正并行写库——分块只为进度可见+降低内存峰值，
                        // 且失败时可整体回滚，保持"清旧+插新"的原子性。
                        long totalRowsToInsert = sourceFilesToInsert.Count + blocksToInsert.Count + logicUnitsToInsert.Count
                            + interfacesToInsert.Count + varAccessToInsert.Count + blockCallsToInsert.Count
                            + dbFbBindingsToInsert.Count + tagsToInsert.Count;
                        long insertedRows = 0;
                        const int BULK_CHUNK_SIZE = 50_000;

                        async Task InsertWithProgressAsync<T>(List<T> rows, Func<List<T>, Task> bulkCopyAsync, string label)
                        {
                            if (rows == null || rows.Count == 0) return;
                            if (rows.Count <= BULK_CHUNK_SIZE)
                            {
                                await bulkCopyAsync(rows);
                                insertedRows += rows.Count;
                                progressService.SetSavingSubProgress(taskId, insertedRows, totalRowsToInsert, label);
                                return;
                            }
                            for (int offset = 0; offset < rows.Count; offset += BULK_CHUNK_SIZE)
                            {
                                int take = Math.Min(BULK_CHUNK_SIZE, rows.Count - offset);
                                await bulkCopyAsync(rows.GetRange(offset, take));
                                insertedRows += take;
                                progressService.SetSavingSubProgress(taskId, insertedRows, totalRowsToInsert,
                                    $"{label} {Math.Min(offset + take, rows.Count)}/{rows.Count}");
                            }
                        }

                        await InsertWithProgressAsync(sourceFilesToInsert, r => _sourceFileService.BulkCopyAsync(r), "源文件");
                        await InsertWithProgressAsync(blocksToInsert, r => _blockService.BulkCopyAsync(r), "程序块");
                        await InsertWithProgressAsync(logicUnitsToInsert, r => _logicUnitService.BulkCopyAsync(r), "逻辑单元");
                        await InsertWithProgressAsync(interfacesToInsert, r => _blockInterfaceService.BulkCopyAsync(r), "接口变量");
                        await InsertWithProgressAsync(varAccessToInsert, r => _variableAccessService.BulkCopyAsync(r), "变量访问");
                        await InsertWithProgressAsync(blockCallsToInsert, r => _blockCallService.BulkCopyAsync(r), "块调用");
                        await InsertWithProgressAsync(dbFbBindingsToInsert, r => _dbFbBindingService.BulkCopyAsync(r), "DB-FB绑定");
                        await InsertWithProgressAsync(tagsToInsert, r => _ioTagService.BulkCopyAsync(r), "IO标签");
                        await WriteLogHelper.WriteLogsAsync("[BulkCopy完成] 所有数据插入成功", "PLC上传");

                        // Post-process
                        progressService.SetSavingDetail(taskId, "统计块调用关系并更新 Block 元数据");
                        await PostProcessBlockStatisticsAsync(projectId, blocksToInsert, logicUnitsToInsert, blockCallsToInsert);
                        // Post-process
                        //progressService.SetMessage(taskId, "计算所有DB块偏移量");
                        //await _offsetCalculator.CalculateOffsetsForProjectAsync(projectId);

                        //progressService.SetMessage(taskId, "分析IO标签属性");
                        //await AnalyzeTagAttributes(logicUnitsToInsert, varAccessToInsert, tagsToInsert);

                        tran.Complete();
                        // DB→FB 绑定可能已换绑，清空工步前缀缓存
                        _dbFbBindingService.InvalidateStepPrefixCache();
                        await WriteLogHelper.WriteLogsAsync("[事务提交] tran.Complete() 成功", "PLC上传");

                        // GRAPH 步位注册已从上传后台链路移除（2026-09-07）：注册依赖工站绑定
                        // （stationblockbinding，按工站分组注册），上传时人工绑定尚未产生，提前注册只会
                        // 产生空工站分组。现改由「图纸台账 → 批量绑定工站」弹框的
                        // 「确认并自动生成预览 + 保存结构 + graph步位注册」按钮在人工绑定完成后触发。
                        // IO 约束生成不再由注册任务链式接管，统一走下方 step7 本地直触发（增量模式）。
                        try
                        {
                            if (blockNamesToClean.Count == 0 && !tagsContentChanged)
                            {
                                // P1 收益兑现：zip 内全部文件内容未变（且无缺失块、点表内容未变）→ 本次上传
                                // 没有任何数据库变更，GRAPH 注册与约束生成均无需触发。graphStepChained=true
                                // 仅表示「无需触发约束生成」，下方 step7 兜底不会再入队约束任务。
                                // 2026-10-09 检查清单 #7：点表内容变化（tagsContentChanged）时不算零变更。
                                graphStepChained = true;
                                _logger.LogInformation("[PLC上传-后台] 无任何变更块（全部文件内容未变），跳过约束生成: ProjectId={ProjectId}", projectId);
                                await WriteLogHelper.WriteLogsAsync("[无变更] 全部文件内容未变，跳过约束生成", "PLC上传");
                            }
                            else
                            {
                                _logger.LogInformation("[PLC上传-后台] GRAPH 步位注册已移至「批量绑定工站」确认流程，上传阶段不再触发: ProjectId={ProjectId}, 变更块 {Count} 个", projectId, blockNamesToClean.Count);
                                await WriteLogHelper.WriteLogsAsync("[流程调整] GRAPH 步位注册移至批量绑定工站确认按钮，此处仅触发 IO 约束预处理", "PLC上传");
                            }
                        }
                        catch (Exception graphStepEx)
                        {
                            // 日志失败不影响上传流程
                            _logger.LogWarning(graphStepEx, "[PLC上传-后台] 记录流程调整日志失败（不影响上传结果）");
                        }
                    }
                }
                catch (Exception dbEx)
                {
                    // 回滚防御（2026-08-27）：连接断开（如长 BulkCopy 读流中断）时事务对象已被
                    // Npgsql dispose，RollbackTran 会抛 ObjectDisposedException("NpgsqlTransaction")
                    // 掩盖真实根因。PG 侧连接断开时未提交事务由服务器自动 abort，客户端回滚失败是安全的。
                    try
                    {
                        _proj.RollbackTran();
                    }
                    catch (Exception rbEx)
                    {
                        _logger.LogWarning(rbEx, "[PLC上传-后台] 事务回滚失败（连接可能已断开，将由数据库自动回滚）");
                    }
                    var baseEx = dbEx.GetBaseException();
                    _logger.LogError(dbEx, "[PLC上传-后台] 数据库保存失败(事务回滚)，整个批量上传的旧数据未被修改");
                    await WriteLogHelper.WriteLogsAsync($"[事务失败] 数据库保存失败(事务回滚): {dbEx.Message}\n根因: {baseEx.Message}\nStackTrace: {dbEx.StackTrace}\nInner: {dbEx.InnerException?.Message}", "PLC上传");
                    throw new Exception("数据库保存失败: " + baseEx.Message, dbEx);
                }

                // 2026-09-04 PLC 自包含改造：移除上传后自动写 LineUp GRAPH 草稿的联动与统计。

                // 6. Mark completed（带增量结果摘要，前端完成态展示）
                var summaryParts = new List<string>();
                if (skippedUnchanged > 0) summaryParts.Add($"内容未变跳过 {skippedUnchanged} 个文件");
                if (changedBlocksSaved > 0) summaryParts.Add($"更新 {changedBlocksSaved} 个程序块");
                else summaryParts.Add("无程序块数据变更");
                if (missingBlocksCleaned > 0) summaryParts.Add($"全量比对清理缺失块 {missingBlocksCleaned} 个");
                if (staleConstraintsCleaned > 0) summaryParts.Add($"待重生旧约束 {staleConstraintsCleaned} 条（后台自动重生成）");
                if (tagsToInsert.Count > 0) summaryParts.Add($"IO 标签整体刷新 {tagsToInsert.Count} 条");
                var summary = $"上传解析完成：{string.Join("；", summaryParts)}";
                // 2026-09-14 元数据变更联动：块/接口/iotag 已被重写，逐出依赖这些元数据的进程内缓存
                // （LineUp 监控绑定缓存——绑定行 ValidationRevision 不变，版本比对发现不了过期；
                //   精确读取候选回退缓存——iotag 未命中路径的块接口解析结果按项目缓存）。
                // 失败仅记日志，不影响上传结果；周期复查/TTL 兜底。
                try
                {
                    _variableAccessService.InvalidateExactCandidateCache(projectId);
                }
                catch (Exception candidateEx)
                {
                    _logger.LogWarning(candidateEx,
                        "[PLC上传] 候选回退缓存失效失败（不影响上传，由 TTL 兜底） Project={ProjectId}", projectId);
                }
                foreach (var handler in _metadataChangedHandlers)
                {
                    try
                    {
                        await handler.HandleProjectsMetadataChangedAsync(new[] { projectId }, CancellationToken.None);
                    }
                    catch (Exception handlerEx)
                    {
                        _logger.LogWarning(handlerEx,
                            "[PLC上传] 元数据变更联动失败（不影响上传，由周期复查兜底） Project={ProjectId}", projectId);
                    }
                }

                // 7. 约束生成（2026-10-09 拆分为 Hangfire 后台执行，见 PLC_UploadConstraintJob 类注释）：
                //    上传任务在发布锁内完成「入库 → 保存包+版本meta → 入队约束任务 → MarkCompleted」，
                //    XMLV2 整项目发布由 PLC_UploadConstraintJob 接力执行，进度经后处理槽
                //    （xmlv2-constraint）呈现——前端在完成态继续轮询直到槽终态。
                //    语义变化：上传 Completed ≠ 约束已完成；约束发布失败 = 槽 Failed
                //    （保存包与库仍同版本被保留，可用「重置规则」重试，不再 Discard、不再把上传翻成失败）。
                //    版本一致性（接替原全程持锁的后半段）：约束任务执行前校验
                //    「库指纹 == 保存包 meta 指纹 == 入队指纹」，被更新的上传接管时自动让位。
                //    零变更且已有保存包：不发布不入队（原语义保留）；
                //    零变更但没有保存包：照常入队发布——重新上传同一份 zip 是拿到保存包的唯一途径。
                var archiveStore = _serviceProvider.GetRequiredService<PlcArchiveStore>();
                if (graphStepChained && archiveStore.Exists(projectId))
                {
                    progressService.MarkCompleted(taskId, projectId.ToString(), summary + "；约束未变（零变更跳过）");
                }
                else
                {
                    // 指纹失败必须显式失败：约束任务拿不到可信版本校验依据，不能静默丢约束
                    string libraryFingerprint;
                    try
                    {
                        libraryFingerprint = await PLC_UploadConstraintJob.ComputeLibraryFingerprintAsync(_sourceFileService, projectId);
                    }
                    catch (Exception fpEx)
                    {
                        progressService.TryMarkFailed(taskId, "计算库内程序指纹失败，约束生成未入队: " + fpEx.GetBaseException().Message);
                        throw;
                    }

                    // 保存包先于发布落盘（带版本 meta；zip 临时目录在本任务 finally 删除，约束任务读保存包执行）
                    try
                    {
                        archiveStore.SaveWithMeta(projectId, zipPath, new PlcArchiveMeta(libraryFingerprint, taskId, DateTime.UtcNow));
                    }
                    catch (Exception saveEx)
                    {
                        progressService.TryMarkFailed(taskId, "保存 XML 包失败，约束生成未入队（请重新上传）: " + saveEx.GetBaseException().Message);
                        throw;
                    }

                    // 槽先挂 Running（前端拿到 Completed 的第一拍即可见），入队被挡时 Enqueue 内部会改写为接力提示
                    progressService.SetPostProcess(taskId, PLC_UploadConstraintJob.PostProcessKey,
                        PLC_UploadConstraintJob.PostProcessTitle, "Running", "已提交后台约束生成...");
                    PLC_UploadConstraintJob.Enqueue(projectId, taskId, libraryFingerprint, progressService, _logger);
                    progressService.MarkCompleted(taskId, projectId.ToString(), summary + "；约束正在后台生成（完成后见「生成约束」任务状态）");
                }
                } // 结束同项目发布锁（2026-10-09：入库 → 保存包 → 入队约束任务；发布由后台任务接力）
                _logger.LogInformation("[PLC上传-后台] 完成(并发模式): TaskId={TaskId}, Blocks={BlockCount}",
                    taskId, blocksToInsert.Count);
                await WriteLogHelper.WriteLogsAsync($"[完成] TaskId={taskId}, Blocks={blocksToInsert.Count}, LogicUnits={logicUnitsToInsert.Count}, Interfaces={interfacesToInsert.Count}", "PLC上传");

                // 7.5 停机诊断全量分析（2026-09-22 新增）：上传解析只产出 LogicUnit，
                //     AST 是另一条独立链路、不跑就没有；没有 AST 就没有锚点绑定，
                //     没有锚点绑定就播不出监控信号。结果是停机诊断整片「无AST」、
                //     信号心跳每个工位 0核心/0上下文——以前要人去工作台手点两次才能补齐。
                //     这里入队的任务把两步串起来（分析 → 补播缺失的监控信号）。
                //
                //     与手动「一键分析并入库」保持一致：useLlmAnnotation=true（工作台默认值）、
                //     rawContentMaxLength=0、不限工位不限块，走项目级全流程。
                //     带 LLM 很贵，所以靠 IsRunning 防重：上传是高频动作，连传几次不能堆起一摞。
                //     无变更上传（graphStepChained）不触发，语义与上面的约束预处理一致。
                try
                {
                    if (!graphStepChained)
                    {
                        var fullAnalysisProgress = _serviceProvider
                            .GetRequiredService<StopCauseDiagnosis.Services.FullAnalysisProgressService>();
                        var fullAnalysisTaskId =
                            $"full_analysis_upload_{projectId}_{DateTime.Now:yyyyMMddHHmmssfff}";
                        // P2（2026-10-09 检查清单 #14）：占位与检查原子化（TryStartExclusive，锁内
                        // IsRunning + Start），避免同项目并发上传双双通过检查重复入队。
                        if (!fullAnalysisProgress.TryStartExclusive(fullAnalysisTaskId, projectId, null))
                        {
                            _logger.LogInformation(
                                "[PLC上传-后台] 全量分析已在运行，跳过: ProjectId={ProjectId}", projectId);
                        }
                        else
                        {
                            // 与约束任务同样落本机队列，避免被别的机器（dll 版本可能不一致）的 worker 抢走。
                            // P2（2026-10-09 检查清单 #14）：入队失败必须把进度置 Failed——原先 catch 只记
                            // Warning，IsRunning 会一直看到 Running（Cleanup 只删有 CompletedAt 的终态记录），
                            // 后续上传全部跳过 AST 分析与监控信号补播。抛给外层 catch 保持「分析失败不影响
                            // 上传」的既有语义。
                            try
                            {
                                new Hangfire.BackgroundJobClient().Create<StopCauseDiagnosis.StopCauseDiagnosisApp>(
                                    app => app.ExecuteFullAnalysisAndSeedSignalsAsync(
                                        fullAnalysisTaskId, projectId, true, 0),
                                    new Hangfire.States.EnqueuedState(
                                        HSJM.AI.Core.MachineQueue.BuildCurrent()));
                                _logger.LogInformation(
                                    "[PLC上传-后台] 已触发停机诊断全量分析（含监控信号补播）: ProjectId={ProjectId} TaskId={TaskId}",
                                    projectId, fullAnalysisTaskId);
                            }
                            catch (Exception enqueueEx)
                            {
                                fullAnalysisProgress.Fail(fullAnalysisTaskId, 0, "入队失败",
                                    enqueueEx.GetBaseException().Message);
                                throw;
                            }
                        }
                    }
                }
                catch (Exception fullAnalysisEx)
                {
                    // 分析是上传的增值步骤，不能反过来把上传判成失败。
                    _logger.LogWarning(fullAnalysisEx,
                        "[PLC上传-后台] 触发全量分析失败（上传本身不受影响，可到工作台手动跑） ProjectId={ProjectId}",
                        projectId);
                }


                // ==================== 报警变量自动识别（NEW）：上传解析完成后自动入队 ====================
                // 三通道识别（结构证据 + LLM 批量语义）→ upsert 进 ai_plc_alarm_variable 台账（人工确认锁保护）。
                // 默认 MonitorEnabled=false，页面人工复核后才开启监控；失败不影响上传结果。
                try
                {
                    var alarmVarEnqueued = PLC.PLC_AlarmVariable.AlarmVariableRecognitionService.EnqueueIfNotRunning(projectId);
                    _logger.LogInformation("[PLC上传-后台] 报警变量识别任务{Result}: ProjectId={ProjectId}",
                        alarmVarEnqueued ? "已入队" : "已在运行，跳过", projectId);
                }
                catch (Exception alarmVarEx)
                {
                    _logger.LogWarning(alarmVarEx, "[PLC上传-后台] 触发报警变量识别失败（不影响上传结果）");
                }

                // ==================== PLC 标签增量注册（NEW 2026-09-11）：与约束/报警任务并行 ====================
                // 用户拍板并入上传链路：只注册本次变更块的接口行 + （带标签 CSV 时）IOTag，不等工站绑定。
                // 与「注册PLC标签」按钮同规则；失败只记日志，可随时用按钮全量兜底。
                // 无变更且未刷新 IOTag 时不入队（无意义空跑）。
                // 状态对前端可见（路线 A 2026-09-11）：入队瞬间同步写进度后处理槽（前端拿到 Completed 的
                // 第一拍就能看到"标签增量注册: Running"，随后任务把槽推进到终态；入队被跳过/失败也落槽）。
                // 2026-09-19 sub-project B：非老网关 URL 的项目改入队 PLC_IgwRegistrationJob（不探测网关类型，任务内以批量接口探测为准）
                // （批量接口按通道/设备/组注册 DB 变量 + IO 标签，增量替换变更块、清除已删除块）；老网关项目保持原任务不变。
                var tagRegPostKey = PLC_TagRegistrationJob.PostProcessKey;
                var tagRegPostTitle = PLC_TagRegistrationJob.PostProcessTitle;
                try
                {
                    var useIgwRegistration = false;
                    if (blockNamesToClean.Count > 0 || tagsToInsert.Count > 0)
                    {
                        var regProject = await _proj.GetByIdAsync(projectId).ConfigureAwait(false);
                        var regIotUrl = regProject?.IotUrl;
                        // 非老网关 URL 一律入队 IGW 注册任务（不在上传链路探测网关）：任务内的 runner 探测批量接口，
                        // IGW 不可达 / 非 IGW（404/405）/ 版本过低时 Failed，结果都落在「IGW 注册」进度槽
                        useIgwRegistration = PLC_IgwRegistrationJob.IsIgwProject(regIotUrl);
                    }
                    if (useIgwRegistration)
                    {
                        tagRegPostKey = PLC_IgwRegistrationJob.PostProcessKey;
                        tagRegPostTitle = PLC_IgwRegistrationJob.PostProcessTitle;
                        // deleted = 本次清理的块名中，上传事务后数据库已无对应未删除 DB 块的名字（变更块已重插，仍存活）
                        var cleanNames = blockNamesToClean.ToList();
                        var liveDbBlocks = await _blockService.GetListAsync(x =>
                            x.ProjectId == projectId &&
                            x.IsDeleted == false &&
                            x.ProgrammingLanguage == "DB" &&
                            cleanNames.Contains(x.BlockName)).ConfigureAwait(false);
                        var liveDbNames = new HashSet<string>(liveDbBlocks.Select(x => x.BlockName), StringComparer.OrdinalIgnoreCase);
                        var igwChanged = cleanNames.Where(liveDbNames.Contains).ToArray();
                        var igwDeleted = cleanNames.Where(n => !liveDbNames.Contains(n)).ToArray();
                        var igwResult = await PLC_IgwRegistrationJob.EnqueueAsync(
                            projectId, igwChanged, igwDeleted, tagsToInsert.Count > 0, full: false, taskId,
                            _serviceProvider.GetRequiredService<IPLC_IgwRegistrationStateService>(), _logger).ConfigureAwait(false);
                        progressService.SetPostProcess(taskId, tagRegPostKey, tagRegPostTitle, "Running",
                            igwResult == IgwEnqueueResult.Enqueued
                                ? "已入队，后台注册中"
                                : "同项目已有 IGW 注册任务在执行，本次变更已并入，完成后自动补跑");
                        _logger.LogInformation(
                            "[PLC上传-后台] IGW 注册任务{Result}: ProjectId={ProjectId}, 变更 DB 块 {Changed} 个, 删除块候选 {Deleted} 个, IOTag刷新={IoTagRefreshed}",
                            igwResult == IgwEnqueueResult.Enqueued ? "已入队" : "已并入执行中任务的补跑",
                            projectId, igwChanged.Length, igwDeleted.Length, tagsToInsert.Count > 0);
                    }
                    else if (blockNamesToClean.Count > 0 || tagsToInsert.Count > 0)
                    {
                        var tagRegEnqueued = PLC_TagRegistrationJob.EnqueueIfNotRunning(
                            projectId,
                            blockNamesToClean.ToArray(),
                            tagsToInsert.Count > 0,
                            taskId);
                        if (tagRegEnqueued)
                        {
                            progressService.SetPostProcess(taskId, PLC_TagRegistrationJob.PostProcessKey,
                                PLC_TagRegistrationJob.PostProcessTitle, "Running", "已入队，后台注册中");
                        }
                        else
                        {
                            progressService.SetPostProcess(taskId, PLC_TagRegistrationJob.PostProcessKey,
                                PLC_TagRegistrationJob.PostProcessTitle, "Skipped",
                                "同项目已有注册任务在运行，本次跳过（可用「注册PLC标签」按钮全量兜底）");
                        }
                        _logger.LogInformation(
                            "[PLC上传-后台] PLC 标签增量注册任务{Result}: ProjectId={ProjectId}, 变更块 {BlockCount} 个, IOTag刷新={IoTagRefreshed}",
                            tagRegEnqueued ? "已入队" : "已在运行，跳过（可用「注册PLC标签」按钮全量兜底）",
                            projectId, blockNamesToClean.Count, tagsToInsert.Count > 0);
                    }
                }
                catch (Exception tagRegEx)
                {
                    _logger.LogWarning(tagRegEx, "[PLC上传-后台] 触发 PLC 标签增量注册失败（不影响上传结果）");
                    try
                    {
                        progressService.SetPostProcess(taskId, tagRegPostKey,
                            tagRegPostTitle, "Failed",
                            $"任务入队失败: {tagRegEx.GetBaseException().Message}（可用「注册PLC标签」按钮全量兜底）");
                    }
                    catch
                    {
                        // 进度写入失败只记日志，不影响上传结果
                    }
                }
            }
            catch (Exception ex)
            {
                // 用 GetBaseException 透出根因：上层包装异常的 Message 常只有类型名（如 "NpgsqlTransaction"），
                // 真实原因（网络断流/超时）在 Inner 链最底部
                var baseEx = ex.GetBaseException();
                // 先写进度再记日志（日志写失败不能让前端拿不到终态）。已失败（如 XMLV2 发布失败）保留第一条更具体的原因，
                // 已完成的不翻成失败（审查复核 I-1）
                PLC_UploadConstraintStage.ReportUploadFailure(progressService, taskId, ex);
                _logger.LogError(ex, "[PLC上传-后台] 失败: TaskId={TaskId}", taskId);
                await WriteLogHelper.WriteLogsAsync($"[异常] TaskId={taskId}, Error: {ex.Message}\n根因: {baseEx.Message}\nStackTrace: {ex.StackTrace}\nInner: {ex.InnerException?.Message}", "PLC上传");
            }
            finally
            {
                semaphore.Dispose();
                // Cleanup temp folder
                if (Directory.Exists(tempFolder))
                {
                    try { Directory.Delete(tempFolder, true); } catch { }
                }
            }
        }

        /// <summary>
        /// 更新变量访问记录表（IsIOTag）
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="entityLs"></param>
        /// <returns></returns>
        private async Task ExecuteBulkUpdateVariable(long projectId, List<PLC_IOTagEntity> entityLs = null)
        {
            var ls = await _variableAccessService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);
            if (ls.Count <= 0 || entityLs == null || entityLs.Count <= 0)
            {
                return;
            }
            foreach (var item in ls)
            {
                item.IsIOTag = entityLs.Any(it => it.TagName == item.VariableName && it.ProjectId == item.ProjectId);
            }

            await _variableAccessService.BulkUpdateAsync(ls);

        }

        /// <summary>
        /// 解析单个文件（用于并发调用）
        /// </summary>
        private async Task<FileParseResult> ParseSingleFileAsync(
            string filePath,
            string scriptPath,
            string pythonCmd,
            long projectId,
            string parseRunId,
            IReadOnlyDictionary<string, string> blockPathMap)
        {
            try
            {

                PLC_ParseResultDTO parseResult = null;
                var ext = Path.GetExtension(filePath);
                if (ext.ToLower().Contains("csv"))
                {
                    var ls = await ParseTagCSV(filePath, projectId);
                    if (ls != null && ls.Count > 0)
                    {
                        parseResult = new PLC_ParseResultDTO();
                        parseResult.tags = ls;
                    }

                }
                else
                {

                    string outputJsonPath = Path.ChangeExtension(filePath, ".parsed.json");

                    var processInfo = new ProcessStartInfo
                    {
                        FileName = pythonCmd,
                        Arguments = $"\"{scriptPath}\" --file \"{filePath}\" --format json-v2 --output \"{outputJsonPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(scriptPath)
                    };

                    using (var process = Process.Start(processInfo))
                    {
                        string stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (process.ExitCode != 0)
                        {
                            _logger.LogWarning("[PLC上传-并发] 解析文件失败: {File}, Error: {Error}", filePath, stderr);
                            await WriteLogHelper.WriteLogsAsync($"[Python解析失败] File={Path.GetFileName(filePath)}, ExitCode={process.ExitCode}, Stderr={stderr?.Substring(0, Math.Min(stderr?.Length ?? 0, 500))}", "PLC上传");
                            return null;
                        }
                    }

                    if (!File.Exists(outputJsonPath))
                    {
                        return null;
                    }

                    string jsonContent = await File.ReadAllTextAsync(outputJsonPath);

                    try
                    {
                        parseResult = JsonSerializer.Deserialize<PLC_ParseResultDTO>(jsonContent);
                    }
                    catch (Exception jsonEx)
                    {
                        await WriteLogHelper.WriteLogsAsync($"[JSON反序列化失败] File={Path.GetFileName(filePath)}, Error={jsonEx.Message}", "PLC上传");
                        return null;
                    }

                    if (parseResult == null)
                    {
                        return null;
                    }
                }

                // 构建结果
                var result = new FileParseResult
                {
                    PhysicalSourcePath = filePath,
                    LogicUnits = new List<PLC_LogicUnitEntity>(),
                    Interfaces = new List<PLC_BlockInterfaceEntity>(),
                    VariableAccesses = new List<PLC_VariableAccessEntity>(),
                    BlockCalls = new List<PLC_BlockCallEntity>(),
                    Tags = new List<PLC_IOTagEntity>()
                };

                // Source File
                var sourceId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                result.SourceFile = new PLC_SourceFileEntity
                {
                    Id = sourceId,
                    ProjectId = projectId,
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath,
                    FileType = Path.GetExtension(filePath).TrimStart('.'),
                    FileSize = new FileInfo(filePath).Length,
                    // 2026-10-09 检查清单 #3：同步路径同样存「解析输入指纹」（主文件＋伴随源码）。
                    FileHash = ComputeSourceFingerprint(filePath),
                    FileContent = TryReadGraphSourceContent(filePath, parseResult), // GRAPH 块原文归档（供 LineUp 预览复用）
                    ParseStatus = 1,
                    ParseTime = DateTime.Now,
                    IsDeleted = false
                };
                SetBaseField(result.SourceFile);

                // Process blocks
                if (parseResult.blocks != null && parseResult.blocks.Count > 0)
                {
                    var blockInfo = parseResult.blocks[0];
                    var blockId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();

                    string metadataJson = null;
                    if (parseResult.metadata != null)
                    {
                        var metadataObj = new
                        {
                            brand = parseResult.metadata.brand,
                            product = parseResult.metadata.product,
                            version = parseResult.metadata.version,
                            station_id = parseResult.metadata.station_id,
                            parse_config = parseResult.metadata.parse_config,
                            parse_timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            source_file = Path.GetFileName(filePath),
                            semantic_hints = parseResult.semantic_hints != null ? new
                            {
                                station_type = parseResult.semantic_hints.station_type,
                                function_category = parseResult.semantic_hints.function_category,
                                keywords_detected = parseResult.semantic_hints.keywords_detected,
                                confidence = parseResult.semantic_hints.confidence,
                                inferred_purpose = parseResult.semantic_hints.inferred_purpose
                            } : null,
                            errors = parseResult.errors
                        };
                        metadataJson = JsonSerializer.Serialize(metadataObj, new JsonSerializerOptions { WriteIndented = false });
                    }

                    result.Block = new PLC_BlockEntity
                    {
                        Id = blockId,
                        SourceFileId = sourceId,
                        ProjectId = projectId,
                        BlockName = blockInfo.name,
                        BlockType = blockInfo.type,
                        BlockNumber = blockInfo.number ?? 0,
                        ProgrammingLanguage = blockInfo.language,
                        Description = ResolveBlockDescription(blockInfo.name, blockInfo.description, blockPathMap),
                        IsPotentialEntry = blockInfo.is_potential_entry,
                        MetadataJson = metadataJson,
                        ParseRunId = parseRunId,
                        SourceHash = ComputeGraphSourceHash(parseResult),
                        IsDeleted = false
                    };
                    SetBaseField(result.Block);

                    // DB→FB 绑定关系（InstanceDB 专有）：随 DB 块一同入库 ai_plc_dbfbinding
                    if (!string.IsNullOrWhiteSpace(blockInfo.instance_of_name))
                    {
                        result.DbFbBinding = new PLC_DbFbBindingEntity
                        {
                            ProjectId = projectId,
                            BlockId = blockId,
                            DbName = blockInfo.name,
                            InstanceOfName = blockInfo.instance_of_name,
                            InstanceOfType = blockInfo.instance_of_type,
                            IsDeleted = false
                        };
                        SetBaseField(result.DbFbBinding);
                    }

                    // Logic Units with mapping
                    var compileUnitToLogicUnitMap = new Dictionary<string, long>();

                    if (parseResult.logic_units != null)
                    {
                        foreach (var unit in parseResult.logic_units)
                        {
                            var unitId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                            var readCount = CountLogicUnitReads(unit);
                            var writeCount = CountLogicUnitWrites(unit);

                            int callsInUnit = 0;
                            if (parseResult.calls != null && !string.IsNullOrEmpty(unit.compile_unit_id))
                            {
                                callsInUnit = parseResult.calls.Count(c => c.network_id == unit.compile_unit_id);
                            }

                            var logicUnit = new PLC_LogicUnitEntity
                            {
                                Id = unitId,
                                BlockId = blockId,
                                UnitType = unit.unit_type,
                                NetworkId = unit.network_id,
                                Title = unit.title,
                                Comment = unit.comment,
                                RawContent = unit.raw_content,
                                UidSymbolMapJson = unit.uid_symbol_map != null ? JsonSerializer.Serialize(unit.uid_symbol_map) : null,
                                ProgrammingLanguage = unit.programming_language,
                                ReadVarsCount = readCount,
                                WriteVarsCount = writeCount,
                                CallsCount = callsInUnit,
                                CallPinBindingsJson = (unit.call_pin_bindings != null && unit.call_pin_bindings.Count > 0)
                                    ? JsonSerializer.Serialize(unit.call_pin_bindings)
                                    : null,
                                GraphOutgoingTransitionsJson = (unit.outgoing_transition_numbers != null && unit.outgoing_transition_numbers.Count > 0)
                                    ? JsonSerializer.Serialize(unit.outgoing_transition_numbers)
                                    : null,
                                IsDeleted = false
                            };
                            SetBaseField(logicUnit);
                            result.LogicUnits.Add(logicUnit);

                            if (!string.IsNullOrEmpty(unit.compile_unit_id))
                            {
                                compileUnitToLogicUnitMap[unit.compile_unit_id] = unitId;
                            }

                            if (unit.variable_access != null)
                            {
                                foreach (var access in unit.variable_access)
                                {
                                    var accessId = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
                                    var varAccess = new PLC_VariableAccessEntity
                                    {
                                        Id = accessId,
                                        LogicUnitId = unitId,
                                        BlockId = blockId,
                                        ProjectId = projectId,
                                        VariableName = access.variable,
                                        AccessType = access.access_type,
                                        Uid = access.uid,
                                        Comment = access.comment,
                                        Instruction = access.instruction,
                                        AddressMode = access.address_mode,
                                        VariableScope = access.variable_scope,
                                        IsDeleted = false
                                    };
                                    SetBaseField(varAccess);
                                    result.VariableAccesses.Add(varAccess);
                                }
                            }
                        }
                    }

                    // Block Calls
                    if (parseResult.calls != null)
                    {
                        foreach (var call in parseResult.calls)
                        {
                            if (string.IsNullOrEmpty(call.caller))
                                continue;

                            long? logicUnitId = null;
                            if (!string.IsNullOrEmpty(call.network_id) &&
                                compileUnitToLogicUnitMap.TryGetValue(call.network_id, out var mappedId))
                            {
                                logicUnitId = mappedId;
                            }

                            var blockCallEntity = new PLC_BlockCallEntity
                            {
                                ProjectId = projectId,
                                CallerBlockId = blockId,
                                CallerBlockName = call.caller,
                                CalleeBlockName = call.callee,
                                CalleeBlockType = call.callee_type,
                                LogicUnitId = logicUnitId,
                                NetworkId = int.TryParse(call.network_id, System.Globalization.NumberStyles.HexNumber, null, out var nid) ? nid : null,
                                IsDeleted = false
                            };
                            SetBaseField(blockCallEntity);
                            result.BlockCalls.Add(blockCallEntity);
                        }
                    }

                    // Interface
                    if (parseResult.@interface != null)
                    {
                        foreach (var item in parseResult.@interface)
                        {
                            AddInterfaceRecursively(item, blockId, null, 0, result.Interfaces);
                        }
                    }
                }
                if (parseResult.tags != null && parseResult.tags.Count > 0)
                {
                    result.Tags.AddRange(parseResult.tags);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PLC上传-并发] 处理文件异常: {File}", filePath);
                await WriteLogHelper.WriteLogsAsync($"[文件处理异常] File={Path.GetFileName(filePath)}, Error={ex.Message}", "PLC上传");
                return null;
            }
        }

        /// <summary>
        /// 单个文件的解析结果
        /// </summary>
        private class FileParseResult
        {
            public string PhysicalSourcePath { get; set; }
            public PLC_SourceFileEntity SourceFile { get; set; }
            public PLC_BlockEntity Block { get; set; }
            public List<PLC_LogicUnitEntity> LogicUnits { get; set; }
            public List<PLC_BlockInterfaceEntity> Interfaces { get; set; }
            public List<PLC_VariableAccessEntity> VariableAccesses { get; set; }
            public List<PLC_BlockCallEntity> BlockCalls { get; set; }
            public List<PLC_IOTagEntity> Tags { get; set; }
            /// <summary>DB→FB 绑定关系（仅 InstanceDB 且解析出 InstanceOfName 时非空）</summary>
            public PLC_DbFbBindingEntity DbFbBinding { get; set; }
        }


        /// <summary>
        /// 获取上传进度
        /// </summary>
        public UploadProgress GetUploadProgress(string taskId)
        {
            var progressService = _serviceProvider.GetRequiredService<PLC_UploadProgressService>();
            return progressService.GetProgress(taskId);
        }

        #endregion

        /// <summary>
        /// 批量注册标签名，I点，Q点
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExecuteBatchRegistrationTag(long projectId)
        {

            HttpResponseResultModel<string> httpResponseResultModel = new HttpResponseResultModel<string>();
            var projectEntity = await _proj.GetByIdAsync(projectId);
            if (projectEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "项目不存在";
                return httpResponseResultModel;
            }
            // 2026-09-12 修复（NRE 防御链）：IotUrl 为空时网关调用必然失败，前置拦截给出明确错误
            if (string.IsNullOrWhiteSpace(projectEntity.IotUrl))
            {
                httpResponseResultModel.ErrorMessage = "项目未配置网关地址（IotUrl 为空），无法注册";
                return httpResponseResultModel;
            }

            //var stationLs = projectEntity.StationId.Split("|").ToList();
            var blockLs = await _blockService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId && it.ProgrammingLanguage == "DB");

            var ids = blockLs.Select(it => it.Id).ToList();
            var interLs = await _blockInterfaceService.GetListAsync(it => it.IsDeleted == false && ids.Contains(it.BlockId.Value) && !string.IsNullOrEmpty(it.Offset) && (GlobalStaticVariable.PlcDataTypeList.Any(x => x == it.DataType) || it.DataType.StartsWith("String") || it.DataType.StartsWith("WString")));
            List<IQTagItem> list = new List<IQTagItem>();
            var s7 = projectEntity.StationId + "_S7";
            var s7Plus = projectEntity.StationId + "_Plus";
            //blockLs = blockLs.Where(x => x.BlockName == "Yield_DB").ToList();
            foreach (var item in blockLs)
            {
                var ls = interLs.Where(it => it.BlockId == item.Id).ToList();
                foreach (var v in ls)
                {
                    IQTagItem tag = new IQTagItem();
                    tag.ByteSize = 0;
                    tag.DataType = getDataType(v.DataType);
                    tag.Address = item.ProgrammingLanguage + item.BlockNumber + "." + v.Offset;
                    if (tag.DataType != "Bool") { 
                    tag.Address = item.ProgrammingLanguage + item.BlockNumber + "." + v.Offset.Split('.')[0];
                        if (tag.DataType == "String")
                        {
                            // 2026-09-12 防御：OffsetByte 为 null 的行无法注册（不可信偏移），跳过
                            if (v.OffsetByte == null) { continue; }
                            tag.ByteSize = v.OffsetByte.Value;
                            Match match = Regex.Match(v.DataType, @"String\[(\d+)\]");

                            if (match.Success)
                            {
                                // 取捕获组 1 的值
                                string numberStr = match.Groups[1].Value;

                                // 转成 int（如果你需要数字类型）
                                int number = int.Parse(numberStr);
                                tag.ByteSize = number;
                            }
                            else
                            {
                                tag.ByteSize = 255;
                            }
                        }
                        else if (tag.DataType == "WString")
                        {
                            // 2026-09-12 防御：同 String 分支
                            if (v.OffsetByte == null) { continue; }
                            tag.ByteSize = v.OffsetByte.Value;
                            Match match = Regex.Match(v.DataType, @"WString\[(\d+)\]");

                            if (match.Success)
                            {
                                // 取捕获组 1 的值
                                string numberStr = match.Groups[1].Value;

                                // 转成 int（如果你需要数字类型）
                                int number = int.Parse(numberStr);
                                tag.ByteSize = number;
                            }
                            else
                            {
                                tag.ByteSize = 255;
                            }
                        }


                    }
                    tag.TagPathNames = new List<string>
                                        {
                                            item.BlockName,
                                            v.VariablePath,
                                        };
                    list.Add(tag);
                    if (list.Count == 2000)
                    {
                        await _iotService.ManualAddTagItems(projectEntity.IotUrl, s7, list);
                        list.Clear();
                    }
                }
                await _iotService.AddGroupTagItems(projectEntity.IotUrl, s7Plus, item.BlockName);

            }

            var tagLs = await _ioTagService.GetListAsync(it => it.IsDeleted == false && it.ProjectId == projectId);

            foreach (var item in tagLs)
            {
                // 2026-09-12 防御：无地址的 IOTag 行无法注册，跳过（防 Address.Replace NRE）
                if (string.IsNullOrWhiteSpace(item.Address)) { continue; }
                IQTagItem tag = new IQTagItem();
                tag.ByteSize = 1;
                tag.DataType = item.DataType;
                tag.Address = item.Address.Replace("%", "");
                tag.TagPathNames = new List<string>
                {
                    item.TableName,
                    item.TagName
                };
                list.Add(tag);
            }
            WriteLogHelper.WriteLogsAsync(JsonHelper.SerializeObject(list),"S7注册测试");
            var res = await _iotService.ManualAddTagItems(projectEntity.IotUrl, s7, list);
            // 2026-09-12 修复：res 判空防 NRE（IotService 已改为不返回 null，这里双保险）
            httpResponseResultModel.IsSuccess = res?.IsSuccess == true;
            httpResponseResultModel.ErrorMessage = res?.ErrorMessage ?? "网关无返回（若 ioturl 指向新 IGW(48020)，本接口只支持老网关 /DriverInfo 协议）";

            return httpResponseResultModel;
        }

        /// <summary>
        /// V2（2026-09-12 新增；2026-09-19 sub-project B 改走批量注册）：按项目 ioturl 自动分派网关类型的手动全量注册。
        /// 非老网关 URL → 立即入队 PLC_IgwRegistrationJob 全量注册（批量接口，本项目通道下 S7 / S7Plus 设备，DB 变量 + IO 标签），
        /// 返回「已提交 IGW 全量注册（后台执行）」；
        /// 老网关 URL（DcsIgwConsts.IsLegacyIotGatewayUrl）→ 委托原 <see cref="ExecuteBatchRegistrationTag"/>（行为零改动，仍同步）。
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public async Task<HttpResponseResultModel<string>> ExecuteBatchRegistrationTagV2(long projectId)
        {
            var httpResponseResultModel = new HttpResponseResultModel<string>();
            var projectEntity = await _proj.GetByIdAsync(projectId);
            if (projectEntity == null)
            {
                httpResponseResultModel.ErrorMessage = "项目不存在";
                return httpResponseResultModel;
            }
            if (string.IsNullOrWhiteSpace(projectEntity.IotUrl))
            {
                httpResponseResultModel.ErrorMessage = "项目未配置网关地址（IotUrl 为空），无法注册";
                return httpResponseResultModel;
            }

            // 老网关 URL → 原同步方法；其余一律入队 IGW 注册任务（任务内探测批量接口：IGW 不可达 / 版本过低 → Failed）
            if (DcsIgwConsts.IsLegacyIotGatewayUrl(projectEntity.IotUrl))
            {
                // 老网关：委托原方法（零改动，沿用其全部行为）
                return await ExecuteBatchRegistrationTag(projectId).ConfigureAwait(false);
            }

            // ===== IGW 路径（2026-09-19 sub-project B）=====
            // 入队 PLC_IgwRegistrationJob 全量注册（批量接口：本项目通道下 S7/S7Plus 设备，全部 DB 块 + IO 标签整组替换，
            // 台账中已不存在的块一并清除），接口秒回；同项目任务执行中时本次并入其补跑，不会丢失。
            if (string.IsNullOrWhiteSpace(projectEntity.PlcIp))
            {
                httpResponseResultModel.ErrorMessage = "IGW 注册需要 PLC 直连 IP（项目 plcip 为空），请先在项目上填写 PLC IP";
                return httpResponseResultModel;
            }

            var enqueueResult = await PLC_IgwRegistrationJob.EnqueueAsync(projectId, Array.Empty<string>(), Array.Empty<string>(),
                ioTagsRefreshed: true, full: true, taskId: null,
                _serviceProvider.GetRequiredService<IPLC_IgwRegistrationStateService>(), _logger).ConfigureAwait(false);
            httpResponseResultModel.IsSuccess = true;
            httpResponseResultModel.BackResult = enqueueResult == IgwEnqueueResult.Enqueued
                ? "已提交 IGW 全量注册（后台执行）"
                : "已提交 IGW 全量注册（后台执行；同项目注册任务执行中，完成后自动补跑全量）";
            await WriteLogHelper.WriteLogsAsync($"[标签注册-IGW-V2] {httpResponseResultModel.BackResult}", "PLC上传").ConfigureAwait(false);
            return httpResponseResultModel;
        }

        /// <summary>
        /// 获取数据类型
        /// </summary>
        /// <param name="dataType"></param>
        /// <returns></returns>
        private string getDataType(string dataType)
        {
            var lowerStr = dataType.ToLower();
            if (lowerStr.StartsWith("string"))
            {
                return "String";
            }
            else if (lowerStr.StartsWith("wstring"))
            {
                return "WString";
            }
            else
            {
                switch (dataType)
                {
                    case "Word":
                    case "UInt":
                        return "UInt16";
                    case "USInt":
                        return "Byte";
                    case "DWord":
                        return "UInt32";
                    case "Real":
                        return "Float";
                    case "LReal":
                        return "Duoble";
                    default:
                        return dataType;

                }
            }
        }

        /// <summary>
        /// 约束重置（审查 P2，2026-10-02）：不再预先删除规则，也不再走老方法。用项目保存的上传包在后台（本机队列、同项目防重）
        /// 调 XMLV2 发布器整项目重新发布——发布器先生成、再在事务里替换非手工规则，失败时旧规则不动。
        /// projectId&lt;=0：所有未删除项目逐个同样处理，没有保存包的项目跳过并在结果里列出。
        /// </summary>
        public async Task<HttpResponseResultModel<string>> ResetRule(long projectId)
        {
            var archives = _serviceProvider.GetRequiredService<PlcArchiveStore>();
            if (projectId > 0)
            {
                if (!archives.Exists(projectId))
                    return new HttpResponseResultModel<string> { IsSuccess = false, BackResult = PLC_XmlV2RerunJob.NoSavedArchiveMessage, ErrorMessage = PLC_XmlV2RerunJob.NoSavedArchiveMessage };
                if (!PLC_XmlV2RerunJob.EnqueueProject(projectId))
                    return new HttpResponseResultModel<string> { IsSuccess = false, BackResult = "该项目已有约束重置 / 重跑任务在排队或执行，请稍后再试", ErrorMessage = "该项目已有约束重置 / 重跑任务在排队或执行，请稍后再试" };
                return new HttpResponseResultModel<string>
                {
                    IsSuccess = true,
                    BackResult = "已加入后台队列：用保存的 XML 包按 XMLV2 整项目重新生成约束（生成成功后才替换，失败时旧约束不动）"
                };
            }

            var projects = await _proj.GetListAsync(p => !p.IsDeleted);
            var enqueued = new List<string>();
            var noArchive = new List<string>();
            var busy = new List<string>();
            foreach (var project in projects)
            {
                var label = $"{project.ProjectName}#{project.Id}";
                if (!archives.Exists(project.Id)) noArchive.Add(label);
                else if (PLC_XmlV2RerunJob.EnqueueProject(project.Id)) enqueued.Add(label);
                else busy.Add(label);
            }
            var parts = new List<string> { $"已入队 {enqueued.Count} 个项目（XMLV2 整项目重新发布）" };
            if (noArchive.Count > 0) parts.Add($"没有保存的 XML 包、已跳过 {noArchive.Count} 个（请重新上传一次）：{string.Join("、", noArchive)}");
            if (busy.Count > 0) parts.Add($"已有任务在排队或执行、已跳过 {busy.Count} 个：{string.Join("、", busy)}");
            return new HttpResponseResultModel<string> { IsSuccess = true, BackResult = string.Join("；", parts) };
        }

        /// <summary>
        /// 单 LU 重跑（审查 P3，2026-10-02）：用保存的上传包跑全项目生成（代码分析全项目，AI 走缓存），只替换该 LU 所在程序段的
        /// XMLV2 规则、同 LU 的老方法规则与报警清单行。先在请求里校验保存包与映射（映射不上直接失败、不改任何东西），再入后台队列。
        /// refreshAi：只清该范围的普通网络 AI 与规则措辞缓存后再跑（被调 FB 的契约缓存不清）。
        /// </summary>
        public async Task<HttpResponseResultModel<string>> RerunSingleLuAsync(long luId, bool refreshAi = false)
        {
            try
            {
                var (projectId, request) = await PLC_XmlV2RerunJob.BuildLogicUnitRequestAsync(_logicUnitService, _blockService, luId);
                var scope = ValidateRerunScope(projectId, request);
                if (!PLC_XmlV2RerunJob.EnqueueLogicUnit(projectId, luId, refreshAi))
                    return RerunFailed("该项目已有约束重置 / 重跑任务在排队或执行，请稍后再试");
                return new HttpResponseResultModel<string>
                {
                    IsSuccess = true,
                    BackResult = $"已加入后台队列：按 XMLV2 重跑 LU#{luId}（{scope.BlockName} 网络 {request.Target?.NetworkId}），只替换该范围的约束"
                        + (refreshAi ? "，范围内 AI 重新读取" : "")
                        + (scope.Notes.Count > 0 ? "；" + string.Join("；", scope.Notes) : "")
                };
            }
            catch (PLC.IndependentPipeline.PipelineException ex)
            {
                return RerunFailed(PLC_XmlV2RerunJob.DescribeError(ex));
            }
        }

        /// <summary>
        /// 单块重跑（审查 P3，2026-10-02）：同 <see cref="RerunSingleLuAsync"/>，范围 = 该块全部程序段（含库里已不存在的旧程序段的规则）。
        /// </summary>
        public async Task<HttpResponseResultModel<string>> RerunBlockAsync(long blockId, bool refreshAi = false)
        {
            try
            {
                var (projectId, request) = await PLC_XmlV2RerunJob.BuildBlockRequestAsync(_logicUnitService, _blockService, blockId);
                var scope = ValidateRerunScope(projectId, request);
                if (!PLC_XmlV2RerunJob.EnqueueBlock(projectId, blockId, refreshAi))
                    return RerunFailed("该项目已有约束重置 / 重跑任务在排队或执行，请稍后再试");
                return new HttpResponseResultModel<string>
                {
                    IsSuccess = true,
                    BackResult = $"已加入后台队列：按 XMLV2 重跑程序块 {scope.BlockName}（{scope.ArchiveUnitIds.Count} 个程序段），只替换该块范围的约束"
                        + (refreshAi ? "，范围内 AI 重新读取" : "")
                        + (scope.Notes.Count > 0 ? "；" + string.Join("；", scope.Notes) : "")
                };
            }
            catch (PLC.IndependentPipeline.PipelineException ex)
            {
                return RerunFailed(PLC_XmlV2RerunJob.DescribeError(ex));
            }
        }

        private static HttpResponseResultModel<string> RerunFailed(string message) =>
            new() { IsSuccess = false, BackResult = message, ErrorMessage = message };

        /// <summary>请求里先校验：有保存包、保存包里能找到范围（映射不上 / 缺程序体直接失败）。后台任务执行时会再校验一次。</summary>
        private PLC.IndependentPipeline.Original353.Original353RerunScopeResolved ValidateRerunScope(long projectId,
            PLC.IndependentPipeline.Original353.Original353RerunRequest request)
        {
            var archives = _serviceProvider.GetRequiredService<PlcArchiveStore>();
            // 读保存包时持发布锁（拿不到立即失败）：不和上传在锁内替换保存包抢文件（Windows 下被打开的目标文件无法替换，审查复核 M-1）
            using var publishLock = PLC_XmlV2RerunJob.TryAcquirePublishLock(projectId)
                ?? throw new PLC.IndependentPipeline.PipelineException("ProjectPublishBusy");
            if (!archives.Exists(projectId))
                throw new PLC.IndependentPipeline.PipelineException("NoSavedArchive");
            var source = PLC.IndependentPipeline.Original353.Original353ArchiveSource.Read(archives.PathFor(projectId), projectId, []);
            return PLC.IndependentPipeline.Original353.Original353RerunScope.Resolve(source, request);
        }

        /// <summary>
        /// 手动重跑 GRAPH 步位注册（判定 → 台账 → 网关注册 → 读值验证）。
        /// 适合修好项目 IotUrl / 网关恢复后补注册，无需重新上传 zip。
        /// </summary>
        public async Task<HttpResponseResultModel<string>> RerunGraphStepRegistrationAsync(long projectId)
        {
            var projectEntity = await _proj.GetByIdAsync(projectId);
            if (projectEntity == null)
            {
                return new HttpResponseResultModel<string> { IsSuccess = false, BackResult = $"项目#{projectId} 不存在" };
            }

            var enqueued = Agent.Services.Plc_diagnosis.GraphStepTagRegistrationJob.EnqueueIfNotRunning(projectId);
            return new HttpResponseResultModel<string>
            {
                IsSuccess = enqueued,
                BackResult = enqueued
                    ? $"已入队项目#{projectId} 的 GRAPH 步位注册后台任务（IotUrl={projectEntity.IotUrl}，设备 {projectEntity.StationId}_Plus）"
                    : $"项目#{projectId} 已有 GRAPH 步位注册任务执行中，本次未重复入队"
            };
        }
    }

    #region DTO

    /// <summary>
    /// 上传任务启动结果
    /// </summary>
    public class UploadTaskStartResult
    {
        /// <summary>
        /// 任务ID（用于查询进度）
        /// </summary>
        public string TaskId { get; set; }

        /// <summary>
        /// Hangfire 任务ID
        /// </summary>
        public string HangfireJobId { get; set; }

        /// <summary>
        /// 消息
        /// </summary>
        public string Message { get; set; }
    }

    #endregion
}
