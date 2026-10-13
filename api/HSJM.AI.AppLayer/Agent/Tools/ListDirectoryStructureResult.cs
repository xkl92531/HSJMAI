using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 目录结构列出结果
    /// </summary>
    public class ListDirectoryStructureResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string RelativeDirectoryPath { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public int MaxDepth { get; set; }
        public string Tree { get; set; } = string.Empty;

        public override string ToString()
        {
            if (!Success)
                return $"❌ 列出失败: {ErrorMessage}";

            return $@"✅ 目录结构
路径: {RelativeDirectoryPath}

{Tree}";
        }
    }
}
