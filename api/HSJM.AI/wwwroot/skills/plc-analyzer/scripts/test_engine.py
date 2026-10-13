#!/usr/bin/env python3
"""独立测试脚本 - 输出结果到文件"""
import os
import sys

# 设置调试环境变量
os.environ['PLC_DEBUG'] = '1'

# 添加当前目录到路径
sys.path.insert(0, os.path.dirname(__file__))

from engine import PLCAnalyzerEngine

output_file = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\api\HSJM.AI\wwwroot\skills\plc-analyzer\scripts\test_output.txt'
test_file = r'e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\DB20_SafeControl.ml'

# 重定向 stderr 到文件
with open(output_file, 'w', encoding='utf-8') as out:
    old_stderr = sys.stderr
    sys.stderr = out
    
    try:
        out.write("=== 开始测试 ===\n\n")
        
        engine = PLCAnalyzerEngine()
        
        out.write("1. 预处理阶段\n")
        file_info = engine.preprocess(test_file)
        out.write(f"  品牌: {file_info.brand}\n")
        out.write(f"  产品: {file_info.product}\n")
        out.write(f"  版本: {file_info.version}\n")
        out.write(f"  编码: {file_info.encoding}\n\n")
        
        out.write("2. 提取阶段\n")
        result = engine.extract(test_file)
        out.write(f"  程序块数: {len(result.blocks)}\n")
        out.write(f"  变量数: {len(result.variables)}\n")
        out.write(f"  调用数: {len(result.calls)}\n")
        out.write(f"  错误: {result.errors}\n\n")
        
        if result.blocks:
            out.write("3. 程序块详情\n")
            for b in result.blocks:
                out.write(f"  - {b.name} (Type: {b.type}, Lang: {b.language})\n")
        
        out.write("\n=== 测试完成 ===\n")
        
    except Exception as e:
        out.write(f"ERROR: {e}\n")
        import traceback
        traceback.print_exc(file=out)
    finally:
        sys.stderr = old_stderr

print(f"Output written to {output_file}")
