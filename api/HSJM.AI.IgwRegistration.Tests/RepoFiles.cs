using System;
using System.IO;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>测试共用：从测试输出目录向上查找仓库根下的文件（迁移脚本等已 git add -f 纳入版本控制，任意 checkout 都应存在）。</summary>
internal static class RepoFiles
{
    private const int MaxLevels = 10;

    /// <summary>读取仓库根相对路径的文本文件；找不到抛 <see cref="FileNotFoundException"/>。</summary>
    public static string ReadText(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < MaxLevels && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 {relativePath}（从 {AppContext.BaseDirectory} 向上查找了 {MaxLevels} 层）");
    }
}
