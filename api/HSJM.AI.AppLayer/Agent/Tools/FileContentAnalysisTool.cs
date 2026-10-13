using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 文件内容解读分析工具
    /// 用于解读文件内容的核心作用，输出结构化的归纳摘要
    /// </summary>
    public class FileContentAnalysisTool
    {
        private const string ContentAnalyzerRoleCode = "content_analyzer";

        private readonly ILogger<FileContentAnalysisTool> _logger;
        private readonly LLMModelService _llmModelService;
        private readonly IAgent_RoleService _agentRoleService;

        public FileContentAnalysisTool(
            ILogger<FileContentAnalysisTool> logger,
            LLMModelService llmModelService,
            IAgent_RoleService agentRoleService)
        {
            _logger = logger;
            _llmModelService = llmModelService;
            _agentRoleService = agentRoleService;
        }

        /// <summary>
        /// 解读文件内容核心作用
        /// 分析文件的主要功能、结构特征、关键元素，输出结构化归纳
        /// 大文件采用分批全量分析+汇总策略，确保不遗漏任何内容
        /// </summary>
        [Description("解读文件内容的核心详细作用，归纳为结构化摘要。用于存档和文档生成。支持大文件全量分析。输出包含：文件概述、核心功能、关键元素、结构特征、使用场景等。")]
        public async Task<FileContentAnalysisResult> AnalyzeFileContentAsync(
            [Description("文件绝对路径")] string filePath,
            [Description("可选：文件类型提示(PLC/Config/Code等)")] string? fileTypeHint = null,
            [Description("可选：分析深度(Quick/Standard/Deep)，默认Standard")] string analysisDepth = "Standard",
            [Description("可选：每批处理行数，默认500")] int batchSize = 500)
        {
            _logger.LogInformation("[文件解读] 开始: FilePath={Path}, Depth={Depth}, BatchSize={BatchSize}",
                filePath, analysisDepth, batchSize);

            var result = new FileContentAnalysisResult
            {
                FilePath = filePath,
                AnalysisDepth = analysisDepth
            };

            try
            {
                // 1. 验证文件存在
                if (!File.Exists(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    return result;
                }

                // 2. 读取文件基本信息
                var fileInfo = new FileInfo(filePath);
                result.FileName = fileInfo.Name;
                result.FileExtension = fileInfo.Extension;
                result.FileSizeBytes = fileInfo.Length;

                var allLines = await File.ReadAllLinesAsync(filePath);
                result.TotalLines = allLines.Length;

                _logger.LogInformation("[文件解读] 文件读取完成: {Lines}行, {Size}字节", allLines.Length, fileInfo.Length);

                // 3. 根据文件大小选择分析策略
                var chatClient = await ResolveChatClientByRoleCodeAsync(ContentAnalyzerRoleCode);

                if (allLines.Length <= batchSize)
                {
                    // 小文件：直接单次分析
                    _logger.LogInformation("[文件解读] 小文件模式: 直接分析全部 {Lines} 行", allLines.Length);
                    result.BatchCount = 1;
                    return await AnalyzeSingleBatchAsync(result, allLines, filePath, fileTypeHint, analysisDepth, chatClient);
                }
                else
                {
                    // 大文件：分批全量分析+汇总
                    var batchCount = (int)Math.Ceiling((double)allLines.Length / batchSize);
                    _logger.LogInformation("[文件解读] 大文件模式: 分 {BatchCount} 批分析（每批 {BatchSize} 行）", batchCount, batchSize);
                    result.BatchCount = batchCount;
                    return await AnalyzeIncrementalFullAsync(result, allLines, filePath, fileTypeHint, analysisDepth, batchSize, chatClient);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件解读] 分析失败");
                result.Success = false;
                result.ErrorMessage = $"分析失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 小文件单批次分析
        /// </summary>
        private async Task<FileContentAnalysisResult> AnalyzeSingleBatchAsync(
            FileContentAnalysisResult result,
            string[] allLines,
            string filePath,
            string? fileTypeHint,
            string analysisDepth,
            IChatClient chatClient)
        {
            var content = string.Join("\n", allLines);
            var prompt = BuildContentAnalysisPrompt(content, filePath, fileTypeHint, analysisDepth, isTruncated: false);

            var agent = new ChatClientAgent(
                chatClient,
                instructions: "你是一个专业的文件内容分析专家，擅长解读各类技术文件的核心作用和结构特征。请严格按照JSON格式返回分析结果。",
                name: "ContentAnalyzer"
            );

            var response = await agent.RunAsync(prompt);
            var responseText = response.Text ?? string.Empty;

            _logger.LogInformation("[文件解读] 单批次LLM响应长度: {Length}", responseText.Length);

            var analysis = ParseAnalysisResponse(responseText);
            if (analysis != null)
            {
                result.Success = true;
                result.FileOverview = analysis.file_overview ?? "无法生成概述";
                result.CoreFunctions = analysis.core_functions ?? new List<CoreFunctionItem>();
                result.KeyElements = analysis.key_elements ?? new List<KeyElementItem>();
                result.StructureFeatures = analysis.structure_features ?? new List<string>();
                result.UsageScenarios = analysis.usage_scenarios ?? new List<string>();
                result.TechnicalDetails = analysis.technical_details ?? new Dictionary<string, string>();
                result.Dependencies = analysis.dependencies ?? new List<string>();
                result.Complexity = analysis.complexity ?? "Medium";
                result.RecommendedActions = analysis.recommended_actions ?? new List<string>();
                result.Tags = analysis.tags ?? new List<string>();

                _logger.LogInformation("[文件解读] 单批次分析成功: 核心功能={FuncCount}, 关键元素={ElemCount}",
                    result.CoreFunctions.Count, result.KeyElements.Count);
            }
            else
            {
                result.Success = false;
                result.ErrorMessage = "无法解析LLM响应";
            }

            return result;
        }

        /// <summary>
        /// 大文件分批全量分析
        /// 将文件分成多个批次，逐批分析后合并所有结果
        /// </summary>
        private async Task<FileContentAnalysisResult> AnalyzeIncrementalFullAsync(
            FileContentAnalysisResult result,
            string[] allLines,
            string filePath,
            string? fileTypeHint,
            string analysisDepth,
            int batchSize,
            IChatClient chatClient)
        {
            var batchSummaries = new List<BatchSummary>();
            var totalBatches = (int)Math.Ceiling((double)allLines.Length / batchSize);

            // 逐批分析，不遗漏任何批次
            for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                var startLine = batchIndex * batchSize;
                var endLine = Math.Min(startLine + batchSize, allLines.Length);
                var batchLines = allLines.Skip(startLine).Take(endLine - startLine).ToArray();
                var batchContent = string.Join("\n", batchLines);

                _logger.LogInformation("[文件解读] 分析批次 {BatchIndex}/{TotalBatches}: 行 {StartLine}-{EndLine}",
                    batchIndex + 1, totalBatches, startLine + 1, endLine);

                var batchSummary = await AnalyzeBatchContentAsync(
                    batchContent,
                    batchIndex + 1,
                    totalBatches,
                    startLine + 1,
                    endLine,
                    filePath,
                    fileTypeHint,
                    chatClient);

                if (batchSummary != null)
                {
                    batchSummaries.Add(batchSummary);
                }
            }

            _logger.LogInformation("[文件解读] 全部 {Count} 个批次分析完成，开始合并结果", batchSummaries.Count);

            // 合并所有批次结果
            return await MergeBatchSummariesAsync(result, batchSummaries, filePath, fileTypeHint, analysisDepth, chatClient);
        }

        /// <summary>
        /// 分析单个批次的内容
        /// </summary>
        private async Task<BatchSummary?> AnalyzeBatchContentAsync(
            string batchContent,
            int batchIndex,
            int totalBatches,
            int startLine,
            int endLine,
            string filePath,
            string? fileTypeHint,
            IChatClient chatClient)
        {
            var fileName = Path.GetFileName(filePath);
            var prompt = $@"请分析以下文件内容片段，提取所有功能和关键元素。

【文件信息】
- 文件名: {fileName}
- 当前批次: {batchIndex}/{totalBatches}
- 行范围: 第{startLine}-{endLine}行
{(string.IsNullOrWhiteSpace(fileTypeHint) ? "" : $"- 文件类型: {fileTypeHint}")}

【内容片段】
```
{batchContent}
```

请以JSON格式返回该批次内容中发现的所有功能和元素：
```json
{{
  ""batch_summary"": ""该批次内容的简要描述（1-2句话）"",
  ""functions_found"": [
    {{
      ""name"": ""功能/函数/方法名称"",
      ""description"": ""功能描述"",
      ""importance"": ""High/Medium/Low"",
      ""line_range"": ""行范围""
    }}
  ],
  ""elements_found"": [
    {{
      ""element_type"": ""元素类型（变量/常量/配置项/程序块/标签等）"",
      ""name"": ""元素名称"",
      ""purpose"": ""元素用途"",
      ""line_range"": ""行范围""
    }}
  ],
  ""structure_notes"": [""该批次的结构特征观察""],
  ""dependencies_found"": [""发现的依赖项""]
}}
```

请完整提取该批次中的所有功能和元素，不要遗漏。直接返回JSON。";

            try
            {
                var agent = new ChatClientAgent(
                    chatClient,
                    instructions: "你是一个专业的代码分析专家，负责逐批分析文件内容。请完整提取每个批次中的所有功能和元素，不要遗漏任何内容。",
                    name: "BatchAnalyzer"
                );

                var response = await agent.RunAsync(prompt);
                var responseText = response.Text ?? string.Empty;

                var jsonContent = ExtractJsonFromResponse(responseText);
                var batchResult = JsonSerializer.Deserialize<BatchAnalysisResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (batchResult != null)
                {
                    return new BatchSummary
                    {
                        BatchIndex = batchIndex,
                        StartLine = startLine,
                        EndLine = endLine,
                        Summary = batchResult.batch_summary ?? $"批次{batchIndex}内容",
                        Functions = batchResult.functions_found ?? new List<BatchFunctionItem>(),
                        Elements = batchResult.elements_found ?? new List<BatchElementItem>(),
                        StructureNotes = batchResult.structure_notes ?? new List<string>(),
                        Dependencies = batchResult.dependencies_found ?? new List<string>()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[文件解读] 批次 {BatchIndex} 分析失败", batchIndex);
            }

            return null;
        }

        /// <summary>
        /// 合并所有批次的分析结果
        /// </summary>
        private async Task<FileContentAnalysisResult> MergeBatchSummariesAsync(
            FileContentAnalysisResult result,
            List<BatchSummary> batchSummaries,
            string filePath,
            string? fileTypeHint,
            string analysisDepth,
            IChatClient chatClient)
        {
            // 首先进行初步合并：收集所有批次的功能和元素
            var allFunctions = new List<CoreFunctionItem>();
            var allElements = new List<KeyElementItem>();
            var allStructureNotes = new List<string>();
            var allDependencies = new HashSet<string>();
            var batchDescriptions = new List<string>();

            foreach (var batch in batchSummaries)
            {
                batchDescriptions.Add($"[批次{batch.BatchIndex}，行{batch.StartLine}-{batch.EndLine}] {batch.Summary}");

                // 转换功能
                foreach (var func in batch.Functions)
                {
                    allFunctions.Add(new CoreFunctionItem
                    {
                        Name = func.Name,
                        Description = func.Description,
                        Importance = func.Importance
                    });
                }

                // 转换元素
                foreach (var elem in batch.Elements)
                {
                    allElements.Add(new KeyElementItem
                    {
                        ElementType = elem.ElementType,
                        Name = elem.Name,
                        Purpose = elem.Purpose,
                        Location = elem.LineRange
                    });
                }

                allStructureNotes.AddRange(batch.StructureNotes);
                foreach (var dep in batch.Dependencies)
                {
                    allDependencies.Add(dep);
                }
            }

            // 去重：按名称分组，取第一个（或合并描述）
            var uniqueFunctions = allFunctions
                .GroupBy(f => f.Name)
                .Select(g => g.First())
                .ToList();

            var uniqueElements = allElements
                .GroupBy(e => $"{e.ElementType}:{e.Name}")
                .Select(g => g.First())
                .ToList();

            var uniqueStructureNotes = allStructureNotes.Distinct().ToList();

            _logger.LogInformation("[文件解读] 初步合并结果: 功能={FuncCount}(去重前{OrigFunc}), 元素={ElemCount}(去重前{OrigElem})",
                uniqueFunctions.Count, allFunctions.Count, uniqueElements.Count, allElements.Count);

            // 使用LLM生成综合概述
            var mergePrompt = BuildMergePrompt(filePath, fileTypeHint, analysisDepth, batchDescriptions,
                uniqueFunctions, uniqueElements, uniqueStructureNotes, allDependencies.ToList());

            try
            {
                var agent = new ChatClientAgent(
                    chatClient,
                    instructions: "你是一个专业的文件内容分析专家，负责整合多批次分析结果，生成完整的文件分析报告。",
                    name: "MergeAnalyzer"
                );

                var response = await agent.RunAsync(mergePrompt);
                var responseText = response.Text ?? string.Empty;

                var jsonContent = ExtractJsonFromResponse(responseText);
                var mergeResult = JsonSerializer.Deserialize<MergeAnalysisResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (mergeResult != null)
                {
                    result.Success = true;
                    result.FileOverview = mergeResult.file_overview ?? "（由分批分析汇总）";
                    result.CoreFunctions = uniqueFunctions;
                    result.KeyElements = uniqueElements;
                    result.StructureFeatures = mergeResult.structure_features ?? uniqueStructureNotes;
                    result.UsageScenarios = mergeResult.usage_scenarios ?? new List<string>();
                    result.TechnicalDetails = mergeResult.technical_details ?? new Dictionary<string, string>();
                    result.Dependencies = allDependencies.ToList();
                    result.Complexity = mergeResult.complexity ?? "High";
                    result.RecommendedActions = mergeResult.recommended_actions ?? new List<string>();
                    result.Tags = mergeResult.tags ?? new List<string>();

                    _logger.LogInformation("[文件解读] 合并完成: 核心功能={FuncCount}, 关键元素={ElemCount}, 批次数={BatchCount}",
                        result.CoreFunctions.Count, result.KeyElements.Count, result.BatchCount);

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[文件解读] 合并分析响应解析失败，使用基础合并结果");
            }

            // 如果LLM合并失败，使用基础合并结果
            result.Success = true;
            result.FileOverview = $"该文件共 {result.TotalLines} 行，分 {batchSummaries.Count} 批次分析完成。包含 {uniqueFunctions.Count} 个核心功能，{uniqueElements.Count} 个关键元素。";
            result.CoreFunctions = uniqueFunctions;
            result.KeyElements = uniqueElements;
            result.StructureFeatures = uniqueStructureNotes.Take(10).ToList();
            result.UsageScenarios = new List<string> { "大型文件分析", "结构化文档生成" };
            result.TechnicalDetails = new Dictionary<string, string>();
            result.Dependencies = allDependencies.ToList();
            result.Complexity = result.TotalLines > 2000 ? "High" : "Medium";
            result.RecommendedActions = new List<string> { "检查核心功能完整性", "验证关键元素提取准确性" };
            result.Tags = new List<string>();

            return result;
        }

        /// <summary>
        /// 构建合并Prompt
        /// </summary>
        private string BuildMergePrompt(
            string filePath,
            string? fileTypeHint,
            string analysisDepth,
            List<string> batchDescriptions,
            List<CoreFunctionItem> functions,
            List<KeyElementItem> elements,
            List<string> structureNotes,
            List<string> dependencies)
        {
            var fileName = Path.GetFileName(filePath);
            var funcSummary = string.Join("\n", functions.Take(20).Select(f => $"  - [{f.Importance}] {f.Name}: {f.Description}"));
            var elemSummary = string.Join("\n", elements.Take(30).Select(e => $"  - [{e.ElementType}] {e.Name}: {e.Purpose}"));
            var batchSummary = string.Join("\n", batchDescriptions);

            return $@"请根据以下分批分析结果，生成该文件的综合分析报告。

【文件信息】
- 文件名: {fileName}
- 文件路径: {filePath}
{(string.IsNullOrWhiteSpace(fileTypeHint) ? "" : $"- 文件类型: {fileTypeHint}")}
- 分析深度: {analysisDepth}
- 总批次数: {batchDescriptions.Count}

【各批次摘要】
{batchSummary}

【已提取的功能】（共{functions.Count}个）
{funcSummary}
{(functions.Count > 20 ? $"... 还有 {functions.Count - 20} 个功能" : "")}

【已提取的元素】（共{elements.Count}个）
{elemSummary}
{(elements.Count > 30 ? $"... 还有 {elements.Count - 30} 个元素" : "")}

【结构特征观察】
{string.Join("\n", structureNotes.Take(10).Select(n => $"- {n}"))}

【依赖项】
{string.Join(", ", dependencies.Take(20))}

请生成综合分析报告（JSON格式）：
```json
{{
  ""file_overview"": ""文件整体概述（3-5句话，综合描述文件的主要作用、核心内容和设计特点）"",
  ""structure_features"": [""结构特征总结（去重和提炼）""],
  ""usage_scenarios"": [""使用场景""],
  ""technical_details"": {{
    ""format"": ""文件格式"",
    ""language"": ""编程语言"",
    ""pattern"": ""设计模式（如有）""
  }},
  ""complexity"": ""复杂度评估（Low/Medium/High）"",
  ""recommended_actions"": [""建议的后续操作""],
  ""tags"": [""文件标签""]
}}
```

注意：不需要重复输出已提取的功能和元素列表，只需生成概述和元信息。直接返回JSON。";
        }
        /// <summary>
        /// 批量分析多个文件
        /// </summary>
        [Description("批量分析多个文件的内容作用，返回结构化摘要列表")]
        public async Task<BatchFileAnalysisResult> AnalyzeFilesAsync(
            [Description("文件路径列表(逗号分隔或JSON数组)")] string filePathsInput,
            [Description("可选：分析深度(Quick/Standard/Deep)，默认Quick")] string analysisDepth = "Quick")
        {
            _logger.LogInformation("[批量文件解读] 开始: Depth={Depth}", analysisDepth);

            var result = new BatchFileAnalysisResult();

            try
            {
                // 解析文件路径列表
                var filePaths = ParseFilePaths(filePathsInput);
                if (filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "无有效的文件路径";
                    return result;
                }

                result.TotalFiles = filePaths.Count;
                var analyses = new List<FileContentAnalysisResult>();

                foreach (var filePath in filePaths)
                {
                    var analysis = await AnalyzeFileContentAsync(filePath, null, analysisDepth, 1000);
                    analyses.Add(analysis);

                    if (analysis.Success)
                        result.SuccessCount++;
                    else
                        result.FailedCount++;
                }

                result.FileAnalyses = analyses;
                result.Success = result.SuccessCount > 0;

                _logger.LogInformation("[批量文件解读] 完成: 成功={Success}, 失败={Failed}",
                    result.SuccessCount, result.FailedCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[批量文件解读] 失败");
                result.Success = false;
                result.ErrorMessage = $"批量分析失败: {ex.Message}";
                return result;
            }
        }

        #region 私有方法

        private string BuildContentAnalysisPrompt(string content, string filePath, string? fileTypeHint, string depth, bool isTruncated)
        {
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath);
            var typeHint = string.IsNullOrWhiteSpace(fileTypeHint) ? "请自动识别" : fileTypeHint;

            var depthInstruction = depth switch
            {
                "Quick" => "快速分析，仅提取核心要点（2-3个核心功能，3-5个关键元素）",
                "Deep" => "深度分析，详细解读每个组件（5-10个核心功能，10-20个关键元素，包含技术细节）",
                _ => "标准分析，平衡深度与效率（3-5个核心功能，5-10个关键元素）"
            };

            return $@"请分析以下文件的核心内容和作用，生成结构化的归纳摘要。

【文件信息】
- 文件名: {fileName}
- 扩展名: {extension}
- 文件类型提示: {typeHint}
- 分析深度: {depthInstruction}
{(isTruncated ? "- 注意: 文件内容已截断，仅分析部分内容" : "")}

【文件内容】
```
{(content.Length > 8000 ? content.Substring(0, 8000) + "\n... (内容截断)" : content)}
```

请以JSON格式返回分析结果：
```json
{{
  ""file_overview"": ""文件整体概述（2-3句话描述文件的主要作用和用途）"",
  ""core_functions"": [
    {{
      ""name"": ""功能名称"",
      ""description"": ""功能描述"",
      ""importance"": ""High/Medium/Low""
    }}
  ],
  ""key_elements"": [
    {{
      ""element_type"": ""元素类型（如：函数/变量/配置项/程序块）"",
      ""name"": ""元素名称"",
      ""purpose"": ""元素用途"",
      ""location"": ""位置描述（如：第10-50行）""
    }}
  ],
  ""structure_features"": [
    ""结构特征描述（如：使用XML格式、包含多个命名空间、模块化设计等）""
  ],
  ""usage_scenarios"": [
    ""使用场景描述（如：PLC程序解析、配置管理等）""
  ],
  ""technical_details"": {{
    ""encoding"": ""文件编码"",
    ""format"": ""文件格式"",
    ""language"": ""编程语言（如有）"",
    ""framework"": ""使用的框架（如有）""
  }},
  ""dependencies"": [
    ""依赖项（如：引用的库、外部文件等）""
  ],
  ""complexity"": ""复杂度评估（Low/Medium/High）"",
  ""recommended_actions"": [
    ""建议的后续操作（如：需要进一步解析、可用于生成文档等）""
  ],
  ""tags"": [
    ""标签（如：PLC、西门子、配置文件、解析器等）""
  ]
}}
```

请直接返回JSON，不要包含其他文本。";
        }

        private ContentAnalysisResponse? ParseAnalysisResponse(string responseText)
        {
            try
            {
                var jsonContent = ExtractJsonFromResponse(responseText);
                return JsonSerializer.Deserialize<ContentAnalysisResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件解读] JSON解析失败");
                return null;
            }
        }

        private string ExtractJsonFromResponse(string content)
        {
            var jsonContent = content.Trim();

            if (jsonContent.Contains("```json"))
            {
                var startIndex = jsonContent.IndexOf("```json") + 7;
                var endIndex = jsonContent.LastIndexOf("```");
                if (endIndex > startIndex)
                {
                    jsonContent = jsonContent.Substring(startIndex, endIndex - startIndex).Trim();
                }
            }
            else if (jsonContent.Contains("```"))
            {
                var startIndex = jsonContent.IndexOf("```") + 3;
                var endIndex = jsonContent.LastIndexOf("```");
                if (endIndex > startIndex)
                {
                    jsonContent = jsonContent.Substring(startIndex, endIndex - startIndex).Trim();
                }
            }

            return jsonContent;
        }

        private List<string> ParseFilePaths(string input)
        {
            var paths = new List<string>();

            if (string.IsNullOrWhiteSpace(input))
                return paths;

            // 尝试解析为JSON数组
            if (input.TrimStart().StartsWith("["))
            {
                try
                {
                    var jsonPaths = JsonSerializer.Deserialize<List<string>>(input);
                    if (jsonPaths != null)
                        paths.AddRange(jsonPaths);
                }
                catch
                {
                    // 解析失败，尝试其他方式
                }
            }

            // 如果JSON解析失败，按逗号分隔
            if (paths.Count == 0)
            {
                paths.AddRange(input.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            return paths;
        }

        private async Task<IChatClient> ResolveChatClientByRoleCodeAsync(string roleCode)
        {
            var role = await _agentRoleService.GetFirstAsync(r => r.RoleCode == roleCode && r.IsDeleted == false);
            if (role != null && role.ModelID.HasValue && role.ModelID.Value > 0)
            {
                return (await _llmModelService.CreateChatClientByModelIdAsync(role.ModelID.Value)).AsIChatClient();
            }

            var bound = await _llmModelService.RecommendModelAsync(requiredContextSize: 8192);
            return (await _llmModelService.CreateChatClientByModelIdAsync(bound.Id)).AsIChatClient();
        }

        #endregion
    }

    #region 返回模型

    /// <summary>
    /// 文件内容分析结果
    /// </summary>
    public class FileContentAnalysisResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public int TotalLines { get; set; }
        public string AnalysisDepth { get; set; } = "Standard";

        /// <summary>
        /// 实际处理的批次数（大文件分批分析时>1）
        /// </summary>
        public int BatchCount { get; set; } = 1;

        /// <summary>
        /// 文件整体概述
        /// </summary>
        public string FileOverview { get; set; } = string.Empty;

        /// <summary>
        /// 核心功能列表
        /// </summary>
        public List<CoreFunctionItem> CoreFunctions { get; set; } = new();

        /// <summary>
        /// 关键元素列表
        /// </summary>
        public List<KeyElementItem> KeyElements { get; set; } = new();

        /// <summary>
        /// 结构特征
        /// </summary>
        public List<string> StructureFeatures { get; set; } = new();

        /// <summary>
        /// 使用场景
        /// </summary>
        public List<string> UsageScenarios { get; set; } = new();

        /// <summary>
        /// 技术细节
        /// </summary>
        public Dictionary<string, string> TechnicalDetails { get; set; } = new();

        /// <summary>
        /// 依赖项
        /// </summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>
        /// 复杂度评估
        /// </summary>
        public string Complexity { get; set; } = "Medium";

        /// <summary>
        /// 建议操作
        /// </summary>
        public List<string> RecommendedActions { get; set; } = new();

        /// <summary>
        /// 标签
        /// </summary>
        public List<string> Tags { get; set; } = new();

        public override string ToString()
        {
            if (!Success)
                return $"[错误] 文件分析失败: {ErrorMessage}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[文件分析结果] {FileName}");
            sb.AppendLine($"大小: {FileSizeBytes / 1024.0:F1}KB | 行数: {TotalLines} | 复杂度: {Complexity} | 批次: {BatchCount}");
            sb.AppendLine();
            sb.AppendLine($"[概述] {FileOverview}");
            sb.AppendLine();

            if (CoreFunctions.Any())
            {
                sb.AppendLine($"[核心功能] ({CoreFunctions.Count}个)");
                foreach (var func in CoreFunctions.Take(5))
                {
                    sb.AppendLine($"  - [{func.Importance}] {func.Name}: {func.Description}");
                }
            }

            if (KeyElements.Any())
            {
                sb.AppendLine();
                sb.AppendLine($"[关键元素] ({KeyElements.Count}个)");
                foreach (var elem in KeyElements.Take(5))
                {
                    sb.AppendLine($"  - [{elem.ElementType}] {elem.Name}: {elem.Purpose}");
                }
            }

            if (Tags.Any())
            {
                sb.AppendLine();
                sb.AppendLine($"[标签] {string.Join(", ", Tags)}");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 核心功能项
    /// </summary>
    public class CoreFunctionItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Importance { get; set; } = "Medium";
    }

    /// <summary>
    /// 关键元素项
    /// </summary>
    public class KeyElementItem
    {
        public string ElementType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批量文件分析结果
    /// </summary>
    public class BatchFileAnalysisResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int TotalFiles { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public List<FileContentAnalysisResult> FileAnalyses { get; set; } = new();
    }

    /// <summary>
    /// LLM内容分析响应
    /// </summary>
    internal class ContentAnalysisResponse
    {
        public string? file_overview { get; set; }
        public List<CoreFunctionItem>? core_functions { get; set; }
        public List<KeyElementItem>? key_elements { get; set; }
        public List<string>? structure_features { get; set; }
        public List<string>? usage_scenarios { get; set; }
        public Dictionary<string, string>? technical_details { get; set; }
        public List<string>? dependencies { get; set; }
        public string? complexity { get; set; }
        public List<string>? recommended_actions { get; set; }
        public List<string>? tags { get; set; }
    }

    /// <summary>
    /// 单批次分析摘要
    /// </summary>
    internal class BatchSummary
    {
        public int BatchIndex { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<BatchFunctionItem> Functions { get; set; } = new();
        public List<BatchElementItem> Elements { get; set; } = new();
        public List<string> StructureNotes { get; set; } = new();
        public List<string> Dependencies { get; set; } = new();
    }

    /// <summary>
    /// 批次分析响应
    /// </summary>
    internal class BatchAnalysisResponse
    {
        public string? batch_summary { get; set; }
        public List<BatchFunctionItem>? functions_found { get; set; }
        public List<BatchElementItem>? elements_found { get; set; }
        public List<string>? structure_notes { get; set; }
        public List<string>? dependencies_found { get; set; }
    }

    /// <summary>
    /// 批次功能项
    /// </summary>
    internal class BatchFunctionItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Importance { get; set; } = "Medium";
        public string LineRange { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批次元素项
    /// </summary>
    internal class BatchElementItem
    {
        public string ElementType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public string LineRange { get; set; } = string.Empty;
    }

    /// <summary>
    /// 合并分析响应
    /// </summary>
    internal class MergeAnalysisResponse
    {
        public string? file_overview { get; set; }
        public List<string>? structure_features { get; set; }
        public List<string>? usage_scenarios { get; set; }
        public Dictionary<string, string>? technical_details { get; set; }
        public string? complexity { get; set; }
        public List<string>? recommended_actions { get; set; }
        public List<string>? tags { get; set; }
    }

    #endregion
}
