using System.Threading.Tasks;
using HSJM.AI.Utility;

namespace HSJM.AI.Mcp
{
    /// <summary>
    /// 将 MCP 调用 JSON 日志写入现有 Serilog 分类目录。
    /// </summary>
    public sealed class McpInvocationFileLogSink : IMcpInvocationLogSink
    {
        private const string LogDirectory = "MCP调用";

        /// <inheritdoc />
        public Task WriteAsync(string line) => WriteLogHelper.WriteLogsAsync(line, LogDirectory);
    }
}
