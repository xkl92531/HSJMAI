#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 输出格式化

提供各种格式的输出功能
"""

import os
from collections import defaultdict
from typing import Dict, List


def format_for_llm(result: Dict, max_tokens: int = 4000) -> str:
    """格式化输出供LLM分析（控制token）"""
    output = []
    
    # ========== 语义摘要（置顶，最重要） ==========
    semantic = result.get('semantic_hints', {})
    if semantic.get('inferred_purpose'):
        output.append("## 🎯 功能摘要")
        output.append(f"**{semantic.get('inferred_purpose')}**")
        output.append("")
        
        if semantic.get('station_type'):
            output.append(f"- 工位类型: **{semantic.get('station_type')}**")
        if semantic.get('function_category'):
            output.append(f"- 功能类别: {semantic.get('function_category')}")
        if semantic.get('keywords_detected'):
            keywords = semantic.get('keywords_detected', [])[:10]
            output.append(f"- 关键词: {', '.join(keywords)}")
        output.append("")
    
    # ========== 关键注释（语义上下文） ==========
    key_comments = result.get('key_comments', [])
    if key_comments:
        output.append("## 📝 关键注释")
        for comment in key_comments[:8]:
            output.append(f"- {comment}")
        output.append("")
    
    # ========== Network段落（逻辑结构） ==========
    networks = result.get('networks', [])
    if networks:
        output.append(f"## 📋 程序段落 ({len(networks)}个Network)")
        for net in networks[:12]:
            title = net.get('title', '')
            comment = net.get('comment', '')
            line = f"- N{net.get('id', '')}: {title}"
            if comment and comment != title:
                line += f" ({comment[:30]})"
            output.append(line)
        output.append("")
    
    # ========== 文件信息 ==========
    file_info = result.get('file_info', {})
    output.append(f"## 文件信息")
    output.append(f"- 路径: {file_info.get('path', '')}")
    output.append(f"- 品牌: {file_info.get('brand', '')} {file_info.get('product', '')}")
    output.append(f"- 版本: {file_info.get('version', '')}")
    output.append(f"- 总行数: {file_info.get('total_lines', 0)}")
    output.append("")
    
    # 程序块
    blocks = result.get('blocks', [])
    output.append(f"## 程序块 ({len(blocks)}个)")
    for block in blocks[:10]:  # 最多显示10个块
        output.append(f"- **{block.get('name', '')}** ({block.get('type', '')}{block.get('number', '')}) - {block.get('language', '')}")
        if block.get('comment'):
            output.append(f"  注释: {block.get('comment', '')[:100]}")
    output.append("")
    
    # 变量（按优先级裁剪）
    variables = result.get('variables', [])
    scope_priority = {'IN': 1, 'OUT': 2, 'INOUT': 3, 'STAT': 4, 'TEMP': 5}
    sorted_vars = sorted(variables, key=lambda v: scope_priority.get(v.get('scope', ''), 10))
    
    output.append(f"## 关键变量 (共{len(variables)}个，显示前30)")
    for var in sorted_vars[:30]:
        line = f"- `{var.get('path', '')}`: {var.get('datatype', '')} [{var.get('scope', '')}]"
        if var.get('comment'):
            line += f" // {var.get('comment', '')[:50]}"
        output.append(line)
    output.append("")
    
    # 调用关系
    calls = result.get('calls', [])
    if calls:
        output.append(f"## 调用关系 ({len(calls)}个)")
        for call in calls[:20]:
            output.append(f"- {call.get('caller', '')} → {call.get('callee', '')}")
        output.append("")
    
    # GRAPH步序
    steps = result.get('graph_steps', [])
    if steps:
        output.append(f"## GRAPH步序 ({len(steps)}个)")
        for step in steps[:15]:
            line = f"- Step {step.get('number', '')}: {step.get('name', '')}"
            if step.get('is_init'):
                line += " [初始步]"
            output.append(line)
        output.append("")
    
    return '\n'.join(output)


def format_section(result: Dict, section: str) -> str:
    """
    分段输出 - 支持LLM逐段分析
    
    section参数:
    - overview: 整体概览（语义摘要+块信息）
    - networks: 所有Network段落详情
    - network_N: 第N个Network的详情
    - variables: 变量分析（按作用域分组）
    - variables_IN/OUT/STAT: 特定作用域的变量
    - calls: 调用关系分析
    - graph: GRAPH步序详情
    - step_N: 第N步的详情
    - comments: 所有关键注释
    """
    output = []
    
    if section == 'overview':
        # ========== 整体概览 ==========
        semantic = result.get('semantic_hints', {})
        output.append("# 📊 程序整体概览")
        output.append("")
        
        if semantic.get('inferred_purpose'):
            output.append(f"## 功能定位")
            output.append(f"**{semantic.get('inferred_purpose')}**")
            output.append("")
        
        file_info = result.get('file_info', {})
        output.append("## 文件信息")
        output.append(f"| 属性 | 值 |")
        output.append(f"|------|-----|")
        output.append(f"| 文件 | {os.path.basename(file_info.get('path', ''))} |")
        output.append(f"| 品牌 | {file_info.get('brand', '')} {file_info.get('product', '')} |")
        output.append(f"| 版本 | {file_info.get('version', '')} |")
        output.append(f"| 总行数 | {file_info.get('total_lines', 0)} |")
        output.append("")
        
        # 块列表
        blocks = result.get('blocks', [])
        output.append("## 程序块")
        for block in blocks:
            output.append(f"- **{block.get('name')}** ({block.get('type')}{block.get('number')}) - {block.get('language', 'N/A')}")
        output.append("")
        
        # 统计摘要
        summary = result.get('summary', {})
        output.append("## 统计信息")
        output.append(f"| 类型 | 数量 |")
        output.append(f"|------|------|")
        output.append(f"| 变量 | {summary.get('total_variables', 0)} |")
        output.append(f"| Network段 | {summary.get('total_networks', 0)} |")
        output.append(f"| 调用关系 | {summary.get('total_calls', 0)} |")
        output.append(f"| GRAPH步 | {summary.get('total_graph_steps', 0)} |")
        output.append("")
        
        # 分析导航
        output.append("## 📍 分段分析导航")
        output.append("接下来可以按以下顺序深入分析：")
        output.append("1. `--section networks` - 查看所有Network段落结构")
        output.append("2. `--section variables` - 查看变量定义（按作用域分组）")
        output.append("3. `--section calls` - 查看调用关系")
        if summary.get('total_graph_steps', 0) > 0:
            output.append("4. `--section graph` - 查看GRAPH步序详情")
        output.append("5. `--section comments` - 查看关键注释")
        
    elif section == 'networks':
        # ========== Network段落 ==========
        networks = result.get('networks', [])
        output.append(f"# 📋 Network段落结构 ({len(networks)}个)")
        output.append("")
        
        if not networks:
            output.append("此文件无Network结构（可能是DB数据块或SCL程序）")
        else:
            output.append("| # | 标题 | 注释 |")
            output.append("|---|------|------|")
            for net in networks:
                title = net.get('title', '-')[:40]
                comment = net.get('comment', '-')[:30]
                output.append(f"| N{net.get('id')} | {title} | {comment} |")
            output.append("")
            output.append("💡 使用 `--section network_N` 查看特定Network详情")
    
    elif section.startswith('network_'):
        # ========== 特定Network ==========
        try:
            net_id = int(section.split('_')[1])
            networks = result.get('networks', [])
            net = next((n for n in networks if n.get('id') == net_id), None)
            if net:
                output.append(f"# Network {net_id} 详情")
                output.append(f"**标题**: {net.get('title', 'N/A')}")
                output.append(f"**注释**: {net.get('comment', 'N/A')}")
                output.append("")
                # TODO: 可以添加该Network内的具体逻辑
            else:
                output.append(f"未找到 Network {net_id}")
        except:
            output.append("参数格式错误，使用 --section network_1")
    
    elif section == 'variables':
        # ========== 变量分析 ==========
        variables = result.get('variables', [])
        output.append(f"# 📦 变量定义 ({len(variables)}个)")
        output.append("")
        
        # 按作用域分组
        by_scope = defaultdict(list)
        for var in variables:
            scope = var.get('scope', 'OTHER')
            by_scope[scope].append(var)
        
        scope_order = ['IN', 'OUT', 'INOUT', 'STAT', 'TEMP', 'CONSTANT']
        scope_desc = {
            'IN': '输入参数（外部传入）',
            'OUT': '输出参数（对外输出）',
            'INOUT': '双向参数',
            'STAT': '静态变量（保持值）',
            'TEMP': '临时变量（每次清零）',
            'CONSTANT': '常量'
        }
        
        for scope in scope_order:
            if scope in by_scope:
                vars_list = by_scope[scope]
                output.append(f"## {scope} - {scope_desc.get(scope, '')} ({len(vars_list)}个)")
                output.append("")
                # 显示前15个
                for var in vars_list[:15]:
                    line = f"- `{var.get('name')}`: {var.get('datatype', 'N/A')}"
                    if var.get('comment'):
                        line += f" // {var.get('comment')[:40]}"
                    output.append(line)
                if len(vars_list) > 15:
                    output.append(f"  ... 及其他 {len(vars_list)-15} 个")
                output.append("")
        
        output.append("💡 使用 `--section variables_IN` 查看特定作用域的完整变量")
    
    elif section.startswith('variables_'):
        # ========== 特定作用域变量 ==========
        scope_filter = section.split('_')[1].upper()
        variables = result.get('variables', [])
        filtered = [v for v in variables if v.get('scope', '').upper() == scope_filter]
        
        output.append(f"# {scope_filter} 变量 ({len(filtered)}个)")
        output.append("")
        
        for var in filtered:
            line = f"- `{var.get('path', var.get('name'))}`: {var.get('datatype', 'N/A')}"
            if var.get('comment'):
                line += f"\n  > {var.get('comment')}"
            if var.get('initial_value'):
                line += f" [初值: {var.get('initial_value')}]"
            output.append(line)
    
    elif section == 'calls':
        # ========== 调用关系 ==========
        calls = result.get('calls', [])
        output.append(f"# 🔗 调用关系 ({len(calls)}个)")
        output.append("")
        
        # 按被调用者分组
        by_callee = defaultdict(list)
        for call in calls:
            callee = call.get('callee', 'Unknown')
            by_callee[callee].append(call)
        
        output.append("## 调用的功能块")
        for callee, call_list in by_callee.items():
            callee_type = call_list[0].get('callee_type', '')
            output.append(f"- **{callee}** ({callee_type}) - 被调用 {len(call_list)} 次")
        output.append("")
        
        output.append("## 调用详情")
        for call in calls[:30]:
            output.append(f"- {call.get('caller', '?')} → {call.get('callee', '?')}")
    
    elif section == 'graph':
        # ========== GRAPH步序 ==========
        steps = result.get('graph_steps', [])
        trans = result.get('graph_transitions', [])
        
        output.append(f"# ⚙️ GRAPH步序 ({len(steps)}步, {len(trans)}转换)")
        output.append("")
        
        if not steps:
            output.append("此程序不包含GRAPH步序")
        else:
            output.append("## 步序列表")
            output.append("| 步号 | 名称 | 初始步 | 动作数 |")
            output.append("|------|------|--------|--------|")
            for step in steps:
                init_mark = "✓" if step.get('is_init') else ""
                actions_count = len(step.get('actions', []))
                output.append(f"| {step.get('number')} | {step.get('name')} | {init_mark} | {actions_count} |")
            output.append("")
            output.append("💡 使用 `--section step_N` 查看特定步的详情")
    
    elif section.startswith('step_'):
        # ========== 特定GRAPH步 ==========
        try:
            step_num = int(section.split('_')[1])
            steps = result.get('graph_steps', [])
            step = next((s for s in steps if s.get('number') == step_num), None)
            if step:
                output.append(f"# Step {step_num}: {step.get('name')}")
                output.append("")
                if step.get('is_init'):
                    output.append("🏁 **这是初始步**")
                if step.get('comment'):
                    output.append(f"注释: {step.get('comment')}")
                if step.get('max_time'):
                    output.append(f"最大时间: {step.get('max_time')}")
                if step.get('actions'):
                    output.append("### 动作:")
                    for action in step.get('actions', []):
                        output.append(f"- {action}")
            else:
                output.append(f"未找到 Step {step_num}")
        except:
            output.append("参数格式错误，使用 --section step_1")
    
    elif section == 'comments':
        # ========== 关键注释 ==========
        key_comments = result.get('key_comments', [])
        output.append(f"# 💬 关键注释 ({len(key_comments)}条)")
        output.append("")
        
        if not key_comments:
            output.append("未提取到关键注释")
        else:
            for i, comment in enumerate(key_comments, 1):
                output.append(f"{i}. {comment}")
        output.append("")
        
        # 语义关键词
        semantic = result.get('semantic_hints', {})
        keywords = semantic.get('keywords_detected', [])
        if keywords:
            output.append("## 检测到的关键词")
            output.append(f"{', '.join(keywords)}")
    
    else:
        output.append(f"未知的section参数: {section}")
        output.append("可用: overview, networks, network_N, variables, variables_IN/OUT/STAT, calls, graph, step_N, comments")
    
    return '\n'.join(output)


def generate_summary_output(data: Dict, json_filepath: str) -> str:
    """
    生成精简摘要输出
    - stdout：关键信息供LLM理解
    - 完整数据路径：供下游工具读取入库
    """
    lines = []
    
    # 文件信息
    file_info = data.get('file_info', {})
    lines.append("# PLC文件解析结果")
    lines.append("")
    lines.append("## 文件信息")
    lines.append(f"- 源文件: {file_info.get('path', '')}")
    lines.append(f"- 品牌: {file_info.get('brand', '')} {file_info.get('product', '')}")
    lines.append(f"- 版本: {file_info.get('version', '')}")
    lines.append("")
    
    # 统计摘要
    summary = data.get('summary', {})
    lines.append("## 统计摘要")
    lines.append(f"- 程序块: {summary.get('total_blocks', 0)}个")
    lines.append(f"- 变量: {summary.get('total_variables', 0)}个")
    lines.append(f"- 调用关系: {summary.get('total_calls', 0)}个")
    lines.append(f"- Network: {summary.get('total_networks', 0)}个")
    lines.append(f"- GRAPH步序: {summary.get('total_graph_steps', 0)}个")
    lines.append("")
    
    # 程序块列表
    blocks = data.get('blocks', [])
    if blocks:
        lines.append("## 程序块")
        for b in blocks[:10]:
            lines.append(f"- {b['name']} ({b['type']}{b['number']}) [{b['language']}]")
        lines.append("")
    
    # 语义提示
    semantic = data.get('semantic_hints', {})
    if semantic.get('inferred_purpose'):
        lines.append("## 功能推断")
        lines.append(f"**{semantic.get('inferred_purpose')}**")
        lines.append("")
    
    # 关键注释（最多5条）
    key_comments = data.get('key_comments', [])
    if key_comments:
        lines.append("## 关键注释")
        for c in key_comments[:5]:
            lines.append(f"- {c}")
        lines.append("")
    
    # 调用关系
    calls = data.get('calls', [])
    if calls:
        lines.append("## 调用关系")
        for c in calls[:10]:
            lines.append(f"- {c['caller']} → {c['callee']}")
        lines.append("")
    
    # 完整数据文件路径（重要！供下游工具使用）
    lines.append("---")
    lines.append("## 📁 完整数据文件")
    lines.append(f"**路径**: `{json_filepath}`")
    lines.append("")
    lines.append("> 此文件包含完整的变量列表、Network详情等数据。")
    lines.append("> 可传递给入库工具进行持久化保存。")
    
    return '\n'.join(lines)


def format_architecture_analysis(arch_result: Dict) -> str:
    """格式化架构分析结果 - 纯数据输出供 LLM 分析"""
    output = []
    output.append("# 📊 程序块与调用关系数据")
    output.append("")
    output.append("> ⚠️ 注意：以下数据仅反映**本文件内**的调用关系")
    output.append("> 跨文件调用关系需汇总多个文件后由 LLM 综合分析")
    output.append("")
    
    # 程序块列表
    blocks = arch_result.get('blocks', [])
    output.append(f"## 程序块 ({len(blocks)}个)")
    output.append("")
    output.append("| 块名 | 类型 | 编号 | 语言 | 调用数 | 被调用数(本文件内) |")
    output.append("|------|------|------|------|--------|-------------------|")
    for b in blocks:
        output.append(f"| {b['name']} | {b['type']} | {b['number']} | {b['language']} | {b['calls_out_count']} | {b['called_by_count']} |")
    output.append("")
    
    # 本文件内未被调用的块
    not_called = arch_result.get('not_called_in_file', [])
    if not_called:
        output.append("## 本文件内未被调用的块")
        output.append(f"以下块在本文件内没有被其他块调用（可能是入口或被其他文件调用）：")
        output.append("")
        for name in not_called:
            output.append(f"- {name}")
        output.append("")
    
    # 调用关系
    call_graph = arch_result.get('call_graph', {})
    if call_graph:
        output.append("## 调用关系")
        output.append("")
        for caller, callees in call_graph.items():
            output.append(f"- **{caller}** 调用 → {', '.join(callees)}")
        output.append("")
    
    # 被调用关系
    reverse_graph = arch_result.get('reverse_graph', {})
    if reverse_graph:
        output.append("## 被调用关系 (仅本文件内)")
        output.append("")
        for callee, callers in reverse_graph.items():
            output.append(f"- **{callee}** 被调用 ← {', '.join(callers)}")
        output.append("")
    
    output.append("---")
    output.append("💡 **LLM分析提示**：")
    output.append("1. 汇总多个文件的调用关系后，找出全局未被调用的块 = 主入口候选")
    output.append("2. OB1 通常是西门子PLC的主循环入口")
    output.append("3. GRAPH语言的块通常是顺序控制主程序")
    
    return '\n'.join(output)


def format_monitor_analysis(monitor_result: Dict) -> str:
    """格式化监控变量分析结果 - 纯数据输出供 LLM 分析"""
    output = []
    output.append("# 📊 变量分类数据")
    output.append("")
    output.append("> ⚠️ 注意：以下分类仅基于关键词匹配")
    output.append("> 具体哪些变量值得监控，请 LLM 根据业务语境判断")
    output.append("")
    
    # 按作用域统计
    by_scope = monitor_result.get('by_scope', {})
    output.append("## 按作用域分布")
    output.append("")
    output.append("| 作用域 | 数量 |")
    output.append("|--------|------|")
    scope_order = ['IN', 'OUT', 'INOUT', 'STAT', 'TEMP']
    for scope in scope_order:
        if scope in by_scope:
            output.append(f"| {scope} | {by_scope[scope]['count']} |")
    output.append("")
    
    # 按类别统计
    by_category = monitor_result.get('by_category', {})
    output.append("## 按类别分布")
    output.append("")
    output.append("| 类别 | 数量 | 示例变量 |")
    output.append("|------|------|----------|")
    for category, data in by_category.items():
        examples = [v['name'] for v in data['variables'][:3]]
        output.append(f"| {category} | {data['count']} | {', '.join(examples)} |")
    output.append("")
    
    # 各类别详细变量
    output.append("## 各类别变量详情")
    output.append("")
    for category, data in by_category.items():
        output.append(f"### {category} ({data['count']}个)")
        for var in data['variables'][:15]:  # 每类最多15个
            line = f"- `{var['path']}` ({var['datatype']}) [{var['scope']}]"
            if var.get('comment'):
                line += f" // {var['comment'][:40]}"
            output.append(line)
        if data['count'] > 15:
            output.append(f"  ... 还有 {data['count'] - 15} 个")
        output.append("")
    
    output.append("---")
    output.append("💡 **LLM分析提示**：")
    output.append("1. **状态类**变量通常反映程序执行位置，适合显示在HMI")
    output.append("2. **报警类**变量建议配置报警弹窗")
    output.append("3. **运行/完成类**变量可用于节拍统计")
    output.append("4. 具体监控价值需结合工艺流程判断")
    
    return '\n'.join(output)


def _format_hierarchy_tree(node: Dict, prefix: str = "", is_last: bool = True) -> str:
    """格式化层级树"""
    lines = []
    connector = "└── " if is_last else "├── "
    lines.append(f"{prefix}{connector}{node['name']}")
    
    children = node.get('children', [])
    for i, child in enumerate(children):
        is_last_child = (i == len(children) - 1)
        new_prefix = prefix + ("    " if is_last else "│   ")
        lines.append(_format_hierarchy_tree(child, new_prefix, is_last_child))
    
    return '\n'.join(lines)
