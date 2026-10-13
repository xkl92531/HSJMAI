#!/usr/bin/env python3
"""检查变量注释提取情况"""

import xml.etree.ElementTree as ET
import re

def extract_key_comments(file_path: str):
    """提取关键注释信息"""
    # 读取并处理
    content = open(file_path, 'r', encoding='utf-8').read()
    content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
    content = re.sub(r'<(/?)[\w]+:', r'<\1', content)
    
    root = ET.fromstring(content)
    
    # 查找所有Member
    members = root.findall('.//Member')
    print(f"Total members: {len(members)}")
    
    # 检查有注释的Member
    with_comment = 0
    sample_comments = []
    for m in members:
        name = m.get('Name', '')
        
        # 尝试不同路径找Comment
        comment_elem = m.find('.//Comment')
        if comment_elem is None:
            comment_elem = m.find('./Comment')
        
        if comment_elem is not None:
            # 找MultiLanguageText
            texts = []
            for text_elem in comment_elem.iter():
                if text_elem.text and text_elem.text.strip():
                    texts.append(text_elem.text.strip())
            
            if texts:
                with_comment += 1
                if len(sample_comments) < 10:
                    sample_comments.append((name, texts[0][:60]))
    
    print(f"Members with comments: {with_comment}")
    print("\nSample comments:")
    for name, comment in sample_comments:
        print(f"  {name}: {comment}")
    
    # 检查MultilingualText结构
    print("\n\n=== 检查 MultilingualText 结构 ===")
    mlt = root.findall('.//MultilingualText')[:5]
    for mt in mlt:
        print(f"MultilingualText found:")
        for child in mt:
            print(f"  {child.tag}: {child.get('Lang', '')} = {child.text}")
    
    return sample_comments

if __name__ == "__main__":
    # 为了兼容原有脚本
    file_path = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\LAD_FB720_OP70Manual.xml'
    extract_key_comments(file_path)