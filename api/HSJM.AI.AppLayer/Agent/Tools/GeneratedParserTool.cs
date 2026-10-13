using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 动态生成解析器执行工具（Python脚本）
    /// </summary>
    public class GeneratedParserTool
    {
        private readonly ILogger<GeneratedParserTool> _logger;

        public GeneratedParserTool(ILogger<GeneratedParserTool> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 执行已生成的解析器代码文件（Python脚本）
        /// </summary>
        [Description("执行已生成的Python解析器脚本文件，返回解析结果")]
        public async Task<ExecuteParserSandboxResult> ExecuteAsync(
            [Description("解析器代码文件路径")] string parserCodeFilePath,
            [Description("待解析文件路径")] string filePath,
            [Description("可选：超时秒数(默认30)")] int timeoutSeconds = 30)
        {
            if (!File.Exists(parserCodeFilePath))
            {
                return new ExecuteParserSandboxResult
                {
                    Success = false,
                    TestFilePath = filePath,
                    Errors = new List<string> { $"解析器代码文件不存在: {parserCodeFilePath}" }
                };
            }

            var code = await File.ReadAllTextAsync(parserCodeFilePath);
            return await ParserSandboxRunner.RunAsync(code, filePath, timeoutSeconds, _logger);
        }
    }
}
