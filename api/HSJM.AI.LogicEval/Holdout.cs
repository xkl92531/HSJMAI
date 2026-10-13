using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>保留集（测试线 PLC2，spec §13.1）：整个计划只在最终验收运行一次，且只能经 holdout-acceptance。
/// 其它命令一律拒绝：按项目号（测试库与正式库各一个）与归档文件名（含测试库项目号，如 holdout-plc2-xmlv2-rerun-&lt;测试库项目号&gt;.zip）判定，在读取任何文件之前；
/// 标准答案按项目字段判定，在反序列化后、产出任何输出之前。truth-formal 例外（只从正式库导出旧规则作标注底稿，不运行本期生成）。</summary>
static class Holdout
{
    public const long TestProject = 4617382048292802560;
    public const long FormalProject = 4541738602710831104;

    public static bool IsHoldoutProject(long projectId) => projectId is TestProject or FormalProject;

    /// <summary>归档文件名含保留集项目号（测试库或正式库）即视为保留集归档。</summary>
    public static bool IsHoldoutArchive(string path)
    {
        var name = Path.GetFileName(path);
        return ContainsHoldoutId(name);
    }

    /// <summary>标准答案的项目字段（TruthFile.Project，truth-formal 写成 formal:&lt;正式库项目号&gt;）含保留集项目号即视为保留集标准答案。</summary>
    public static bool IsHoldoutTruth(TruthFile truth) => ContainsHoldoutId(truth.Project ?? "");

    /// <summary>非 holdout-acceptance 命令读标准答案的唯一入口：反序列化后、产出任何输出之前，保留集标准答案一律拒绝。</summary>
    public static async Task<TruthFile> ReadTruthAsync(string path)
    {
        var truth = JsonSerializer.Deserialize<TruthFile>(await File.ReadAllTextAsync(path), TruthJson.Options)!;
        if (IsHoldoutTruth(truth)) throw new HoldoutRefusedException();
        return truth;
    }

    static bool ContainsHoldoutId(string text) =>
        text.Contains(TestProject.ToString(), StringComparison.Ordinal) || text.Contains(FormalProject.ToString(), StringComparison.Ordinal);
}

/// <summary>其它命令碰到保留集（项目号、归档文件名或标准答案项目字段）：Program 统一输出 OfflinePipeline.HoldoutRefusal、退出码 2。</summary>
sealed class HoldoutRefusedException() : Exception(OfflinePipeline.HoldoutRefusal);
