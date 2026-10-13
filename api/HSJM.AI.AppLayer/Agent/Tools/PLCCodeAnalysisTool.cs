using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using HSJM.AI.AppLayer.Agent.Services;
using HSJM.AI.IDbService;
using static HSJM.AI.AppLayer.Agent.Tools.ExtractStructureInfoResult;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// PLC 代码分析工具
    /// 用于智能体分析程序块结构、提取元数据
    /// </summary>
    public class PLCCodeAnalysisTool
    {
        private const string StructureAnalyzerRoleCode = "structure_analyzer";
        private const string DataExtractorRoleCode = "data_extractor";

        private readonly ILogger<PLCCodeAnalysisTool> _logger;
        private readonly LLMModelService _llmModelService;
        private readonly IAgent_RoleService _agentRoleService;

        public PLCCodeAnalysisTool(
            ILogger<PLCCodeAnalysisTool> logger,
            LLMModelService llmModelService,
            IAgent_RoleService agentRoleService)
        {
            _logger = logger;
            _llmModelService = llmModelService;
            _agentRoleService = agentRoleService;
        }

        /// <summary>
        /// 分析单个代码块
        /// 提取变量、逻辑、调用关系等信息
        /// </summary>
        [Description("分析单个PLC程序块(200-500行)的代码内容，提取变量、逻辑、调用关系等结构信息。⚠️ 这是第二层诊断工具，不用于第一层解析器生成。需要先用ReadFileLines获取程序块代码内容。")]
        public async Task<AnalyzeCodeBlockResult> AnalyzeCodeBlockAsync(
            [Description("程序块代码内容(从ReadFileLines获取)")] string codeContent,
            [Description("程序块类型：FB/FC/OB/DB")] string blockType,
            [Description("可选：程序块名称")] string? blockName = null,
            [Description("可选：PLC品牌(Siemens/Mitsubishi)")] string? plcBrand = null)
        {
            _logger.LogInformation("[代码分析] 开始分析: BlockType={Type}, Name={Name}", blockType, blockName);

            var result = new AnalyzeCodeBlockResult
            {
                BlockType = blockType,
                BlockName = blockName ?? "Unknown"
            };

            try
            {
                // 1. 构建分析 Prompt
                var prompt = BuildAnalysisPrompt(codeContent, blockType, blockName, plcBrand);

                // 2. 调用 LLM 分析
                var chatClient = await ResolveChatClientByRoleCodeAsync(DataExtractorRoleCode);
                var agent = new ChatClientAgent(
                    chatClient,
                    instructions: "你是一个PLC程序分析专家，擅长提取代码结构信息。请严格按照JSON格式返回分析结果。",
                    name: "CodeAnalyzer"
                );

                var response = await agent.RunAsync(prompt);
                var content = response.Text ?? string.Empty;

                _logger.LogInformation("[代码分析] LLM 响应长度: {Length}", content.Length);

                // 3. 解析 LLM 响应
                var analysisResult = ParseAnalysisResponse(content);

                if (analysisResult != null)
                {
                    result.Success = true;
                    result.Variables = analysisResult.variables ?? new List<VariableInfo>();
                    result.LogicSummary = analysisResult.logic_summary ?? "无逻辑摘要";
                    result.CallRelations = analysisResult.call_relations ?? new List<string>();
                    result.Complexity = analysisResult.complexity ?? "Medium";
                    result.PotentialIssues = analysisResult.potential_issues ?? new List<string>();
                    result.KeyFeatures = analysisResult.key_features ?? new List<string>();

                    _logger.LogInformation("[代码分析] 成功: 变量数={VarCount}, 调用关系={CallCount}",
                        result.Variables.Count, result.CallRelations.Count);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "无法解析 LLM 响应";
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[代码分析] 分析失败");
                result.Success = false;
                result.ErrorMessage = $"分析失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 分析PLC代码结构（兼容工具定义）
        /// </summary>
        [Description("分析PLC代码结构，提取变量/指令/调用关系等信息")]
        public async Task<AnalyzePLCCodeStructureResult> AnalyzePLCCodeStructureAsync(
            [Description("PLC代码块内容")] string codeBlock,
            [Description("语言类型，如 AWL/SCL/STL/XML")] string language,
            [Description("PLC品牌，如 Siemens/Mitsubishi")] string brand)
        {
            var (blockType, blockName) = InferBlockInfo(codeBlock);
            var analysis = await AnalyzeCodeBlockAsync(codeBlock, blockType, blockName, brand);

            return new AnalyzePLCCodeStructureResult
            {
                Success = analysis.Success,
                ErrorMessage = analysis.ErrorMessage,
                BlockType = analysis.BlockType,
                BlockName = analysis.BlockName,
                Variables = analysis.Variables,
                Instructions = ExtractInstructions(codeBlock, language),
                CallRelations = analysis.CallRelations,
                Complexity = analysis.Complexity,
                LogicSummary = analysis.LogicSummary
            };
        }

      
        /// <summary>
        /// 🚀 增量提取文件结构信息（自动分批读取大文件）
        /// 智能体只需调用此方法，工具内部自动完成：读取→分析→总结→合并
        /// </summary>
        [Description("⚡ 智能增量分析：自动分批读取PLC文件(每批500行)并逐步总结结构，直到完全理解文件格式。只需传入文件路径，无需手动调用ReadFileLines。返回完整的structureInfo用于GenerateParserCode。")]
        public async Task<ExtractStructureInfoResult> ExtractStructureInfoIncrementalAsync(
            [Description("文件绝对路径(从上游Analysis_output的FilePath字段获取)")] string filePath,
            [Description("可选:PLC品牌。如果上游已提供则使用，否则工具自动识别")] string? plcBrand = null,
            [Description("可选:文件格式。如果上游已提供则使用，否则工具自动识别")] string? fileFormat = null)
        {
            _logger.LogInformation("[增量结构提取] 开始: FilePath={Path}", filePath);

            var result = new ExtractStructureInfoResult
            {
                PlcBrand = plcBrand ?? "Unknown",
                FileFormat = fileFormat ?? "Unknown"
            };

            try
            {
                // 1. 验证文件
                if (!System.IO.File.Exists(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    return result;
                }

                // 2. 读取全部行
                var allLines = await System.IO.File.ReadAllLinesAsync(filePath);
                var totalLines = allLines.Length;
                _logger.LogInformation("[增量结构提取] 文件总行数: {Total}", totalLines);

                // 3. 增量分析（每批500行）
                const int batchSize = 500;
                var maxBatches = 10;
                var summaries = new List<string>();

                for (int i = 0; i < maxBatches && i * batchSize < totalLines; i++)
                {
                    var start = i * batchSize;
                    var count = Math.Min(batchSize, totalLines - start);
                    var batch = string.Join("\n", allLines.Skip(start).Take(count));

                    _logger.LogInformation("[增量结构提取-批次{Index}] 行{Start}-{End}", 
                        i + 1, start + 1, start + count);

                    var summary = await AnalyzeBatchStructure(batch, i + 1, start + 1, start + count, plcBrand, fileFormat);
                    summaries.Add(summary);

                    if (i >= 2 && HasSufficientPatterns(summaries))
                    {
                        _logger.LogInformation("[增量结构提取] 已识别足够模式，停止（共{Count}批）", i + 1);
                        break;
                    }
                }

                // 4. 合并结果
                var merged = await MergeStructureSummaries(summaries, plcBrand, fileFormat);
                if (merged != null)
                {
                    result.Success = true;
                    result.PlcBrand = merged.plc_brand ?? result.PlcBrand;
                    result.FileFormat = merged.file_format ?? result.FileFormat;
                    result.ParsingStrategy = merged.parsing_strategy ?? "Regex";
                    result.Namespaces = merged.namespaces ?? new Dictionary<string, string>();
                    result.BlockPatterns = merged.block_patterns ?? new Dictionary<string, string>();
                    result.VariablePatterns = merged.variable_patterns ?? new Dictionary<string, string>();
                    result.HierarchyPaths = merged.hierarchy_paths ?? new List<string>();
                    result.CodeSamples = merged.code_samples ?? new Dictionary<string, string>();
                    result.CommentStyle = merged.comment_style ?? "Unknown";
                    result.Encoding = merged.encoding ?? "UTF-8";
                    result.StructureFeatures = merged.structure_features ?? new List<string>();
                    result.ParsingRecommendations = merged.parsing_recommendations ?? new List<string>();

                    _logger.LogInformation("[增量结构提取] ✅ 成功: 品牌={Brand}, 格式={Format}, 策略={Strategy}, 模式数={Count}",
                        result.PlcBrand, result.FileFormat, result.ParsingStrategy, result.BlockPatterns.Count);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "无法合并结构信息";
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[增量结构提取] 失败");
                result.Success = false;
                result.ErrorMessage = $"提取失败: {ex.Message}";
                return result;
            }
        }

        #region 私有方法

        private static (string BlockType, string? BlockName) InferBlockInfo(string codeContent)
        {
            if (string.IsNullOrWhiteSpace(codeContent))
                return ("Unknown", null);

            var patterns = new (string Type, string Pattern)[]
            {
                ("FB", @"FUNCTION_BLOCK\s+(\w+)") ,
                ("FC", @"FUNCTION\s+(\w+)") ,
                ("OB", @"ORGANIZATION_BLOCK\s+(\w+)") ,
                ("DB", @"DATA_BLOCK\s+(\w+)") ,
                ("FB", @"<SW\.Blocks\.FB[^>]*Name=""(?<name>[^""]+)""") ,
                ("FC", @"<SW\.Blocks\.FC[^>]*Name=""(?<name>[^""]+)""") ,
                ("OB", @"<SW\.Blocks\.OB[^>]*Name=""(?<name>[^""]+)""") ,
                ("DB", @"<SW\.Blocks\.DB[^>]*Name=""(?<name>[^""]+)""")
            };

            foreach (var (type, pattern) in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(codeContent, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var nameGroup = match.Groups[1].Success ? match.Groups[1].Value : match.Groups["name"].Value;
                    return (type, string.IsNullOrWhiteSpace(nameGroup) ? null : nameGroup);
                }
            }

            return ("Unknown", null);
        }

        private static List<string> ExtractInstructions(string codeContent, string language)
        {
            var instructions = new List<string>();
            if (string.IsNullOrWhiteSpace(codeContent))
                return instructions;

            // 仅做轻量级提取，避免误报
            var lines = codeContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.StartsWith("//") || trimmed.StartsWith("(*") || trimmed.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
                    continue;

                var token = trimmed.Split(' ', '\t', ';').FirstOrDefault();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (token.Length <= 6 && char.IsLetter(token[0]))
                {
                    instructions.Add(token.ToUpperInvariant());
                }
            }

            return instructions.Distinct().Take(50).ToList();
        }

        /// <summary>
        /// 构建代码分析 Prompt
        /// </summary>
        private string BuildAnalysisPrompt(string codeContent, string blockType, string? blockName, string? plcBrand)
        {
            var brandInfo = string.IsNullOrWhiteSpace(plcBrand) ? "" : $"PLC品牌: {plcBrand}\n";
            var nameInfo = string.IsNullOrWhiteSpace(blockName) ? "" : $"程序块名称: {blockName}\n";

            return $@"请分析以下 {blockType} 类型的PLC程序块，提取结构化信息。

{brandInfo}{nameInfo}程序块类型: {blockType}

代码内容:
```
{(codeContent.Length > 2000 ? codeContent.Substring(0, 2000) + "\n... (已截断)" : codeContent)}
```

请以 JSON 格式返回分析结果，结构如下：
{{
  ""variables"": [
    {{
      ""name"": ""变量名"",
      ""type"": ""数据类型（INT、BOOL、REAL等）"",
      ""scope"": ""作用域（INPUT、OUTPUT、TEMP、STATIC）"",
      ""default_value"": ""默认值（如有）"",
      ""description"": ""变量用途说明""
    }}
  ],
  ""logic_summary"": ""程序块的核心逻辑摘要（2-3句话）"",
  ""call_relations"": [
    ""调用的其他程序块或函数（如 CALL FB10, CALL FC5）""
  ],
  ""complexity"": ""复杂度评估（Low、Medium、High）"",
  ""potential_issues"": [
    ""潜在问题或风险（如有）""
  ],
  ""key_features"": [
    ""关键特性或功能点""
  ]
}}

注意：
1. 变量要完整提取（INPUT、OUTPUT、INOUT、TEMP、STATIC）
2. 识别所有 CALL、JMP、RET 等调用关系
3. 如果代码有明显问题，在 potential_issues 中指出
4. 直接返回 JSON，不要包含任何其他文本

请开始分析：";
        }

        /// <summary>
        /// 构建结构提取 Prompt
        /// </summary>
        private string BuildStructureExtractionPrompt(string fileContent, string? plcBrand, string? fileFormat)
        {
            var brandHint = string.IsNullOrWhiteSpace(plcBrand) ? "请识别PLC品牌" : $"已知品牌: {plcBrand}";
            var formatHint = string.IsNullOrWhiteSpace(fileFormat) ? "请识别文件格式" : $"已知格式: {fileFormat}";

            return $@"请分析以下PLC文件的结构特征,提取用于生成解析器的元数据。

{brandHint}
{formatHint}

文件内容片段:
```
{(fileContent.Length > 3000 ? fileContent.Substring(0, 3000) + "\n... (已截断)" : fileContent)}
```

请以 JSON 格式返回结构信息，结构如下：
{{
  ""plc_brand"": ""PLC品牌（Siemens、Mitsubishi、Omron等）"",
  ""file_format"": ""文件格式（XML、AWL、SCL、LAD、GX3等）"",
  ""parsing_strategy"": ""解析策略（Regex、XPath、LineByLine、Mixed）"",
  ""encoding"": ""文件编码（UTF-8、GBK等）"",
  ""comment_style"": ""注释风格（如 // 或 (* *) 或 REM）"",
  ""namespaces"": {{
    ""prefix"": ""命名空间URI（仅XML格式需要）""
  }},
  ""block_patterns"": {{
    ""FB"": ""识别FB块的规则（正则/XPath/行特征）"",
    ""FC"": ""识别FC块的规则"",
    ""OB"": ""识别OB块的规则"",
    ""DB"": ""识别DB块的规则""
  }},
  ""variable_patterns"": {{
    ""input"": ""识别输入变量的规则"",
    ""output"": ""识别输出变量的规则"",
    ""temp"": ""识别临时变量的规则""
  }},
  ""hierarchy_paths"": [
    ""Document > SW.Blocks.FB > Interface > Sections > Section（描述文件层级结构，用于嵌套格式）""
  ],
  ""code_samples"": {{
    ""fb_definition"": ""真实的FB定义代码片段（20-50行）"",
    ""variable_declaration"": ""真实的变量声明代码片段"",
    ""network_logic"": ""真实的逻辑网络代码片段（如有）""
  }},
  ""structure_features"": [
    ""文件的结构特征描述（如：使用XML标签、包含多个NETWORK、有明确的BEGIN/END标记等）""
  ],
  ""parsing_recommendations"": [
    ""具体的解析建议（如：'使用XPath //SW.Blocks.FB 提取所有功能块'、'逐行匹配正则 ^FUNCTION_BLOCK\\s+(\\w+)'）""
  ]
}}

**重要说明：**
1. **parsing_strategy**: 根据文件格式选择：
   - XML格式 → XPath
   - 纯文本（STL/AWL）→ Regex 或 LineByLine
   - 混合格式 → Mixed
2. **block_patterns/variable_patterns**: 
   - XML格式使用XPath（如：//SW.Blocks.FB[@ID]）
   - 文本格式使用正则（如：FUNCTION_BLOCK\\s+(\\w+)）
3. **hierarchy_paths**: 仅XML/JSON等嵌套格式需要，描述从根到目标节点的完整路径
4. **code_samples**: 必须从实际文件中提取，至少包含3个不同类型的示例
5. **parsing_recommendations**: 要具体到可执行的代码级别建议

直接返回 JSON，不要包含任何其他文本

请开始分析：";
        }

        /// <summary>
        /// 解析代码分析响应
        /// </summary>
        private LlmAnalysisResponse? ParseAnalysisResponse(string content)
        {
            try
            {
                var jsonContent = ExtractJsonFromResponse(content);
                return JsonSerializer.Deserialize<LlmAnalysisResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[代码分析] JSON 解析失败");
                return null;
            }
        }

        /// <summary>
        /// 解析结构提取响应
        /// </summary>
        private LlmStructureResponse? ParseStructureResponse(string content)
        {
            try
            {
                var jsonContent = ExtractJsonFromResponse(content);
                return JsonSerializer.Deserialize<LlmStructureResponse>(jsonContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[结构提取] JSON 解析失败");
                return null;
            }
        }

        /// <summary>
        /// 从响应中提取 JSON
        /// </summary>
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

        /// <summary>
        /// 分析单批次文件内容（增量分析辅助方法）
        /// </summary>
        private async Task<string> AnalyzeBatchStructure(
            string batchContent,
            int batchIndex,
            int startLine,
            int endLine,
            string? knownBrand,
            string? knownFormat)
        {
            var prompt = $@"请分析以下PLC文件片段（第{batchIndex}批，行{startLine}-{endLine}），提取结构特征：

{(knownBrand != null ? $"已知品牌: {knownBrand}\n" : "")}
{(knownFormat != null ? $"已知格式: {knownFormat}\n" : "")}

文件内容：
```
{batchContent.Substring(0, Math.Min(2000, batchContent.Length))}
{(batchContent.Length > 2000 ? "\n...(内容截断)" : "")}
```

请识别并总结：
1. 程序块模式（如何识别FB/FC/OB/DB）
2. 变量定义模式
3. 关键结构特征
4. PLC品牌和格式（如果尚未确定）

以简洁文字形式总结（200字以内），不要用JSON。";

            var agent = new ChatClientAgent(
                await ResolveChatClientByRoleCodeAsync(StructureAnalyzerRoleCode),
                instructions: "你是PLC文件结构分析专家，擅长识别各种PLC文件的格式特征。",
                name: "BatchAnalyzer"
            );

            var response = await agent.RunAsync(prompt);
            return response.Text ?? "";
        }

        /// <summary>
        /// 判断是否已获取足够的模式信息（增量分析辅助方法）
        /// </summary>
        private bool HasSufficientPatterns(List<string> summaries)
        {
            if (summaries.Count < 2) return false;

            var lastTwo = string.Join(" ", summaries.TakeLast(2));
            var hasBlockPattern = lastTwo.Contains("程序块") || lastTwo.Contains("FB") || lastTwo.Contains("FC");
            var hasVariablePattern = lastTwo.Contains("变量") || lastTwo.Contains("VAR");
            var hasFormatInfo = lastTwo.Contains("XML") || lastTwo.Contains("STL") || lastTwo.Contains("Siemens");

            return hasBlockPattern && (hasVariablePattern || hasFormatInfo);
        }

        /// <summary>
        /// 合并多批次分析结果为最终结构（增量分析辅助方法）
        /// </summary>
        private async Task<LlmStructureResponse?> MergeStructureSummaries(
            List<string> summaries,
            string? knownBrand,
            string? knownFormat)
        {
            var combinedSummary = string.Join("\n\n", summaries.Select((s, i) => $"【批次{i + 1}】\n{s}"));

            var prompt = $@"请根据以下多批次PLC文件分析结果，合并为完整的结构信息：

{(knownBrand != null ? $"已知品牌: {knownBrand}\n" : "")}
{(knownFormat != null ? $"已知格式: {knownFormat}\n" : "")}

{combinedSummary}

请输出完整的结构信息JSON：
```json
{{
  ""plc_brand"": ""品牌名称"",
  ""file_format"": ""文件格式（XML/STL/SCL等）"",
  ""parsing_strategy"": ""解析策略（XPath/Regex/LineByLine/Mixed）"",
  ""encoding"": ""UTF-8"",
  ""comment_style"": ""注释风格"",
  ""namespaces"": {{
    ""ns"": ""命名空间URI（仅XML需要）""
  }},
  ""block_patterns"": {{
    ""FB"": ""功能块的定位规则（XPath或正则）"",
    ""FC"": ""功能的定位规则""
  }},
  ""variable_patterns"": {{
    ""input"": ""输入变量定位规则"",
    ""output"": ""输出变量定位规则""
  }},
  ""hierarchy_paths"": [
    ""Document > SW.Blocks.FB > Interface > Sections（层级路径）""
  ],
  ""code_samples"": {{
    ""fb_sample"": ""真实的FB定义代码片段"",
    ""var_sample"": ""真实的变量声明片段""
  }},
  ""structure_features"": [""特征1"", ""特征2""],
  ""parsing_recommendations"": [""建议1（如：使用XPath //SW.Blocks.FB提取功能块）"", ""建议2""]
}}
```";

            var agent = new ChatClientAgent(
                await ResolveChatClientByRoleCodeAsync(StructureAnalyzerRoleCode),
                instructions: "你是PLC文件结构分析专家，擅长整合分析结果。请严格按照JSON格式输出。",
                name: "StructureMerger"
            );

            var response = await agent.RunAsync(prompt);
            return ParseStructureResponse(response.Text ?? "");
        }

        #endregion

        private async Task<IChatClient> ResolveChatClientByRoleCodeAsync(string roleCode)
        {
            var role = await _agentRoleService.GetFirstAsync(r => r.RoleCode == roleCode && r.IsDeleted == false);
            if (role != null && role.ModelID.HasValue && role.ModelID.Value > 0)
            {
                return (await _llmModelService.CreateChatClientByModelIdAsync(role.ModelID.Value)).AsIChatClient();
            }

            var bound = await _llmModelService.RecommendModelAsync();
            return (await _llmModelService.CreateChatClientByModelIdAsync(bound.Id)).AsIChatClient();
        }
    }

    #region 返回模型

    /// <summary>
    /// 代码块分析结果
    /// </summary>
    public class AnalyzeCodeBlockResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string BlockType { get; set; } = string.Empty;
        public string BlockName { get; set; } = string.Empty;
        public List<VariableInfo> Variables { get; set; } = new();
        public string LogicSummary { get; set; } = string.Empty;
        public List<string> CallRelations { get; set; } = new();
        public string Complexity { get; set; } = "Medium";
        public List<string> PotentialIssues { get; set; } = new();
        public List<string> KeyFeatures { get; set; } = new();

        public override string ToString()
        {
            if (!Success)
                return $"❌ 分析失败: {ErrorMessage}";

            return $@"✅ 程序块分析成功

程序块: {BlockName} ({BlockType})
复杂度: {Complexity}

变量信息: {Variables.Count} 个
{string.Join("\n", Variables.Take(5).Select(v => $"  - {v.Name} ({v.Type}) - {v.Scope}"))}
{(Variables.Count > 5 ? $"  ... 还有 {Variables.Count - 5} 个变量" : "")}

逻辑摘要:
{LogicSummary}

调用关系: {CallRelations.Count} 个
{string.Join("\n", CallRelations.Select(c => $"  - {c}"))}

关键特性:
{string.Join("\n", KeyFeatures.Select(f => $"  • {f}"))}

{(PotentialIssues.Any() ? $@"
⚠️ 潜在问题:
{string.Join("\n", PotentialIssues.Select(i => $"  - {i}"))}" : "")}";
        }
    }

    /// <summary>
    /// 变量信息
    /// </summary>
    public class VariableInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string? DefaultValue { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// 结构信息提取结果
    /// </summary>
    public class ExtractStructureInfoResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string PlcBrand { get; set; } = string.Empty;
        public string FileFormat { get; set; } = string.Empty;
        public string Encoding { get; set; } = "UTF-8";
        public string CommentStyle { get; set; } = string.Empty;
        
        /// <summary>
        /// 解析策略（Regex=正则、XPath=XML路径、LineByLine=逐行、Mixed=混合）
        /// </summary>
        public string ParsingStrategy { get; set; } = "Regex";
        
        /// <summary>
        /// 程序块定位规则（支持正则/XPath/行特征等多种格式）
        /// 示例：
        /// - Regex: { "FB": "FUNCTION_BLOCK\\s+(\\w+)" }
        /// - XPath: { "FB": "//SW.Blocks.FB[@ID]" }
        /// - LinePattern: { "FB": "FUNCTION_BLOCK {name}" }
        /// </summary>
        public Dictionary<string, string> BlockPatterns { get; set; } = new();
        
        /// <summary>
        /// 变量定位规则
        /// </summary>
        public Dictionary<string, string> VariablePatterns { get; set; } = new();
        
        /// <summary>
        /// 文件层级结构描述（用于XML/JSON等嵌套格式）
        /// 示例：["Document > SW.Blocks.FB > Interface > Sections > Section > Member"]
        /// </summary>
        public List<string> HierarchyPaths { get; set; } = new();
        
        /// <summary>
        /// XML命名空间信息（如有）
        /// 示例：{ "ns": "http://www.siemens.com/automation/Openness/SW/Interface/v5" }
        /// </summary>
        public Dictionary<string, string> Namespaces { get; set; } = new();
        
        /// <summary>
        /// 真实代码示例（用于LLM参考生成解析代码）
        /// 示例：包含实际的FB定义、变量声明等片段
        /// </summary>
        public Dictionary<string, string> CodeSamples { get; set; } = new();
        
        public List<string> StructureFeatures { get; set; } = new();
        public List<string> ParsingRecommendations { get; set; } = new();

        public override string ToString()
        {
            if (!Success)
                return $"❌ 提取失败: {ErrorMessage}";

            return $@"✅ 文件结构提取成功

PLC品牌: {PlcBrand}
文件格式: {FileFormat}
解析策略: {ParsingStrategy}
编码: {Encoding}
注释风格: {CommentStyle}

{(Namespaces.Any() ? $@"XML命名空间: {Namespaces.Count} 个
{string.Join("\n", Namespaces.Select(ns => $"  - {ns.Key}: {ns.Value}"))}" : "")}

程序块模式: {BlockPatterns.Count} 个
{string.Join("\n", BlockPatterns.Select(p => $"  - {p.Key}: {p.Value}"))}

变量模式: {VariablePatterns.Count} 个
{string.Join("\n", VariablePatterns.Select(p => $"  - {p.Key}: {p.Value}"))}

{(HierarchyPaths.Any() ? $@"层级结构:
{string.Join("\n", HierarchyPaths.Select(h => $"  📂 {h}"))}" : "")}

{(CodeSamples.Any() ? $@"代码示例: {CodeSamples.Count} 个
{string.Join("\n", CodeSamples.Keys.Take(3).Select(k => $"  - {k}"))}" : "")}

结构特征:
{string.Join("\n", StructureFeatures.Select(f => $"  • {f}"))}

解析建议:
{string.Join("\n", ParsingRecommendations.Select(r => $"  💡 {r}"))}";
        }

        /// <summary>
        /// PLC代码结构分析结果
        /// </summary>
        public class AnalyzePLCCodeStructureResult
        {
            public bool Success { get; set; }
            public string? ErrorMessage { get; set; }
            public string BlockType { get; set; } = string.Empty;
            public string BlockName { get; set; } = string.Empty;
            public List<VariableInfo> Variables { get; set; } = new();
            public List<string> Instructions { get; set; } = new();
            public List<string> CallRelations { get; set; } = new();
            public string Complexity { get; set; } = "Medium";
            public string LogicSummary { get; set; } = string.Empty;

            public override string ToString()
            {
                if (!Success)
                    return $"❌ 分析失败: {ErrorMessage}";

                return $@"✅ 结构分析成功

程序块: {BlockName} ({BlockType})
复杂度: {Complexity}

变量数: {Variables.Count}
调用关系: {CallRelations.Count}
指令数: {Instructions.Count}";
            }
        }
    }

    #region 数据模型

    /// <summary>
    /// LLM 代码分析响应
    /// </summary>
    internal class LlmAnalysisResponse
    {
        public List<VariableInfo>? variables { get; set; }
        public string? logic_summary { get; set; }
        public List<string>? call_relations { get; set; }
        public string? complexity { get; set; }
        public List<string>? potential_issues { get; set; }
        public List<string>? key_features { get; set; }
    }

    /// <summary>
    /// LLM 结构提取响应
    /// </summary>
    internal class LlmStructureResponse
    {
        public string? plc_brand { get; set; }
        public string? file_format { get; set; }
        public string? parsing_strategy { get; set; }
        public string? encoding { get; set; }
        public string? comment_style { get; set; }
        public Dictionary<string, string>? namespaces { get; set; }
        public Dictionary<string, string>? block_patterns { get; set; }
        public Dictionary<string, string>? variable_patterns { get; set; }
        public List<string>? hierarchy_paths { get; set; }
        public Dictionary<string, string>? code_samples { get; set; }
        public List<string>? structure_features { get; set; }
        public List<string>? parsing_recommendations { get; set; }
    }

    #endregion
}
#endregion
