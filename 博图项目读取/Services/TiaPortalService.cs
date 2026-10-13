using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using 博图项目读取.Models;
using 博图项目读取.Services.Parsers;
using System.Text;
#if TIA_PORTAL_AVAILABLE
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.ExternalSources;
#endif
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using Microsoft.Win32;






#if TIA_PORTAL_AVAILABLE
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.HW.Features;
#endif

namespace 博图项目读取.Services
{
    /// <summary>
    /// TIA Portal服务类 - 负责与博图软件交互
    /// </summary>
    public class TiaPortalService : IDisposable
    {
        private readonly Action<string> _logAction;
        
#if TIA_PORTAL_AVAILABLE
        private TiaPortal _tiaPortal;
        private Project _currentProject;
#endif
        private bool _disposed = false;

        public TiaPortalService(Action<string> logAction = null)
        {
            _logAction = logAction ?? (msg => { });
        }

        /// <summary>
        /// 连接到TIA Portal
        /// </summary>
        /// <returns>是否连接成功</returns>
        public async Task<bool> ConnectAsync()
        {
#if TIA_PORTAL_AVAILABLE
            try
            {
                _logAction("正在连接TIA Portal...");
                
                // 连接前自检本机 Openness 环境（安装状态 / 版本匹配 / 位数），
                // 把"未安装 / 版本不匹配"从晦涩的 TiaPortalAccess 类型初始值设定项异常变为人话提示
                var envError = CheckOpennessEnvironment();
                if (envError != null)
                {
                    _logAction(envError);
                    return false;
                }

                // 尝试连接到已打开的TIA Portal实例
                var processes = TiaPortal.GetProcesses();
                if (processes.Any())
                {
                    _logAction($"发现{processes.Count()}个TIA Portal实例");
                    _tiaPortal = processes.First().Attach();
                    _logAction("已连接到现有TIA Portal实例");
                }
                else
                {
                    _logAction("未发现运行中的TIA Portal实例，尝试启动新实例...");
                    // 启动新的TIA Portal实例
                    _tiaPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                    _logAction("已启动新的TIA Portal实例");
                }

                // 验证连接
                if (_tiaPortal != null)
                {
                    _logAction("TIA Portal连接成功");
                    return true;
                }
                
                return false;
            }
            catch (TypeInitializationException ex)
            {
                _logAction($"连接TIA Portal失败: {ex.Message}");
                Exception root = ex;
                while (root.InnerException != null) root = root.InnerException;
                if (root != ex)
                {
                    _logAction($"根因异常: {root.GetType().Name}: {root.Message}");
                }
                _logAction("TiaPortalAccess 类型初始化失败的常见原因（按概率排序）：");
                _logAction("  1. 引用的 Siemens.Engineering.dll 版本与本机安装的 TIA Portal 大版本不一致（Openness 不跨大版本兼容，必须同版本）；");
                _logAction("  2. 本机未安装 TIA Portal，或安装时未勾选 \"TIA Portal Openness\" 组件；");
                _logAction("  3. 程序以 32 位进程运行（TIA V16+ 仅支持 64 位）；");
                _logAction("  4. V17+ 首次运行需以管理员身份运行一次并在白名单弹窗中确认（或注册表 HKLM\\SOFTWARE\\SIEMENS\\Automation\\Openness\\<版本>\\Whitelist 登记本程序路径）。");
                _logAction($"详细错误: {ex.StackTrace}");
                LogHelper.WriteLogsAsync(ex.ToString(), "连接TIA Portal类型初始化异常");
                return false;
            }
            catch (Exception ex)
            {
                _logAction($"连接TIA Portal失败: {ex.Message}");
                var inner = ex.InnerException;
                var depth = 1;
                while (inner != null && depth <= 5)
                {
                    _logAction($"  └─ 内部异常[{depth}]: {inner.GetType().Name}: {inner.Message}");
                    inner = inner.InnerException;
                    depth++;
                }
                _logAction($"详细错误: {ex.StackTrace}");
                return false;
            }
#else
            _logAction("TIA Portal SDK未安装或不可用，无法连接");
            await Task.Delay(1000); // 模拟连接时间
            return false;
#endif
        }

#if TIA_PORTAL_AVAILABLE
        /// <summary>
        /// 连接前自检本机 Openness 环境；返回 null 表示通过，否则返回可直接展示给用户的错误描述
        /// </summary>
        private string CheckOpennessEnvironment()
        {
            try
            {
                if (!Environment.Is64BitProcess)
                {
                    return "环境自检失败: 当前进程是 32 位，而 TIA Portal V16 及以上只有 64 位 Openness。" +
                           "请把平台目标改为 x64（或 AnyCPU 且不勾选\"首选 32 位\"）后重新编译。";
                }

                // 引用的 Openness DLL 版本（决定本程序只能在装有大版本相同的 TIA Portal 的机器上运行）
                var dllVer = typeof(TiaPortal).Assembly.GetName().Version;

                // 注册表里本机已注册的 Openness 版本（64/32 位视图都扫）
                var installedNames = new List<string>();
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var openness = baseKey.OpenSubKey(@"SOFTWARE\SIEMENS\Automation\Openness"))
                    {
                        if (openness == null) continue;
                        foreach (var name in openness.GetSubKeyNames())
                        {
                            if (!installedNames.Contains(name)) installedNames.Add(name);
                        }
                    }
                }

                _logAction($"环境自检: 引用的 Openness DLL = V{dllVer.Major}.{dllVer.Minor}; 本机注册的 Openness 版本 = " +
                           (installedNames.Count > 0 ? string.Join(", ", installedNames) : "(无)"));

                if (installedNames.Count == 0)
                {
                    return "环境自检失败: 本机注册表没有注册任何 TIA Portal Openness 版本" +
                           "(HKLM\\SOFTWARE\\SIEMENS\\Automation\\Openness)。本机未安装 TIA Portal，或安装时未勾选 " +
                           "\"TIA Portal Openness\" 组件。本工具只能在装有与引用 DLL 同大版本 TIA Portal 的机器上运行。";
                }

                var installedDesc = string.Join(" / ", installedNames);
                var installedMajors = new HashSet<int>();
                foreach (var name in installedNames)
                {
                    if (int.TryParse(name.Split('.')[0], out var major)) installedMajors.Add(major);
                }
                if (!installedMajors.Contains(dllVer.Major))
                {
                    return $"环境自检失败: 版本不匹配 —— 本程序引用的是 V{dllVer.Major} 的 Siemens.Engineering.dll，" +
                           $"本机安装的 TIA Portal 是 {installedDesc}。Openness 不跨大版本兼容，" +
                           "请任选其一：① 用本机版本的 PublicAPI DLL 重新编译（位于 " +
                           "C:\\Program Files\\Siemens\\Automation\\Portal V<版本>\\PublicAPI\\V<版本>\\，拷入本项目对应的 " +
                           "<版本>dll 文件夹）；② 在本机安装与本程序引用版本一致的 TIA Portal。";
                }

                return null;
            }
            catch (Exception ex)
            {
                // 自检本身异常不阻断连接，交给后续真实调用暴露问题
                _logAction($"环境自检异常(忽略，继续尝试连接): {ex.Message}");
                return null;
            }
        }
#endif

        /// <summary>
        /// 打开项目
        /// </summary>
        /// <param name="projectPath">项目文件路径</param>
        /// <returns>是否打开成功</returns>
        public async Task<bool> OpenProjectAsync(string projectPath)
        {
#if TIA_PORTAL_AVAILABLE
            try
            {
                if (_tiaPortal == null)
                {
                    _logAction("TIA Portal未连接");
                    return false;
                }

                _logAction($"正在打开项目: {projectPath}");
                
                // 检查项目文件是否存在
                if (!File.Exists(projectPath))
                {
                    _logAction($"项目文件不存在: {projectPath}");
                    return false;
                }

                // 打开项目
                _currentProject = _tiaPortal.Projects.Open(new FileInfo(projectPath));
                _logAction($"项目打开成功: {_currentProject.Name}");
                
                return true;
            }
            catch (Exception ex)
            {
                _logAction($"打开项目失败: {ex.Message}");
                return false;
            }
#else
            _logAction("TIA Portal SDK未安装或不可用，无法打开项目");
            await Task.Delay(1000); // 模拟打开时间
            return false;
#endif
        }

        /// <summary>
        /// 解析项目并提取信息
        /// </summary>
        /// <returns>项目信息</returns>
        public async Task<TiaPortalProject> ParseProjectAsync()
        {
#if TIA_PORTAL_AVAILABLE
            try
            {
                if (_currentProject == null)
                {
                    _logAction("没有打开的项目");
                    return null;
                }

                _logAction("开始解析项目...");

                var projectInfo = new TiaPortalProject
                {
                    Name = _currentProject.Name,
                    Path = _currentProject.Path.FullName,
                    Version = "1.0",
                    CreatedTime = DateTime.Now,
                    ModifiedTime = DateTime.Now,
                    Author = "未知",
                    Comment = ""
                };

                // 解析设备
                await ParseDevicesAsync(projectInfo);

                _logAction("项目解析完成");
                return projectInfo;
            }
            catch (Exception ex)
            {
                _logAction($"解析项目失败: {ex.Message}");
                return null;
            }
#else
            _logAction("TIA Portal SDK未安装或不可用，无法解析项目");
            await Task.Delay(1000);
            return null;
#endif
        }

#if TIA_PORTAL_AVAILABLE
        /// <summary>
        /// 解析设备信息
        /// </summary>
        private async Task ParseDevicesAsync(TiaPortalProject projectInfo)
        {
            if (_currentProject?.Devices == null) return;

            foreach (Device device in _currentProject.Devices)
            {
                try
                {
                    _logAction($"解析设备: {device.Name}");

                    var deviceInfo = new PlcDevice
                    {
                        Name = device.Name,
                        Type = device.TypeIdentifier,
                        OrderNumber = GetDeviceOrderNumber(device),
                        Version = GetDeviceVersion(device),
                        IpAddress = GetDeviceIpAddress(device),
                        Rack = GetDeviceRack(device),
                        Slot = GetDeviceSlot(device)
                    };

                    // 查找PLC设备项
                    var plcDeviceItems = device.DeviceItems.Where(di => 
                        di.Classification == DeviceItemClassifications.CPU);

                    foreach (var plcItem in plcDeviceItems)
                    {
                        await ParsePlcSoftwareAsync(plcItem, deviceInfo, projectInfo);
                    }

                    projectInfo.Devices.Add(deviceInfo);
                }
                catch (Exception ex)
                {
                    _logAction($"解析设备失败: {device.Name} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 解析PLC软件
        /// </summary>
        private async Task ParsePlcSoftwareAsync(DeviceItem plcItem, PlcDevice deviceInfo, TiaPortalProject projectInfo)
        {
            try
            {
                _logAction($"解析PLC软件: {plcItem.Name}");

                var software = plcItem.GetService<SoftwareContainer>();
                if (software?.Software is PlcSoftware plcSoftware)
                {
                    // 解析符号表
                   // await ParseSymbolTableAsync(plcSoftware, projectInfo);
                    
                    // 解析程序块
                    await ParseProgramBlocksAsync(plcSoftware, deviceInfo, projectInfo);
                    
                    // 解析数据块
                   // await ParseDataBlocksAsync(plcSoftware, projectInfo);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析PLC软件失败: {plcItem.Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// 解析符号表
        /// </summary>
        private async Task ParseSymbolTableAsync(PlcSoftware plcSoftware, TiaPortalProject projectInfo)
        {
            try
            {
                _logAction("解析符号表...");
                
                var tagTables = plcSoftware.TagTableGroup.TagTables;
                foreach (var tagTable in tagTables)
                {
                    foreach (var tag in tagTable.Tags)
                    {
                        var symbolInfo = new SymbolInfo
                        {
                            Name = tag.Name,
                            Address = tag.LogicalAddress,
                            DataType = tag.DataTypeName,
                            Comment = GetMultilingualText(tag.Comment),
                            Scope = SymbolScope.Global,
                            AccessLevel = "ReadWrite",
                            LogicalAddress = tag.LogicalAddress,
                            IsRetentive = false,
                            IsConstant = false,
                            InitialValue = null
                        };
                        
                        projectInfo.Symbols.Add(symbolInfo);
                    }
                }
                
                _logAction($"符号表解析完成，共{projectInfo.Symbols.Count}个符号");
            }
            catch (Exception ex)
            {
                _logAction($"解析符号表失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析程序块
        /// </summary>
        private async Task ParseProgramBlocksAsync(PlcSoftware plcSoftware, PlcDevice deviceInfo, TiaPortalProject projectInfo)
        {
            try
            {
                _logAction("解析程序块...");
                
                var blockGroup = plcSoftware.BlockGroup;
               
                // 递归解析程序块组（包括文件夹）
                await ParseBlockGroupRecursiveAsync(blockGroup, "", deviceInfo, projectInfo, plcSoftware);
                
                    _logAction($"程序块解析完成，共{projectInfo.ProgramBlocks.Count}个程序块");
                string mlFilePath = Path.Combine(Directory.GetCurrentDirectory(),"xml", projectInfo.Name, "plcTags");
                if (!Directory.Exists(mlFilePath)) { 
                    Directory.CreateDirectory(mlFilePath);
                }
                ExportDetailedTagInventory(plcSoftware, mlFilePath, projectInfo);

                string mlFilePath2 = Path.Combine(Directory.GetCurrentDirectory(), "xml", projectInfo.Name, "udt");
                if (!Directory.Exists(mlFilePath2))
                {
                    Directory.CreateDirectory(mlFilePath2);
                }
                ExportUdtType(plcSoftware, mlFilePath2);


            }
            catch (Exception ex)
            {
                _logAction($"解析程序块失败: {ex.Message}");
                LogHelper.WriteLogsAsync($"{ex.ToString()}","解析程序块异常");
            }
        }
        public static string CleanFileNameWithRegex(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            // 获取系统非法字符，并用 Regex.Escape 转义（防止正则语法冲突）
            var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));

            // 构建正则表达式，匹配所有非法字符
            var invalidRegStr = string.Format(@"[{0}]", invalidChars);

            // 将非法字符替换为空字符串（或者你想要的 "_"）
            return Regex.Replace(fileName, invalidRegStr, "");
        }
        public void ExportUdtType(PlcSoftware plcSoftware, string savePath) {
            // TIA Openness SDK 导出 UDT
            // ⚠ 不一致的 UDT 同样无法导出（"Inconsistent blocks and PLC data types (UDT) cannot be exported"），
            //    且单个 UDT 失败不应中断其余 UDT 的导出 —— 逐个容错，不一致时先自动编译再导出。
            void ExportTypeGroupRecursive(PlcTypeGroup group)
            {
                foreach (var type in group.Types)
                {
                    try
                    {
                        var tmppath = Path.Combine(savePath, $"{CleanFileNameWithRegex(type.Name)}.xml");
                        if (File.Exists(tmppath))
                        {
                            File.Delete(tmppath);
                        }
                        if (!type.IsConsistent)
                        {
                            _logAction($"UDT {type.Name} 处于不一致状态(被修改后未重新编译)，尝试自动编译...");
                            TryCompile(type, $"UDT {type.Name}");
                        }
                        // 导出为 XML，和 DB/FB 一样的方式
                        type.Export(new FileInfo(tmppath), ExportOptions.WithDefaults);
                    }
                    catch (Exception ex)
                    {
                        _logAction($"导出UDT失败(跳过该UDT继续): {type.Name} - {ex.Message}");
                        LogHelper.WriteLogsAsync($"{type.Name},{ex.ToString()}", "UDT导出异常");
                    }
                }
                foreach (var subGroup in group.Groups)
                    ExportTypeGroupRecursive(subGroup);
            }

            // 从根组递归导出（含嵌套 UDT 文件夹）
            ExportTypeGroupRecursive(plcSoftware.TypeGroup);
        }


        public void ExportDetailedTagInventory(PlcSoftware plcSoftware, string savePath, TiaPortalProject projectInfo = null)
        {
            StringBuilder csv = new StringBuilder();

            // 1. 定义表头 (包含你源码里的属性和你关心的保持性)
            csv.AppendLine("Path,TableName,TagName,Address,DataType,IsSafety,Retentive,HmiVisible,HmiWritable,Comment");

            // 2. 获取变量表根组
            PlcTagTableGroup rootGroup = plcSoftware.TagTableGroup;

            // 3. 开始递归遍历（projectInfo 非空时同步收集符号到内存模型，供窗体"符号表"页签显示）
            TraverseGroups(rootGroup, "Root", csv, projectInfo);

            // 4. 保存文件
            string fileName = Path.Combine(savePath, $"PLC_Tags_Full_{DateTime.Now:yyyyMMdd}.csv");
            if (File.Exists(fileName)) { 
             File.Delete(fileName);
            }
            File.WriteAllText(fileName, csv.ToString(), Encoding.UTF8);

            Console.WriteLine($"导出完成！文件路径: {fileName}");
        }

        private void TraverseGroups(PlcTagTableGroup group, string currentPath, StringBuilder csv, TiaPortalProject projectInfo)
        {
            // 处理当前组下的所有变量表 (TagTables)
            foreach (PlcTagTable table in group.TagTables)
            {
                foreach (PlcTag tag in table.Tags)
                {
                    // A. 直接从你源码中看到的属性读取
                    string name = tag.Name;
                    string address = tag.LogicalAddress;
                    string dataType = tag.DataTypeName;
                    bool isSafety = tag.IsSafety;
                    bool hmiVisible = tag.ExternalVisible;
                    bool hmiWritable = tag.ExternalWritable;

                    // B. 处理保持性 (Retentive) - 使用 GetAttribute 绕过源码中缺失属性的问题
                    string retentiveValue = "N/A";
                    try
                    {
                        // 仅 M 区变量支持此属性，I/Q 区会报异常
                        object attr = tag.GetAttribute("Retentive");
                        retentiveValue = attr?.ToString() ?? "False";
                    }
                    catch
                    {
                        // 捕获异常说明该变量类型不支持保持性设置
                        retentiveValue = "False";
                    }

                    // C. 处理多语言注释 (MultilingualText)
                    string comment = "";
                    if (tag.Comment != null && tag.Comment.Items.Count > 0)
                    {
                        // 默认取第一种语言
                        comment = tag.Comment.Items[0].Text ?? "";
                    }
                    // 清洗注释中的换行和引号，防止破坏 CSV 格式
                    comment = comment.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ");

                    // 收集到内存模型（与 CSV 同源同批次，供窗体"符号表"页签显示）
                    if (projectInfo != null)
                    {
                        projectInfo.Symbols.Add(new SymbolInfo
                        {
                            Name = name,
                            Address = address,
                            DataType = dataType,
                            Comment = comment,
                            Scope = SymbolScope.Global,
                            AccessLevel = hmiWritable ? "ReadWrite" : "Read",
                            LogicalAddress = address,
                            IsRetentive = string.Equals(retentiveValue, "True", StringComparison.OrdinalIgnoreCase),
                            IsConstant = false,
                            InitialValue = null
                        });
                    }

                    // D. 拼接行
                    csv.AppendLine($"\"{currentPath}\",\"{table.Name}\",\"{name}\",\"{address}\",\"{dataType}\",{isSafety},{retentiveValue},{hmiVisible},{hmiWritable},\"{comment}\"");
                }
            }

            // 递归处理子文件夹 (SubGroups)
            foreach (PlcTagTableGroup subGroup in group.Groups)
            {
                TraverseGroups(subGroup, $"{currentPath}/{subGroup.Name}", csv, projectInfo);
            }
        }


        /// <summary>
        /// 递归解析程序块组
        /// </summary>
        private async Task ParseBlockGroupRecursiveAsync(PlcBlockGroup blockGroup, string currentPath, PlcDevice deviceInfo, TiaPortalProject projectInfo, PlcSoftware plcSoftware)
        {
            try
            {
                // 解析当前组中的程序块
                foreach (var block in blockGroup.Blocks)
                {
                    var programBlock = new ProgramBlock
                    {
                        Name = block.Name,
                        Type = GetBlockType(block),
                        Number = block.Number,
                        Language = block.ProgrammingLanguage.ToString(),
                        Comment = "",
                        Author = block.HeaderAuthor,
                        CreatedTime = DateTime.Now,
                        ModifiedTime = DateTime.Now,
                        Version = "1.0",
                        FolderPath = currentPath
                    };

                   
                    // 解析程序块内部逻辑
                    await ParseBlockLogicAsync(block, programBlock,plcSoftware, projectInfo);
                    
                    deviceInfo.Blocks.Add(programBlock);
                    projectInfo.ProgramBlocks.Add(programBlock);
                    
                    _logAction($"找到程序块: {programBlock.Name} ({programBlock.Type}) - 路径: {(string.IsNullOrEmpty(currentPath) ? "根目录" : currentPath)}");
                }

                // 递归解析子程序块组（子文件夹）
                foreach (var subGroup in blockGroup.Groups)
                {
                    string subPath = string.IsNullOrEmpty(currentPath) ? subGroup.Name : $"{currentPath}/{subGroup.Name}";
                    _logAction($"进入文件夹: {subPath}");
                    await ParseBlockGroupRecursiveAsync(subGroup, subPath, deviceInfo, projectInfo,plcSoftware);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析程序块组失败: {currentPath} - {ex.Message}");
            }
        }


        public static void WriteBlock(string path,ProgramBlock programBlock)
        {
            if (programBlock == null) return;

            try
            {
                // 格式：路径,名称
                string line = $"{programBlock.FolderPath},{programBlock.Name}";
                var directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                // 追加写入（不会覆盖原有内容）
                using (StreamWriter sw = File.AppendText(path))
                {
                    sw.WriteLine(line);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 解析程序块内部逻辑
        /// </summary>
        private async Task ParseBlockLogicAsync(PlcBlock block, ProgramBlock programBlock,PlcSoftware plcSoftware, TiaPortalProject projectInfo)
        {
            try
            {
                // 解析程序块接口（输入输出参数）
                // await ParseBlockInterfaceAsync(block, programBlock);
                // ⚠ 不一致块（引用的 UDT 被修改后未重新编译）既无法 Export 也无法生成源码，
                //    Openness 会抛 "Inconsistent blocks and PLC data types (UDT) cannot be exported"。
                //    先检查 IsConsistent，不一致就自动编译该块，编译失败由下方导出 catch 兜底跳过。
                if (!block.IsConsistent)
                {
                    _logAction($"块 {block.Name} 处于不一致状态(引用的UDT/块被修改后未重新编译)，尝试自动编译...");
                    TryCompile(block, block.Name);
                }
                // block.Export() → XML格式，SCL代码体可能缺失
                // GenerateSourceFromBlocks() → 纯文本源码，包含完整SCL/STL代码
                string blcokName = block.Name.Replace(":","：");
                // 生成ML文件路径
                string mlFilePath = Path.Combine(Directory.GetCurrentDirectory(), "xml", projectInfo.Name, "blocks" , block.ProgrammingLanguage.ToString(), 
                    $"{blcokName}.xml");
                string blockFile = Path.Combine(Directory.GetCurrentDirectory(), "xml", projectInfo.Name, "blocks",
                  $"block_path.txt");
                WriteBlock(blockFile,programBlock);
                  // 确保目录存在
                  var directory = Path.GetDirectoryName(mlFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                try
                {
                    if ((block.ProgrammingLanguage == ProgrammingLanguage.SCL ||
        block.ProgrammingLanguage == ProgrammingLanguage.STL)&&block.IsKnowHowProtected==false)
                    {
                        try
                        {
                            string smlFilePath = Path.Combine(Directory.GetCurrentDirectory(), "xml", projectInfo.Name, "blocks",
                  block.ProgrammingLanguage.ToString(),
                  $"{blcokName}.{(block.ProgrammingLanguage == ProgrammingLanguage.STL?"awl":"scl")}");
                            PlcExternalSourceSystemGroup sourceGroup = plcSoftware.ExternalSourceGroup;
                            var director2y = Path.GetDirectoryName(smlFilePath);
                            if (!Directory.Exists(director2y))
                            {
                                Directory.CreateDirectory(director2y);
                            }
                            if (File.Exists(smlFilePath)) { 
                                File.Delete(smlFilePath);
                            }
                            // 将一个或多个块导出为源文件
                            FileInfo sourceFile = new FileInfo(smlFilePath);
                            sourceGroup.GenerateSource(
                                new PlcBlock[] { block },  // 可以传多个块
                                sourceFile
                            );
                        }
                        catch (Exception ex){
                            LogHelper.WriteLogsAsync($"{block.Name},{ex.ToString()}","scl导出异常");                        }
                    }
                    if (File.Exists(mlFilePath)) {
                        File.Delete(mlFilePath);
                    }
                    if (programBlock.Name.ToLower().Contains("3002")) { 
                    
                    }
                    // 导出程序块到ML文件
                    block.Export(new FileInfo(mlFilePath), ExportOptions.WithDefaults);
                    _logAction($"程序块已导出到: {mlFilePath}");
                    
                    // 从ML文件解析逻辑
                    //await ParseFromMlFileAsync(mlFilePath, programBlock);
                }
                catch (Exception exportEx)
                {
                    _logAction($"导出程序块失败: {exportEx.Message}");
                    LogHelper.WriteLogsAsync($"{block.Name},{exportEx.ToString()}", "块导出异常");
                    // 如果导出失败，尝试基本的接口解析
                    _logAction("尝试基本接口解析...");
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析程序块逻辑失败: {programBlock.Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// 通过 Openness 编译服务(GetService&lt;ICompilable&gt;().Compile())编译单个块/UDT。
        /// 用于"不一致对象无法导出"(Inconsistent blocks and PLC data types (UDT) cannot be exported)的自愈：
        /// 块引用的 UDT 被修改后未重新编译时 IsConsistent=false，Export/GenerateSource 会直接抛异常；
        /// 先编译使其恢复一致；编译失败不抛出，由调用方的导出异常兜底（跳过该块继续处理其余块）。
        /// 注意：Compile() 为同步阻塞调用；工程以只读方式打开时会失败，已捕获并仅记日志。
        /// </summary>
        private void TryCompile(IEngineeringServiceProvider engineeringObject, string name)
        {
            try
            {
                var compilable = engineeringObject.GetService<ICompilable>();
                if (compilable == null)
                {
                    _logAction($"{name}: 该对象不支持 Openness 编译，跳过自动编译");
                    return;
                }

                var result = compilable.Compile();
                if (result.State == CompilerResultState.Error)
                {
                    _logAction($"{name} 自动编译失败({result.ErrorCount}个错误，导出可能仍会失败): {DescribeCompileErrors(result)}");
                }
                else
                {
                    _logAction($"{name} 自动编译完成: State={result.State}, 错误={result.ErrorCount}, 警告={result.WarningCount}");
                }
            }
            catch (Exception ex)
            {
                _logAction($"{name} 自动编译异常(忽略，继续尝试导出): {ex.Message}");
            }
        }

        /// <summary>
        /// 汇总编译错误消息（最多取前几条，用于日志诊断）
        /// </summary>
        private static string DescribeCompileErrors(CompilerResult result, int maxMessages = 3)
        {
            try
            {
                var errors = result.Messages
                    .Where(m => m.State == CompilerResultState.Error)
                    .Take(maxMessages)
                    .Select(m => string.IsNullOrEmpty(m.Path) ? m.Description : $"{m.Path}: {m.Description}")
                    .ToList();
                return errors.Count > 0 ? string.Join(" | ", errors) : "(无错误详情)";
            }
            catch
            {
                return "(错误详情获取失败)";
            }
        }

        /// <summary>
        /// 从ML文件解析程序块逻辑
        /// </summary>
        private async Task ParseFromMlFileAsync(string mlFilePath, ProgramBlock programBlock)        {
            try
            {
                if (!File.Exists(mlFilePath))
                {
                    _logAction($"ML文件不存在: {mlFilePath}");
                    return;
                }

                // 根据编程语言选择合适的解析器
                var parser = GetParserForLanguage(programBlock.Language);
                if (parser != null)
                {
                    await parser.ParseFromXmlAsync(mlFilePath, programBlock);
                    _logAction($"使用{parser.GetType().Name}解析完成: {programBlock.Name}");
                }
                else
                {
                    _logAction($"不支持的编程语言: {programBlock.Language}");
                }
            }
            catch (Exception ex)
            {
                _logAction($"从ML文件解析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据编程语言获取解析器
        /// </summary>
        private IBlockParser GetParserForLanguage(string language)
        {
            return language switch
            {
                "LAD" => new LadderParser(_logAction),
                "FBD" => new LadderParser(_logAction), // 暂时使用LadderParser
                "STL" => new LadderParser(_logAction), // 暂时使用LadderParser
                "SCL" => new LadderParser(_logAction), // 暂时使用LadderParser
                "GRAPH" => new GraphParser(_logAction),
                _ => null
            };
        }

        /// <summary>
        /// 解析程序块接口
        /// </summary>
        private async Task ParseBlockInterfaceAsync(PlcBlock block, ProgramBlock programBlock)
        {
            try
            {
                _logAction($"解析程序块接口: {programBlock.Name}");
            }
            catch (Exception ex)
            {
                _logAction($"解析程序块接口失败: {programBlock.Name} - {ex.Message}");
            }
        }

        /// <summary>
        /// 解析数据块
        /// </summary>
        private async Task ParseDataBlocksAsync(PlcSoftware plcSoftware, TiaPortalProject projectInfo)
        {
            try
            {
                _logAction("解析数据块...");
                
                var blockGroup = plcSoftware.BlockGroup;
                
                // 递归解析数据块组（包括文件夹）
                await ParseDataBlockGroupRecursiveAsync(blockGroup, "", projectInfo);
                
                _logAction($"数据块解析完成，共{projectInfo.DataBlocks.Count}个数据块");
            }
            catch (Exception ex)
            {
                _logAction($"解析数据块失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归解析数据块组
        /// </summary>
        private async Task ParseDataBlockGroupRecursiveAsync(PlcBlockGroup blockGroup, string currentPath, TiaPortalProject projectInfo)
        {
            try
            {
                // 解析当前组中的数据块
                foreach (var block in blockGroup.Blocks)
                {
                    if (block is Siemens.Engineering.SW.Blocks.DataBlock dataBlock)
                    {
                        var dbInfo = new Models.DataBlock
                        {
                            Name = dataBlock.Name,
                            Number = dataBlock.Number,
                            Type = "Global DB",
                            Comment = "",
                            Size = 1024,
                            IsOptimized = false,
                            FolderPath = currentPath
                        };
                        
                        projectInfo.DataBlocks.Add(dbInfo);
                        
                        _logAction($"找到数据块: {dbInfo.Name} (DB{dbInfo.Number}) - 路径: {(string.IsNullOrEmpty(currentPath) ? "根目录" : currentPath)}");
                    }
                }

                // 递归解析子数据块组（子文件夹）
                foreach (var subGroup in blockGroup.Groups)
                {
                    string subPath = string.IsNullOrEmpty(currentPath) ? subGroup.Name : $"{currentPath}/{subGroup.Name}";
                    await ParseDataBlockGroupRecursiveAsync(subGroup, subPath, projectInfo);
                }
            }
            catch (Exception ex)
            {
                _logAction($"解析数据块组失败: {currentPath} - {ex.Message}");
            }
        }

        /// <summary>
        /// 获取程序块类型
        /// </summary>
        private string GetBlockType(PlcBlock block)
        {
            if (block is Siemens.Engineering.SW.Blocks.OB) return "OB";
            if (block is Siemens.Engineering.SW.Blocks.FB) return "FB";
            if (block is Siemens.Engineering.SW.Blocks.FC) return "FC";
            if (block is Siemens.Engineering.SW.Blocks.DataBlock) return "DB";
            return "Unknown";
        }

        /// <summary>
        /// 获取设备订货号
        /// </summary>
        private string GetDeviceOrderNumber(Device device)
        {
            try
            {
                return device.DeviceItems.FirstOrDefault()?.GetAttribute("OrderNumber")?.ToString() ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }

        /// <summary>
        /// 获取设备版本
        /// </summary>
        private string GetDeviceVersion(Device device)
        {
            try
            {
                return device.DeviceItems.FirstOrDefault()?.GetAttribute("Version")?.ToString() ?? "未知";
            }
            catch
            {
                return "未知";
            }
        }

        /// <summary>
        /// 获取设备IP地址
        /// </summary>
        private string GetDeviceIpAddress(Device device)
        {
            try
            {
                var networkInterface = device.DeviceItems
                    .SelectMany(di => di.DeviceItems)
                    .FirstOrDefault(di => di.Name.Contains("PN") || di.Name.Contains("Ethernet"));
                
                if (networkInterface != null)
                {
                    return networkInterface.GetAttribute("IpAddress")?.ToString() ?? "192.168.1.100";
                }
                
                return "192.168.1.100";
            }
            catch
            {
                return "192.168.1.100";
            }
        }

        /// <summary>
        /// 获取设备机架号
        /// </summary>
        private int GetDeviceRack(Device device)
        {
            try
            {
                var rackAttr = device.DeviceItems.FirstOrDefault()?.GetAttribute("Rack");
                if (rackAttr != null && int.TryParse(rackAttr.ToString(), out int rack))
                {
                    return rack;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 获取设备槽位号
        /// </summary>
        private int GetDeviceSlot(Device device)
        {
            try
            {
                var slotAttr = device.DeviceItems.FirstOrDefault()?.GetAttribute("Slot");
                if (slotAttr != null && int.TryParse(slotAttr.ToString(), out int slot))
                {
                    return slot;
                }
                return 1;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// 获取多语言文本的字符串值
        /// </summary>
        private string GetMultilingualText(MultilingualText multilingualText)
        {
            if (multilingualText == null) return "";
            
            try
            {
                return "";
            }
            catch
            {
                return "";
            }
        }

#endif

        public void CloseProj() {
#if TIA_PORTAL_AVAILABLE
            try
            {
                _currentProject.Close();
            }
            catch { }
#endif
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
#if TIA_PORTAL_AVAILABLE
                try
                {
                    _currentProject?.Close();
                    _tiaPortal?.Dispose();
                }
                catch (Exception ex)
                {
                    _logAction($"释放资源时发生错误: {ex.Message}");
                }
#endif
                _disposed = true;
            }
        }
    }
} 