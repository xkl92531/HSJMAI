using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using HSJM.AI.IDbService;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// PLC解析器模版管理工具
    /// 用于保存、查询、更新解析器模版
    /// </summary>
    public class ParserTemplateTools
    {
        private readonly ILogger<ParserTemplateTools> _logger;
        private readonly IPLC_Parser_TemplateService _templateService;

        public ParserTemplateTools(
            ILogger<ParserTemplateTools> logger,
            IPLC_Parser_TemplateService templateService)
        {
            _logger = logger;
            _templateService = templateService;
        }

        /// <summary>
        /// 保存解析器模版
        /// 将生成的解析器代码和元数据保存到数据库
        /// </summary>
        [Description("保存PLC解析器模版到数据库。用于持久化动态生成的解析器代码，支持版本管理和复用。")]
        public async Task<SaveParserTemplateResult> SaveParserTemplateAsync(
            [Description("解析器代码")] string parserCode,
            [Description("PLC品牌(Siemens/Mitsubishi/Omron等)")] string plcBrand,
            [Description("文件格式(XML/AWL/SCL等)")] string fileFormat,
            [Description("可选：模版名称，默认自动生成")] string? templateName = null,
            [Description("可选：解析器语言(Python/CSharp)，默认Python")] string parserLanguage = "Python",
            [Description("可选：版本号，默认1.0.0")] string version = "1.0.0",
            [Description("可选：结构信息JSON(BlockPatterns等)")] string? structureInfoJson = null,
            [Description("可选：模版描述")] string? description = null,
            [Description("可选：适用型号正则(如S7-(1200|1500))")] string? modelPattern = null,
            [Description("可选：优先级(1-10)，默认5")] int priority = 5)
        {
            _logger.LogInformation("[保存解析器模版] 开始: Brand={Brand}, Format={Format}", plcBrand, fileFormat);

            var result = new SaveParserTemplateResult();

            try
            {
                if (string.IsNullOrWhiteSpace(parserCode))
                {
                    result.Success = false;
                    result.ErrorMessage = "解析器代码不能为空";
                    return result;
                }

                // 生成模版编码（唯一标识）
                var templateCode = GenerateTemplateCode(plcBrand, fileFormat, version);
                var finalTemplateName = templateName ?? GenerateTemplateName(plcBrand, fileFormat, version);

                // 检查是否已存在相同编码的模版
                var existing = await _templateService.GetFirstAsync(t =>
                    t.TemplateCode == templateCode && t.IsDeleted == false);

                if (existing != null)
                {
                    _logger.LogInformation("[保存解析器模版] 发现已存在模版，执行更新: ID={Id}", existing.Id);

                    // 更新现有模版
                    existing.ParserCode = parserCode;
                    existing.Version = version;
                    existing.StructureInfoJson = structureInfoJson;
                    existing.Description = description;
                    existing.ModelPattern = modelPattern;
                    existing.Priority = priority;
                    existing.LastValidationTime = DateTime.UtcNow;

                    var updated = await _templateService.UpdateAsync(existing);
                    if (updated)
                    {
                        result.Success = true;
                        result.TemplateId = existing.Id;
                        result.TemplateCode = templateCode;
                        result.TemplateName = existing.TemplateName;
                        result.IsNew = false;
                        result.Message = $"解析器模版已更新: {finalTemplateName}";
                    }
                    else
                    {
                        result.Success = false;
                        result.ErrorMessage = "更新模版失败";
                    }

                    return result;
                }

                // 创建新模版
                var entity = new PLC_Parser_TemplateEntity
                {
                    TemplateName = finalTemplateName,
                    TemplateCode = templateCode,
                    PlcBrand = plcBrand,
                    FileFormat = fileFormat,
                    ParserLanguage = parserLanguage,
                    ParserCode = parserCode,
                    Version = version,
                    StructureInfoJson = structureInfoJson,
                    Description = description ?? $"{plcBrand} {fileFormat} 解析器",
                    ModelPattern = modelPattern,
                    Priority = priority,
                    IsActive = true,
                    ApprovalStatus = "Pending",
                    UsageCount = 0,
                    SuccessCount = 0
                };

                // 保存代码文件
                var codeFilePath = await SaveCodeFileAsync(parserCode, finalTemplateName, parserLanguage);
                entity.CodeFilePath = codeFilePath;

                await _templateService.InsertAsync(entity);

                result.Success = true;
                result.TemplateId = entity.Id;
                result.TemplateCode = templateCode;
                result.TemplateName = finalTemplateName;
                result.CodeFilePath = codeFilePath;
                result.IsNew = true;
                result.Message = $"解析器模版已保存: {finalTemplateName}";

                _logger.LogInformation("[保存解析器模版] 成功: ID={Id}, Name={Name}", entity.Id, finalTemplateName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[保存解析器模版] 失败");
                result.Success = false;
                result.ErrorMessage = $"保存失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 查找最匹配的解析器模版
        /// </summary>
        [Description("根据PLC品牌和文件格式查找最匹配的解析器模版")]
        public async Task<FindParserTemplateResult> FindBestTemplateAsync(
            [Description("PLC品牌")] string plcBrand,
            [Description("文件格式")] string fileFormat)
        {
            _logger.LogInformation("[查找解析器模版] 开始: Brand={Brand}, Format={Format}", plcBrand, fileFormat);

            var result = new FindParserTemplateResult
            {
                PlcBrand = plcBrand,
                FileFormat = fileFormat
            };

            try
            {
                var template = await _templateService.GetBestTemplateAsync(plcBrand, fileFormat);

                if (template == null)
                {
                    result.Success = true;
                    result.Found = false;
                    result.Message = $"未找到匹配的解析器模版: {plcBrand}/{fileFormat}";
                    return result;
                }

                result.Success = true;
                result.Found = true;
                result.TemplateId = template.Id;
                result.TemplateCode = template.TemplateCode;
                result.TemplateName = template.TemplateName;
                result.ParserCode = template.ParserCode;
                result.ParserLanguage = template.ParserLanguage;
                result.Version = template.Version;
                result.CodeFilePath = template.CodeFilePath;
                result.SuccessRate = template.UsageCount > 0 && template.SuccessCount.HasValue
                    ? (decimal)template.SuccessCount.Value / template.UsageCount.Value * 100
                    : null;
                result.Message = $"找到解析器模版: {template.TemplateName}";

                _logger.LogInformation("[查找解析器模版] 找到: ID={Id}, Name={Name}", template.Id, template.TemplateName);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[查找解析器模版] 失败");
                result.Success = false;
                result.ErrorMessage = $"查找失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 列出所有解析器模版
        /// </summary>
        [Description("列出所有PLC解析器模版，支持按品牌筛选")]
        public async Task<ListParserTemplatesResult> ListTemplatesAsync(
            [Description("可选：按品牌筛选")] string? plcBrand = null,
            [Description("可选：仅显示激活的模版")] bool activeOnly = true)
        {
            _logger.LogInformation("[列出解析器模版] Brand={Brand}, ActiveOnly={ActiveOnly}", plcBrand, activeOnly);

            var result = new ListParserTemplatesResult();

            try
            {
                var templates = await _templateService.GetListAsync(t =>
                    t.IsDeleted == false &&
                    (string.IsNullOrWhiteSpace(plcBrand) || t.PlcBrand == plcBrand) &&
                    (!activeOnly || t.IsActive == true));

                result.Success = true;
                result.TotalCount = templates.Count;
                result.Templates = templates.Select(t => new ParserTemplateSummary
                {
                    TemplateId = t.Id,
                    TemplateCode = t.TemplateCode,
                    TemplateName = t.TemplateName,
                    PlcBrand = t.PlcBrand,
                    FileFormat = t.FileFormat,
                    ParserLanguage = t.ParserLanguage,
                    Version = t.Version,
                    IsActive = t.IsActive ?? false,
                    UsageCount = t.UsageCount ?? 0,
                    SuccessRate = t.UsageCount > 0 && t.SuccessCount.HasValue
                        ? (decimal)t.SuccessCount.Value / t.UsageCount.Value * 100
                        : null,
                    Priority = t.Priority ?? 5
                }).OrderByDescending(t => t.Priority)
                  .ThenBy(t => t.PlcBrand)
                  .ThenBy(t => t.FileFormat)
                  .ToList();

                _logger.LogInformation("[列出解析器模版] 找到 {Count} 个模版", result.TotalCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[列出解析器模版] 失败");
                result.Success = false;
                result.ErrorMessage = $"列出失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 更新解析器模版使用统计
        /// </summary>
        [Description("更新解析器模版的使用统计（使用次数、成功次数、解析时间）")]
        public async Task<UpdateTemplateStatsResult> UpdateTemplateStatsAsync(
            [Description("模版ID")] long templateId,
            [Description("是否成功")] bool isSuccess,
            [Description("解析耗时(毫秒)")] int parseTimeMs)
        {
            _logger.LogInformation("[更新模版统计] TemplateId={Id}, Success={Success}, TimeMs={Ms}",
                templateId, isSuccess, parseTimeMs);

            var result = new UpdateTemplateStatsResult { TemplateId = templateId };

            try
            {
                var template = await _templateService.GetByIdAsync(templateId);
                if (template == null || template.IsDeleted)
                {
                    result.Success = false;
                    result.ErrorMessage = "模版不存在";
                    return result;
                }

                // 更新统计
                template.UsageCount = (template.UsageCount ?? 0) + 1;
                if (isSuccess)
                {
                    template.SuccessCount = (template.SuccessCount ?? 0) + 1;
                }

                // 计算平均解析时间
                var totalUsage = template.UsageCount.Value;
                var currentAvg = template.AvgParseTimeMs ?? 0;
                template.AvgParseTimeMs = (int)((currentAvg * (totalUsage - 1) + parseTimeMs) / totalUsage);

                template.LastValidationTime = DateTime.UtcNow;

                var updated = await _templateService.UpdateAsync(template);

                result.Success = updated;
                result.NewUsageCount = template.UsageCount.Value;
                result.NewSuccessCount = template.SuccessCount ?? 0;
                result.NewAvgParseTimeMs = template.AvgParseTimeMs ?? 0;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[更新模版统计] 失败");
                result.Success = false;
                result.ErrorMessage = $"更新失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 激活/停用解析器模版
        /// </summary>
        [Description("激活或停用指定的解析器模版")]
        public async Task<ToggleTemplateResult> ToggleTemplateActiveAsync(
            [Description("模版ID")] long templateId,
            [Description("是否激活")] bool isActive)
        {
            _logger.LogInformation("[切换模版状态] TemplateId={Id}, IsActive={Active}", templateId, isActive);

            var result = new ToggleTemplateResult { TemplateId = templateId };

            try
            {
                var template = await _templateService.GetByIdAsync(templateId);
                if (template == null || template.IsDeleted)
                {
                    result.Success = false;
                    result.ErrorMessage = "模版不存在";
                    return result;
                }

                template.IsActive = isActive;
                var updated = await _templateService.UpdateAsync(template);

                result.Success = updated;
                result.IsActive = isActive;
                result.TemplateName = template.TemplateName;
                result.Message = isActive ? "模版已激活" : "模版已停用";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[切换模版状态] 失败");
                result.Success = false;
                result.ErrorMessage = $"切换失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 删除解析器模版（软删除）
        /// </summary>
        [Description("删除指定的解析器模版（软删除）")]
        public async Task<DeleteTemplateResult> DeleteTemplateAsync(
            [Description("模版ID")] long templateId)
        {
            _logger.LogInformation("[删除解析器模版] TemplateId={Id}", templateId);

            var result = new DeleteTemplateResult { TemplateId = templateId };

            try
            {
                var deleted = await _templateService.SoftDeleteByIdAsync(templateId);

                result.Success = deleted;
                result.Message = deleted ? "模版已删除" : "删除失败";

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[删除解析器模版] 失败");
                result.Success = false;
                result.ErrorMessage = $"删除失败: {ex.Message}";
                return result;
            }
        }

        #region 私有方法

        private static string GenerateTemplateCode(string brand, string format, string version)
        {
            var safeBrand = brand.Replace(" ", "_").ToUpperInvariant();
            var safeFormat = format.Replace(" ", "_").ToUpperInvariant();
            var safeVersion = version.Replace(".", "_");
            return $"{safeBrand}_{safeFormat}_V{safeVersion}";
        }

        private static string GenerateTemplateName(string brand, string format, string version)
        {
            return $"{brand}_{format}_Parser_v{version}";
        }

        private async Task<string> SaveCodeFileAsync(string code, string templateName, string language)
        {
            var outputDir = Path.Combine(AppContext.BaseDirectory, "ParserTemplates");
            Directory.CreateDirectory(outputDir);

            var extension = language.Equals("Python", StringComparison.OrdinalIgnoreCase) ? "py" : "cs";
            var safeFileName = SanitizeFileName(templateName);
            var filePath = Path.Combine(outputDir, $"{safeFileName}.{extension}");

            await File.WriteAllTextAsync(filePath, code);

            _logger.LogInformation("[保存代码文件] 路径: {Path}", filePath);

            return filePath;
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        }

        #endregion
    }

    #region 返回模型

    /// <summary>
    /// 保存解析器模版结果
    /// </summary>
    public class SaveParserTemplateResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long TemplateId { get; set; }
        public string TemplateCode { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public string? CodeFilePath { get; set; }
        public bool IsNew { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 查找解析器模版结果
    /// </summary>
    public class FindParserTemplateResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public bool Found { get; set; }
        public string PlcBrand { get; set; } = string.Empty;
        public string FileFormat { get; set; } = string.Empty;
        public long TemplateId { get; set; }
        public string TemplateCode { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public string ParserCode { get; set; } = string.Empty;
        public string ParserLanguage { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? CodeFilePath { get; set; }
        public decimal? SuccessRate { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 列出解析器模版结果
    /// </summary>
    public class ListParserTemplatesResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public int TotalCount { get; set; }
        public List<ParserTemplateSummary> Templates { get; set; } = new();
    }

    /// <summary>
    /// 解析器模版摘要
    /// </summary>
    public class ParserTemplateSummary
    {
        public long TemplateId { get; set; }
        public string TemplateCode { get; set; } = string.Empty;
        public string TemplateName { get; set; } = string.Empty;
        public string PlcBrand { get; set; } = string.Empty;
        public string FileFormat { get; set; } = string.Empty;
        public string ParserLanguage { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public int UsageCount { get; set; }
        public decimal? SuccessRate { get; set; }
        public int Priority { get; set; }
    }

    /// <summary>
    /// 更新模版统计结果
    /// </summary>
    public class UpdateTemplateStatsResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long TemplateId { get; set; }
        public int NewUsageCount { get; set; }
        public int NewSuccessCount { get; set; }
        public int NewAvgParseTimeMs { get; set; }
    }

    /// <summary>
    /// 切换模版状态结果
    /// </summary>
    public class ToggleTemplateResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long TemplateId { get; set; }
        public bool IsActive { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 删除模版结果
    /// </summary>
    public class DeleteTemplateResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long TemplateId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    #endregion
}
