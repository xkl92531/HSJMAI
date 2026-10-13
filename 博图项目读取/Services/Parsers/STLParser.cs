using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 语句表(STL)解析器
    /// </summary>
    public class STLParser : BaseBlockParser
    {
        public STLParser(Action<string> logAction) : base(logAction)
        {
        }

        public override string SupportedLanguage => "STL";

        protected override async Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 解析通用信息
                ParseCommonBlockInfo(xmlDoc, programBlock);

                // 解析STL代码
                await ParseSTLCodeAsync(xmlDoc, programBlock);

                // 解析接口信息
                ParseInterfaceInfo(xmlDoc, programBlock);

                _logAction($"STL解析完成: {programBlock.Networks.Count} 个网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析STL XML内容失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析STL代码
        /// </summary>
        private async Task ParseSTLCodeAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 查找STL代码段
                var codeElements = xmlDoc.Descendants("Code");
                var instructionElements = xmlDoc.Descendants("Instruction");
                var networkElements = xmlDoc.Descendants("Network");

                if (networkElements.Any())
                {
                    // 按网络解析
                    foreach (var networkElement in networkElements)
                    {
                        var networkInfo = await ParseSTLNetworkAsync(networkElement);
                        if (networkInfo != null)
                        {
                            programBlock.Networks.Add(networkInfo);
                        }
                    }
                }
                else if (codeElements.Any() || instructionElements.Any())
                {
                    // 解析单一代码块
                    var networkInfo = await ParseSTLInstructionsAsync(xmlDoc);
                    if (networkInfo != null)
                    {
                        programBlock.Networks.Add(networkInfo);
                    }
                }
                else
                {
                    // 尝试解析其他可能的结构
                    await ParseAlternativeSTLStructureAsync(xmlDoc, programBlock);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析STL代码失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析STL网络
        /// </summary>
        private async Task<NetworkInfo> ParseSTLNetworkAsync(XElement networkElement)
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

                // 解析网络中的STL指令
                var instructions = networkElement.Descendants("Instruction");
                foreach (var instruction in instructions)
                {
                    var element = ParseSTLInstructionElement(instruction);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 如果没有找到指令，尝试解析代码文本
                if (!networkInfo.Elements.Any())
                {
                    var codeText = networkElement.Element("Code")?.Value ?? networkElement.Value;
                    if (!string.IsNullOrEmpty(codeText))
                    {
                        ParseSTLCodeText(codeText, networkInfo);
                    }
                }

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析STL网络失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析STL指令集合
        /// </summary>
        private async Task<NetworkInfo> ParseSTLInstructionsAsync(XDocument xmlDoc)
        {
            try
            {
                var networkInfo = CreateNetworkInfo(1, "STL代码", "语句表指令");

                // 解析所有指令元素
                var instructions = xmlDoc.Descendants("Instruction");
                foreach (var instruction in instructions)
                {
                    var element = ParseSTLInstructionElement(instruction);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析代码文本
                var codeElements = xmlDoc.Descendants("Code");
                foreach (var codeElement in codeElements)
                {
                    var codeText = codeElement.Value;
                    if (!string.IsNullOrEmpty(codeText))
                    {
                        ParseSTLCodeText(codeText, networkInfo);
                    }
                }

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析STL指令集合失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析STL指令元素
        /// </summary>
        private NetworkElement ParseSTLInstructionElement(XElement instructionElement)
        {
            try
            {
                var mnemonic = instructionElement.Attribute("Mnemonic")?.Value ?? 
                              instructionElement.Element("Mnemonic")?.Value ?? "未知指令";
                var operand = instructionElement.Attribute("Operand")?.Value ?? 
                             instructionElement.Element("Operand")?.Value ?? "";
                var comment = instructionElement.Element("Comment")?.Value ?? "";

                var instructionText = string.IsNullOrEmpty(operand) ? mnemonic : $"{mnemonic} {operand}";
                
                var element = CreateNetworkElement("STL_Instruction", instructionText, comment);
                element.Properties["Mnemonic"] = mnemonic;
                element.Properties["Operand"] = operand;
                element.Properties["InstructionText"] = instructionText;
                
                // 解析指令类型
                var instructionType = GetSTLInstructionType(mnemonic);
                element.Properties["InstructionType"] = instructionType;
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析STL指令元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析STL代码文本
        /// </summary>
        private void ParseSTLCodeText(string codeText, NetworkInfo networkInfo)
        {
            try
            {
                var lines = codeText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
                        continue;

                    // 解析STL指令行
                    var parts = trimmedLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        var mnemonic = parts[0];
                        var operand = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
                        
                        var element = CreateNetworkElement("STL_Instruction", trimmedLine, $"STL指令: {trimmedLine}");
                        element.Properties["Mnemonic"] = mnemonic;
                        element.Properties["Operand"] = operand;
                        element.Properties["InstructionText"] = trimmedLine;
                        element.Properties["InstructionType"] = GetSTLInstructionType(mnemonic);
                        
                        networkInfo.Elements.Add(element);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析STL代码文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取STL指令类型
        /// </summary>
        private string GetSTLInstructionType(string mnemonic)
        {
            if (string.IsNullOrEmpty(mnemonic))
                return "Unknown";

            var upperMnemonic = mnemonic.ToUpper();
            
            // 位逻辑指令
            if (new[] { "A", "AN", "O", "ON", "X", "XN", "=", "S", "R" }.Contains(upperMnemonic))
                return "BitLogic";
            
            // 加载指令
            if (new[] { "L", "LC", "LAR1", "LAR2" }.Contains(upperMnemonic))
                return "Load";
            
            // 传送指令
            if (new[] { "T", "TAR1", "TAR2" }.Contains(upperMnemonic))
                return "Transfer";
            
            // 比较指令
            if (new[] { "==I", "==D", "==R", "<>I", "<>D", "<>R", "<I", "<D", "<R", "<=I", "<=D", "<=R", ">I", ">D", ">R", ">=I", ">=D", ">=R" }.Contains(upperMnemonic))
                return "Compare";
            
            // 算术指令
            if (new[] { "+I", "+D", "+R", "-I", "-D", "-R", "*I", "*D", "*R", "/I", "/D", "/R" }.Contains(upperMnemonic))
                return "Arithmetic";
            
            // 跳转指令
            if (new[] { "JU", "JC", "JCN", "JCB", "JNB", "JBI", "JNBI", "JO", "JOS", "JZ", "JN", "JP", "JM", "JPZ", "JMZ", "JUO" }.Contains(upperMnemonic))
                return "Jump";
            
            // 调用指令
            if (new[] { "CALL", "CC", "UC" }.Contains(upperMnemonic))
                return "Call";
            
            // 定时器指令
            if (new[] { "FR", "L", "LC", "R", "S", "SE", "SI", "SS", "SF" }.Contains(upperMnemonic) && mnemonic.Contains("T"))
                return "Timer";
            
            // 计数器指令
            if (new[] { "FR", "L", "LC", "R", "S", "CU", "CD" }.Contains(upperMnemonic) && mnemonic.Contains("C"))
                return "Counter";
            
            return "Other";
        }

        /// <summary>
        /// 解析备用STL结构
        /// </summary>
        private async Task ParseAlternativeSTLStructureAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 如果没有找到标准结构，创建一个默认网络
                var networkInfo = CreateNetworkInfo(1, "STL代码", "从XML解析的语句表代码");
                
                // 尝试解析根级别的文本内容
                var rootText = xmlDoc.Root?.Value;
                if (!string.IsNullOrEmpty(rootText))
                {
                    ParseSTLCodeText(rootText, networkInfo);
                }
                
                // 如果还是没有内容，添加一个占位符
                if (!networkInfo.Elements.Any())
                {
                    var element = CreateNetworkElement("STL_Placeholder", "无法解析的STL内容", "XML结构不符合预期格式");
                    networkInfo.Elements.Add(element);
                }
                
                programBlock.Networks.Add(networkInfo);
                _logAction("使用备用解析策略创建了默认STL网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析备用STL结构失败: {ex.Message}");
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
                _logAction($"解析STL接口信息失败: {ex.Message}");
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
                _logAction($"解析STL参数失败: {ex.Message}");
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
                _logAction($"解析STL静态变量失败: {ex.Message}");
                return null;
            }
        }
    }
}
