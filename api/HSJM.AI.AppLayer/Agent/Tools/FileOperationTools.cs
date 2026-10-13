using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 文件操作工具集
    /// 用于智能体分块读取、搜索大文件
    /// </summary>
    public class FileOperationTools
    {
        private readonly ILogger<FileOperationTools> _logger;
        private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _environment;
        private const int MaxFileSize = 100 * 1024 * 1024; // 100MB限制
        private const int MaxLinesPerRead = 10000; // 单次最多读取10000行
        private const string SKILLS_ROOT_FOLDER = "skills"; // 技能根目录

        public FileOperationTools(
            ILogger<FileOperationTools> logger,
            Microsoft.AspNetCore.Hosting.IWebHostEnvironment environment)
        {
            _logger = logger;
            _environment = environment;
        }

        /// <summary>
        /// 读取文件指定行范围
        /// 支持大文件（10MB+），按行读取，避免内存溢出
        /// </summary>
        [Description("读取文件指定行范围(分块读取大文件)。⚠️ filePath必须从上游任务输出的FilePath字段获取，不能虚构路径。用于分段读取XML文件内容。")]
        public async Task<ReadFileLinesResult> ReadFileLinesAsync(
            [Description("文件绝对路径(必须从上游FilePath字段获取,不能用null)")] string filePath,
            [Description("起始行号(从1开始)")] int startLine,
            [Description("结束行号(包含)")] int endLine,
            [Description("可选：文件编码，默认UTF-8")] string? encoding = null)
        {
            _logger.LogInformation("[文件读取] 开始读取: {FilePath}, 行范围: {Start}-{End}", filePath, startLine, endLine);

            var result = new ReadFileLinesResult
            {
                FilePath = filePath,
                RequestedStartLine = startLine,
                RequestedEndLine = endLine
            };

            try
            {
                // 1. 验证文件存在
                if (!File.Exists(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    _logger.LogWarning("[文件读取] {Error}", result.ErrorMessage);
                    return result;
                }

                var fileInfo = new FileInfo(filePath);

                // 2. 验证文件大小
                if (fileInfo.Length > MaxFileSize)
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件过大: {fileInfo.Length / 1024 / 1024}MB，超过限制 {MaxFileSize / 1024 / 1024}MB";
                    _logger.LogWarning("[文件读取] {Error}", result.ErrorMessage);
                    return result;
                }

                // 3. 验证行号参数
                if (startLine < 1)
                {
                    result.Success = false;
                    result.ErrorMessage = "起始行号必须 >= 1";
                    return result;
                }

                if (endLine < startLine)
                {
                    result.Success = false;
                    result.ErrorMessage = "结束行号必须 >= 起始行号";
                    return result;
                }

                if (endLine - startLine + 1 > MaxLinesPerRead)
                {
                    result.Success = false;
                    result.ErrorMessage = $"请求行数过多（{endLine - startLine + 1}行），单次最多读取 {MaxLinesPerRead} 行";
                    return result;
                }

                // 4. 确定编码
                var enc = string.IsNullOrWhiteSpace(encoding) 
                    ? Encoding.UTF8 
                    : Encoding.GetEncoding(encoding);

                // 5. 流式读取文件
                var lines = new List<string>();
                int currentLine = 0;
                int totalLines = 0;
                using (var reader = new StreamReader(filePath, enc))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        currentLine++;
                        totalLines++;
                        // 跳过起始行之前的内容
                        if (currentLine < startLine)
                            continue;

                        // 收集目标行
                        if (currentLine >= startLine && currentLine <= endLine)
                        {
                            lines.Add(line);
                        }

                       
                    }

                    result.TotalLinesInFile = totalLines;
                }

                result.Lines = lines;
                result.ActualStartLine = startLine;
                result.ActualEndLine = Math.Min(endLine, result.TotalLinesInFile);
                result.Success = true;

                _logger.LogInformation("[文件读取] 成功读取 {Count} 行，文件总行数: {Total}", 
                    lines.Count, result.TotalLinesInFile);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件读取] 读取失败");
                result.Success = false;
                result.ErrorMessage = $"读取失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 使用正则表达式搜索文件
        /// 返回匹配行号、匹配内容和上下文
        /// </summary>
        [Description("使用正则表达式搜索文件，返回匹配行号和上下文")]
        public async Task<SearchFileByRegexResult> SearchFileByRegexAsync(
            [Description("文件绝对路径")] string filePath,
            [Description("正则表达式模式")] string regexPattern,
            [Description("可选：上下文行数，默认2行")] int contextLines = 2,
            [Description("可选：最大匹配数，默认100")] int maxMatches = 100,
            [Description("可选：是否忽略大小写，默认false")] bool ignoreCase = false)
        {
            _logger.LogInformation("[正则搜索] 开始搜索: {FilePath}, 模式: {Pattern}", filePath, regexPattern);

            var result = new SearchFileByRegexResult
            {
                FilePath = filePath,
                RegexPattern = regexPattern,
                ContextLines = contextLines
            };

            try
            {
                // 1. 验证文件存在
                if (!File.Exists(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    _logger.LogWarning("[正则搜索] {Error}", result.ErrorMessage);
                    return result;
                }

                // 2. 验证正则表达式
                System.Text.RegularExpressions.Regex regex;
                try
                {
                    var options = ignoreCase 
                        ? System.Text.RegularExpressions.RegexOptions.IgnoreCase 
                        : System.Text.RegularExpressions.RegexOptions.None;
                    regex = new System.Text.RegularExpressions.Regex(regexPattern, options);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = $"正则表达式无效: {ex.Message}";
                    return result;
                }

                // 3. 流式读取文件并搜索
                var matches = new List<RegexMatch>();
                var allLines = new List<string>(); // 存储所有行用于上下文提取
                int currentLine = 0;

                using (var reader = new StreamReader(filePath, Encoding.UTF8))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        currentLine++;
                        allLines.Add(line);

                        // 检查是否匹配
                        if (regex.IsMatch(line))
                        {
                            matches.Add(new RegexMatch
                            {
                                LineNumber = currentLine,
                                MatchedContent = line
                            });

                            // 达到最大匹配数，停止搜索
                            if (matches.Count >= maxMatches)
                            {
                                _logger.LogInformation("[正则搜索] 达到最大匹配数限制: {Max}", maxMatches);
                                break;
                            }
                        }
                    }
                }

                result.TotalLinesInFile = currentLine;

                // 4. 为每个匹配添加上下文
                foreach (var match in matches)
                {
                    var contextStart = Math.Max(1, match.LineNumber - contextLines);
                    var contextEnd = Math.Min(allLines.Count, match.LineNumber + contextLines);

                    match.ContextBefore = allLines
                        .Skip(contextStart - 1)
                        .Take(match.LineNumber - contextStart)
                        .ToList();

                    match.ContextAfter = allLines
                        .Skip(match.LineNumber) // 跳过匹配行本身
                        .Take(contextEnd - match.LineNumber)
                        .ToList();
                }

                result.Matches = matches;
                result.TotalMatches = matches.Count;
                result.Success = true;

                _logger.LogInformation("[正则搜索] 搜索完成，找到 {Count} 个匹配", matches.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[正则搜索] 搜索失败");
                result.Success = false;
                result.ErrorMessage = $"搜索失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 写入文件内容（安全限定在 wwwroot/skills 目录下）
        /// 用于技能创建后填充 SKILL.md、main.py 等文件
        /// </summary>
        [Description("写入文件内容到skills目录。支持创建新文件或覆盖已有文件，可选追加模式。⚠️ 安全限制：仅能操作wwwroot/skills目录下的文件。")]
        public async Task<WriteFileContentResult> WriteFileContentAsync(
            [Description("相对于skills/的文件路径，如：pdf-reader/SKILL.md 或 pdf-reader/scripts/main.py")] string relativeFilePath,
            [Description("文件内容")] string content,
            [Description("可选：文件编码，默认UTF-8")] string? encoding = null,
            [Description("可选：是否追加模式，默认false(覆盖)")] bool append = false)
        {
            _logger.LogInformation("[文件写入] 开始写入: {Path}, 追加={Append}", relativeFilePath, append);

            var result = new WriteFileContentResult
            {
                RelativeFilePath = relativeFilePath,
                Append = append
            };

            try
            {
                // 1. 安全验证：必须在 skills 目录下
                var skillsRoot = Path.Combine(_environment.WebRootPath, SKILLS_ROOT_FOLDER);
                var fullPath = Path.Combine(skillsRoot, relativeFilePath);
                var normalizedPath = Path.GetFullPath(fullPath);

                // 防止路径穿越攻击
                if (!normalizedPath.StartsWith(Path.GetFullPath(skillsRoot), StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = false;
                    result.ErrorMessage = $"安全错误：文件路径必须在 skills 目录下。路径: {relativeFilePath}";
                    _logger.LogWarning("[文件写入] {Error}", result.ErrorMessage);
                    return result;
                }

                result.FullPath = normalizedPath;

                // 2. 确保目录存在
                var directory = Path.GetDirectoryName(normalizedPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation("[文件写入] 创建目录: {Dir}", directory);
                }

                // 3. 确定编码
                var enc = string.IsNullOrWhiteSpace(encoding)
                    ? Encoding.UTF8
                    : Encoding.GetEncoding(encoding);

                // 4. 写入文件
                if (append)
                {
                    await File.AppendAllTextAsync(normalizedPath, content, enc);
                }
                else
                {
                    await File.WriteAllTextAsync(normalizedPath, content, enc);
                }

                result.Success = true;
                result.BytesWritten = enc.GetByteCount(content);

                _logger.LogInformation("[文件写入] 成功: {Path}, {Bytes} 字节", normalizedPath, result.BytesWritten);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件写入] 写入失败");
                result.Success = false;
                result.ErrorMessage = $"写入失败: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// 列出目录结构（限定在 wwwroot/skills 目录下）
        /// 用于 LLM 自检生成的技能包结构
        /// </summary>
        [Description("列出skills目录下的文件和文件夹结构。用于检查技能包文件组织。⚠️ 安全限制：仅能查看wwwroot/skills目录下的内容。")]
        public async Task<ListDirectoryStructureResult> ListDirectoryStructureAsync(
            [Description("相对于skills/的目录路径，如：pdf-reader 或 pdf-reader/scripts")] string relativeDirectoryPath,
            [Description("可选：最大递归深度，默认2层")] int maxDepth = 2,
            [Description("可选：是否显示文件大小，默认true")] bool showSize = true)
        {
            _logger.LogInformation("[目录结构] 开始列出: {Path}", relativeDirectoryPath);

            var result = new ListDirectoryStructureResult
            {
                RelativeDirectoryPath = relativeDirectoryPath,
                MaxDepth = maxDepth
            };

            try
            {
                // 1. 安全验证：必须在 skills 目录下
                var skillsRoot = Path.Combine(_environment.WebRootPath, SKILLS_ROOT_FOLDER);
                var fullPath = Path.Combine(skillsRoot, relativeDirectoryPath);
                var normalizedPath = Path.GetFullPath(fullPath);

                // 防止路径穿越攻击
                if (!normalizedPath.StartsWith(Path.GetFullPath(skillsRoot), StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = false;
                    result.ErrorMessage = $"安全错误：目录路径必须在 skills 目录下。路径: {relativeDirectoryPath}";
                    _logger.LogWarning("[目录结构] {Error}", result.ErrorMessage);
                    return result;
                }

                result.FullPath = normalizedPath;

                // 2. 验证目录存在
                if (!Directory.Exists(normalizedPath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"目录不存在: {normalizedPath}";
                    _logger.LogWarning("[目录结构] {Error}", result.ErrorMessage);
                    return result;
                }

                // 3. 递归构建目录树
                var tree = new StringBuilder();
                tree.AppendLine($"{new DirectoryInfo(normalizedPath).Name}/");
                await BuildDirectoryTreeAsync(normalizedPath, tree, "", 0, maxDepth, showSize);
                result.Tree = tree.ToString();
                result.Success = true;

                _logger.LogInformation("[目录结构] 列出成功: {Path}", normalizedPath);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[目录结构] 列出失败");
                result.Success = false;
                result.ErrorMessage = $"列出失败: {ex.Message}";
                return result;
            }
        }

        private async Task BuildDirectoryTreeAsync(string path, StringBuilder sb, string indent, int currentDepth, int maxDepth, bool showSize)
        {
            if (currentDepth > maxDepth) return;

            await Task.Run(() =>
            {
                try
                {
                    var dirInfo = new DirectoryInfo(path);
                    
                    // 列出文件
                    var files = dirInfo.GetFiles().OrderBy(f => f.Name).ToList();
                    var dirs = dirInfo.GetDirectories().OrderBy(d => d.Name).ToList();
                    var totalItems = files.Count + dirs.Count;
                    var currentItem = 0;

                    // 列出文件
                    foreach (var file in files)
                    {
                        currentItem++;
                        var isLast = (currentItem == totalItems);
                        var prefix = isLast ? "└──" : "├──";
                        var sizeStr = showSize ? $" ({FormatFileSize(file.Length)})" : "";
                        sb.AppendLine($"{indent}{prefix} {file.Name}{sizeStr}");
                    }

                    // 列出子目录
                    foreach (var dir in dirs)
                    {
                        currentItem++;
                        var isLast = (currentItem == totalItems);
                        var prefix = isLast ? "└──" : "├──";
                        var childIndent = isLast ? "    " : "│   ";

                        sb.AppendLine($"{indent}{prefix} {dir.Name}/");

                        if (currentDepth < maxDepth)
                        {
                            BuildDirectoryTreeAsync(dir.FullName, sb, indent + childIndent, currentDepth + 1, maxDepth, showSize).Wait();
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    sb.AppendLine($"{indent}[访问被拒绝]");
                }
            });
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
            return $"{bytes / (1024 * 1024)}MB";
        }

        /// <summary>
        /// 获取文件元数据（大小、行数、编码等）
        /// </summary>
        [Description("获取文件元数据(大小/行数/编码/创建时间)。用于文件路由与解析策略判断")]
        public async Task<FileMetadataResult> GetFileMetadataAsync(
            [Description("文件绝对路径")] string filePath,
            [Description("可选：是否检测编码，默认true")] bool detectEncoding = true)
        {
            _logger.LogInformation("[文件元数据] 开始: {FilePath}", filePath);

            var result = new FileMetadataResult
            {
                FilePath = filePath
            };

            try
            {
                if (!File.Exists(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    return result;
                }

                var fileInfo = new FileInfo(filePath);
                result.SizeBytes = fileInfo.Length;
                result.CreateTimeUtc = fileInfo.CreationTimeUtc;
                result.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;

                if (detectEncoding)
                {
                    result.Encoding = DetectFileEncoding(filePath);
                }
                else
                {
                    result.Encoding = "Unknown";
                }

                // 统计行数（流式）
                var lineCount = 0;
                using (var reader = new StreamReader(filePath, Encoding.UTF8,  true))
                {
                    while (await reader.ReadLineAsync() != null)
                    {
                        lineCount++;
                    }
                }

                result.LineCount = lineCount;
                result.Success = true;

                _logger.LogInformation("[文件元数据] 完成: Size={Size}B, Lines={Lines}, Encoding={Encoding}",
                    result.SizeBytes, result.LineCount, result.Encoding);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[文件元数据] 失败");
                result.Success = false;
                result.ErrorMessage = $"获取失败: {ex.Message}";
                return result;
            }
        }

        private static string DetectFileEncoding(string filePath)
        {
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[4];
                    var read = stream.Read(buffer, 0, buffer.Length);

                    if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                        return "UTF-8";
                    if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                        return "UTF-16LE";
                    if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
                        return "UTF-16BE";
                    if (read >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
                        return "UTF-32BE";
                    if (read >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
                        return "UTF-32LE";
                }

                // 无BOM，尝试UTF-8检测
                var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                using (var reader = new StreamReader(filePath, utf8, detectEncodingFromByteOrderMarks: false))
                {
                    reader.ReadToEnd();
                }

                return "UTF-8";
            }
            catch
            {
                return "Unknown";
            }
        }
    }

    #region 返回模型

    /// <summary>
    /// 文件行读取结果
    /// </summary>
    public class ReadFileLinesResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public int RequestedStartLine { get; set; }
        public int RequestedEndLine { get; set; }
        public int ActualStartLine { get; set; }
        public int ActualEndLine { get; set; }
        public int TotalLinesInFile { get; set; }
        public List<string> Lines { get; set; } = new();

        public override string ToString()
        {
            if (!Success)
                return $"❌ 读取失败: {ErrorMessage}";

            return $@"✅ 读取成功
文件: {FilePath}
读取范围: 第 {ActualStartLine}-{ActualEndLine} 行 (共 {Lines.Count} 行)
文件总行数: {TotalLinesInFile}

内容:
{string.Join("\n", Lines.Select((line, idx) => $"{ActualStartLine + idx,4}: {line}"))}";
        }
    }

    /// <summary>
    /// 正则搜索结果
    /// </summary>
    public class SearchFileByRegexResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string RegexPattern { get; set; } = string.Empty;
        public int ContextLines { get; set; }
        public int TotalLinesInFile { get; set; }
        public int TotalMatches { get; set; }
        public List<RegexMatch> Matches { get; set; } = new();

        public override string ToString()
        {
            if (!Success)
                return $"❌ 搜索失败: {ErrorMessage}";

            var result = $@"✅ 搜索成功
文件: {FilePath}
正则模式: {RegexPattern}
文件总行数: {TotalLinesInFile}
找到匹配: {TotalMatches} 个

";
            foreach (var match in Matches.Take(10)) // 最多显示前10个
            {
                result += $"\n--- 第 {match.LineNumber} 行 ---\n";
                
                if (match.ContextBefore.Any())
                    result += string.Join("\n", match.ContextBefore.Select(l => $"  {l}")) + "\n";
                
                result += $"▶ {match.MatchedContent}\n";
                
                if (match.ContextAfter.Any())
                    result += string.Join("\n", match.ContextAfter.Select(l => $"  {l}")) + "\n";
            }

            if (TotalMatches > 10)
                result += $"\n... 还有 {TotalMatches - 10} 个匹配未显示 ...";

            return result;
        }
    }

    /// <summary>
    /// 正则匹配项
    /// </summary>
    public class RegexMatch
    {
        public int LineNumber { get; set; }
        public string MatchedContent { get; set; } = string.Empty;
        public List<string> ContextBefore { get; set; } = new();
        public List<string> ContextAfter { get; set; } = new();
    }

    /// <summary>
    /// 文件元数据结果
    /// </summary>
    public class FileMetadataResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public int LineCount { get; set; }
        public string Encoding { get; set; } = "Unknown";
        public DateTime CreateTimeUtc { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }

        public override string ToString()
        {
            if (!Success)
                return $"❌ 获取失败: {ErrorMessage}";

            return $@"✅ 文件元数据
文件: {FilePath}
大小: {SizeBytes} 字节
行数: {LineCount}
编码: {Encoding}
创建时间(UTC): {CreateTimeUtc:yyyy-MM-dd HH:mm:ss}
更新时间(UTC): {LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss}";
        }
    }

    /// <summary>
    /// 文件写入结果
    /// </summary>
    public class WriteFileContentResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string RelativeFilePath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool Append { get; set; }
        public long BytesWritten { get; set; }

        public override string ToString()
        {
            if (!Success)
                return $"❌ 写入失败: {ErrorMessage}";

            return $@"✅ 写入成功
文件: {RelativeFilePath}
完整路径: {FullPath}
模式: {(Append ? "追加" : "覆盖")}
写入字节: {BytesWritten}";
        }
    }

    #endregion
}
