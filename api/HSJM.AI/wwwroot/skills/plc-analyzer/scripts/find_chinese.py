#!/usr/bin/env python3
"""深度探索XML中的所有中文注释"""

import xml.etree.ElementTree as ET
import re
from collections import defaultdict

def has_chinese_characters(text):
    """检查文本是否包含中文字符"""
    if not text:
        return False
    return any('\u4e00' <= c <= '\u9fff' for c in str(text))

def analyze_chinese_content(file_path: str):
    """分析XML文件中的中文内容"""
    # 读取并处理
    content = open(file_path, 'r', encoding='utf-8').read()
    content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
    content = re.sub(r'<(/?)[\w]+:', r'<\1', content)
    
    root = ET.fromstring(content)
    
    # 收集所有包含中文的文本
    chinese_texts = []
    
    def get_path(root, target):
        """获取元素路径（简化版）"""
        return target.tag
    
    # 遍历所有元素
    for elem in root.iter():
        # 检查元素文本
        if elem.text and has_chinese_characters(elem.text):
            parent_tag = elem.tag
            chinese_texts.append({
                'tag': parent_tag,
                'text': elem.text.strip()[:80],
                'path': get_path(root, elem)
            })
        
        # 检查属性
        for attr, val in elem.attrib.items():
            if has_chinese_characters(str(val)):
                chinese_texts.append({
                    'tag': f"{elem.tag}@{attr}",
                    'text': str(val)[:80],
                    'path': ''
                })
    
    # 按标签分组统计
    by_tag = defaultdict(list)
    for item in chinese_texts:
        by_tag[item['tag']].append(item['text'])
    
    print("=" * 60)
    print("中文文本按标签分布")
    print("=" * 60)
    
    for tag, texts in sorted(by_tag.items(), key=lambda x: -len(x[1])):
        print(f"\n【{tag}】({len(texts)}条)")
        for t in texts[:5]:
            print(f"  - {t}")
        if len(texts) > 5:
            print(f"  ... 及其他 {len(texts)-5} 条")
    
    # 特别关注 MultilingualTextItem
    print("\n" + "=" * 60)
    print("MultilingualTextItem 详细内容")
    print("=" * 60)
    
    for mti in root.iter('MultilingualTextItem'):
        lang = mti.get('Id', '')
        text_elem = mti.find('Text')
        if text_elem is not None and text_elem.text:
            text = text_elem.text.strip()
            if text and len(text) > 3:
                # 找父元素
                print(f"[{lang}] {text[:80]}")
    
    # 检查 CompileUnit (Network)
    print("\n" + "=" * 60)
    print("CompileUnit/Network 标题和注释")
    print("=" * 60)
    
    for cu in root.iter('SW.Blocks.CompileUnit'):
        # 查找Title
        for title in cu.iter('Title'):
            for mti in title.iter('MultilingualTextItem'):
                text_elem = mti.find('Text')
                if text_elem is not None and text_elem.text:
                    print(f"[Title] {text_elem.text.strip()[:80]}")
        
        # 查找Comment
        for comment in cu.iter('Comment'):
            for mti in comment.iter('MultilingualTextItem'):
                text_elem = mti.find('Text')
                if text_elem is not None and text_elem.text:
                    print(f"[Comment] {text_elem.text.strip()[:80]}")
    
    return by_tag

if __name__ == "__main__":
    # 为了兼容原有脚本
    file_path = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\LAD_FB720_OP70Manual.xml'
    analyze_chinese_content(file_path)