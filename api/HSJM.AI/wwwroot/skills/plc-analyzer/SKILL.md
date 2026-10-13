---
name: plc-analyzer
description: 解析西门子TIA Portal导出的PLC程序XML文件，提取结构化数据
version: 1.4.0
author: HSJM-AI
---

# PLC程序解析技能

## ⚡ 快速使用

```bash
# 推荐：摘要输出+自动保存完整数据到临时文件
python scripts/engine.py --file "<PLC文件路径>" --format summary
```

---

## 📋 命令参数

| 参数 | 说明 |
|------|------|
| `--format summary` | **推荐** 摘要→stdout，完整JSON→临时文件，返回文件路径 |
| `--format json` | 完整JSON直接输出 |
| `--format llm` | LLM友好的摘要输出 |
| `--format section --section <名称>` | 分段输出 |

### section 可选值

| 值 | 内容 |
|----|------|
| `overview` | 概览统计 |
| `networks` | Network段落 |
| `variables` | 变量汇总 |
| `variables_IN` | 输入变量 |
| `variables_OUT` | 输出变量 |
| `calls` | 调用关系 |
| `graph` | GRAPH步序 |
| `comments` | 关键注释 |
| `architecture` | 程序块+调用数据 |
| `monitors` | 变量分类数据 |

---

## 📤 输出说明

**`--format summary` 输出：**
- stdout：精简摘要（程序块、统计、关键注释）
- 临时文件：完整JSON数据，保存在 `skills/plc-analyzer/temp/` 目录
- **文件路径**：在输出末尾，可传给入库工具

**输出内容包含：**
- 程序块、变量、调用关系、Network、GRAPH步序

---

## 🔧 支持格式

| 品牌 | 文件 | 块类型 | 语言 |
|------|------|--------|------|
| Siemens TIA V16-18 | .xml | FB/FC/DB/OB | LAD/SCL/GRAPH |

---

## ❌ 错误返回

```json
{"error": true, "message": "错误描述"}
```
