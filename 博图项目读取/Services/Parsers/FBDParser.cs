using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 功能块图(FBD)解析器
    /// </summary>
    public class FBDParser : BaseBlockParser
    {
        public FBDParser(Action<string> logAction) : base(logAction)
        {
        }

        public override string SupportedLanguage => "FBD";

        protected override async Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 解析通用信息
                ParseCommonBlockInfo(xmlDoc, programBlock);

                // 解析功能块图网络
                await ParseFBDNetworksAsync(xmlDoc, programBlock);

                // 解析接口信息
                ParseInterfaceInfo(xmlDoc, programBlock);

                _logAction($"功能块图解析完成: {programBlock.Networks.Count} 个网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析功能块图XML内容失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析功能块图网络
        /// </summary>
        private async Task ParseFBDNetworksAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 查找所有网络元素
                var networkElements = xmlDoc.Descendants("Network");
                
                foreach (var networkElement in networkElements)
                {
                    var networkInfo = await ParseSingleNetworkAsync(networkElement);
                    if (networkInfo != null)
                    {
                        programBlock.Networks.Add(networkInfo);
                    }
                }

                if (!programBlock.Networks.Any())
                {
                    // 如果没有找到标准的网络结构，尝试解析其他可能的结构
                    await ParseAlternativeNetworkStructureAsync(xmlDoc, programBlock);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析功能块图网络失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个网络
        /// </summary>
        private async Task<NetworkInfo> ParseSingleNetworkAsync(XElement networkElement)
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

                // 解析网络中的功能块图元素
                await ParseFBDElementsAsync(networkElement, networkInfo);

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析单个FBD网络失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析功能块图元素
        /// </summary>
        private async Task ParseFBDElementsAsync(XElement networkElement, NetworkInfo networkInfo)
        {
            try
            {
                // 解析功能块元素
                var functionBlocks = networkElement.Descendants("FunctionBlock");
                foreach (var fb in functionBlocks)
                {
                    var element = ParseFunctionBlockElement(fb);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析逻辑门元素
                var logicGates = networkElement.Descendants("LogicGate");
                foreach (var gate in logicGates)
                {
                    var element = ParseLogicGateElement(gate);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析比较器元素
                var comparators = networkElement.Descendants("Comparator");
                foreach (var comp in comparators)
                {
                    var element = ParseComparatorElement(comp);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析算术运算元素
                var arithmetic = networkElement.Descendants("Arithmetic");
                foreach (var arith in arithmetic)
                {
                    var element = ParseArithmeticElement(arith);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析连接线
                var connections = networkElement.Descendants("Connection");
                foreach (var conn in connections)
                {
                    var element = ParseConnectionElement(conn);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                _logAction($"FBD网络 {networkInfo.Number} 解析到 {networkInfo.Elements.Count} 个元素");
            }
            catch (Exception ex)
            {
                _logAction($"解析功能块图元素失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析功能块元素
        /// </summary>
        private NetworkElement ParseFunctionBlockElement(XElement fbElement)
        {
            try
            {
                var blockName = fbElement.Attribute("Name")?.Value ?? 
                               fbElement.Element("Name")?.Value ?? "未知功能块";
                var blockType = fbElement.Attribute("Type")?.Value ?? 
                               fbElement.Element("Type")?.Value ?? "FB";
                
                var element = CreateNetworkElement("FunctionBlock", blockName, $"功能块: {blockType} {blockName}");
                element.Properties["BlockType"] = blockType;
                element.Properties["BlockName"] = blockName;
                
                // 解析输入输出引脚
                var inputs = fbElement.Descendants("Input");
                foreach (var input in inputs)
                {
                    var pinName = input.Attribute("Name")?.Value;
                    var pinValue = input.Value;
                    if (!string.IsNullOrEmpty(pinName))
                    {
                        element.Properties[$"Input_{pinName}"] = pinValue;
                    }
                }

                var outputs = fbElement.Descendants("Output");
                foreach (var output in outputs)
                {
                    var pinName = output.Attribute("Name")?.Value;
                    var pinValue = output.Value;
                    if (!string.IsNullOrEmpty(pinName))
                    {
                        element.Properties[$"Output_{pinName}"] = pinValue;
                    }
                }
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析功能块元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析逻辑门元素
        /// </summary>
        private NetworkElement ParseLogicGateElement(XElement gateElement)
        {
            try
            {
                var gateType = gateElement.Attribute("Type")?.Value ?? "AND";
                var gateName = gateElement.Attribute("Name")?.Value ?? $"{gateType}门";
                
                var element = CreateNetworkElement("LogicGate", gateName, $"逻辑门: {gateType}");
                element.Properties["GateType"] = gateType;
                
                // 解析输入
                var inputs = gateElement.Descendants("Input");
                var inputCount = 0;
                foreach (var input in inputs)
                {
                    inputCount++;
                    var inputValue = input.Value;
                    element.Properties[$"Input{inputCount}"] = inputValue;
                }
                
                // 解析输出
                var output = gateElement.Element("Output")?.Value;
                if (!string.IsNullOrEmpty(output))
                {
                    element.Properties["Output"] = output;
                }
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析逻辑门元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析比较器元素
        /// </summary>
        private NetworkElement ParseComparatorElement(XElement compElement)
        {
            try
            {
                var compType = compElement.Attribute("Type")?.Value ?? "EQ"; // 等于
                var compName = compElement.Attribute("Name")?.Value ?? $"{compType}比较器";
                
                var element = CreateNetworkElement("Comparator", compName, $"比较器: {compType}");
                element.Properties["CompareType"] = compType;
                
                // 解析输入值
                var input1 = compElement.Element("Input1")?.Value;
                var input2 = compElement.Element("Input2")?.Value;
                var output = compElement.Element("Output")?.Value;
                
                if (!string.IsNullOrEmpty(input1))
                    element.Properties["Input1"] = input1;
                if (!string.IsNullOrEmpty(input2))
                    element.Properties["Input2"] = input2;
                if (!string.IsNullOrEmpty(output))
                    element.Properties["Output"] = output;
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析比较器元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析算术运算元素
        /// </summary>
        private NetworkElement ParseArithmeticElement(XElement arithElement)
        {
            try
            {
                var operation = arithElement.Attribute("Operation")?.Value ?? "ADD";
                var arithName = arithElement.Attribute("Name")?.Value ?? $"{operation}运算";
                
                var element = CreateNetworkElement("Arithmetic", arithName, $"算术运算: {operation}");
                element.Properties["Operation"] = operation;
                
                // 解析操作数
                var operand1 = arithElement.Element("Operand1")?.Value;
                var operand2 = arithElement.Element("Operand2")?.Value;
                var result = arithElement.Element("Result")?.Value;
                
                if (!string.IsNullOrEmpty(operand1))
                    element.Properties["Operand1"] = operand1;
                if (!string.IsNullOrEmpty(operand2))
                    element.Properties["Operand2"] = operand2;
                if (!string.IsNullOrEmpty(result))
                    element.Properties["Result"] = result;
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析算术运算元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析连接线元素
        /// </summary>
        private NetworkElement ParseConnectionElement(XElement connElement)
        {
            try
            {
                var fromElement = connElement.Attribute("From")?.Value ?? "";
                var toElement = connElement.Attribute("To")?.Value ?? "";
                
                var element = CreateNetworkElement("Connection", $"{fromElement}->{toElement}", $"连接: {fromElement} -> {toElement}");
                element.Properties["From"] = fromElement;
                element.Properties["To"] = toElement;
                
                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析连接线元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析备用网络结构
        /// </summary>
        private async Task ParseAlternativeNetworkStructureAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 如果没有找到标准网络结构，创建一个默认网络
                var networkInfo = CreateNetworkInfo(1, "功能块图逻辑", "从XML解析的功能块图逻辑");
                
                // 尝试解析根级别的元素
                var rootElements = xmlDoc.Root?.Elements();
                if (rootElements != null)
                {
                    foreach (var element in rootElements)
                    {
                        var networkElement = CreateNetworkElement("XMLElement", element.Name.LocalName, element.Value);
                        networkInfo.Elements.Add(networkElement);
                    }
                }
                
                programBlock.Networks.Add(networkInfo);
                _logAction("使用备用解析策略创建了默认FBD网络");
            }
            catch (Exception ex)
            {
                _logAction($"解析备用FBD网络结构失败: {ex.Message}");
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
                _logAction($"解析FBD接口信息失败: {ex.Message}");
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
                _logAction($"解析FBD参数失败: {ex.Message}");
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
                _logAction($"解析FBD静态变量失败: {ex.Message}");
                return null;
            }
        }
    }
}
