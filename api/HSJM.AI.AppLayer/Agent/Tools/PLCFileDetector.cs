using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// PLC 文件类型检测工具
    /// </summary>
    public class PLCFileDetector
    {
        private readonly ILogger<PLCFileDetector> _logger;

        public PLCFileDetector(ILogger<PLCFileDetector> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 检测PLC文件类型
        /// </summary>
        [Description("检测PLC文件的品牌、型号、语言、软件版本等信息")]
        public async Task<PlcFileDetectionResult> DetectAsync(
            [Description("PLC文件绝对路径")] string filePath)
        {
            _logger.LogInformation("[PLC检测] 开始: {FilePath}", filePath);

            var result = new PlcFileDetectionResult
            {
                FilePath = filePath
            };

            try
            {
                if (!File.Exists(filePath))
                {
                    result.ErrorMessage = $"文件不存在: {filePath}";
                    result.DetectionConfidence = 0;
                    return result;
                }

                var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
                var header = await ReadFileHeaderAsync(filePath, 200);

                // 初步基于扩展名
                if (ext is ".awl" or ".stl")
                {
                    result.Language = ext.Trim('.').ToUpperInvariant();
                    result.FileFormat = "Text";
                }
                else if (ext is ".scl")
                {
                    result.Language = "SCL";
                    result.FileFormat = "Text";
                }
                else if (ext is ".xml")
                {
                    result.Language = "XML";
                    result.FileFormat = "XML";
                }
                else if (ext is ".ldp" or ".ld")
                {
                    result.Language = "LD";
                    result.FileFormat = "Text";
                }

                // 识别品牌/软件
                var headerLower = header.ToLowerInvariant();

                if (headerLower.Contains("siemens") || headerLower.Contains("tia portal") || headerLower.Contains("sw.blocks"))
                {
                    result.Brand = "Siemens";
                    result.Software = headerLower.Contains("tia portal") ? "TIA Portal" : "Siemens";
                }
                else if (headerLower.Contains("mitsubishi") || headerLower.Contains("gx works"))
                {
                    result.Brand = "Mitsubishi";
                    result.Software = "GX Works";
                }
                else if (headerLower.Contains("omron") || headerLower.Contains("cx-programmer"))
                {
                    result.Brand = "Omron";
                    result.Software = "CX-Programmer";
                }
                else if (headerLower.Contains("rockwell") || headerLower.Contains("rslogix"))
                {
                    result.Brand = "Rockwell";
                    result.Software = "RSLogix";
                }
                else if (headerLower.Contains("schneider") || headerLower.Contains("unity pro"))
                {
                    result.Brand = "Schneider";
                    result.Software = "Unity Pro";
                }
                else
                {
                    result.Brand = "Unknown";
                }

                // 尝试提取型号
                result.Model = InferModel(header);

                // 置信度
                var signals = 0;
                if (!string.IsNullOrWhiteSpace(result.Brand) && result.Brand != "Unknown") signals++;
                if (!string.IsNullOrWhiteSpace(result.Language)) signals++;
                if (!string.IsNullOrWhiteSpace(result.FileFormat)) signals++;
                if (!string.IsNullOrWhiteSpace(result.Model) && result.Model != "Unknown") signals++;

                result.DetectionConfidence = signals switch
                {
                    >= 3 => 0.9,
                    2 => 0.75,
                    1 => 0.55,
                    _ => 0.3
                };

                _logger.LogInformation("[PLC检测] 完成: Brand={Brand}, Model={Model}, Language={Language}, Confidence={Confidence}",
                    result.Brand, result.Model, result.Language, result.DetectionConfidence);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PLC检测] 失败");
                result.ErrorMessage = ex.Message;
                result.DetectionConfidence = 0;
                return result;
            }
        }

        private static async Task<string> ReadFileHeaderAsync(string filePath, int maxLines)
        {
            var builder = new StringBuilder();
            using (var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                var count = 0;
                while ((line = await reader.ReadLineAsync()) != null && count < maxLines)
                {
                    builder.AppendLine(line);
                    count++;
                }
            }

            return builder.ToString();
        }

        private static string InferModel(string content)
        {
            var candidates = new[]
            {
                "S7-1200", "S7-1500", "S7-300", "S7-400",
                "FX3U", "FX5U", "Q00", "Q01",
                "CJ1", "CJ2", "NX1", "NJ",
                "CompactLogix", "ControlLogix"
            };

            foreach (var c in candidates)
            {
                if (content.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                    return c;
            }

            return "Unknown";
        }
    }

    public class PlcFileDetectionResult
    {
        public string FilePath { get; set; } = string.Empty;
        public string Brand { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public string Language { get; set; } = string.Empty;
        public string FileFormat { get; set; } = string.Empty;
        public string Software { get; set; } = string.Empty;
        public double DetectionConfidence { get; set; }
        public string? ErrorMessage { get; set; }

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
                return $"❌ 检测失败: {ErrorMessage}";

            return $@"✅ PLC文件检测结果
+品牌: {Brand}
+型号: {Model}
+语言: {Language}
+格式: {FileFormat}
+软件: {Software}
+置信度: {DetectionConfidence:0.00}";
        }
    }
}
