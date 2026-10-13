# Web Search Skill

## 描述
这是一个联网搜索增强技能，赋予 Agent "上网" 的能力。
它使用 DuckDuckGo 搜索引擎进行匿名、即时的网络搜索，无需额外 API Key。
适用于：查询实时新闻、查找技术文档、验证事实、搜索代码片段等。

## 技能特性
- **实时性**：获取最新信息（训练数据之外）。
- **隐私性**：使用 DuckDuckGo，不主动跟踪用户。
- **轻量级**：直接返回搜索结果摘要和链接。

## 输入格式 (Input Schema)
```json
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "搜索关键词 (例如: 'C# 12 new features', '今日AI新闻')"
    },
    "max_results": {
      "type": "integer",
      "description": "最大结果数量 (默认 5)",
      "default": 5
    },
    "region": {
      "type": "string",
      "description": "搜索地区代码 (例如 'wt-wt' 全球, 'cn-zh' 中国, 'us-en' 美国)",
      "default": "wt-wt"
    }
  },
  "required": ["query"]
}
```

## 输出格式 (Output Schema)
脚本将输出 JSON 格式的结果：
```json
{
  "success": true,
  "results": [
    {
      "title": "News Title",
      "href": "https://example.com/news",
      "body": "News summary content..."
    }
  ],
  "error": null
}
```

## 使用指南 (Instructions)
1. **明确目的**：当用户问及最新的事件、具体的报错信息库、或者需要外部验证的事实与数据时，请优先使用此技能。
2. **关键词优化**：根据用户意图提取最核心的 2-3 个关键词。避免使用过长的自然语言句子搜索。
3. **结果处理**：技能返回的是搜索结果列表。请阅读 `body` 摘要，从中提取答案回答用户。如果摘要信息不足，告诉用户你找到了相关链接，并给出链接。
