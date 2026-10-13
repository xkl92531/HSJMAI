---
name: python-sandbox
description: Python 代码沙箱 (Python REPL)。执行 Python 代码来进行复杂的数据处理、数学计算或生成图表。
version: 1.0.0
author: System
---

# Python Sandbox Skill

## 🎯 描述
当 LLM 需要进行精确计算、处理复杂数据（如 CSV/JSON）、或生成图表（Matplotlib）时使用此技能。
比 LLM 直接生成答案更准确，且能产生可视化的结果。

## 📥 输入 (Input)
```json
{
  "code": "import math\nprint(math.sqrt(1024))"
}
```

- `code` (required): 要执行的 Python 完整代码。

## 📤 输出 (Output)
```json
{
  "success": true,
  "stdout": "32.0\n",
  "stderr": "",
  "result_files": []
}
```

## ⚠️ 注意事项
- 代码将在一个相对隔离的子进程中运行。
- 支持 `pandas`, `numpy`, `matplotlib` 等常用库。
- 如果代码生成了图片，请将其保存到当前工作目录，并打印文件路径。
