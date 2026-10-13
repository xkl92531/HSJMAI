#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 分析器模块

包含语义分析、架构分析、监控变量分析等
"""

import re
import xml.etree.ElementTree as ET
from collections import defaultdict
from typing import Dict, List, Any, Tuple

from models import (
    ParseResult, SemanticHint, CallInfo
)


# ============================================================
# 语义分析器
# ============================================================

class SemanticAnalyzer:
    """语义分析器 - 基于上下文推断功能"""
    
    # 工位/功能关键词词典
    STATION_KEYWORDS = {
        '机器人': ['Robot', 'ABB', 'KUKA', 'Fanuc', 'UR_', 'Arm', 'robot', 'gripper', 'Gripper', 'RB_', 'R_Axis'],
        '涂胶': ['Glue', 'Dispens', 'Coating', 'Seal', 'glue', 'dispens', 'Sealant', '涂胶', '打胶'],
        '焊接': ['Weld', 'Spot', 'Arc', 'weld', 'Laser', 'laser', '焊接', '点焊', '弧焊'],
        '装配': ['Assembly', 'Press', 'Screw', 'assembly', 'press', 'Nut', 'Bolt', '装配', '压装', '拧紧'],
        '输送': ['Conveyor', 'Transfer', 'Belt', 'conveyor', 'transfer', 'AGV', 'Shuttle', '输送', '传送'],
        '检测': ['Vision', 'Camera', 'Sensor', 'Check', 'Inspect', 'vision', 'camera', 'Test', 'Measure', '检测', '测量'],
        '搬运': ['Pick', 'Place', 'Lift', 'Clamp', 'Grip', '搬运', '抓取', '夹取'],
        '清洗': ['Clean', 'Wash', 'Rinse', 'Dry', '清洗', '烘干'],
        '包装': ['Pack', 'Label', 'Wrap', 'Box', '包装', '贴标'],
        '电池': ['Cell', 'Battery', 'Module', 'Pack', '电芯', '电池', '模组'],
    }
    
    FUNCTION_KEYWORDS = {
        '顺序控制': ['Graph', 'Step', 'Sequence', 'GRAPH', 'SFC', '步序', '顺控'],
        '手动模式': ['Manual', 'Jog', 'Hand', 'manual', '手动', '点动'],
        '自动模式': ['Auto', 'Cycle', 'Sequence', 'auto', '自动', '循环'],
        '安全联锁': ['Safety', 'EStop', 'Emergency', 'Guard', 'Door', 'Interlock', '安全', '联锁', '急停'],
        '气缸控制': ['Cylinder', 'Valve', 'Air', 'Pneumatic', '气缸', '阀'],
        '伺服控制': ['Servo', 'Motor', 'Axis', 'Position', 'MC_', '伺服', '电机', '轴'],
        '通讯处理': ['Comm', 'ProfiBus', 'ProfiNet', 'Ethernet', 'TCP', 'OPC', '通讯'],
        '报警处理': ['Alarm', 'Fault', 'Error', 'Warning', 'Diag', '报警', '故障'],
        '数据处理': ['Data', 'Recipe', 'Param', 'Config', '数据', '配方', '参数'],
        '回原点': ['Home', 'Init', 'Origin', 'Reference', '原点', '回原', '初始化'],
    }
    
    def analyze(self, result: ParseResult, root: ET.Element = None):
        """分析解析结果，提取语义信息"""
        
        # 1. 收集所有文本内容用于关键词分析
        all_texts = self._collect_texts(result)
        
        # 2. 检测工位类型
        station_scores = {}
        for station, keywords in self.STATION_KEYWORDS.items():
            score = 0
            matched = []
            for kw in keywords:
                count = sum(1 for t in all_texts if kw in t)
                if count > 0:
                    score += count
                    matched.append(kw)
            if score > 0:
                station_scores[station] = (score, matched)
        
        # 3. 检测功能类型
        function_scores = {}
        for func, keywords in self.FUNCTION_KEYWORDS.items():
            score = 0
            matched = []
            for kw in keywords:
                count = sum(1 for t in all_texts if kw in t)
                if count > 0:
                    score += count
                    matched.append(kw)
            if score > 0:
                function_scores[func] = (score, matched)
        
        # 4. 确定主要类型
        top_station = max(station_scores.items(), key=lambda x: x[1][0])[0] if station_scores else ""
        top_functions = sorted(function_scores.items(), key=lambda x: -x[1][0])[:3]
        
        # 5. 提取关键注释
        key_comments = self._extract_key_comments(result, root)
        result.key_comments = key_comments[:10]  # 最多10条关键注释
        
        # 6. 基于块名推断工位编号
        station_id = self._infer_station_id(result)
        
        # 7. 构建语义提示
        all_keywords = []
        for _, (_, matched) in list(station_scores.items())[:3]:
            all_keywords.extend(matched)
        for _, (_, matched) in list(function_scores.items())[:3]:
            all_keywords.extend(matched)
        
        # 生成推断描述
        purpose = self._generate_purpose(result, top_station, top_functions, station_id, key_comments)
        
        result.semantic_hints = SemanticHint(
            station_type=top_station,
            function_category=', '.join([f[0] for f in top_functions]),
            keywords_detected=list(set(all_keywords))[:20],
            confidence=min(0.9, (len(station_scores) + len(function_scores)) / 10),
            inferred_purpose=purpose
        )
    
    def _collect_texts(self, result: ParseResult) -> List[str]:
        """收集所有可用于分析的文本"""
        texts = []
        
        # 块名和注释
        for block in result.blocks:
            texts.append(block.name)
            if block.comment:
                texts.append(block.comment)
        
        # 变量名和注释
        for var in result.variables:
            texts.append(var.name)
            texts.append(var.path)
            if var.comment:
                texts.append(var.comment)
        
        # 调用的块名
        for call in result.calls:
            texts.append(call.callee)
        
        # GRAPH步骤名
        for step in result.graph_steps:
            texts.append(step.name)
            if step.comment:
                texts.append(step.comment)
        
        # Network标题
        for network in result.networks:
            texts.append(network.title)
            if network.comment:
                texts.append(network.comment)
        
        return texts
    
    def _extract_key_comments(self, result: ParseResult, root: ET.Element = None) -> List[str]:
        """提取关键注释（有语义价值的注释）"""
        comments = []
        
        # 从变量注释中提取
        for var in result.variables:
            if var.comment and len(var.comment) > 5:
                # 过滤无意义的注释
                if not var.comment.startswith('Step') and not var.comment.startswith('Transition'):
                    comments.append(var.comment)
        
        # 从Network标题中提取
        for network in result.networks:
            if network.title and len(network.title) > 3:
                comments.append(network.title)
            if network.comment and len(network.comment) > 3 and network.comment != network.title:
                comments.append(network.comment)
        
        # 从XML根节点直接提取所有中文文本（重要语义来源）
        if root is not None:
            for elem in root.iter():
                tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
                if tag in ['Text', 'MultiLanguageText', 'MultilingualTextItem']:
                    if elem.text:
                        text = elem.text.strip()
                        # 只要中文注释，且有实际内容
                        if text and len(text) > 5 and any('\u4e00' <= c <= '\u9fff' for c in text):
                            # 过滤重复和无意义的
                            if text not in comments:
                                # 过滤结构性注释
                                if not text.startswith('步结构') and not text.startswith('转换结构'):
                                    comments.append(text)
        
        # 去重并按相关性排序
        unique_comments = list(dict.fromkeys(comments))  # 保持顺序去重
        
        # 优先级：包含工位号 > 包含设备名 > 其他
        def score_comment(c):
            score = 0
            if 'OP0' in c or 'OP1' in c or '工位' in c:
                score += 100
            if '气缸' in c or '夹爪' in c or '电芯' in c or '定位' in c:
                score += 50
            if '--->' in c or '互锁' in c or '条件' in c:
                score += 30
            score += len(c)  # 较长的注释通常更有价值
            return -score  # 负数用于降序
        
        unique_comments.sort(key=score_comment)
        
        return unique_comments[:15]  # 返回最多15条最有价值的注释

    def analyze_from_text(self, result: ParseResult, source_text: str):
        """基于纯文本源码(SCL/STL)进行语义分析
        
        与 analyze() 不同，这里没有 XML root，所以所有语义信息从 ParseResult 和源码文本中提取
        """
        # 1. 收集文本（复用已有方法）
        all_texts = self._collect_texts(result)
        
        # 从源码注释中提取中文文本
        for line in source_text.split('\n'):
            stripped = line.strip()
            # 行注释
            comment_match = re.search(r'//(.+)$', stripped)
            if comment_match:
                comment = comment_match.group(1).strip()
                if comment and len(comment) > 2:
                    all_texts.append(comment)
            # TITLE 行
            title_match = re.match(r'^TITLE\s*=\s*(.+)', stripped)
            if title_match:
                all_texts.append(title_match.group(1).strip())
        
        # 2. 工位检测
        station_scores = {}
        for station, keywords in self.STATION_KEYWORDS.items():
            score = 0
            matched = []
            for kw in keywords:
                count = sum(1 for t in all_texts if kw in t)
                if count > 0:
                    score += count
                    matched.append(kw)
            if score > 0:
                station_scores[station] = (score, matched)
        
        # 3. 功能检测
        function_scores = {}
        for func, keywords in self.FUNCTION_KEYWORDS.items():
            score = 0
            matched = []
            for kw in keywords:
                count = sum(1 for t in all_texts if kw in t)
                if count > 0:
                    score += count
                    matched.append(kw)
            if score > 0:
                function_scores[func] = (score, matched)
        
        # 4. 结果
        top_station = max(station_scores.items(), key=lambda x: x[1][0])[0] if station_scores else ""
        top_functions = sorted(function_scores.items(), key=lambda x: -x[1][0])[:3]
        
        # 5. 关键注释（从源码中提取）
        key_comments = []
        for var in result.variables:
            if var.comment and len(var.comment) > 3:
                key_comments.append(var.comment)
        for network in result.networks:
            if network.title and len(network.title) > 3:
                key_comments.append(network.title)
        
        # 从源码行注释中提取
        for line in source_text.split('\n'):
            comment_match = re.search(r'//(.+)$', line.strip())
            if comment_match:
                comment = comment_match.group(1).strip()
                if comment and len(comment) > 5 and any('\u4e00' <= c <= '\u9fff' for c in comment):
                    if comment not in key_comments:
                        key_comments.append(comment)
        
        result.key_comments = key_comments[:10]
        
        # 6. 推断工位ID
        station_id = self._infer_station_id(result)
        
        # 7. 构建语义提示
        all_keywords = []
        for _, (_, matched) in list(station_scores.items())[:3]:
            all_keywords.extend(matched)
        for _, (_, matched) in list(function_scores.items())[:3]:
            all_keywords.extend(matched)
        
        purpose = self._generate_purpose(result, top_station, top_functions, station_id, key_comments)
        
        result.semantic_hints = SemanticHint(
            station_type=top_station,
            function_category=', '.join([f[0] for f in top_functions]),
            keywords_detected=list(set(all_keywords))[:20],
            confidence=min(0.9, (len(station_scores) + len(function_scores)) / 10),
            inferred_purpose=purpose
        )

    def _infer_station_id(self, result: ParseResult) -> str:
        """从块名推断工位编号"""
        # 常见模式: OP070, ST10, Station5, WS_01
        patterns = [
            r'OP(\d+)',
            r'ST(\d+)',
            r'Station(\d+)',
            r'WS[_]?(\d+)',
            r'工位(\d+)',
        ]
        
        for block in result.blocks:
            for pattern in patterns:
                match = re.search(pattern, block.name, re.IGNORECASE)
                if match:
                    return f"OP{match.group(1)}"
        
        return ""
    
    def _generate_purpose(self, result: ParseResult, station: str, functions: List, 
                          station_id: str, comments: List[str]) -> str:
        """生成功能描述"""
        parts = []
        
        # 工位信息
        if station_id:
            parts.append(f"工位: {station_id}")
        if station:
            parts.append(f"类型: {station}")
        
        # 功能信息
        func_names = [f[0] for f in functions]
        if func_names:
            parts.append(f"功能: {', '.join(func_names)}")
        
        # 块信息
        if result.blocks:
            block = result.blocks[0]
            parts.append(f"程序块: {block.name} ({block.type}{block.number})")
            if block.language:
                parts.append(f"语言: {block.language}")
        
        # 取第一条有价值的注释
        for comment in comments[:1]:
            if not comment.startswith('[Network]') and len(comment) < 50:
                parts.append(f"描述: {comment}")
                break
        
        return " | ".join(parts)


# ============================================================
# 架构分析器 - 提取调用关系数据供 LLM 分析
# ============================================================

class ArchitectureAnalyzer:
    """
    架构分析器 - 提取程序块和调用关系的原始数据
    
    注意：此类仅负责数据提取，不做"主入口判断"
    主入口识别应由 LLM 基于跨文件汇总数据分析
    """
    
    def analyze(self, result: ParseResult) -> Dict[str, Any]:
        """提取架构相关数据，供 LLM 分析"""
        
        # 1. 构建调用图
        call_graph = self._build_call_graph(result)
        reverse_graph = self._build_reverse_graph(call_graph)
        
        # 2. 收集每个块的基础信息（不评分）
        blocks_info = []
        for block in result.blocks:
            blocks_info.append({
                'name': block.name,
                'type': block.type,
                'number': block.number,
                'language': block.language,
                'calls_out': call_graph.get(block.name, []),      # 调用了谁
                'calls_out_count': len(call_graph.get(block.name, [])),
                'called_by': reverse_graph.get(block.name, []),   # 被谁调用（仅本文件内）
                'called_by_count': len(reverse_graph.get(block.name, [])),
                'comment': block.comment,
            })
        
        # 3. 找出本文件内未被调用的块（候选顶层入口）
        not_called_in_file = [b for b in blocks_info if b['called_by_count'] == 0]
        
        return {
            'blocks': blocks_info,
            'call_graph': call_graph,
            'reverse_graph': reverse_graph,
            'not_called_in_file': [b['name'] for b in not_called_in_file],
            'note': '⚠️ called_by 仅反映本文件内的调用关系，跨文件调用需汇总多个文件分析'
        }
    
    def _build_call_graph(self, result: ParseResult) -> Dict[str, List[str]]:
        """构建调用图: caller -> [callees]"""
        graph = defaultdict(list)
        for call in result.calls:
            if call.callee not in graph[call.caller]:
                graph[call.caller].append(call.callee)
        return dict(graph)
    
    def _build_reverse_graph(self, call_graph: Dict) -> Dict[str, List[str]]:
        """构建反向调用图: callee -> [callers]"""
        reverse = defaultdict(list)
        for caller, callees in call_graph.items():
            for callee in callees:
                if caller not in reverse[callee]:
                    reverse[callee].append(caller)
        return dict(reverse)


# ============================================================
# 监控变量分析器 - 提取变量数据供 LLM 分析
# ============================================================

class MonitorVariableAnalyzer:
    """
    监控变量分析器 - 按类型分组提取变量数据
    
    注意：此类仅负责数据分组，不做"是否值得监控"的判断
    监控价值评估应由 LLM 基于业务上下文分析
    """
    
    # 变量分类关键词（仅用于分组，不评分）
    CATEGORY_KEYWORDS = {
        '状态类': ['Step', 'State', 'Phase', 'Mode', 'Stage', 'Status', '步', '状态', '模式'],
        '报警类': ['Error', 'Fault', 'Alarm', 'Err', 'Warn', 'Diag', '故障', '报警', '错误'],
        '运行类': ['Running', 'Active', 'Busy', 'Working', 'Run', 'Done', 'Ready', '运行', '完成'],
        '位置类': ['Position', 'Pos', 'Sensor', 'Limit', 'Home', '位置', '传感器', '限位'],
        '控制类': ['Cmd', 'Command', 'Request', 'Start', 'Stop', 'Enable', '命令', '启动'],
        '气缸类': ['Cylinder', 'CYL', 'Valve', 'Fwd', 'Bwd', 'Extend', 'Retract', '气缸'],
        '计时类': ['Time', 'Timer', 'Timeout', 'Duration', '时间', '定时'],
        '计数类': ['Count', 'Counter', 'Total', 'Qty', '计数', '数量'],
    }
    
    def analyze(self, result: ParseResult) -> Dict[str, Any]:
        """按类型分组提取变量数据"""
        
        # 1. 按作用域分组
        by_scope = defaultdict(list)
        for var in result.variables:
            by_scope[var.scope].append({
                'name': var.name,
                'path': var.path,
                'datatype': var.datatype,
                'comment': var.comment,
                'initial_value': var.initial_value,
            })
        
        # 2. 按关键词分类（仅辅助 LLM 理解）
        by_category = defaultdict(list)
        uncategorized = []
        
        for var in result.variables:
            check_text = f"{var.name} {var.path} {var.comment}".lower()
            categorized = False
            
            for category, keywords in self.CATEGORY_KEYWORDS.items():
                if any(kw.lower() in check_text for kw in keywords):
                    by_category[category].append({
                        'name': var.name,
                        'path': var.path,
                        'datatype': var.datatype,
                        'scope': var.scope,
                        'comment': var.comment,
                    })
                    categorized = True
                    break  # 只归入第一个匹配的类别
            
            if not categorized:
                uncategorized.append(var.name)
        
        # 3. 统计信息
        return {
            'total_count': len(result.variables),
            'by_scope': {k: {'count': len(v), 'variables': v} for k, v in by_scope.items()},
            'by_category': {k: {'count': len(v), 'variables': v[:20]} for k, v in by_category.items()},  # 每类最多20个
            'uncategorized_count': len(uncategorized),
            'note': '⚠️ 分类仅基于关键词匹配，具体监控价值请 LLM 根据业务语境判断'
        }
