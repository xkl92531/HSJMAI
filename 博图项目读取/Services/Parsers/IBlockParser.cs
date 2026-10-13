using System.Threading.Tasks;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 程序块解析器接口
    /// </summary>
    public interface IBlockParser
    {
        /// <summary>
        /// 从XML文件解析程序块逻辑
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <param name="programBlock">要填充的程序块对象</param>
        /// <returns>解析任务</returns>
        Task ParseFromXmlAsync(string xmlFilePath, ProgramBlock programBlock);

        /// <summary>
        /// 支持的编程语言类型
        /// </summary>
        string SupportedLanguage { get; }
    }
}
