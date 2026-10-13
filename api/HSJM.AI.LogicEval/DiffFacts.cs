using System.Text.Json;
using HSJM.AI.AppLayer.PLC.IndependentPipeline.Understanding;

namespace HSJM.AI.LogicEval;

/// <summary>diff-facts：两份 --facts-out 输出按「约束类型 + 信号」比对。</summary>
static class DiffFacts
{
    public static async Task<int> RunAsync(string[] args)
    {
        var a = Load(CliArgs.Req(args, "--a"));
        var b = Load(CliArgs.Req(args, "--b"));
        var diff = RunComparison.Diff(a, b);
        await File.WriteAllTextAsync(CliArgs.Req(args, "--out"), RunComparison.Report(diff, CliArgs.Opt(args, "--title") ?? "事实对比",
            CliArgs.Opt(args, "--a-name") ?? "A", CliArgs.Opt(args, "--b-name") ?? "B"));
        Console.WriteLine($"只在 A {diff.OnlyA.Count}，只在 B {diff.OnlyB.Count}，两者同 {diff.Both.Count}（前提不同 {diff.PreconditionsDiffer.Count}）");
        return 0;
    }

    static IReadOnlyList<EvalFact> Load(string path) =>
        JsonSerializer.Deserialize<List<EvalFact>>(File.ReadAllText(path), TruthJson.Options) ?? [];
}
