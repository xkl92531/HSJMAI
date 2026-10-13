#!/usr/bin/env python3
"""最简单的XML标签测试 - 输出到文件"""
import re
import xml.etree.ElementTree as ET

file_path = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\DB20_SafeControl.ml'
output_file = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\api\HSJM.AI\wwwroot\skills\plc-analyzer\scripts\simple_test_output.txt'

with open(output_file, 'w', encoding='utf-8') as out:
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
        
        out.write("=== 原始内容前100字符 ===\n")
        out.write(content[:100] + "\n\n")
        
        # 移除命名空间
        content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
        content = re.sub(r'<(\/?)[a-zA-Z0-9_]+:', r'<\1', content)
        
        out.write("=== 处理后内容前100字符 ===\n")
        out.write(content[:100] + "\n\n")
        
        root = ET.fromstring(content)
        
        out.write("=== Root 标签 ===\n")
        out.write(f"root.tag = {root.tag}\n\n")
        
        out.write("=== 顶层子元素 ===\n")
        for child in root:
            out.write(f"  child.tag = {child.tag}\n")
        
        out.write("\n=== 所有包含 SW 或 Block 的标签 ===\n")
        count = 0
        for elem in root.iter():
            if 'SW' in elem.tag or 'Block' in elem.tag:
                out.write(f"  {elem.tag}\n")
                count += 1
        out.write(f"共找到 {count} 个匹配标签\n\n")
        
        out.write("=== 测试标签匹配逻辑 ===\n")
        block_tags = ['SW.Blocks.FB', 'SW.Blocks.FC', 'SW.Blocks.DB', 
                      'SW.Blocks.OB', 'SW.Blocks.GlobalDB', 'SW.Blocks.InstanceDB']
        
        for elem in root.iter():
            if elem.tag in block_tags:
                out.write(f"EXACT MATCH: {elem.tag}\n")
            elif '.' in elem.tag and elem.tag.startswith('SW.Blocks.'):
                out.write(f"PATTERN MATCH: {elem.tag}\n")
        
        out.write("\n=== 完成 ===\n")
        
    except Exception as e:
        out.write(f"ERROR: {e}\n")
        import traceback
        out.write(traceback.format_exc())

print("done")
