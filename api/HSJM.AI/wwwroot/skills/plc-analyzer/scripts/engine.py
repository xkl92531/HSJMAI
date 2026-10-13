#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 配置驱动的通用解析器

功能:
1. 自动检测PLC文件品牌/格式
2. 加载对应品牌配置
3. 基于配置规则提取结构化信息
4. 支持大文件分块处理
5. 输出标准化JSON

使用:
    python engine.py --file <文件路径> [--action preprocess|extract|full]

模块结构:
- models.py: 数据模型定义
- config_loader.py: 配置加载器
- parser.py: 核心解析引擎
- analyzers.py: 语义/架构/监控分析器
- formatters.py: 输出格式化
- engine.py: 命令行入口（本文件）
"""

import os
import sys
import json
import argparse
from dataclasses import asdict
from pathlib import Path
from typing import Dict
import uuid
from datetime import datetime

# 导入各模块
from models import FileInfo, ParseResult
from config_loader import ConfigLoader
from parser import PLCAnalyzerEngine
from analyzers import SemanticAnalyzer, ArchitectureAnalyzer, MonitorVariableAnalyzer
from formatters import (
    format_for_llm, 
    format_section, 
    generate_summary_output,
    format_architecture_analysis,
    format_monitor_analysis
)


def main():
    parser = argparse.ArgumentParser(description='PLC程序分析引擎')
    parser.add_argument('--file', '-f', required=True, help='PLC文件路径')
    parser.add_argument('--action', '-a', choices=['preprocess', 'extract', 'full'], 
                        default='full', help='操作类型')
    parser.add_argument('--output', '-o', help='输出文件路径，需要有文件名xx.json')
    parser.add_argument('--format', choices=['json', 'json-v2', 'llm', 'section', 'summary'], default='json',
                        help='输出格式: json=完整JSON, json-v2=V2.0 AI诊断格式, llm=LLM概览, section=分段, summary=摘要+文件路径')
    parser.add_argument('--section', '-s', default='overview',
                        help='分段输出时的段落: overview/networks/variables/calls/graph/comments/architecture/monitors')
    parser.add_argument('--max-tokens', type=int, default=4000,
                        help='LLM格式的最大token数')
    parser.add_argument('--brands-dir', help='品牌配置目录')
    
    args = parser.parse_args()

    # 检查文件存在
    if not os.path.exists(args.file):
        print(json.dumps({"error": f"文件不存在: {args.file}"}, ensure_ascii=False))
        sys.exit(1)

    # 初始化引擎
    engine = PLCAnalyzerEngine(
        ConfigLoader(args.brands_dir) if args.brands_dir else ConfigLoader()
    )

    # 2026-10-09 检查清单 #15：json-v2 只需要一次 extract，前置短路。
    # 原实现不传 --action 时默认 full，先执行 analyze_full（内部再调一次 extract）产出
    # output，随后 json-v2 分支又调 extract——同一文件被完整解析两遍且第一份结果被丢弃
    #（上传链路正是 "--format json-v2" 不带 --action 的组合）。任何 --action 组合下
    # json-v2 的输出本来就只由 extract 决定，前置处理后输出保持一致。
    if args.format == 'json-v2':
        result_v2 = engine.extract(args.file)
        output_v2 = result_v2.to_dict_v2()
        json_str = json.dumps(output_v2, ensure_ascii=False, indent=2)
        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(json_str)
        else:
            print(json_str)
        return

    # 执行操作
    if args.action == 'preprocess':
        result = engine.preprocess(args.file)
        output = asdict(result)
    elif args.action == 'extract':
        result = engine.extract(args.file)
        output = result.to_dict()
    else:  # full
        output = engine.analyze_full(args.file)
    
    # 格式化输出
    if args.format == 'summary':
        # 新模式：摘要输出到stdout，完整JSON保存到临时文件
        # 生成临时文件路径 - 保存在 skills/plc-analyzer/temp 目录下
        script_dir = Path(__file__).parent  # scripts/
        skill_dir = script_dir.parent  # plc-analyzer/
        temp_dir = skill_dir / 'temp'
        temp_dir.mkdir(exist_ok=True)
        
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        source_name = Path(args.file).stem
        json_filename = f"plc_parse_{source_name}_{timestamp}_{uuid.uuid4().hex[:6]}.json"
        json_filepath = str(temp_dir / json_filename)
        
        # 保存完整JSON到临时文件
        with open(json_filepath, 'w', encoding='utf-8') as f:
            json.dump(output, f, ensure_ascii=False, indent=2)
        
        # 生成精简摘要
        summary_output = generate_summary_output(output, json_filepath)
        print(summary_output)
        
    elif args.format == 'llm':
        formatted = format_for_llm(output, args.max_tokens)
        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(formatted)
        else:
            print(formatted)
    elif args.format == 'section':
        # 特殊处理架构和监控分析
        if args.section in ['architecture', 'monitors']:
            # 需要先获取 ParseResult 对象
            parse_result = engine.extract(args.file)
            if args.section == 'architecture':
                analyzer = ArchitectureAnalyzer()
                arch_result = analyzer.analyze(parse_result)
                formatted = format_architecture_analysis(arch_result)
            else:  # monitors
                analyzer = MonitorVariableAnalyzer()
                monitor_result = analyzer.analyze(parse_result)
                formatted = format_monitor_analysis(monitor_result)
        else:
            formatted = format_section(output, args.section)

        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(formatted)
        else:
            print(formatted)
    else:
        json_str = json.dumps(output, ensure_ascii=False, indent=2)
        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(json_str)
        else:
            print(json_str)


if __name__ == '__main__':
    main()
