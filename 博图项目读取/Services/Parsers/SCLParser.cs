using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 结构化控制语言(SCL)解析器
    /// </summary>
    public class SCLParser : BaseBlockParser
    {
        public SCLParser(Action<string> logAction) : base(logAction)
        {
        }

        public override string SupportedLanguage => "SCL";

        protected override async Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 解析通用信息
                ParseCommonBlockInfo(xmlDoc, programBlock);

                // 解析SCL代码
                await ParseSCLCodeAsync(xmlDoc, programBlock);

                // 解析接口信息
                ParseInterfaceInfo(xmlDoc, programBlock);

                _logAction($"SCL解析完成: {programBlock.Networks.Count} 个网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL XML内容失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析SCL代码
        /// </summary>
        private async Task ParseSCLCodeAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 查找SCL代码段
                var codeElements = xmlDoc.Descendants("Code");
                var sourceElements = xmlDoc.Descendants("Source");
                var networkElements = xmlDoc.Descendants("Network");

                if (networkElements.Any())
                {
                    // 按网络解析
                    foreach (var networkElement in networkElements)
                    {
                        var networkInfo = await ParseSCLNetworkAsync(networkElement);
                        if (networkInfo != null)
                        {
                            programBlock.Networks.Add(networkInfo);
                        }
                    }
                }
                else if (codeElements.Any() || sourceElements.Any())
                {
                    // 解析单一代码块
                    var networkInfo = await ParseSCLSourceCodeAsync(xmlDoc);
                    if (networkInfo != null)
                    {
                        programBlock.Networks.Add(networkInfo);
                    }
                }
                else
                {
                    // 尝试解析其他可能的结构
                    await ParseAlternativeSCLStructureAsync(xmlDoc, programBlock);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL代码失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析SCL网络
        /// </summary>
        private async Task<NetworkInfo> ParseSCLNetworkAsync(XElement networkElement)
        {
            try
            {
                // 获取网络编号
                var networkNumber = 1;
                var numberAttr = networkElement.Attribute("Number") ?? networkElement.Attribute("UId");
                if (numberAttr != null && int.TryParse(numberAttr.Value, out var num))
                {
                    networkNumber = num;
                }

                // 获取网络标题和注释
                var title = networkElement.Element("Title")?.Value ?? $"网络 {networkNumber}";
                var comment = networkElement.Element("Comment")?.Value ?? "";

                var networkInfo = CreateNetworkInfo(networkNumber, title, comment);

                // 解析网络中的SCL代码
                var codeText = networkElement.Element("Code")?.Value ?? 
                              networkElement.Element("Source")?.Value ?? 
                              networkElement.Value;

                if (!string.IsNullOrEmpty(codeText))
                {
                    ParseSCLCodeText(codeText, networkInfo);
                }

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL网络失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析SCL源代码
        /// </summary>
        private async Task<NetworkInfo> ParseSCLSourceCodeAsync(XDocument xmlDoc)
        {
            try
            {
                var networkInfo = CreateNetworkInfo(1, "SCL代码", "结构化控制语言代码");

                // 解析所有代码元素
                var codeElements = xmlDoc.Descendants("Code");
                var sourceElements = xmlDoc.Descendants("Source");

                foreach (var codeElement in codeElements.Concat(sourceElements))
                {
                    var codeText = codeElement.Value;
                    if (!string.IsNullOrEmpty(codeText))
                    {
                        ParseSCLCodeText(codeText, networkInfo);
                    }
                }

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL源代码失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析SCL代码文本
        /// </summary>
        private void ParseSCLCodeText(string codeText, NetworkInfo networkInfo)
        {
            try
            {
                // 分析SCL代码结构
                var codeBlocks = AnalyzeSCLStructure(codeText);
                
                foreach (var block in codeBlocks)
                {
                    var element = CreateNetworkElement("SCL_Block", block.Name, block.Description);
                    element.Properties["BlockType"] = block.Type;
                    element.Properties["Code"] = block.Code;
                    element.Properties["StartLine"] = block.StartLine.ToString();
                    element.Properties["EndLine"] = block.EndLine.ToString();
                    
                    networkInfo.Elements.Add(element);
                }

                // 如果没有识别出结构化块，将整个代码作为一个元素
                if (!networkInfo.Elements.Any())
                {
                    var element = CreateNetworkElement("SCL_Code", "SCL代码块", "完整的SCL代码");
                    element.Properties["Code"] = codeText;
                    element.Properties["BlockType"] = "Complete";
                    networkInfo.Elements.Add(element);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL代码文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 分析SCL代码结构
        /// </summary>
        private SCLCodeBlock[] AnalyzeSCLStructure(string codeText)
        {
            var blocks = new List<SCLCodeBlock>();
            var lines = codeText.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            
            try
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    
                    // 识别IF语句块
                    if (line.StartsWith("IF", StringComparison.OrdinalIgnoreCase))
                    {
                        var block = ExtractIfBlock(lines, i);
                        if (block != null)
                        {
                            blocks.Add(block);
                            i = block.EndLine - 1; // 跳过已处理的行
                        }
                    }
                    // 识别FOR循环块
                    else if (line.StartsWith("FOR", StringComparison.OrdinalIgnoreCase))
                    {
                        var block = ExtractForBlock(lines, i);
                        if (block != null)
                        {
                            blocks.Add(block);
                            i = block.EndLine - 1;
                        }
                    }
                    // 识别WHILE循环块
                    else if (line.StartsWith("WHILE", StringComparison.OrdinalIgnoreCase))
                    {
                        var block = ExtractWhileBlock(lines, i);
                        if (block != null)
                        {
                            blocks.Add(block);
                            i = block.EndLine - 1;
                        }
                    }
                    // 识别CASE语句块
                    else if (line.StartsWith("CASE", StringComparison.OrdinalIgnoreCase))
                    {
                        var block = ExtractCaseBlock(lines, i);
                        if (block != null)
                        {
                            blocks.Add(block);
                            i = block.EndLine - 1;
                        }
                    }
                    // 识别赋值语句
                    else if (line.Contains(":=") && !string.IsNullOrEmpty(line))
                    {
                        var block = new SCLCodeBlock
                        {
                            Type = "Assignment",
                            Name = "赋值语句",
                            Description = $"赋值: {line}",
                            Code = line,
                            StartLine = i + 1,
                            EndLine = i + 1
                        };
                        blocks.Add(block);
                    }
                    // 识别函数调用
                    else if (line.Contains("(") && line.Contains(")") && !string.IsNullOrEmpty(line) && !line.StartsWith("//"))
                    {
                        var block = new SCLCodeBlock
                        {
                            Type = "FunctionCall",
                            Name = "函数调用",
                            Description = $"函数调用: {line}",
                            Code = line,
                            StartLine = i + 1,
                            EndLine = i + 1
                        };
                        blocks.Add(block);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"分析SCL结构失败: {ex.Message}");
            }
            
            return blocks.ToArray();
        }

        /// <summary>
        /// 提取IF语句块
        /// </summary>
        private SCLCodeBlock ExtractIfBlock(string[] lines, int startIndex)
        {
            try
            {
                var endIndex = FindBlockEnd(lines, startIndex, "IF", "END_IF");
                if (endIndex > startIndex)
                {
                    var code = string.Join("\n", lines.Skip(startIndex).Take(endIndex - startIndex + 1));
                    return new SCLCodeBlock
                    {
                        Type = "IfStatement",
                        Name = "IF语句",
                        Description = $"条件判断: {lines[startIndex].Trim()}",
                        Code = code,
                        StartLine = startIndex + 1,
                        EndLine = endIndex + 1
                    };
                }
            }
            catch (Exception ex)
            {
                _logAction($"提取IF块失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 提取FOR循环块
        /// </summary>
        private SCLCodeBlock ExtractForBlock(string[] lines, int startIndex)
        {
            try
            {
                var endIndex = FindBlockEnd(lines, startIndex, "FOR", "END_FOR");
                if (endIndex > startIndex)
                {
                    var code = string.Join("\n", lines.Skip(startIndex).Take(endIndex - startIndex + 1));
                    return new SCLCodeBlock
                    {
                        Type = "ForLoop",
                        Name = "FOR循环",
                        Description = $"循环: {lines[startIndex].Trim()}",
                        Code = code,
                        StartLine = startIndex + 1,
                        EndLine = endIndex + 1
                    };
                }
            }
            catch (Exception ex)
            {
                _logAction($"提取FOR块失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 提取WHILE循环块
        /// </summary>
        private SCLCodeBlock ExtractWhileBlock(string[] lines, int startIndex)
        {
            try
            {
                var endIndex = FindBlockEnd(lines, startIndex, "WHILE", "END_WHILE");
                if (endIndex > startIndex)
                {
                    var code = string.Join("\n", lines.Skip(startIndex).Take(endIndex - startIndex + 1));
                    return new SCLCodeBlock
                    {
                        Type = "WhileLoop",
                        Name = "WHILE循环",
                        Description = $"循环: {lines[startIndex].Trim()}",
                        Code = code,
                        StartLine = startIndex + 1,
                        EndLine = endIndex + 1
                    };
                }
            }
            catch (Exception ex)
            {
                _logAction($"提取WHILE块失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 提取CASE语句块
        /// </summary>
        private SCLCodeBlock ExtractCaseBlock(string[] lines, int startIndex)
        {
            try
            {
                var endIndex = FindBlockEnd(lines, startIndex, "CASE", "END_CASE");
                if (endIndex > startIndex)
                {
                    var code = string.Join("\n", lines.Skip(startIndex).Take(endIndex - startIndex + 1));
                    return new SCLCodeBlock
                    {
                        Type = "CaseStatement",
                        Name = "CASE语句",
                        Description = $"选择: {lines[startIndex].Trim()}",
                        Code = code,
                        StartLine = startIndex + 1,
                        EndLine = endIndex + 1
                    };
                }
            }
            catch (Exception ex)
            {
                _logAction($"提取CASE块失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 查找代码块结束位置
        /// </summary>
        private int FindBlockEnd(string[] lines, int startIndex, string startKeyword, string endKeyword)
        {
            int nestLevel = 0;
            for (int i = startIndex; i < lines.Length; i++)
            {
                var line = lines[i].Trim().ToUpper();
                
                if (line.StartsWith(startKeyword))
                    nestLevel++;
                else if (line.StartsWith(endKeyword))
                {
                    nestLevel--;
                    if (nestLevel == 0)
                        return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 解析备用SCL结构
        /// </summary>
        private async Task ParseAlternativeSCLStructureAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 如果没有找到标准结构，创建一个默认网络
                var networkInfo = CreateNetworkInfo(1, "SCL代码", "从XML解析的结构化控制语言代码");
                
                // 尝试解析根级别的文本内容
                var rootText = xmlDoc.Root?.Value;
                if (!string.IsNullOrEmpty(rootText))
                {
                    ParseSCLCodeText(rootText, networkInfo);
                }
                
                // 如果还是没有内容，添加一个占位符
                if (!networkInfo.Elements.Any())
                {
                    var element = CreateNetworkElement("SCL_Placeholder", "无法解析的SCL内容", "XML结构不符合预期格式");
                    networkInfo.Elements.Add(element);
                }
                
                programBlock.Networks.Add(networkInfo);
                _logAction("使用备用解析策略创建了默认SCL网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析备用SCL结构失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析接口信息
        /// </summary>
        private void ParseInterfaceInfo(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                var interfaceElement = xmlDoc.Descendants("Interface").FirstOrDefault();
                if (interfaceElement != null)
                {
                    // 解析输入参数
                    var inputs = interfaceElement.Descendants("Input");
                    foreach (var input in inputs)
                    {
                        var param = ParseParameter(input, "Input");
                        if (param != null)
                        {
                            programBlock.Interface.Input.Add(param);
                        }
                    }

                    // 解析输出参数
                    var outputs = interfaceElement.Descendants("Output");
                    foreach (var output in outputs)
                    {
                        var param = ParseParameter(output, "Output");
                        if (param != null)
                        {
                            programBlock.Interface.Output.Add(param);
                        }
                    }

                    // 解析静态变量
                    var statics = interfaceElement.Descendants("Static");
                    foreach (var staticVar in statics)
                    {
                        var variable = ParseVariable(staticVar);
                        if (variable != null)
                        {
                            programBlock.Interface.Static.Add(variable);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL接口信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析参数
        /// </summary>
        private BlockParameter ParseParameter(XElement paramElement, string direction)
        {
            try
            {
                var name = paramElement.Attribute("Name")?.Value ?? paramElement.Element("Name")?.Value;
                var dataType = paramElement.Attribute("DataType")?.Value ?? paramElement.Element("DataType")?.Value;
                var comment = paramElement.Element("Comment")?.Value ?? "";

                if (string.IsNullOrEmpty(name))
                    return null;

                return new BlockParameter
                {
                    Name = name,
                    DataType = dataType ?? "Unknown",
                    Direction = direction,
                    Comment = comment
                };
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL参数失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析静态变量
        /// </summary>
        private BlockVariable ParseVariable(XElement varElement)
        {
            try
            {
                var name = varElement.Attribute("Name")?.Value ?? varElement.Element("Name")?.Value;
                var dataType = varElement.Attribute("DataType")?.Value ?? varElement.Element("DataType")?.Value;
                var comment = varElement.Element("Comment")?.Value ?? "";
                var initialValue = varElement.Element("InitialValue")?.Value ?? varElement.Attribute("InitialValue")?.Value;

                if (string.IsNullOrEmpty(name))
                    return null;

                return new BlockVariable
                {
                    Name = name,
                    DataType = dataType ?? "Unknown",
                    Comment = comment,
                    InitialValue = initialValue
                };
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL静态变量失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// SCL代码块结构
        /// </summary>
        private class SCLCodeBlock
        {
            public string Type { get; set; }
            public string Name { get; set; }
            public string Description { get; set; }
            public string Code { get; set; }
            public int StartLine { get; set; }
            public int EndLine { get; set; }
        }
    }
}
