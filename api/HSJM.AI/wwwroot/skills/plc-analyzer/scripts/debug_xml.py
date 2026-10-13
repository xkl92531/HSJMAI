#!/usr/bin/env python3
"""调试脚本 - 检查XML标签名"""
import re
import xml.etree.ElementTree as ET
import sys

file_path = sys.argv[1] if len(sys.argv) > 1 else r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\DB20_SafeControl.ml'
output_file = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\api\HSJM.AI\wwwroot\skills\plc-analyzer\scripts\debug_output.txt'

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# 移除命名空间
content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
content = re.sub(r'<(\/?)[a-zA-Z0-9_]+:', r'<\1', content)

root = ET.fromstring(content)

with open(output_file, 'w', encoding='utf-8') as out:
    out.write("=== 所有元素标签 ===\n")
    for elem in root.iter():
        if 'Block' in elem.tag or 'SW' in elem.tag or 'Blocks' in elem.tag:
            out.write(f"Tag: {elem.tag}\n")

    out.write("\n=== 顶层子元素 ===\n")
    for child in root:
        out.write(f"Child: {child.tag}\n")

print(f"Output written to {output_file}")
