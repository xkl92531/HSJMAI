using HSJM.AI.AppLayer.DiagnosticStrategyFactory.DTOS;
using HSJM.AI.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 提示词模板服务
    /// 负责加载和管理提示词模板文件（md格式）
    /// 支持从文件加载模板，并进行变量替换
    /// </summary>
    public class PromptTemplateService : ISelfScopedAutoInject
    {
        private readonly ILogger<PromptTemplateService> _logger;
        private readonly Dictionary<PromptTemplateType, string> _templateCache = new();
        private readonly string _basePath;

        // 模板文件路径映射
        private static readonly Dictionary<PromptTemplateType, string> TemplateFilePaths = new()
        {
            { PromptTemplateType.EnergyFlowDiagnostic, "prompts/energy_flow/prompt_energy_flow_diagnostic.md" },
            { PromptTemplateType.QueryStrategy, "prompts/energy_flow/prompt_query_strategy.md" },
            { PromptTemplateType.CrossReference, "prompts/energy_flow/prompt_cross_reference.md" },
            { PromptTemplateType.SummaryReport, "prompts/energy_flow/prompt_summary_report.md" },
            { PromptTemplateType.StrategyDecision, "prompts/energy_flow/prompt_strategy_decision.md" }
        };

        // 模板名称映射
        private static readonly Dictionary<PromptTemplateType, string> TemplateNames = new()
        {
            { PromptTemplateType.EnergyFlowDiagnostic, "能量流诊断" },
            { PromptTemplateType.QueryStrategy, "查询策略生成" },
            { PromptTemplateType.CrossReference, "交叉引用分析" },
            { PromptTemplateType.SummaryReport, "摘要报告生成" },
            { PromptTemplateType.StrategyDecision, "诊断策略决策" }
        };

        /// <summary>
        /// 提示词模板服务构造函数
        /// </summary>
        /// <param name="logger">日志记录器</param>
        public PromptTemplateService(ILogger<PromptTemplateService> logger)
        {
            _logger = logger;
            // 获取项目根目录
            _basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs");
        }

        /// <summary>
        /// 加载提示词模板
        /// </summary>
        /// <param name="templateType">模板类型</param>
        /// <returns>提示词模板</returns>
        public async Task<PromptTemplateDto> LoadTemplateAsync(PromptTemplateType templateType)
        {
            // 先从缓存获取
            if (_templateCache.TryGetValue(templateType, out var cachedContent))
            {
                _logger.LogDebug("从缓存加载模板: {TemplateType}", templateType);
                return new PromptTemplateDto
                {
                    TemplateType = templateType,
                    TemplateName = TemplateNames[templateType],
                    FilePath = TemplateFilePaths[templateType],
                    Content = cachedContent,
                    Placeholders = ExtractPlaceholders(cachedContent)
                };
            }

            // 从文件加载
            var filePath = TemplateFilePaths[templateType];
            var fullPath = Path.Combine(_basePath, filePath);

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("模板文件不存在: {FilePath}", fullPath);
                return null;
            }

            var content = await File.ReadAllTextAsync(fullPath);
            _templateCache[templateType] = content;

            _logger.LogInformation("已加载模板: {TemplateType}, 文件路径: {FilePath}", templateType, fullPath);

            return new PromptTemplateDto
            {
                TemplateType = templateType,
                TemplateName = TemplateNames[templateType],
                FilePath = filePath,
                Content = content,
                Placeholders = ExtractPlaceholders(content)
            };
        }

        /// <summary>
        /// 加载模板并替换变量
        /// </summary>
        /// <param name="templateType">模板类型</param>
        /// <param name="variables">变量字典</param>
        /// <returns>填充后的提示词</returns>
        public async Task<string> RenderTemplateAsync(PromptTemplateType templateType, Dictionary<string, string> variables)
        {
            var template = await LoadTemplateAsync(templateType);
            if (template == null)
            {
                // 降级处理：使用内置默认提示词
                _logger.LogWarning("模板加载失败: {TemplateType}，使用内置默认提示词", templateType);
                return BuildFallbackPrompt(templateType, variables);
            }

            return RenderTemplate(template.Content, variables);
        }

        /// <summary>
        /// 构建降级提示词（当模板加载失败时使用）
        /// </summary>
        private static string BuildFallbackPrompt(PromptTemplateType templateType, Dictionary<string, string> variables)
        {
            return templateType switch
            {
                PromptTemplateType.EnergyFlowDiagnostic => BuildFallbackEnergyFlowPrompt(variables),
                PromptTemplateType.QueryStrategy => BuildFallbackQueryStrategyPrompt(variables),
                PromptTemplateType.CrossReference => BuildFallbackCrossReferencePrompt(variables),
                PromptTemplateType.SummaryReport => BuildFallbackSummaryReportPrompt(variables),
                PromptTemplateType.StrategyDecision => BuildFallbackStrategyDecisionPrompt(variables),
                _ => BuildGenericFallbackPrompt(variables)
            };
        }

        /// <summary>
        /// 构建策略决策的降级提示词
        /// </summary>
        private static string BuildFallbackStrategyDecisionPrompt(Dictionary<string, string> variables)
        {
            return $@"# 诊断策略决策

你是一个PLC诊断专家，负责根据用户描述的问题，判断需要执行哪些诊断检查步骤。

## 可用的检查步骤
1. safety_check: 检查安全类IO点位
2. constraint_check: 检查IO约束规则
3. io_check: 检查IO点位状态
4. stuck_point_locate: 定位卡滞点
5. energy_flow_analysis: 能量流分析

## 判断逻辑

根据问题类型判断：

| 问题类型 | 需要执行的检查 |
|---------|--------------|
| 初步排查/不知道什么问题/帮我看一下/有什么问题/检查一下/看看正常吗 | safety_check + constraint_check + io_check + stuck_point_locate（初步全面检查）|
| 某个动作卡住了/卡住不动/卡在/卡住了 | safety_check + constraint_check |
| 步骤卡住不动/步骤停在那里/步骤没有继续 | safety_check + constraint_check |
| 设备停机/机器停了/不动了 | safety_check + constraint_check |
| 急停触发/安全报警 | safety_check + energy_flow_analysis |
| 安全门打开 | safety_check + energy_flow_analysis |
| 安全光幕被遮挡 | safety_check + energy_flow_analysis |
| 互锁报警 | constraint_check + energy_flow_analysis |
| 顺序错误 | constraint_check + energy_flow_analysis |
| IO异常/报警 | io_check + stuck_point_locate + energy_flow_analysis |
| 设备故障/异常（通用） | safety_check + constraint_check + io_check + stuck_point_locate + energy_flow_analysis（全面检查）|

**重要**：当用户描述属于以下情况时，必须执行初步全面检查（不包括能量流分析）：
- 用户说""帮我看一下""、""有什么问题""、""检查一下""、""看看正常吗""等
- 用户没有描述具体故障现象
- 用户只是初步排查，不确定问题在哪里
- 问题类型不明确时

**目前阶段：stuck_point_locate 和 energy_flow_analysis 暂不使用，始终设为 false**

## 输入信息
产线ID: {variables.GetValueOrDefault("PRODUCTION_LINE_ID", "未知")}
用户描述: {variables.GetValueOrDefault("USER_DESCRIPTION", "未知")}

## 重要：解析用户描述中的StationId/PLC信息

请仔细分析用户描述，从中提取StationId或PLC相关信息：

1. 如果用户描述中提到具体的PLC或工位（如""PLC2""、""1号PLC""、""OP70工位""、""2号工位""等），请提取出来
2. StationId可能的形式：PLC1、PLC2、1号PLC、2号PLC、OP10、OP20、OP30、OP40、OP50、OP60、OP70等工位编号
3. 提取后系统会根据StationId去查询对应的PLC ID
4. 如果用户描述中提到了具体的设备名称或工位名称，也请提取出来

## 输出要求
请以JSON格式输出判断结果：
```json
{{
  ""safety_check"": true/false,
  ""constraint_check"": true/false,
  ""io_check"": true/false,
  ""stuck_point_locate"": true/false,
  ""energy_flow_analysis"": true/false,
  ""reason"": ""判断理由"",
  ""problem_type"": ""问题类型"",
  ""priority"": ""建议的诊断优先级"",
  ""parsed_plc_name"": ""从用户描述中解析出的StationId或PLC名称""
}}
```

注意：
- 严格按照JSON格式输出，不要包含其他内容
- 所有字段都必须填写
- 如果用户描述中提到PLC或工位，请准确提取StationId（如""PLC2""就提取""PLC2""，""OP70""就提取""OP70""）
- 如果用户没有提到具体PLC或工位，parsed_plc_name 设为空字符串
- 如果用户描述不明确或只是初步排查，problem_type 设为""初步排查""
- **必须根据用户描述中的关键词判断问题类型**：只要描述中有""卡住""、""停机""、""报警""、""异常""等具体现象，就必须设置对应的检查为true
- **绝对不能将所有检查项都设为false**
- 如果不确定，倾向于执行更多检查以确保诊断完整性";
        }

        private static string BuildFallbackEnergyFlowPrompt(Dictionary<string, string> variables)
        {
            return $@"
# PLC 逻辑诊断 (Energy Flow Analysis)

## Role
你是顶级 PLC 电气诊断专家，擅长从复杂的 XML 源代码中还原梯形图能流（Energy Flow）。

## Task
基于 [Logic_Expression] 的 Wire 拓扑：[UID_Mapping] 的变量映射、[Live_Signals] 的快照、[IO_Tags] 的点位信息及 [IO_Constraints] 的约束规则，找出阻断最终输出线圈（Coil）励磁的根源故障点。

## Input Data

### [Logic_Expression]
{variables.GetValueOrDefault("RAW_CONTENT", "(无数据)")}

### [UID_Mapping]
{variables.GetValueOrDefault("UID_SYMBOL_MAP", "(无数据)")}

### [Live_Signals]
{variables.GetValueOrDefault("LIVE_SIGNALS", "(无数据)")}

### [IO_Tags]
{variables.GetValueOrDefault("IO_TAGS", "(无数据)")}
**重要**：请在输出的变量中使用 Comment 字段作为中文描述。格式：变量名 (中文描述)

### [IO_Constraints]
{variables.GetValueOrDefault("IO_CONSTRAINTS", "(无数据)")}

## Output Requirement: 请直接输出一份人类可读的 Markdown 格式诊断报告，**不要输出 JSON**。报告要求如下：

### 1. 诊断摘要
用简洁的白话文说明：
- 发现了几个问题（严重/警告）
- 主要问题是什么
- 需要重点关注哪些

### 2. 异常详情
使用 Markdown 表格列出每个异常：

| 优先级 | 变量 | 当前状态 | 原因分析 |
|--------|------|----------|----------|
| 🔴 严重 | 变量名 (中文描述) | True/False | 用白话文说明为什么这个点导致问题 |
| 🟡 警告 | 变量名 (中文描述) | True/False | 用白话文说明问题原因 |

### 3. 解决建议
针对每个严重问题，给出具体可操作的建议，例如：
- ""检查 XX 传感器是否触发""
- ""确认 XX 按钮是否按下""
- ""复位 XX 继电器""
- ""检查气缸是否正常动作""

## 重要注意事项

1. **变量名必须完整**：保留原始变量名，不要缩写
2. **使用中文描述**：优先使用 IO_Tags 中的 Comment 字段作为中文描述
3. **reason 字段要白话文**：不要使用技术术语，用通俗易懂的语言解释问题原因
4. **建议要具体**：不要只说""检查设备""，要说明具体检查什么

请直接输出 Markdown 报告，**不要包含任何 JSON 或代码块标记**。
";
        }

        private static string BuildFallbackQueryStrategyPrompt(Dictionary<string, string> variables)
        {
            return $@"
# 查询策略生成任务

## 任务描述
根据以下诊断异常，生成针对性的查询策略以获取更多诊断信息。

## 当前逻辑单元 ID
{variables.GetValueOrDefault("CURRENT_LOGIC_UNIT_ID", "(无数据)")}

## 诊断异常
{variables.GetValueOrDefault("CRITICAL_ISSUES", "(无数据)")}

## Output
请生成 JSON 格式的查询策略。
";
        }

        private static string BuildFallbackCrossReferencePrompt(Dictionary<string, string> variables)
        {
            return $@"
# 交叉引用分析任务

## 任务描述
对以下异常进行交叉引用分析，追溯根本原因。

## 诊断异常
{variables.GetValueOrDefault("CRITICAL_ISSUES", "(无数据)")}

## 相关逻辑单元
{variables.GetValueOrDefault("RELATED_LOGIC_UNITS", "(无数据)")}

## Output
请生成 JSON 格式的交叉引用分析结果。
";
        }

        private static string BuildFallbackSummaryReportPrompt(Dictionary<string, string> variables)
        {
            var issuesJson = variables.GetValueOrDefault("ISSUES", "[]");
            var criticalCount = variables.GetValueOrDefault("CRITICAL_COUNT", "0");
            var warningCount = variables.GetValueOrDefault("WARNING_COUNT", "0");
            var totalCount = int.TryParse(criticalCount, out var cc) && int.TryParse(warningCount, out var wc) ? (cc + wc).ToString() : "0";

            return $@"
# 摘要报告生成任务

## 任务描述
根据以下诊断结果生成简洁的摘要报告。

## 诊断结果
- 异常数量: {totalCount}
- CRITICAL: {criticalCount}
- WARNING: {warningCount}

## 异常详情
{issuesJson}

## Output
请生成 Markdown 格式的摘要报告。
";
        }

        private static string BuildGenericFallbackPrompt(Dictionary<string, string> _)
        {
            return "无法生成提示词，模板类型未知";
        }

        /// <summary>
        /// 渲染模板（同步版本）
        /// </summary>
        /// <param name="content">模板内容</param>
        /// <param name="variables">变量字典</param>
        /// <returns>填充后的内容</returns>
        public string RenderTemplate(string content, Dictionary<string, string> variables)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            var result = content;

            foreach (var variable in variables)
            {
                // 支持 {{VARIABLE_NAME}} 格式
                var pattern = @"\{\{" + variable.Key + @"\}\}";
                result = Regex.Replace(result, pattern, variable.Value, RegexOptions.IgnoreCase);
            }

            return result;
        }

        /// <summary>
        /// 从模板中提取变量占位符
        /// </summary>
        /// <param name="content">模板内容</param>
        /// <returns>变量列表</returns>
        private Dictionary<string, string> ExtractPlaceholders(string content)
        {
            var placeholders = new Dictionary<string, string>();
            var matches = Regex.Matches(content, @"\{\{(\w+)\}\}");

            foreach (Match match in matches)
            {
                var variableName = match.Groups[1].Value;
                if (!placeholders.ContainsKey(variableName))
                {
                    placeholders[variableName] = variableName;
                }
            }

            return placeholders;
        }

        /// <summary>
        /// 获取所有可用的模板类型
        /// </summary>
        /// <returns>模板类型列表</returns>
        public List<PromptTemplateType> GetAvailableTemplateTypes()
        {
            return TemplateFilePaths.Keys.ToList();
        }

        /// <summary>
        /// 重新加载模板（清除缓存）
        /// </summary>
        public void ReloadTemplates()
        {
            _templateCache.Clear();
            _logger.LogInformation("已清除模板缓存");
        }

        /// <summary>
        /// 保存优化后的提示词到文件
        /// </summary>
        /// <param name="templateType">模板类型</param>
        /// <param name="content">优化后的内容</param>
        /// <param name="isOptimized">是否为优化版本</param>
        public async Task SaveOptimizedPromptAsync(PromptTemplateType templateType, string content, bool isOptimized = true)
        {
            var fileName = Path.GetFileName(TemplateFilePaths[templateType]);
            var directory = isOptimized
                ? Path.Combine(_basePath, "prompts/energy_flow/optimized")
                : Path.Combine(_basePath, "prompts/energy_flow");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var filePath = isOptimized
                ? Path.Combine(directory, $"optimized_{fileName}")
                : Path.Combine(directory, fileName);

            await File.WriteAllTextAsync(filePath, content);
            _logger.LogInformation("已保存提示词到: {FilePath}", filePath);

            // 更新缓存
            _templateCache[templateType] = content;
        }

        /// <summary>
        /// 加载优化后的提示词
        /// </summary>
        /// <param name="templateType">模板类型</param>
        /// <returns>优化后的提示词，如果不存在则返回原始模板</returns>
        public async Task<string> LoadOptimizedPromptAsync(PromptTemplateType templateType)
        {
            var fileName = Path.GetFileName(TemplateFilePaths[templateType]);
            var optimizedPath = Path.Combine(_basePath, $"prompts/energy_flow/optimized/optimized_{fileName}");

            if (File.Exists(optimizedPath))
            {
                var content = await File.ReadAllTextAsync(optimizedPath);
                _logger.LogInformation("已加载优化后的模板: {TemplateType}", templateType);
                return content;
            }

            // 如果优化版本不存在，返回原始版本
            _logger.LogWarning("优化版本不存在，使用原始模板: {TemplateType}", templateType);
            return await LoadTemplateAsync(templateType).ContinueWith(t => t.Result?.Content);
        }
    }
}
