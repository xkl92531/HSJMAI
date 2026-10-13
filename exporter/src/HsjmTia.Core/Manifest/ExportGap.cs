namespace HsjmTia.Core.Manifest
{
    /// <summary>缺口种类。导出端任何"跳过 / 失败 / 能力缺失"都必须归到其中一类。</summary>
    public static class GapKinds
    {
        public const string KnowHowProtected = "KnowHowProtected";
        public const string ExportFailed = "ExportFailed";

        /// <summary>
        /// 块本次没有进 zip（知产保护跳过，或块 XML 导出本身抛异常）——这是 FullUpdateSafety
        /// 唯一需要拦截 --full 的缺口种类。不要与 ExportFailed 混用：ExportFailed 同时也被
        /// "块已经导出成功、只是源码 .scl/.awl 导出失败"、Tag 的 Retentive 属性无法识别、
        /// UDT 导出失败/重名 这四种"块（或压根不是块）仍然在 zip 里"的场景复用。
        /// </summary>
        public const string BlockMissing = "BlockMissing";
        public const string NameSanitized = "NameSanitized";
        public const string CapabilityMissing = "CapabilityMissing";
        public const string PlcIpMissing = "PlcIpMissing";
        public const string AnalyzeFailed = "AnalyzeFailed";
    }

    public class ExportGap
    {
        public string Kind { get; set; }
        public string Target { get; set; }
        public string Detail { get; set; }

        public static ExportGap Create(string kind, string target, string detail)
            => new ExportGap { Kind = kind, Target = target, Detail = detail };
    }
}
