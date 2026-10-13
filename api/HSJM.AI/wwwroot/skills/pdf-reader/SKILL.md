---
name: pdf-reader
description: 一个用于读取和提取PDF内容的技能包，可以从PDF中提取文本内容，支持多种PDF格式
---

# PDF 读取器

## 概述
这是一个专门用于读取和提取PDF文件内容的技能包。它能够从PDF文档中提取纯文本内容，并支持多种PDF格式。

## 使用方法
1. 准备一个PDF文件并获取其路径
2. 调用技能包，传入PDF文件路径
3. 技能将返回提取的文本内容

## 功能特性
- 从PDF中提取文本内容
- 支持多种PDF格式
- 简单易用的接口

## 输入格式
```json
{
  "pdfPath": "str"
}
```

## 输出格式
```json
{
  "textContent": "str"
}
```

## 依赖项
- PyPDF2

## 资源说明
- `scripts/` 目录包含主要的Python脚本实现
- `references/` 目录可用于存放相关技术文档
- `assets/` 目录可用于存放配置模板或其他资源