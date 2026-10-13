using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;

namespace 博图项目读取.Services.Parsers
{
    /// <summary>
    /// 梯形图(LAD)解析器 - 优化版本
    /// 支持TIA Portal V16标准XML格式解析
    /// </summary>
    public class LadderParser : BaseBlockParser
    {
        public LadderParser(Action<string> logAction) : base(logAction)
        {
        }

        public override string SupportedLanguage => "LAD";

        protected override async Task ParseXmlContentAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                _logAction("开始解析LAD XML结构...");

                // 解析TIA Portal标准结构
                await ParseTiaPortalStructureAsync(xmlDoc, programBlock);

                _logAction($"LAD解析完成: {programBlock.Networks.Count} 个网络, " +
                          $"{programBlock.Interface.Input.Count} 个输入, " +
                          $"{programBlock.Interface.Output.Count} 个输出");
            }
            catch (Exception ex)
            {
                _logAction($"解析LAD XML内容失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 解析TIA Portal标准XML结构
        /// </summary>
        private async Task ParseTiaPortalStructureAsync(XDocument xmlDoc, ProgramBlock programBlock)
        {
            try
            {
                // 查找功能块元素 (SW.Blocks.FB, SW.Blocks.FC, SW.Blocks.OB等)
                var blockElement = xmlDoc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));

                if (blockElement == null)
                {
                    _logAction("未找到标准的TIA Portal块结构");
                    return;
                }

                _logAction($"找到块类型: {blockElement.Name.LocalName}");

                // 解析块的基本属性
                await ParseBlockAttributesAsync(blockElement, programBlock);

                // 解析接口定义
                await ParseInterfaceDefinitionAsync(blockElement, programBlock);

                // 解析编译单元(网络)
                await ParseCompileUnitsAsync(blockElement, programBlock);

                // 解析多语言文本
                ParseMultilingualTexts(blockElement, programBlock);
            }
            catch (Exception ex)
            {
                _logAction($"解析TIA Portal结构失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 解析块的基本属性
        /// </summary>
        private async Task ParseBlockAttributesAsync(XElement blockElement, ProgramBlock programBlock)
        {
            try
            {
                var attributeList = blockElement.Element("AttributeList");
                if (attributeList == null) return;

                // 解析基本属性
                programBlock.Number = int.TryParse(attributeList.Element("Number")?.Value, out var num) ? num : 0;
                programBlock.Name = attributeList.Element("Name")?.Value ??
                                   attributeList.Element("n")?.Value ??
                                   $"FB{programBlock.Number}";
                programBlock.Version = attributeList.Element("HeaderVersion")?.Value ?? "1.0";
                programBlock.Author = attributeList.Element("HeaderAuthor")?.Value ?? "";
                programBlock.Family = attributeList.Element("HeaderFamily")?.Value ?? "";
                programBlock.Language = attributeList.Element("ProgrammingLanguage")?.Value ?? "LAD";

                // 解析其他属性
                programBlock.Properties["AutoNumber"] = attributeList.Element("AutoNumber")?.Value ?? "false";
                programBlock.Properties["MemoryLayout"] = attributeList.Element("MemoryLayout")?.Value ?? "Optimized";
                programBlock.Properties["MemoryReserve"] = attributeList.Element("MemoryReserve")?.Value ?? "100";
                programBlock.Properties["IsIECCheckEnabled"] = attributeList.Element("IsIECCheckEnabled")?.Value ?? "false";

                _logAction($"解析块属性: {programBlock.Name} (FB{programBlock.Number})");
            }
            catch (Exception ex)
            {
                _logAction($"解析块属性失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析接口定义
        /// </summary>
        private async Task ParseInterfaceDefinitionAsync(XElement blockElement, ProgramBlock programBlock)
        {
            try
            {
                var interfaceElement = blockElement.Descendants("Interface").FirstOrDefault();
                if (interfaceElement == null)
                {
                    _logAction("未找到接口定义");
                    return;
                }

                var sectionsElement = interfaceElement.Element("Sections");
                if (sectionsElement == null)
                {
                    _logAction("未找到接口Sections");
                    return;
                }

                // 解析各个接口段
                await ParseInterfaceSectionsAsync(sectionsElement, programBlock);

                _logAction($"接口解析完成: Input({programBlock.Interface.Input.Count}), " +
                          $"Output({programBlock.Interface.Output.Count}), " +
                          $"InOut({programBlock.Interface.InOut.Count}), " +
                          $"Static({programBlock.Interface.Static.Count})");
            }
            catch (Exception ex)
            {
                _logAction($"解析接口定义失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析接口段
        /// </summary>
        private async Task ParseInterfaceSectionsAsync(XElement sectionsElement, ProgramBlock programBlock)
        {
            try
            {
                var sections = sectionsElement.Elements("Section");
                foreach (var section in sections)
                {
                    var sectionName = section.Attribute("Name")?.Value;
                    _logAction($"解析接口段: {sectionName}");

                    switch (sectionName)
                    {
                        case "Input":
                            await ParseParameterSectionAsync(section, programBlock.Interface.Input, "Input");
                            break;
                        case "Output":
                            await ParseParameterSectionAsync(section, programBlock.Interface.Output, "Output");
                            break;
                        case "InOut":
                            await ParseParameterSectionAsync(section, programBlock.Interface.InOut, "InOut");
                            break;
                        case "Static":
                            await ParseVariableSectionAsync(section, programBlock.Interface.Static, "Static");
                            break;
                        case "Temp":
                            await ParseVariableSectionAsync(section, programBlock.Interface.Temp, "Temp");
                            break;
                        case "Constant":
                            await ParseVariableSectionAsync(section, programBlock.Interface.Constant, "Constant");
                            break;
                        default:
                            _logAction($"未知接口段类型: {sectionName}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析接口段失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析参数段成员
        /// </summary>
        private async Task ParseParameterSectionAsync(XElement section, List<BlockParameter> targetList, string direction)
        {
            try
            {
                var members = section.Elements("Member");
                foreach (var member in members)
                {
                    var parameter = await ParseMemberAsParameterAsync(member, direction);
                    if (parameter != null)
                    {
                        targetList.Add(parameter);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析参数段成员失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析变量段成员
        /// </summary>
        private async Task ParseVariableSectionAsync(XElement section, List<BlockVariable> targetList, string direction)
        {
            try
            {
                var members = section.Elements("Member");
                foreach (var member in members)
                {
                    var variable = await ParseMemberAsVariableAsync(member, direction);
                    if (variable != null)
                    {
                        targetList.Add(variable);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析变量段成员失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析成员为参数
        /// </summary>
        private async Task<BlockParameter> ParseMemberAsParameterAsync(XElement member, string direction)
        {
            try
            {
                var name = member.Attribute("Name")?.Value;
                var datatype = member.Attribute("Datatype")?.Value;
                var remanence = member.Attribute("Remanence")?.Value;
                var accessibility = member.Attribute("Accessibility")?.Value;

                if (string.IsNullOrEmpty(name))
                    return null;

                var parameter = new BlockParameter
                {
                    Name = name,
                    DataType = datatype ?? "Unknown",
                    Direction = direction,
                    Properties = new Dictionary<string, string>()
                };

                // 添加属性
                if (!string.IsNullOrEmpty(remanence))
                    parameter.Properties["Remanence"] = remanence;
                if (!string.IsNullOrEmpty(accessibility))
                    parameter.Properties["Accessibility"] = accessibility;

                // 解析注释
                var commentElement = member.Element("Comment");
                if (commentElement != null)
                {
                    var multiLangText = commentElement.Element("MultiLanguageText");
                    if (multiLangText != null)
                    {
                        parameter.Comment = multiLangText.Value;
                        var lang = multiLangText.Attribute("Lang")?.Value;
                        if (!string.IsNullOrEmpty(lang))
                            parameter.Properties["CommentLanguage"] = lang;
                    }
                }

                // 解析属性列表
                var attributeList = member.Element("AttributeList");
                if (attributeList != null)
                {
                    await ParseAttributeListAsync(attributeList, parameter.Properties);
                }

                // 解析嵌套结构体成员
                await ParseNestedParameterMembersAsync(member, parameter);

                return parameter;
            }
            catch (Exception ex)
            {
                _logAction($"解析参数成员失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析成员为变量
        /// </summary>
        private async Task<BlockVariable> ParseMemberAsVariableAsync(XElement member, string direction)
        {
            try
            {
                var name = member.Attribute("Name")?.Value;
                var datatype = member.Attribute("Datatype")?.Value;
                var remanence = member.Attribute("Remanence")?.Value;
                var accessibility = member.Attribute("Accessibility")?.Value;
                var version = member.Attribute("Version")?.Value;

                if (string.IsNullOrEmpty(name))
                    return null;

                var variable = new BlockVariable
                {
                    Name = name,
                    DataType = datatype ?? "Unknown",
                    IsRetentive = remanence == "SetInIDB" || remanence == "Retain"
                };

                // 解析注释
                var commentElement = member.Element("Comment");
                if (commentElement != null)
                {
                    var multiLangText = commentElement.Element("MultiLanguageText");
                    if (multiLangText != null)
                    {
                        variable.Comment = multiLangText.Value;
                    }
                }

                // 解析初始值
                var initialValueElement = member.Element("StartValue") ?? member.Element("InitialValue");
                if (initialValueElement != null)
                {
                    variable.InitialValue = initialValueElement.Value;
                }

                return variable;
            }
            catch (Exception ex)
            {
                _logAction($"解析变量成员失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析属性列表
        /// </summary>
        private async Task ParseAttributeListAsync(XElement attributeList, Dictionary<string, string> properties)
        {
            try
            {
                var attributes = attributeList.Elements();
                foreach (var attr in attributes)
                {
                    var name = attr.Name.LocalName;
                    var value = attr.Value;
                    var systemDefined = attr.Attribute("SystemDefined")?.Value;

                    properties[name] = value;
                    if (!string.IsNullOrEmpty(systemDefined))
                        properties[$"{name}_SystemDefined"] = systemDefined;
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析属性列表失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析嵌套参数成员(结构体成员)
        /// </summary>
        private async Task ParseNestedParameterMembersAsync(XElement parentMember, BlockParameter parameter)
        {
            try
            {
                var nestedMembers = parentMember.Elements("Member");
                if (nestedMembers.Any())
                {
                    parameter.Properties["HasNestedMembers"] = "true";
                    parameter.Properties["NestedMemberCount"] = nestedMembers.Count().ToString();

                    // 记录嵌套成员信息
                    var nestedInfo = new List<string>();
                    foreach (var nested in nestedMembers)
                    {
                        var nestedName = nested.Attribute("Name")?.Value;
                        var nestedType = nested.Attribute("Datatype")?.Value;
                        if (!string.IsNullOrEmpty(nestedName))
                        {
                            nestedInfo.Add($"{nestedName}:{nestedType}");
                        }
                    }
                    parameter.Properties["NestedMembers"] = string.Join(";", nestedInfo);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析嵌套参数成员失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析编译单元(网络)
        /// </summary>
        private async Task ParseCompileUnitsAsync(XElement blockElement, ProgramBlock programBlock)
        {
            try
            {
                var compileUnits = blockElement.Descendants("SW.Blocks.CompileUnit");
                var networkNumber = 1;

                foreach (var compileUnit in compileUnits)
                {
                    var networkInfo = await ParseCompileUnitAsync(compileUnit, networkNumber);
                    if (networkInfo != null)
                    {
                        programBlock.Networks.Add(networkInfo);
                        networkNumber++;
                    }
                }

                _logAction($"解析了 {programBlock.Networks.Count} 个编译单元(网络)");
            }
            catch (Exception ex)
            {
                _logAction($"解析编译单元失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析单个编译单元
        /// </summary>
        private async Task<NetworkInfo> ParseCompileUnitAsync(XElement compileUnit, int networkNumber)
        {
            try
            {
                var attributeList = compileUnit.Element("AttributeList");
                if (attributeList == null) return null;

                // 获取编程语言
                var programmingLanguage = attributeList.Element("ProgrammingLanguage")?.Value ?? "LAD";

                // 获取网络标题
                var title = await GetNetworkTitleAsync(compileUnit) ?? $"网络 {networkNumber}";

                // 获取网络注释
                var comment = await GetNetworkCommentAsync(compileUnit) ?? "";

                var networkInfo = CreateNetworkInfo(networkNumber, title, comment);
                networkInfo.Properties["ProgrammingLanguage"] = programmingLanguage;

                // 根据编程语言解析网络源码
                var networkSource = attributeList.Element("NetworkSource");
                if (networkSource != null)
                {
                    await ParseNetworkSourceAsync(networkSource, networkInfo, programmingLanguage);
                }

                return networkInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析编译单元失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取网络标题
        /// </summary>
        private async Task<string> GetNetworkTitleAsync(XElement compileUnit)
        {
            try
            {
                var titleElement = compileUnit.Descendants("MultilingualText")
                    .FirstOrDefault(e => e.Attribute("CompositionName")?.Value == "Title");

                if (titleElement != null)
                {
                    var titleItem = titleElement.Descendants("MultilingualTextItem")
                        .FirstOrDefault(e => e.Element("AttributeList")?.Element("Culture")?.Value == "zh-CN");

                    if (titleItem != null)
                    {
                        return titleItem.Element("AttributeList")?.Element("Text")?.Value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logAction($"获取网络标题失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取网络注释
        /// </summary>
        private async Task<string> GetNetworkCommentAsync(XElement compileUnit)
        {
            try
            {
                var commentElement = compileUnit.Descendants("MultilingualText")
                    .FirstOrDefault(e => e.Attribute("CompositionName")?.Value == "Comment");

                if (commentElement != null)
                {
                    var commentItem = commentElement.Descendants("MultilingualTextItem")
                        .FirstOrDefault(e => e.Element("AttributeList")?.Element("Culture")?.Value == "zh-CN");

                    if (commentItem != null)
                    {
                        return commentItem.Element("AttributeList")?.Element("Text")?.Value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logAction($"获取网络注释失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析网络源码
        /// </summary>
        private async Task ParseNetworkSourceAsync(XElement networkSource, NetworkInfo networkInfo, string programmingLanguage)
        {
            try
            {
                switch (programmingLanguage.ToUpper())
                {
                    case "LAD":
                        await ParseFlgNetAsync(networkSource, networkInfo);
                        break;
                    case "SCL":
                        await ParseStructuredTextAsync(networkSource, networkInfo);
                        break;
                    default:
                        _logAction($"不支持的编程语言: {programmingLanguage}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析网络源码失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析FlgNet(LAD网络)
        /// </summary>
        private async Task ParseFlgNetAsync(XElement networkSource, NetworkInfo networkInfo)
        {
            try
            {
                var flgNet = networkSource.Element("{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4}FlgNet");
                if (flgNet == null) return;

                // 解析Parts(元素)
                var parts = flgNet.Element("{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4}Parts");
                if (parts != null)
                {
                    await ParseFlgNetPartsAsync(parts, networkInfo);
                }

                // 解析Wires(连线)
                var wires = flgNet.Element("{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4}Wires");
                if (wires != null)
                {
                    await ParseFlgNetWiresAsync(wires, networkInfo);
                }

                _logAction($"FlgNet解析完成: {networkInfo.Elements.Count} 个元素");
            }
            catch (Exception ex)
            {
                _logAction($"解析FlgNet失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析FlgNet Parts(元素)
        /// </summary>
        private async Task ParseFlgNetPartsAsync(XElement parts, NetworkInfo networkInfo)
        {
            try
            {
                var ns = "{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4}";

                // 解析Access元素(变量访问)
                var accessElements = parts.Elements($"{ns}Access");
                foreach (var access in accessElements)
                {
                    var element = await ParseAccessElementAsync(access, ns);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }

                // 解析Call元素(功能块调用)
                var callElements = parts.Elements($"{ns}Call");
                foreach (var call in callElements)
                {
                    var element = await ParseCallElementAsync(call, ns);
                    if (element != null)
                    {
                        networkInfo.Elements.Add(element);
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析FlgNet Parts失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析Access元素
        /// </summary>
        private async Task<NetworkElement> ParseAccessElementAsync(XElement access, string ns)
        {
            try
            {
                var scope = access.Attribute("Scope")?.Value ?? "Unknown";
                var uid = access.Attribute("UId")?.Value ?? "";

                var symbol = access.Element($"{ns}Symbol");
                if (symbol == null) return null;

                var componentName = symbol.Element($"{ns}Component")?.Attribute("Name")?.Value ?? "Unknown";

                var element = CreateNetworkElement("Access", componentName, $"变量访问: {componentName}");
                element.Properties["Scope"] = scope;
                element.Properties["UId"] = uid;
                element.Properties["ComponentName"] = componentName;

                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析Access元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析Call元素(功能块调用)
        /// </summary>
        private async Task<NetworkElement> ParseCallElementAsync(XElement call, string ns)
        {
            try
            {
                var uid = call.Attribute("UId")?.Value ?? "";

                var callInfo = call.Element($"{ns}CallInfo");
                if (callInfo == null) return null;

                var blockName = callInfo.Attribute("Name")?.Value ?? "Unknown";
                var blockType = callInfo.Attribute("BlockType")?.Value ?? "Unknown";

                var element = CreateNetworkElement("Call", blockName, $"功能块调用: {blockName} ({blockType})");
                element.Properties["UId"] = uid;
                element.Properties["BlockName"] = blockName;
                element.Properties["BlockType"] = blockType;

                // 解析实例
                var instance = callInfo.Element($"{ns}Instance");
                if (instance != null)
                {
                    var instanceScope = instance.Attribute("Scope")?.Value;
                    var instanceUId = instance.Attribute("UId")?.Value;
                    var instanceComponent = instance.Element($"{ns}Component")?.Attribute("Name")?.Value;

                    element.Properties["InstanceScope"] = instanceScope ?? "";
                    element.Properties["InstanceUId"] = instanceUId ?? "";
                    element.Properties["InstanceComponent"] = instanceComponent ?? "";
                }

                // 解析参数
                var parameters = callInfo.Elements($"{ns}Parameter");
                var paramCount = 0;
                foreach (var param in parameters)
                {
                    var paramName = param.Attribute("Name")?.Value;
                    var paramSection = param.Attribute("Section")?.Value;
                    var paramType = param.Attribute("Type")?.Value;

                    if (!string.IsNullOrEmpty(paramName))
                    {
                        element.Properties[$"Param_{paramCount}_Name"] = paramName;
                        element.Properties[$"Param_{paramCount}_Section"] = paramSection ?? "";
                        element.Properties[$"Param_{paramCount}_Type"] = paramType ?? "";
                        paramCount++;
                    }
                }
                element.Properties["ParameterCount"] = paramCount.ToString();

                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析Call元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析FlgNet Wires(连线)
        /// </summary>
        private async Task ParseFlgNetWiresAsync(XElement wires, NetworkInfo networkInfo)
        {
            try
            {
                var ns = "{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4}";
                var wireElements = wires.Elements($"{ns}Wire");

                var wireCount = 0;
                foreach (var wire in wireElements)
                {
                    var wireUId = wire.Attribute("UId")?.Value ?? "";

                    // 解析连线的起点和终点
                    var connections = new List<string>();

                    var powerrail = wire.Element($"{ns}Powerrail");
                    if (powerrail != null)
                    {
                        connections.Add("Powerrail");
                    }

                    var nameCons = wire.Elements($"{ns}NameCon");
                    foreach (var nameCon in nameCons)
                    {
                        var nameConUId = nameCon.Attribute("UId")?.Value;
                        var nameConName = nameCon.Attribute("Name")?.Value;
                        connections.Add($"NameCon:{nameConUId}:{nameConName}");
                    }

                    var identCons = wire.Elements($"{ns}IdentCon");
                    foreach (var identCon in identCons)
                    {
                        var identConUId = identCon.Attribute("UId")?.Value;
                        connections.Add($"IdentCon:{identConUId}");
                    }

                    var openCons = wire.Elements($"{ns}OpenCon");
                    foreach (var openCon in openCons)
                    {
                        var openConUId = openCon.Attribute("UId")?.Value;
                        connections.Add($"OpenCon:{openConUId}");
                    }

                    // 创建连线元素
                    var wireElement = CreateNetworkElement("Wire", $"Wire_{wireCount}", $"连线: {string.Join(" -> ", connections)}");
                    wireElement.Properties["UId"] = wireUId;
                    wireElement.Properties["Connections"] = string.Join(";", connections);
                    wireElement.Properties["ConnectionCount"] = connections.Count.ToString();

                    networkInfo.Elements.Add(wireElement);
                    wireCount++;
                }

                _logAction($"解析了 {wireCount} 条连线");
            }
            catch (Exception ex)
            {
                _logAction($"解析FlgNet Wires失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析结构化文本(SCL)
        /// </summary>
        private async Task ParseStructuredTextAsync(XElement networkSource, NetworkInfo networkInfo)
        {
            try
            {
                var structuredText = networkSource.Element("{http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3}StructuredText");
                if (structuredText == null) return;

                var ns = "{http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3}";
                var elements = structuredText.Elements();

                var sclCode = new List<string>();
                foreach (var element in elements)
                {
                    var elementName = element.Name.LocalName;
                    var elementValue = element.Value;

                    switch (elementName)
                    {
                        case "Token":
                            sclCode.Add(element.Attribute("Text")?.Value ?? "");
                            break;
                        case "Text":
                            sclCode.Add(elementValue);
                            break;
                        case "Blank":
                            var blankNum = element.Attribute("Num")?.Value;
                            if (int.TryParse(blankNum, out var spaces))
                            {
                                sclCode.Add(new string(' ', spaces));
                            }
                            break;
                        case "NewLine":
                            sclCode.Add("\n");
                            break;
                        case "LineComment":
                            var commentText = element.Element($"{ns}Text")?.Value ?? "";
                            sclCode.Add($"// {commentText}");
                            break;
                        case "Access":
                            var accessElement = await ParseSCLAccessAsync(element, ns);
                            if (accessElement != null)
                            {
                                networkInfo.Elements.Add(accessElement);
                                sclCode.Add(accessElement.Name);
                            }
                            break;
                    }
                }

                // 创建SCL代码元素
                var codeElement = CreateNetworkElement("SCLCode", "SCL代码", "结构化控制语言代码");
                codeElement.Properties["Code"] = string.Join("", sclCode);
                codeElement.Properties["Language"] = "SCL";
                networkInfo.Elements.Add(codeElement);

                _logAction($"解析SCL代码完成，代码长度: {string.Join("", sclCode).Length} 字符");
            }
            catch (Exception ex)
            {
                _logAction($"解析结构化文本失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析SCL访问元素
        /// </summary>
        private async Task<NetworkElement> ParseSCLAccessAsync(XElement access, string ns)
        {
            try
            {
                var scope = access.Attribute("Scope")?.Value ?? "Unknown";
                var uid = access.Attribute("UId")?.Value ?? "";

                var symbol = access.Element($"{ns}Symbol");
                if (symbol == null) return null;

                var componentName = symbol.Element($"{ns}Component")?.Attribute("Name")?.Value ?? "Unknown";

                var element = CreateNetworkElement("SCLAccess", componentName, $"SCL变量访问: {componentName}");
                element.Properties["Scope"] = scope;
                element.Properties["UId"] = uid;
                element.Properties["ComponentName"] = componentName;

                return element;
            }
            catch (Exception ex)
            {
                _logAction($"解析SCL访问元素失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 解析多语言文本
        /// </summary>
        private void ParseMultilingualTexts(XElement blockElement, ProgramBlock programBlock)
        {
            try
            {
                var multilingualTexts = blockElement.Descendants("MultilingualText");
                foreach (var text in multilingualTexts)
                {
                    var compositionName = text.Attribute("CompositionName")?.Value;
                    if (compositionName == "Comment")
                    {
                        var zhItem = text.Descendants("MultilingualTextItem")
                            .FirstOrDefault(item => item.Element("AttributeList")?.Element("Culture")?.Value == "zh-CN");

                        if (zhItem != null)
                        {
                            var commentText = zhItem.Element("AttributeList")?.Element("Text")?.Value;
                            if (!string.IsNullOrEmpty(commentText))
                            {
                                programBlock.Comment = commentText;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析多语言文本失败: {ex.Message}");
            }
        }
    }
}
