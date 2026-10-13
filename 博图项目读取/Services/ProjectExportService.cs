using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using 博图项目读取.Models;
using Newtonsoft.Json;

namespace 博图项目读取.Services
{
    /// <summary>
    /// 项目导出服务 - 将解析的项目数据导出为各种格式
    /// </summary>
    public class ProjectExportService
    {
        private readonly Action<string> _logAction;
        private readonly JsonSerializerSettings _jsonSettings;

        public ProjectExportService(Action<string> logAction = null)
        {
            _logAction = logAction ?? (msg => { });
            _jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DateFormatHandling = DateFormatHandling.IsoDateFormat
            };
        }

        /// <summary>
        /// 导出项目为JSON文件
        /// </summary>
        /// <param name="project">项目信息</param>
        /// <param name="outputPath">输出路径</param>
        /// <returns>是否导出成功</returns>
        public async Task<bool> ExportToJsonAsync(TiaPortalProject project, string outputPath)
        {
            try
            {
                _logAction($"开始导出项目到JSON: {outputPath}");

                var json = JsonConvert.SerializeObject(project, _jsonSettings);
                await WriteAllTextAsync(outputPath, json);

                _logAction($"项目导出完成: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logAction($"导出项目到JSON失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出符号表为CSV文件
        /// </summary>
        /// <param name="symbols">符号列表</param>
        /// <param name="outputPath">输出路径</param>
        /// <returns>是否导出成功</returns>
        public async Task<bool> ExportSymbolsToCsvAsync(List<SymbolInfo> symbols, string outputPath)
        {
            try
            {
                _logAction($"开始导出符号表到CSV: {outputPath}");

                var csv = new List<string>
                {
                    "名称,地址,数据类型,注释,逻辑地址"
                };

                foreach (var symbol in symbols)
                {
                    csv.Add($"\"{symbol.Name}\",\"{symbol.Address}\",\"{symbol.DataType}\",\"{symbol.Comment}\",\"{symbol.LogicalAddress}\"");
                }

                await WriteAllLinesAsync(outputPath, csv);

                _logAction($"符号表导出完成: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logAction($"导出符号表到CSV失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出程序块信息为HTML报告
        /// </summary>
        /// <param name="project">项目信息</param>
        /// <param name="outputPath">输出路径</param>
        /// <returns>是否导出成功</returns>
        public async Task<bool> ExportToHtmlReportAsync(TiaPortalProject project, string outputPath)
        {
            try
            {
                _logAction($"开始导出HTML报告: {outputPath}");

                var html = GenerateHtmlReport(project);
                await WriteAllTextAsync(outputPath, html);

                _logAction($"HTML报告导出完成: {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logAction($"导出HTML报告失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 导出业务系统JSON
        /// </summary>
        /// <param name="project">项目信息</param>
        /// <param name="filePath">文件路径</param>
        /// <param name="systemName">业务系统名称</param>
        /// <returns>是否成功</returns>
        public async Task<bool> ExportBusinessSystemJsonAsync(TiaPortalProject project, string filePath, string systemName = "生产监控系统")
        {
            try
            {
                _logAction?.Invoke($"开始导出业务系统JSON: {filePath}");

                // 创建业务系统数据
                var businessData = new BusinessSystemData
                {
                    SystemName = systemName,
                    Version = "1.0",
                    ExportTime = DateTime.Now,
                    ExportedBy = Environment.UserName,
                    ProjectData = project,
                    MonitoringPoints = CreateMonitoringPoints(project),
                    AlarmConfigurations = CreateAlarmConfigurations(project),
                    CommunicationSettings = CreateCommunicationSettings(project)
                };

                // 序列化为JSON
                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    DateFormatString = "yyyy-MM-dd HH:mm:ss",
                    NullValueHandling = NullValueHandling.Ignore
                };

                var json = JsonConvert.SerializeObject(businessData, settings);

                // 写入文件
                await WriteFileAsync(filePath, json);

                _logAction?.Invoke($"业务系统JSON导出成功: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                _logAction?.Invoke($"导出业务系统JSON失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 生成HTML报告
        /// </summary>
        private string GenerateHtmlReport(TiaPortalProject project)
        {
            var html = $@"
<!DOCTYPE html>
<html lang='zh-CN'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>TIA Portal 项目报告 - {project.Name}</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; }}
        .header {{ background-color: #f0f0f0; padding: 20px; border-radius: 5px; }}
        .section {{ margin: 20px 0; }}
        .section h2 {{ color: #333; border-bottom: 2px solid #007acc; padding-bottom: 5px; }}
        table {{ border-collapse: collapse; width: 100%; }}
        th, td {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
        th {{ background-color: #f2f2f2; }}
        .device {{ background-color: #f9f9f9; padding: 10px; margin: 10px 0; border-radius: 3px; }}
    </style>
</head>
<body>
    <div class='header'>
        <h1>TIA Portal 项目报告</h1>
        <p><strong>项目名称:</strong> {project.Name}</p>
        <p><strong>项目路径:</strong> {project.Path}</p>
        <p><strong>项目版本:</strong> {project.Version}</p>
        <p><strong>生成时间:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
    </div>

    <div class='section'>
        <h2>设备信息</h2>
        {GenerateDevicesHtml(project.Devices)}
    </div>

    <div class='section'>
        <h2>符号表</h2>
        {GenerateSymbolsHtml(project.Symbols)}
    </div>

    <div class='section'>
        <h2>程序块</h2>
        {GenerateProgramBlocksHtml(project.ProgramBlocks)}
    </div>
</body>
</html>";

            return html;
        }

        /// <summary>
        /// 生成设备HTML
        /// </summary>
        private string GenerateDevicesHtml(List<PlcDevice> devices)
        {
            if (!devices.Any()) return "<p>无设备信息</p>";

            var html = "";
            foreach (var device in devices)
            {
                html += $@"
                <div class='device'>
                    <h3>{device.Name}</h3>
                    <p><strong>类型:</strong> {device.Type}</p>
                    <p><strong>订货号:</strong> {device.OrderNumber}</p>
                    <p><strong>版本:</strong> {device.Version}</p>
                    <p><strong>程序块数量:</strong> {device.Blocks.Count}</p>
                </div>";
            }

            return html;
        }

        /// <summary>
        /// 生成符号表HTML
        /// </summary>
        private string GenerateSymbolsHtml(List<SymbolInfo> symbols)
        {
            if (!symbols.Any()) return "<p>无符号信息</p>";

            var html = @"
            <table>
                <tr>
                    <th>名称</th>
                    <th>地址</th>
                    <th>数据类型</th>
                    <th>注释</th>
                    <th>逻辑地址</th>
                </tr>";

            foreach (var symbol in symbols)
            {
                html += $@"
                <tr>
                    <td>{symbol.Name}</td>
                    <td>{symbol.Address}</td>
                    <td>{symbol.DataType}</td>
                    <td>{symbol.Comment}</td>
                    <td>{symbol.LogicalAddress}</td>
                </tr>";
            }

            html += "</table>";
            return html;
        }

        /// <summary>
        /// 生成程序块HTML
        /// </summary>
        private string GenerateProgramBlocksHtml(List<ProgramBlock> blocks)
        {
            if (!blocks.Any()) return "<p>无程序块信息</p>";

            var html = @"
            <table>
                <tr>
                    <th>名称</th>
                    <th>类型</th>
                    <th>编号</th>
                    <th>语言</th>
                    <th>注释</th>
                </tr>";

            foreach (var block in blocks)
            {
                html += $@"
                <tr>
                    <td>{block.Name}</td>
                    <td>{block.Type}</td>
                    <td>{block.Number}</td>
                    <td>{block.Language}</td>
                    <td>{block.Comment}</td>
                </tr>";
            }

            html += "</table>";
            return html;
        }

        /// <summary>
        /// 创建监控点配置
        /// </summary>
        private List<MonitoringPoint> CreateMonitoringPoints(TiaPortalProject project)
        {
            var monitoringPoints = new List<MonitoringPoint>();

            // 从符号表中创建监控点
            foreach (var symbol in project.Symbols)
            {
                // 选择重要的监控点
                if (IsImportantSymbol(symbol))
                {
                    var monitoringPoint = new MonitoringPoint
                    {
                        Name = symbol.Name,
                        Address = symbol.Address,
                        DataType = symbol.DataType,
                        Description = symbol.Comment,
                        IsEnabled = true,
                        ScanInterval = GetScanInterval(symbol),
                        Unit = GetSymbolUnit(symbol)
                    };

                    // 设置数值范围
                    if (symbol.DataType == "Int" || symbol.DataType == "Word")
                    {
                        monitoringPoint.MinValue = 0;
                        monitoringPoint.MaxValue = 65535;
                    }
                    else if (symbol.DataType == "DInt")
                    {
                        monitoringPoint.MinValue = -2147483648;
                        monitoringPoint.MaxValue = 2147483647;
                    }

                    monitoringPoints.Add(monitoringPoint);
                }
            }

            return monitoringPoints;
        }

        /// <summary>
        /// 创建报警配置
        /// </summary>
        private List<AlarmConfiguration> CreateAlarmConfigurations(TiaPortalProject project)
        {
            var alarmConfigurations = new List<AlarmConfiguration>
            {
                new AlarmConfiguration
                {
                    Name = "急停报警",
                    Condition = "Emergency_Stop = FALSE",
                    Message = "系统急停按钮被按下！",
                    Severity = "Critical",
                    IsEnabled = true
                },
                new AlarmConfiguration
                {
                    Name = "系统故障报警",
                    Condition = "System_Fault = TRUE",
                    Message = "系统发生故障，请检查设备状态！",
                    Severity = "High",
                    IsEnabled = true
                },
                new AlarmConfiguration
                {
                    Name = "安全门报警",
                    Condition = "Safety_Door = FALSE",
                    Message = "安全门未关闭，请检查安全门状态！",
                    Severity = "Medium",
                    IsEnabled = true
                },
                new AlarmConfiguration
                {
                    Name = "温度超限报警",
                    Condition = "Temperature_Sensor > 80",
                    Message = "温度传感器读数超过限制值！",
                    Severity = "High",
                    IsEnabled = true
                },
                new AlarmConfiguration
                {
                    Name = "压力异常报警",
                    Condition = "Pressure_Sensor < 2 OR Pressure_Sensor > 10",
                    Message = "系统压力异常，请检查压力系统！",
                    Severity = "Medium",
                    IsEnabled = true
                }
            };

            return alarmConfigurations;
        }

        /// <summary>
        /// 创建通讯设置
        /// </summary>
        private CommunicationSettings CreateCommunicationSettings(TiaPortalProject project)
        {
            // 从项目设备中获取通讯设置
            var firstDevice = project.Devices.FirstOrDefault();
            var ipAddress = firstDevice?.IpAddress ?? "192.168.1.100";

            return new CommunicationSettings
            {
                Protocol = "S7",
                IpAddress = ipAddress,
                Port = 102,
                Timeout = 5000,
                AdditionalSettings = new Dictionary<string, object>
                {
                    { "Rack", firstDevice?.Rack ?? 0 },
                    { "Slot", firstDevice?.Slot ?? 1 },
                    { "MaxConnections", 8 },
                    { "KeepAlive", true },
                    { "RetryCount", 3 },
                    { "RetryInterval", 1000 }
                }
            };
        }

        /// <summary>
        /// 判断是否为重要符号
        /// </summary>
        private bool IsImportantSymbol(SymbolInfo symbol)
        {
            // 过滤重要的监控符号
            var importantKeywords = new[] { "temperature", "pressure", "speed", "position", "running", "fault", "alarm", "status", "count" };
            var symbolName = symbol.Name.ToLower();
            
            return importantKeywords.Any(keyword => symbolName.Contains(keyword)) ||
                   symbol.Address.StartsWith("Q") || // 输出信号
                   symbol.Address.StartsWith("I") || // 输入信号
                   (symbol.Address.StartsWith("M") && symbol.IsRetentive); // 重要的内部变量
        }

        /// <summary>
        /// 获取扫描间隔
        /// </summary>
        private int GetScanInterval(SymbolInfo symbol)
        {
            // 根据符号类型设置扫描间隔
            if (symbol.Name.ToLower().Contains("temperature"))
                return 5000; // 温度5秒扫描一次
            else if (symbol.Name.ToLower().Contains("pressure"))
                return 2000; // 压力2秒扫描一次
            else if (symbol.Name.ToLower().Contains("speed"))
                return 1000; // 速度1秒扫描一次
            else if (symbol.Address.StartsWith("I") || symbol.Address.StartsWith("Q"))
                return 500;  // 数字量500ms扫描一次
            else
                return 1000; // 默认1秒
        }

        /// <summary>
        /// 获取符号单位
        /// </summary>
        private string GetSymbolUnit(SymbolInfo symbol)
        {
            var symbolName = symbol.Name.ToLower();
            if (symbolName.Contains("temperature"))
                return "°C";
            else if (symbolName.Contains("pressure"))
                return "Bar";
            else if (symbolName.Contains("speed"))
                return "rpm";
            else if (symbolName.Contains("flow"))
                return "L/min";
            else if (symbolName.Contains("position") || symbolName.Contains("valve"))
                return "%";
            else
                return "";
        }

        /// <summary>
        /// 异步写入所有文本（.NET Framework 4.8.1兼容）
        /// </summary>
        private async Task WriteAllTextAsync(string path, string content)
        {
            await Task.Run(() => File.WriteAllText(path, content, Encoding.UTF8));
        }

        /// <summary>
        /// 异步写入所有行（.NET Framework 4.8.1兼容）
        /// </summary>
        private async Task WriteAllLinesAsync(string path, IEnumerable<string> lines)
        {
            await Task.Run(() => File.WriteAllLines(path, lines, Encoding.UTF8));
        }

        /// <summary>
        /// 异步写入文件（.NET Framework 4.8.1兼容）
        /// </summary>
        private async Task WriteFileAsync(string path, string content)
        {
            await Task.Run(() => File.WriteAllText(path, content, Encoding.UTF8));
        }
    }
} 