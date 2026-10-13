---
name: web-browser
description: 网页深度浏览技能 (Deep Browser)。使用无头浏览器 (Playwright) 访问 URL，可以处理 JavaScript 动态渲染的页面。支持提取文本、获取 HTML、截图等功能。
version: 1.0.0
author: System
---

# Web Browser Skill

## 🎯 描述
当用户需要访问具体网页链接 (URL) 并获取其内容时使用此技能。
特别适用于：
1. 需要 JavaScript 渲染的现代网页 (SPA)。
2. 需要抓取网页正文内容进行总结。
3. 验证网页是否可以访问。

## 📥 输入 (Input)
```json
{
  "url": "https://example.com",
  "action": "extract_text", 
  "selector": "main" 
}
```

- `url` (required): 目标网页地址。
- `action` (optional): 执行动作。
    - `extract_text` (default): 提取可见文本 (适合阅读)。
    - `get_html`: 获取完整 HTML 源码。
    - `screenshot`: 截取页面快照 (返回保存路径)。
- `selector` (optional): CSS 选择器，仅提取特定区域的内容 (例如 `article`, `.content`)。

## 📤 输出 (Output)
```json
{
  "success": true,
  "title": "Example Domain",
  "content": "Example Domain This domain is for use in illustrative examples...",
  "screenshot_path": "",
  "error": ""
}
```

## ⚠️ 注意事项
- 此技能使用 Playwright 运行 Chromium 内核。
- 首次运行可能需要下载浏览器二进制文件 (自动处理或需人工干预)。
- 对于简单的静态页面，搜索技能可能够用，但此技能更强大。
