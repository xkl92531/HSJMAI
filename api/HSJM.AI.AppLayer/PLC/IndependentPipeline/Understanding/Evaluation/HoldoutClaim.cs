#nullable enable
using System.IO;
using System.Text;

namespace HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

/// <summary>spec §13.1：保留集只在最终验收运行一次。原子创建标记文件（FileMode.CreateNew），已存在即拒绝（R5）。</summary>
public static class HoldoutClaim
{
    public static bool TryClaim(string markerPath, string note)
    {
        var full = Path.GetFullPath(markerPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = Encoding.UTF8.GetBytes(note);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (IOException) when (File.Exists(full))
        {
            return false;
        }
    }
}
