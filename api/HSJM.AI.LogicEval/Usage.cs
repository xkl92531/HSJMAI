namespace HSJM.AI.LogicEval;

static class Usage
{
    public static readonly string[] Lines =
    [
        "truth-formal --formal-project <id> --archive <zip> --categories <json> --out <truth.json>",
        "evaluate --project <测试库项目id> --archive <zip> --truth <truth.json> --categories <json> --out <report.md> [--only-truth-units]",
        "candidates --project <测试库项目id> --archive <zip> --categories <json> --per-category <n> --out <dir>",
        "render-check --archive <zip> [--archive <zip> …] [--block <块名> …] [--dump]   （离线，不连数据库：按产品路径渲染并截断每个程序段的白话文，附 SCL 翻译统计；--dump 另输出 --block 各程序段的翻译）",
        "evaluate-offline --project <测试库项目id> --archive <zip> --truth <truth.json> --categories <json> --cache <目录> --out <report.md> [--only-truth-units] [--no-ai] [--no-network-ai] [--legacy-cache <旧缓存目录>] [--facts-out <facts.json>] [--catalog-out <报警清单.md>]",
        "diff-facts --a <facts.json> --b <facts.json> --out <report.md> [--a-name <名>] [--b-name <名>] [--title <标题>]",
        CachePush.UsageLine,
        "compare-old-new --project <测试库项目id> --archive <zip> --categories <json> --cache <目录> --out <report.md> [--truth <truth.json>] [--no-ai] [--no-network-ai] [--legacy-cache <旧缓存目录>]   （同时写 <report>.json）",
        "stability --project <测试库项目id> --archive <zip> --categories <json> --work <目录> --out <report.md> [--no-network-ai]   （<work>/run1、run2 须为空；真实调用 AI 两次）",
        "stability --project <测试库项目id> --archive <zip> --categories <json> --no-ai --cache <目录> --out <report.md> [--no-network-ai]   （冒烟：三次读同一已有缓存，不调 AI）",
        "holdout-acceptance --archive <文件名含保留集测试库项目号的 zip，如 holdout-plc2-xmlv2-rerun-4617382048292802560.zip> --truth <truth.json> --categories <json> --cache <目录> --out-dir <目录> --marker <标记文件>   （保留集唯一入口，只能运行一次；名字规则族全开；占标记前先探测测试库与模型，失败不占标记）",
        "保留集守卫：除 holdout-acceptance 与 truth-formal 外，--project/--formal-project 或 --archive 文件名或 --truth 标准答案的项目字段含保留集项目号即拒绝（退出码 2）",
    ];

    public static string Text => "用法：\n" + string.Join("\n", Lines.Select(l => "  " + l));
}
