using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 程序块解析器基类
    /// </summary>
    public abstract class BaseBlockParser : IBlockParser
    {
        protected readonly Action<string> _logAction;

        protected BaseBlockParser(Action<string> logAction)
        {
            _logAction = logAction ?? (msg => { });
        }

        /// <summary>
        /// 支持的编程语言类型
        /// </summary>
        public abstract string SupportedLanguage { get; }

        /// <summary>
        /// 从XML文件解析程序块逻辑
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <param name="programBlock">要填充的程序块对象</param>
        /// <returns>解析任务</returns>
        public virtual async Task ParseFromXmlAsync(string xmlFilePath, ProgramBlock programBlock)
        {
            try
            {
                if (!File.Exists(xmlFilePath))
                {
                    _logAction($"XML文件不存在: {xmlFilePath}");
                    return;
                }

                _logAction($"开始解析{SupportedLanguage}程序块XML: {programBlock.Name}");

                // 加载XML文档
                var xmlDoc = await LoadXmlDocumentAsync(xmlFilePath);
                if (xmlDoc == null)
                {
                    _logAction($"加载XML文档失败: {xmlFilePath}");
                    return;
                }

                // 执行具体的解析逻辑
                await ParseXmlContentAsync(xmlDoc, programBlock);

                _logAction($"{SupportedLanguage}程序块解析完成: {programBlock.Name}");
            }
            catch (Exception ex)
            {
                _logAction($"解析{SupportedLanguage}程序块失败: {programBlock.Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// 异步加载XML文档
        /// </summary>
        /// <param name="xmlFilePath">XML文件路径</param>
        /// <returns>XML文档</returns>
        protected virtual async Task<XDocument> LoadXmlDocumentAsync(string xmlFilePath)
        {
            try
            {
                using (var fileStream = new FileStream(xmlFilePath, FileMode.Open, FileAccess.Read))
                {
                    return await Task.Run(() => XDocument.Load(fileStream));
                }
            }
            catch (Exception ex)
            {
                _logAction($"加载XML文件失败: {xmlFilePath} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析XML内容的具体实现 - 由子类重写
        /// </summary>
        /// <param name="xmlDoc">XML文档</param>
        /// <param name="programBlock">程序块对象</param>
        /// <returns>解析任务</returns>
        protected abstract Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock);

        /// <summary>
        /// 解析通用的程序块信息
        /// </summary>
        /// <param name="xmlDoc">XML文档</param>
        /// <param name="programBlock">程序块对象</param>
        protected virtual void ParseCommonBlockInfo(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 解析基本信息
                var blockElement = xmlDoc.Root;
                if (blockElement != null)
                {
                    // 提取版本信息
                    var versionAttr = blockElement.Attribute("Version");
                    if (versionAttr != null)
                    {
                        programBlock.Version = versionAttr.Value;
                    }

                    // 提取注释信息
                    var commentElement = blockElement.Element("Comment");
                    if (commentElement != null)
                    {
                        programBlock.Comment = commentElement.Value;
                    }

                    // 提取作者信息
                    var authorElement = blockElement.Element("Author");
                    if (authorElement != null)
                    {
                        programBlock.Author = authorElement.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析通用程序块信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建网络信息对象
        /// </summary>
        /// <param name="networkNumber">网络编号</param>
        /// <param name="title">网络标题</param>
        /// <param name="comment">网络注释</param>
        /// <returns>网络信息对象</returns>
        protected NetworkInfo CreateNetworkInfo(int networkNumber, string title = "", string comment = "")
        {
            return new NetworkInfo
            {
                Number = networkNumber,
                Title = string.IsNullOrEmpty(title) ? $"网络 {networkNumber}" : title,
                Comment = comment,
                Language = SupportedLanguage,
                IsEnabled = true
            };
        }

        /// <summary>
        /// 创建网络元素对象
        /// </summary>
        /// <param name="elementType">元素类型</param>
        /// <param name="elementName">元素名称</param>
        /// <param name="comment">元素注释</param>
        /// <returns>网络元素对象</returns>
        protected NetworkElement CreateNetworkElement(string elementType, string elementName, string comment = "")
        {
            return new NetworkElement
            {
                Id = Guid.NewGuid().ToString(),
                Type = elementType,
                Name = elementName,
                Comment = comment
            };
        }
    }
}
