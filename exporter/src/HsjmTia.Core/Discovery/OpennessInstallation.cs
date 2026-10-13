namespace HsjmTia.Core.Discovery
{
    public class OpennessInstallation
    {
        /// <summary>注册表里的 TIA 版本键名，如 "16.0"。</summary>
        public string TiaVersion { get; set; }
        /// <summary>PublicAPI 子键名，如 "V16"。这是对外展示与匹配用的版本号。</summary>
        public string ApiVersion { get; set; }
        public string AssemblyPath { get; set; }
    }
}
