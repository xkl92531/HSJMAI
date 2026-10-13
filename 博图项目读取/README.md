# 博图项目读取工具

这是一个基于 .NET Framework 4.8.1 的博图项目读取工具，用于解析西门子TIA Portal项目文件并导出为多种格式。

## 功能特性

- **连接博图软件**：自动连接到已运行的TIA Portal实例或启动新实例
- **项目解析**：深度解析博图项目结构，提取关键信息
- **数据提取**：
  - 设备信息（PLC型号、订货号等）
  - 符号表（变量名、地址、数据类型、注释）
  - 程序块（OB、FB、FC、DB的详细信息）
- **多格式导出**：
  - JSON格式（供API接口使用）
  - HTML报告（可视化展示）
  - CSV格式（符号表导出）

## 系统要求

- Windows 10/11
- .NET Framework 4.8.1
- 西门子TIA Portal V15/V16/V17/V18
- 管理员权限（用于COM组件访问）

## 项目结构

```
博图项目读取/
├── Models/                 # 数据模型
│   └── TiaPortalProject.cs
├── Services/              # 服务层
│   ├── TiaPortalService.cs      # TIA Portal交互服务
│   └── ProjectExportService.cs  # 项目导出服务
├── Form1.cs              # 主窗体
├── Form1.Designer.cs     # 窗体设计器
├── Form1.resx           # 窗体资源
├── Program.cs           # 程序入口
└── README.md           # 说明文档
```

## 安装说明

1. 确保已安装TIA Portal软件
2. 安装 .NET Framework 4.8.1
3. 以管理员身份运行应用程序
4. 首次运行时会自动查找TIA Portal SDK

## 使用步骤

### 1. 连接博图软件
点击"连接博图软件"按钮，程序会：
- 尝试连接到已运行的TIA Portal实例
- 如果没有运行实例，会启动新的TIA Portal（无界面模式）

### 2. 选择项目
点击"选择项目"按钮，选择要解析的博图项目文件：
- 支持格式：`.ap17`, `.ap16`, `.ap15`
- 项目文件通常位于项目文件夹中

### 3. 解析项目
点击"解析项目"按钮，程序会：
- 打开选定的项目文件
- 遍历所有设备和程序块
- 提取符号表信息
- 分析程序块结构

### 4. 查看结果
解析完成后，可以在三个选项卡中查看结果：
- **设备信息**：显示项目中的所有PLC设备
- **符号表**：显示变量定义和地址映射
- **程序块**：显示所有程序块的基本信息

### 5. 导出数据
根据需要选择导出格式：
- **JSON导出**：结构化数据，适合API集成
- **HTML报告**：可视化报告，适合文档归档
- **CSV导出**：符号表数据，适合Excel处理

## 技术特点

### 架构设计
- **模块化设计**：清晰的分层架构，便于维护和扩展
- **服务分离**：TIA Portal交互和数据导出分离
- **异步处理**：使用async/await模式，避免界面卡顿
- **错误处理**：完善的异常处理和日志记录

### 兼容性
- 支持 .NET Framework 4.8.1
- 兼容多个TIA Portal版本（V15/V16/V17/V18）
- 智能SDK路径检测
- 条件编译支持（有/无SDK环境）

### 代码特色
- 遵循中文编程思路：代码即文档，简洁明了
- 函数命名直观，一看就知道功能
- 变量作用域最小化
- 完善的错误提示和日志输出

## 注意事项

1. **权限要求**：必须以管理员身份运行，否则无法访问COM组件
2. **TIA Portal版本**：确保安装了正确版本的TIA Portal
3. **项目兼容性**：程序支持V15及以上版本的项目文件
4. **内存占用**：大型项目解析时可能占用较多内存
5. **SDK路径**：程序会自动检测多个常见的SDK安装路径

## 故障排除

### 连接失败：`"Siemens.Engineering.Private.TiaPortalAccess"的类型初始值设定项引发异常`

这是 TIA Portal Openness 初始化失败的典型症状，原因按概率排序：

1. **引用的 Openness DLL 版本与运行机器安装的 TIA Portal 大版本不一致（最常见）**。
   Openness **不跨大版本兼容**：程序引用哪个版本的 `Siemens.Engineering.dll`，运行它的机器就必须安装同一大版本的 TIA Portal。
   例：远程机装的是 TIA Portal **V20**，程序却引用了 V19 的 DLL → 一调 `TiaPortal.GetProcesses()` 即报此错。
   **修复**：从远程机 `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20\` 拷贝
   `Siemens.Engineering.dll`、`Siemens.Engineering.Hmi.dll` 到本项目 `20dll\` 文件夹，重新编译部署。
   csproj 会自动按 `20dll → 19dll → 18dll → 本机已安装的 Portal V15.1~V20 PublicAPI` 顺序探测引用。
2. **目标机未安装 TIA Portal，或安装时没勾选 "TIA Portal Openness" 组件**（V16 起该组件默认不安装）。
3. **程序以 32 位进程运行**：TIA V16+ 只有 64 位 Openness，平台目标须 x64 / AnyCPU（不勾选首选 32 位）。
4. **V17+ 白名单**：Openness 程序首次使用需以管理员身份运行一次并在弹窗中确认加入白名单，
   或在 `HKLM\SOFTWARE\SIEMENS\Automation\Openness\<版本>\Whitelist` 手工登记 exe 路径。

其他常规检查：以管理员身份运行、关闭占用 TIA 的其他程序、查看日志。

> 程序在连接前会做环境自检（进程位数 / 注册表 Openness 版本 / 引用 DLL 版本匹配），不匹配时直接给出中文原因与修复建议，
> 不再抛出晦涩的类型初始化异常；真发生异常时也会展开内部异常链输出根因。

### 项目打开失败
- 检查项目文件是否完整
- 确认项目版本与TIA Portal版本兼容
- 关闭TIA Portal中已打开的项目

### 解析异常
- 检查项目是否包含不支持的功能块
- 确认项目没有密码保护
- 查看日志输出获取详细错误信息

## 扩展开发

如需扩展功能，可以：
1. 在`TiaPortalService`中添加新的解析逻辑
2. 在`ProjectExportService`中添加新的导出格式
3. 在`Models`中添加新的数据模型
4. 在主窗体中添加新的功能按钮

## 技术支持

如果遇到问题，请：
1. 查看日志输出获取详细错误信息
2. 确认系统要求是否满足
3. 检查TIA Portal安装是否正确
4. 确认项目文件是否完整

## 更新日志

### v1.0.0
- 初始版本发布
- 基础的项目解析功能
- 支持JSON、HTML、CSV导出
- 完整的WinForms界面 