namespace HSJM.AI.LogicEval;

static class CliArgs
{
    public static string Req(string[] args, string name) => Opt(args, name) ?? throw new ArgumentException("缺少参数 " + name);
    public static string? Opt(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
    public static bool Flag(string[] args, string name) => args.Contains(name);
}
