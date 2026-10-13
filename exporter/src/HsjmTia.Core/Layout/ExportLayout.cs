using System.IO;
using HsjmTia.Core.Model;
using HsjmTia.Core.Naming;

namespace HsjmTia.Core.Layout
{
    /// <summary>
    /// 导出目录布局。PLC 层无条件存在（单 PLC 工程也加），
    /// 让下游打包与清理逻辑不必分支判断。
    /// </summary>
    public class ExportLayout
    {
        public string OutputRoot { get; private set; }
        public string TiaProjectDir { get; private set; }
        public string PlcDir { get; private set; }
        public string BlocksDir { get; private set; }
        public string TagsDir { get; private set; }
        public string UdtDir { get; private set; }
        public string AlarmsDir { get; private set; }
        public string HardwareDir { get; private set; }
        public string TechObjectsDir { get; private set; }

        public string ManifestPath { get; private set; }
        public string TagsCsvPath { get; private set; }
        public string BlockPathTxtPath { get; private set; }
        public string UdtIndexPath { get; private set; }

        public static ExportLayout For(string outputRoot, string tiaProjectName, PlcIdentity plc)
        {
            var projectDir = Path.Combine(outputRoot, FileNameSanitizer.Sanitize(tiaProjectName));
            var plcDir = Path.Combine(projectDir, PlcNamer.BuildFolderName(plc));

            var layout = new ExportLayout
            {
                OutputRoot = outputRoot,
                TiaProjectDir = projectDir,
                PlcDir = plcDir,
                BlocksDir = Path.Combine(plcDir, "blocks"),
                TagsDir = Path.Combine(plcDir, "plcTags"),
                UdtDir = Path.Combine(plcDir, "udt"),
                AlarmsDir = Path.Combine(plcDir, "alarms"),
                HardwareDir = Path.Combine(plcDir, "hardware"),
                TechObjectsDir = Path.Combine(plcDir, "tech_objects"),
            };

            layout.ManifestPath = Path.Combine(plcDir, "manifest.json");
            layout.TagsCsvPath = Path.Combine(layout.TagsDir, "tags.csv");
            layout.BlockPathTxtPath = Path.Combine(layout.BlocksDir, "block_path.txt");
            layout.UdtIndexPath = Path.Combine(layout.UdtDir, "udt_index.json");
            return layout;
        }
    }
}
