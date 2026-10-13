using System;

#if TIA_PORTAL_AVAILABLE
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
#endif

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 程序块解析器工厂
    /// </summary>
    public static class BlockParserFactory
    {
        /// <summary>
        /// 根据编程语言创建对应的解析器
        /// </summary>
        /// <param name="language">编程语言</param>
        /// <param name="logAction">日志记录委托</param>
        /// <returns>对应的解析器实例</returns>
#if TIA_PORTAL_AVAILABLE
        public static IBlockParser CreateParser(ProgrammingLanguage language, Action<string> logAction)
        {
            switch (language)
            {
                case ProgrammingLanguage.LAD:
                    return new LadderParser(logAction);
                case ProgrammingLanguage.FBD:
                    return new FBDParser(logAction);
                case ProgrammingLanguage.STL:
                    return new STLParser(logAction);
                case ProgrammingLanguage.SCL:
                    return new SCLParser(logAction);
                case ProgrammingLanguage.GRAPH:
                    return new GraphParser(logAction);
                default:
                    return null;
            }
        }
#else
        public static IBlockParser CreateParser(string language, Action<string> logAction)
        {
            switch (language?.ToUpper())
            {
                case "LAD":
                    return new LadderParser(logAction);
                case "FBD":
                    return new FBDParser(logAction);
                case "STL":
                    return new STLParser(logAction);
                case "SCL":
                    return new SCLParser(logAction);
                case "GRAPH":
                    return new GraphParser(logAction);
                default:
                    return null;
            }
        }
#endif
    }
}
