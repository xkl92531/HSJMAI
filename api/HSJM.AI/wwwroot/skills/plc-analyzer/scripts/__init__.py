#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎包

模块结构:
- models: 数据模型定义
- config_loader: 配置加载器
- parser: 核心解析引擎
- analyzers: 语义/架构/监控分析器
- formatters: 输出格式化
- engine: 命令行入口
"""

from .models import (
    FileInfo, BlockInfo, VariableInfo, CallInfo, NetworkInfo,
    GraphStep, GraphTransition, VariableAccess, LogicUnit,
    CrossReferences, SemanticHint, ParseResult
)
from .config_loader import ConfigLoader
from .parser import PLCAnalyzerEngine
from .analyzers import SemanticAnalyzer, ArchitectureAnalyzer, MonitorVariableAnalyzer
from .formatters import (
    format_for_llm, format_section, generate_summary_output,
    format_architecture_analysis, format_monitor_analysis
)

__all__ = [
    # Models
    'FileInfo', 'BlockInfo', 'VariableInfo', 'CallInfo', 'NetworkInfo',
    'GraphStep', 'GraphTransition', 'VariableAccess', 'LogicUnit',
    'CrossReferences', 'SemanticHint', 'ParseResult',
    # Config
    'ConfigLoader',
    # Parser
    'PLCAnalyzerEngine',
    # Analyzers
    'SemanticAnalyzer', 'ArchitectureAnalyzer', 'MonitorVariableAnalyzer',
    # Formatters
    'format_for_llm', 'format_section', 'generate_summary_output',
    'format_architecture_analysis', 'format_monitor_analysis',
]
