---
name: skill-creator
description: 创建或更新技能包。用于设计、开发、打包带有脚本和资源的 Agent Skills 技能。当需要创建新技能、生成技能模板、或打包技能时使用此技能。
---

# Skill Creator（技能创建器）

用于创建符合 Agent Skills 规范的技能包。

## 技能结构

每个技能包包含：

```
skill-name/
├── SKILL.md          # 必需：技能定义和使用指南
├── scripts/          # 可选：可执行脚本 (Python/Shell/JS)
├── references/       # 可选：参考文档
└── assets/           # 可选：模板、图片等资源
```

### SKILL.md 格式

```yaml
---
name: skill-name               # 必需：kebab-case 格式
description: 技能描述和触发条件  # 必需：说明什么时候使用
---

# 技能标题

## 概述
简要说明技能功能

## 使用方法
具体的使用步骤和指导

## 资源说明（如有）
scripts/、references/、assets/ 目录说明
```

## 创建流程

### 步骤 1: 初始化技能

**命令行调用（推荐）：**

```
ExecuteSkillScriptAsync(
  skillPackagePath: "skill-creator/",
  scriptName: "scripts/init_skill.py",
  arguments: "pdf-reader --path skills/ --resources scripts"
)
```

**JSON 调用（可选）：**

```json
{
  "skillName": "pdf-reader",
  "path": "skills/",
  "resources": ["scripts"],
  "description": "用于读取和提取PDF文件文本的技能"
}
```

**字段说明：**
- `skillName` / `skill_name` / `name`: 技能名称（会自动转为 kebab-case）
- `path`: 输出目录（默认 `skills/`）
- `resources`: 资源目录数组（如 `["scripts", "references", "assets"]`）
- `description`: 技能描述（可选）
- `usage`: 使用方法（可选）
- `workflow`: 工作流程（可选）

⚠️ **重要：**
- JSON 字段名使用 `skillName` (驼峰) 或 `skill_name` (下划线)，**不要用** `skill-name` (短横线)
- 命令行参数直接使用技能名作为第一个位置参数

### 步骤 2: 填充核心文件

⚠️ **重要约定**：
1. **不要创建 README.md** - SKILL.md 已足够，避免文档重复
2. **main.py 作为入口脚本** - 必须保留 scripts/main.py 作为主入口点
3. **允许多个辅助脚本** - 可以创建其他脚本（如 pdf_processor.py, utils.py），但 main.py 负责协调
4. **requirements.txt 只在 scripts/ 下** - 不要在技能根目录创建
5. **更新 SKILL.md 内容** - 替换模板占位符，填充实际功能描述
6. **填充 scripts/requirements.txt** - 添加实际的 Python 依赖包

### 步骤 3: （可选）添加高级功能

如需更复杂的功能：
- 在 `scripts/` 中添加 **辅助模块**（如 `utils.py`、`parser.py`）
- 在 `main.py` 中 import 使用这些模块
- 在 `references/` 添加技术文档
- 在 `assets/` 添加配置模板

**示例项目结构**：
```
pdf-reader/
├── SKILL.md
└── scripts/
    ├── main.py              # 入口脚本（必须保留）
    ├── requirements.txt     # 依赖列表（必须保留）
    ├── pdf_utils.py         # 辅助模块（可选）
    └── config.py            # 配置模块（可选）
```

### 步骤 4: 完成检查

执行前必须确认：
- ✅ SKILL.md frontmatter 包含 name 和 description
- ✅ scripts/main.py 存在且作为入口脚本
- ✅ scripts/requirements.txt 列出了所有依赖
- ✅ 没有创建 README.md
- ✅ 没有在根目录创建 requirements.txt
- ⚠️ **重要**：仅检查文件是否成功创建，**不要**尝试运行脚本进行验证（可能缺少外部环境）。

### 步骤 5: 注册技能

使用 `SaveSkillDefinitionAsync` 工具保存技能定义并自动关联角色：

```csharp
SaveSkillDefinitionAsync(
    skillCode: "skill-name",
    skillName: "Skill Name",
    description: "技能描述",
    skillPackagePath: "skill-name/",
    roleId: 2012  // 默认为通用助手，可省略
)
```

⚠️ **关键规则：**
1. **先检查目录**: 调用 `init_skill.py` 前，先确认目标目录不存在。
2. **避免循环**: 技能创建并注册成功后，任务即视为完成，**请立即停止**，不要重复执行初始化或创建步骤。
3. **无需 SQL**: 使用上述工具即可，不要生成 INSERT SQL 语句。

### 步骤 6: 安装依赖（如果有）

如果技能包含 `scripts/requirements.txt`，生成后请务必指导用户或自动调用工具安装依赖：

```csharp
InstallSkillDependenciesAsync(
    skillPackagePath: "skill-name"
)
```

## 技能开发规范 (重要)

为了确保技能在所有环境中稳定运行，**必须**遵守以下开发规范：

### 1. 脚本 I/O 标准
- **输入 (Input)**: 优先从 `sys.stdin` 读取 JSON 字符串。
  - 必须处理潜在的 BOM 头：`input_str = sys.stdin.read().strip().lstrip('\ufeff')`
- **输出 (Output)**: 
  - **结果**: 仅将最终 JSON 结果打印到 `sys.stdout`。
  - **日志**: 所有调试/日志信息必须打印到 `sys.stderr`。
- **异常**: 捕获所有异常，输出 `{"error": "message"}` 到 stdout 并设置非零退出码。

### 2. 依赖管理
- 如果使用第三方库，必须在 `scripts/requirements.txt` 中列出。
- 尽量使用标准库以减少依赖。

### 3. 文件处理与中文支持 (关键)
- **PDF 解析**: 严禁使用 `PyPDF2` 处理中文文档（易乱码）。**必须优先使用 `pdfplumber`**。
- **Excel/CSV**: 使用 `pandas` 或 `openpyxl`，读取 CSV 时注意尝试 `encoding='utf-8-sig'` 或 `GBK`。
- **文本读写**: `open(file, encoding='utf-8')` 必须显式指定编码。
- **JSON 输出**: 必须通过 `json.dumps(obj, ensure_ascii=False)` 输出，保证日志和结果中的中文可读，而非 Unicode 转义符。

### 4. 代码模板示例 (Python)
### 4. 代码模板示例 (Python)
```python
import sys
import json
import os
import argparse
import base64

def main():
    # 强制 stdout 使用 UTF-8 (解决 Windows 中文乱码)
    sys.stdout.reconfigure(encoding='utf-8')
    
    parser = argparse.ArgumentParser()
    parser.add_argument("input_file", nargs="?", help="Path to JSON input file")
    parser.add_argument("--input-base64", help="Base64 encoded JSON string")
    args_parsed = parser.parse_args()

    input_str = "{}"

    try:
        # 优先级 1: 临时文件 (兼容 ScriptExecutorTool 更新)
        if args_parsed.input_file and os.path.isfile(args_parsed.input_file):
            with open(args_parsed.input_file, "r", encoding="utf-8") as f:
                input_str = f.read()

        # 优先级 2: Base64 参数 (兼容 ScriptExecutorTool 旧版/备用)
        elif args_parsed.input_base64:
            input_bytes = base64.b64decode(args_parsed.input_base64)
            input_str = input_bytes.decode('utf-8')

        # 优先级 3: 标准输入 (sys.stdin)
        else:
            # 尝试读取 stdin，处理潜在的 BOM
            input_data = sys.stdin.read().strip().lstrip('\ufeff')
            if input_data:
                input_str = input_data

        args = json.loads(input_str) if input_str.strip() else {}
        
        # 2. 业务逻辑 (如需打印日志，请用 stderr)
        sys.stderr.write(f"Debug: Processing {args}\n")
        
        # 3. 结果输出 (标准 JSON 到 stdout)
        result = {"status": "success", "data": "..."}
        print(json.dumps(result, ensure_ascii=False))
        
    except Exception as e:
        # 4. 错误处理
        sys.stderr.write(f"Error: {str(e)}\n")
        print(json.dumps({"error": str(e)}, ensure_ascii=False))
        sys.exit(1)

if __name__ == "__main__":
    main()
```

### 1. 简洁优先
只包含必要信息，避免冗余文档。

### 2. 渐进式披露
- SKILL.md 保持 <500 行
- 详细内容放 references/
- 复杂逻辑放 scripts/

### 3. 明确触发条件
description 字段清楚说明何时使用此技能。

### 4. 支持多种脚本类型
- Python (.py): 数据处理、API 调用
- Shell (.sh): 系统命令
- Node.js (.js): JavaScript 处理
- PowerShell (.ps1): Windows 自动化
