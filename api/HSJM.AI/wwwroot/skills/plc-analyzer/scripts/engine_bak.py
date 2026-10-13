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
"""

import os
import sys
import json
import argparse
import re
import xml.etree.ElementTree as ET
from xml.etree.ElementTree import iterparse
from pathlib import Path
from typing import Dict, List, Any, Optional, Tuple, Generator
from dataclasses import dataclass, field, asdict
from collections import defaultdict
import chardet
import yaml

# ============================================================
# 数据模型
# ============================================================

@dataclass
class FileInfo:
    """文件基本信息"""
    path: str
    encoding: str
    brand: str = ""
    product: str = ""
    version: str = ""
    total_lines: int = 0
    file_size: int = 0

@dataclass
class BlockInfo:
    """程序块信息"""
    name: str
    type: str
    number: int = 0
    language: str = ""
    line_start: int = 0
    line_end: int = 0
    networks_count: int = 0
    comment: str = ""
    author: str = ""
    version: str = ""

@dataclass
class VariableInfo:
    """变量信息"""
    name: str
    path: str
    datatype: str
    scope: str
    comment: str = ""
    initial_value: str = ""
    remanence: str = "NonRetain"

@dataclass
class CallInfo:
    """调用关系"""
    caller: str
    callee: str
    callee_type: str = ""
    network_id: str = ""

@dataclass
class NetworkInfo:
    """网络/段落信息（LAD/FBD）"""
    id: int
    title: str
    comment: str = ""
    
@dataclass
class GraphStep:
    """GRAPH步信息"""
    number: int
    name: str
    is_init: bool = False
    comment: str = ""
    actions: List[str] = field(default_factory=list)
    max_time: str = ""
    warning_time: str = ""

@dataclass
class GraphTransition:
    """GRAPH转换信息"""
    name: str
    comment: str = ""
    condition_type: str = "LAD"

# ============================================================
# V2.0 数据模型 - 支持 AI 诊断的精细化结构
# ============================================================

@dataclass
class VariableAccess:
    """变量访问记录（V2.0）"""
    variable: str          # 完整变量名（如 DB420_OP40Manual.General.AutoMod）
    access_type: str       # "READ" | "WRITE" | "READWRITE"
    uid: str               # 对应的 UId
    comment: str = ""      # 变量注释

@dataclass  
class LogicUnit:
    """逻辑单元 - Network/Transition（V2.0）"""
    unit_id: int                                    # Network 编号或 Transition 编号
    unit_type: str                                  # "NETWORK" | "TRANSITION" | "STEP_ACTION"
    title: str                                      # 标题
    raw_content: str = ""                           # 原始 XML 内容
    uid_symbol_map: Dict[str, str] = field(default_factory=dict)   # UID -> 变量名映射
    variable_access: List[VariableAccess] = field(default_factory=list)  # 变量访问列表
    comment: str = ""                               # 注释
    programming_language: str = "LAD"               # 编程语言（LAD/FBD/SCL/STL）

@dataclass
class CrossReferences:
    """交叉引用索引（V2.0）"""
    writers: Dict[str, List[Dict]] = field(default_factory=dict)  # 变量 -> 写入位置列表 [{block, network}]
    readers: Dict[str, List[Dict]] = field(default_factory=dict)  # 变量 -> 读取位置列表 [{block, network}]

@dataclass
class SemanticHint:
    """语义提示（自动推断）"""
    station_type: str = ""         # 工位类型：机器人、涂胶、焊接等
    function_category: str = ""     # 功能类别：控制、监控、通讯等
    keywords_detected: List[str] = field(default_factory=list)
    confidence: float = 0.0
    inferred_purpose: str = ""      # 推断的用途描述

@dataclass
class ParseResult:
    """解析结果"""
    file_info: FileInfo
    blocks: List[BlockInfo] = field(default_factory=list)
    variables: List[VariableInfo] = field(default_factory=list)
    calls: List[CallInfo] = field(default_factory=list)
    graph_steps: List[GraphStep] = field(default_factory=list)
    graph_transitions: List[GraphTransition] = field(default_factory=list)
    networks: List[NetworkInfo] = field(default_factory=list)        # 保留向后兼容
    semantic_hints: SemanticHint = field(default_factory=SemanticHint)
    key_comments: List[str] = field(default_factory=list)
    errors: List[str] = field(default_factory=list)
    # V2.0 新增字段
    logic_units: List[LogicUnit] = field(default_factory=list)        # 逻辑单元（V2.0）
    cross_references: CrossReferences = field(default_factory=CrossReferences)  # 交叉引用（V2.0）
    
    def to_dict(self) -> Dict:
        """标准输出格式"""
        return {
            "file_info": asdict(self.file_info),
            "blocks": [asdict(b) for b in self.blocks],
            "variables": [asdict(v) for v in self.variables],
            "calls": [asdict(c) for c in self.calls],
            "graph_steps": [asdict(s) for s in self.graph_steps],
            "graph_transitions": [asdict(t) for t in self.graph_transitions],
            "networks": [asdict(n) for n in self.networks],
            "semantic_hints": asdict(self.semantic_hints),
            "key_comments": self.key_comments,
            "logic_units": [asdict(lu) for lu in self.logic_units], 
            "summary": {
                "total_blocks": len(self.blocks),
                "total_variables": len(self.variables),
                "total_calls": len(self.calls),
                "total_graph_steps": len(self.graph_steps),
                "total_networks": len(self.networks),
                "total_logic_units": len(self.logic_units),
            },
            "errors": self.errors
        }
    
    def to_dict_v2(self) -> Dict:
        """V2.0 格式输出 - 面向 AI 诊断"""
        return {
            "metadata": {
                "brand": self.file_info.brand,
                "product": self.file_info.product,
                "version": self.file_info.version,
                "file_path": self.file_info.path,
                "encoding": self.file_info.encoding,
            },
            "blocks": [{
                "name": b.name,
                "type": b.type,
                "number": b.number,
                "language": b.language,
                "description": b.comment,
                "networks_count": b.networks_count,
            } for b in self.blocks],
            "interface": [asdict(v) for v in self.variables],
            "logic_units": [{
                "network_id": lu.unit_id,
                "unit_type": lu.unit_type,
                "title": lu.title,
                "raw_content": lu.raw_content,
                "uid_symbol_map": lu.uid_symbol_map,
                "variable_access": [asdict(va) for va in lu.variable_access],
                "programming_language": lu.programming_language,
                "comment": lu.comment,
            } for lu in self.logic_units],
            "cross_references": {
                "writers": self.cross_references.writers,
                "readers": self.cross_references.readers,
            },
            "calls": [asdict(c) for c in self.calls],
            "graph_elements": {
                "steps": [asdict(s) for s in self.graph_steps],
                "transitions": [asdict(t) for t in self.graph_transitions],
            },
            "semantic_hints": asdict(self.semantic_hints),
            "errors": self.errors,
        }


# ============================================================
# 配置加载器
# ============================================================

class ConfigLoader:
    """品牌配置加载器"""
    
    def __init__(self, brands_dir: str = None):
        if brands_dir is None:
            # 默认在同级 brands 目录
            brands_dir = Path(__file__).parent.parent / "brands"
        self.brands_dir = Path(brands_dir)
        self.configs: Dict[str, Dict] = {}
        self._load_all_configs()
    
    def _load_all_configs(self):
        """加载所有品牌配置"""
        if not self.brands_dir.exists():
            return
        for yaml_file in self.brands_dir.glob("*.yaml"):
            try:
                with open(yaml_file, 'r', encoding='utf-8') as f:
                    config = yaml.safe_load(f)
                    if config and 'metadata' in config:
                        key = f"{config['metadata'].get('brand', '')}_{config['metadata'].get('product', '')}"
                        self.configs[yaml_file.stem] = config
            except Exception as e:
                print(f"Warning: Failed to load config {yaml_file}: {e}", file=sys.stderr)
    
    def detect_brand(self, file_path: str, content_sample: str) -> Optional[Dict]:
        """根据文件特征检测品牌配置"""
        file_ext = Path(file_path).suffix.lower()
        best_match = None
        best_score = 0
        
        for name, config in self.configs.items():
            detection = config.get('detection', {})
            score = 0
            
            # 检查文件扩展名
            extensions = detection.get('file_extensions', [])
            if file_ext in extensions:
                score += 0.3
            
            # 检查内容特征
            signatures = detection.get('content_signatures', [])
            for sig in signatures:
                pattern = sig.get('pattern', '')
                position = sig.get('position', 'any')
                is_regex = sig.get('is_regex', False)
                
                check_content = content_sample
                if position == 'start':
                    check_content = content_sample[:2000]
                elif position == 'end':
                    check_content = content_sample[-2000:]
                
                if is_regex:
                    if re.search(pattern, check_content):
                        score += 0.2
                else:
                    if pattern in check_content:
                        score += 0.2
            
            # 检查是否超过阈值
            threshold = detection.get('detection_score_threshold', 0.5)
            if score >= threshold and score > best_score:
                best_score = score
                best_match = config
        
        return best_match
    
    def get_config(self, name: str) -> Optional[Dict]:
        """获取指定品牌配置"""
        return self.configs.get(name)


# ============================================================
# 通用解析引擎
# ============================================================

class PLCAnalyzerEngine:
    """PLC程序分析引擎"""
    
    def __init__(self, config_loader: ConfigLoader = None):
        self.config_loader = config_loader or ConfigLoader()
        self.current_config: Optional[Dict] = None
    
    def detect_encoding(self, file_path: str) -> str:
        """检测文件编码"""
        with open(file_path, 'rb') as f:
            raw = f.read(10000)
        result = chardet.detect(raw)
        encoding = result.get('encoding', 'utf-8')
        # 处理常见编码别名
        if encoding and encoding.lower() in ['gb2312', 'gbk', 'gb18030']:
            encoding = 'gb18030'
        return encoding or 'utf-8'
    
    def preprocess(self, file_path: str) -> FileInfo:
        """预处理：获取文件基本信息"""
        path = Path(file_path)
        encoding = self.detect_encoding(file_path)
        
        # 读取文件样本用于品牌检测
        with open(file_path, 'r', encoding=encoding, errors='ignore') as f:
            content_sample = f.read(50000)
        
        # 检测品牌
        config = self.config_loader.detect_brand(file_path, content_sample)
        brand = ""
        product = ""
        version = ""
        
        if config:
            self.current_config = config
            metadata = config.get('metadata', {})
            brand = metadata.get('brand', '')
            product = metadata.get('product', '')
            
            # 尝试从文件中提取版本
            version_match = re.search(r'<Engineering\s+version="([^"]+)"', content_sample)
            if version_match:
                version = version_match.group(1)
        
        # 统计行数
        total_lines = content_sample.count('\n')
        if len(content_sample) < path.stat().st_size:
            # 文件未完全读取，估算总行数
            with open(file_path, 'r', encoding=encoding, errors='ignore') as f:
                total_lines = sum(1 for _ in f)
        
        return FileInfo(
            path=str(path.absolute()),
            encoding=encoding,
            brand=brand,
            product=product,
            version=version,
            total_lines=total_lines,
            file_size=path.stat().st_size
        )
    
    def extract(self, file_path: str, config: Dict = None) -> ParseResult:
        """提取结构化信息"""
        file_info = self.preprocess(file_path)
        config = config or self.current_config
        
        if not config:
            return ParseResult(
                file_info=file_info,
                errors=["无法识别的PLC文件格式，请检查或添加品牌配置"]
            )
        
        result = ParseResult(file_info=file_info)
        
        try:
            # 读取并预处理XML
            tree = self._load_xml(file_path, file_info.encoding, config)
            if tree is None:
                result.errors.append("XML解析失败")
                return result
            
            root = tree.getroot()
            extraction_config = config.get('extraction', {})
            
            # 提取程序块
            result.blocks = self._extract_blocks(root, extraction_config.get('blocks', {}))
            
            # 获取主块名称用于 cross_references
            main_block_name = result.blocks[0].name if result.blocks else ""
            
            # 提取变量（每个块独立提取）
            for block_elem in self._find_block_elements(root, extraction_config.get('blocks', {})):
                block_name = self._get_field_value(block_elem, 
                    extraction_config.get('blocks', {}).get('fields', {}).get('name', {}))
                vars = self._extract_variables(block_elem, extraction_config.get('variables', {}), block_name)
                result.variables.extend(vars)
            
            # 提取调用关系
            result.calls = self._extract_calls(root, extraction_config.get('calls', {}), result.blocks)
            
            # 提取GRAPH信息（如果是GRAPH程序）
            for block in result.blocks:
                if block.language == "GRAPH":
                    block_elem = self._find_block_by_name(root, block.name, extraction_config.get('blocks', {}))
                    if block_elem is not None:
                        steps, trans = self._extract_graph(block_elem, extraction_config.get('graphs', {}))
                        result.graph_steps.extend(steps)
                        result.graph_transitions.extend(trans)
            
            # 提取Network标题（LAD/FBD）- 向后兼容
            result.networks = self._extract_networks(root)
            
            # ============================================================
            # V2.0 提取 - 逻辑单元和交叉引用
            # ============================================================
            result.logic_units = self._extract_logic_units(root, main_block_name)
            result.cross_references = self._build_cross_references(result.logic_units, main_block_name)
            
            # 存储root用于语义分析
            self.root = root
            
            # 语义分析
            analyzer = SemanticAnalyzer()
            analyzer.analyze(result, root)
            
        except Exception as e:
            result.errors.append(f"解析错误: {str(e)}")
        
        return result
    
    def _extract_networks(self, root: ET.Element) -> List[NetworkInfo]:
        """提取Network段落信息"""
        networks = []
        network_idx = 0
        
        for elem in root.iter():
            tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
            
            # 查找 CompileUnit (Network)
            if tag in ['SW.Blocks.CompileUnit', 'CompileUnit', 'NetworkSource']:
                network_idx += 1
                title = ""
                comment = ""
                
                # 方法1：查找 AttributeList/Title 和 AttributeList/Comment
                for child in elem:
                    child_tag = child.tag.split('}')[-1] if '}' in child.tag else child.tag
                    
                    if child_tag == 'AttributeList':
                        for attr in child:
                            attr_tag = attr.tag.split('}')[-1] if '}' in attr.tag else attr.tag
                            if attr_tag == 'Title':
                                title = self._extract_multilingual_text(attr)
                            elif attr_tag == 'Comment':
                                comment = self._extract_multilingual_text(attr)
                    elif child_tag in ['Title', 'Comment']:
                        text = self._extract_multilingual_text(child)
                        if child_tag == 'Title':
                            title = text
                        else:
                            comment = text
                
                # 方法2：直接在elem下查找所有Text元素（TIA的特殊结构）
                if not title and not comment:
                    for text_elem in elem.iter():
                        text_tag = text_elem.tag.split('}')[-1] if '}' in text_elem.tag else text_elem.tag
                        if text_tag in ['Text', 'MultiLanguageText'] and text_elem.text:
                            text = text_elem.text.strip()
                            if text and len(text) > 3:
                                # 检查是否包含中文（优先）
                                if any('\u4e00' <= c <= '\u9fff' for c in text):
                                    if not title:
                                        title = text
                                    elif not comment:
                                        comment = text
                                elif not title:
                                    title = text
                
                # 如果找到有意义的标题或注释，添加到列表
                if title or comment:
                    networks.append(NetworkInfo(
                        id=network_idx,
                        title=title,
                        comment=comment
                    ))
        
        return networks
    
    # ============================================================
    # V2.0 解析方法 - 支持 AI 诊断的精细化提取
    # ============================================================
    
    def _extract_logic_units(self, root: ET.Element, block_name: str = "") -> List[LogicUnit]:
        """提取逻辑单元（V2.0）- 包含 Network、Transition、Step Actions"""
        logic_units = []
        
        # 处理 LAD/FBD/STL 的 CompileUnit (Network)
        network_idx = 0
        for elem in root.iter():
            tag = self._get_tag_name(elem.tag)
            
            if tag == 'SW.Blocks.CompileUnit':
                network_idx += 1
                logic_unit = self._parse_compile_unit(elem, network_idx, "NETWORK")
                if logic_unit:
                    logic_units.append(logic_unit)
        
        # 处理 GRAPH 的 Transitions
        for transition_elem in root.iter():
            tag = self._get_tag_name(transition_elem.tag)
            if tag == 'Transition':
                trans_number = transition_elem.get('Number', '0')
                trans_name = transition_elem.get('Name', '')
                logic_unit = self._parse_graph_transition(transition_elem, int(trans_number), trans_name)
                if logic_unit:
                    logic_units.append(logic_unit)
        
        # 处理 GRAPH 的 Step Actions、Supervisions、Interlocks
        for step_elem in root.iter():
            tag = self._get_tag_name(step_elem.tag)
            if tag == 'Step':
                step_number = step_elem.get('Number', '0')
                step_name = step_elem.get('Name', '')
                step_units = self._parse_graph_step_logic(step_elem, int(step_number), step_name)
                logic_units.extend(step_units)
        
        return logic_units
    
    def _parse_compile_unit(self, elem: ET.Element, network_idx: int, unit_type: str) -> Optional[LogicUnit]:
        """解析 CompileUnit (Network) 生成 LogicUnit"""
        title = ""
        comment = ""
        programming_language = "LAD"
        
        # 提取标题和注释
        for child in elem:
            child_tag = self._get_tag_name(child.tag)
            if child_tag == 'AttributeList':
                for attr in child:
                    attr_tag = self._get_tag_name(attr.tag)
                    if attr_tag == 'Title':
                        title = self._extract_multilingual_text(attr)
                    elif attr_tag == 'Comment':
                        comment = self._extract_multilingual_text(attr)
                    elif attr_tag == 'ProgrammingLanguage':
                        programming_language = attr.text or "LAD"
        
        # 提取 raw_content（NetworkSource 部分）
        raw_content = ""
        network_source = None
        for child in elem:
            child_tag = self._get_tag_name(child.tag)
            if child_tag == 'NetworkSource':
                network_source = child
                try:
                    raw_content = ET.tostring(child, encoding='unicode')
                except:
                    raw_content = ""
                break
        
        if network_source is None:
            # 可能是直接的 FlgNet 结构
            for child in elem.iter():
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'FlgNet':
                    network_source = child
                    try:
                        raw_content = ET.tostring(child, encoding='unicode')
                    except:
                        raw_content = ""
                    break
        
        # 构建 UID-Symbol 映射
        uid_symbol_map = {}
        variable_access = []
        
        if network_source is not None:
            # 查找 Parts 和 Wires
            parts_elem = None
            wires_elem = None
            for child in network_source.iter():
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'Parts':
                    parts_elem = child
                elif child_tag == 'Wires':
                    wires_elem = child
                elif child_tag == 'FlgNet':
                    # 嵌套在 FlgNet 内
                    for sub in child:
                        sub_tag = self._get_tag_name(sub.tag)
                        if sub_tag == 'Parts':
                            parts_elem = sub
                        elif sub_tag == 'Wires':
                            wires_elem = sub
            
            if parts_elem is not None:
                uid_symbol_map = self._build_uid_symbol_map(parts_elem)
            
            if parts_elem is not None and wires_elem is not None:
                variable_access = self._analyze_wire_connections(wires_elem, parts_elem, uid_symbol_map)
        
        return LogicUnit(
            unit_id=network_idx,
            unit_type=unit_type,
            title=title,
            raw_content=raw_content,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment=comment,
            programming_language=programming_language
        )
    
    def _parse_graph_transition(self, trans_elem: ET.Element, trans_number: int, trans_name: str) -> Optional[LogicUnit]:
        """解析 GRAPH Transition 生成 LogicUnit"""
        programming_language = trans_elem.get('ProgrammingLanguage', 'LAD')
        
        # 提取 raw_content
        raw_content = ""
        try:
            raw_content = ET.tostring(trans_elem, encoding='unicode')
        except:
            raw_content = ""
        
        # 查找 FlgNet
        uid_symbol_map = {}
        variable_access = []
        
        for child in trans_elem:
            child_tag = self._get_tag_name(child.tag)
            if child_tag == 'FlgNet':
                parts_elem = None
                wires_elem = None
                for sub in child:
                    sub_tag = self._get_tag_name(sub.tag)
                    if sub_tag == 'Parts':
                        parts_elem = sub
                    elif sub_tag == 'Wires':
                        wires_elem = sub
                
                if parts_elem is not None:
                    uid_symbol_map = self._build_uid_symbol_map(parts_elem)
                
                if parts_elem is not None and wires_elem is not None:
                    variable_access = self._analyze_wire_connections(wires_elem, parts_elem, uid_symbol_map)
                break
        
        return LogicUnit(
            unit_id=trans_number,
            unit_type="TRANSITION",
            title=trans_name,
            raw_content=raw_content,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment="",
            programming_language=programming_language
        )
    
    def _parse_graph_step_logic(self, step_elem: ET.Element, step_number: int, step_name: str) -> List[LogicUnit]:
        """解析 GRAPH Step 中的逻辑（Actions, Supervisions, Interlocks）"""
        units = []
        
        # 解析 Supervisions
        for supervision in step_elem.iter():
            tag = self._get_tag_name(supervision.tag)
            if tag == 'Supervision':
                prog_lang = supervision.get('ProgrammingLanguage', 'LAD')
                unit = self._parse_flgnet_to_logic_unit(
                    supervision, step_number * 1000 + 1, 
                    f"STEP_{step_number}_SUPERVISION", step_name, prog_lang
                )
                if unit:
                    units.append(unit)
        
        # 解析 Interlocks
        for interlock in step_elem.iter():
            tag = self._get_tag_name(interlock.tag)
            if tag == 'Interlock':
                prog_lang = interlock.get('ProgrammingLanguage', 'LAD')
                unit = self._parse_flgnet_to_logic_unit(
                    interlock, step_number * 1000 + 2,
                    f"STEP_{step_number}_INTERLOCK", step_name, prog_lang
                )
                if unit:
                    units.append(unit)
        
        return units
    
    def _parse_flgnet_to_logic_unit(self, parent_elem: ET.Element, unit_id: int, 
                                     unit_type: str, title: str, prog_lang: str) -> Optional[LogicUnit]:
        """从包含 FlgNet 的元素解析 LogicUnit"""
        flgnet = None
        for child in parent_elem:
            if self._get_tag_name(child.tag) == 'FlgNet':
                flgnet = child
                break
        
        if flgnet is None:
            return None
        
        raw_content = ""
        try:
            raw_content = ET.tostring(parent_elem, encoding='unicode')
        except:
            pass
        
        parts_elem = None
        wires_elem = None
        for sub in flgnet:
            sub_tag = self._get_tag_name(sub.tag)
            if sub_tag == 'Parts':
                parts_elem = sub
            elif sub_tag == 'Wires':
                wires_elem = sub
        
        uid_symbol_map = {}
        variable_access = []
        
        if parts_elem is not None:
            uid_symbol_map = self._build_uid_symbol_map(parts_elem)
        
        if parts_elem is not None and wires_elem is not None:
            variable_access = self._analyze_wire_connections(wires_elem, parts_elem, uid_symbol_map)
        
        return LogicUnit(
            unit_id=unit_id,
            unit_type=unit_type,
            title=title,
            raw_content=raw_content,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment="",
            programming_language=prog_lang
        )
    
    def _build_uid_symbol_map(self, parts_elem: ET.Element) -> Dict[str, str]:
        """构建 UID -> 变量名映射"""
        uid_map = {}
        
        for elem in parts_elem.iter():
            tag = self._get_tag_name(elem.tag)
            if tag == 'Access':
                uid = elem.get('UId')
                scope = elem.get('Scope', '')
                
                if not uid:
                    continue
                
                # 提取变量名
                var_name = self._extract_symbol_name(elem)
                if var_name:
                    uid_map[uid] = var_name
                elif scope == 'LiteralConstant' or scope == 'TypedConstant':
                    # 常量，提取值
                    const_val = self._extract_constant_value(elem)
                    if const_val:
                        uid_map[uid] = const_val
        
        return uid_map
    
    def _extract_symbol_name(self, access_elem: ET.Element) -> str:
        """从 Access 元素提取完整变量名"""
        components = []
        
        for symbol in access_elem.iter():
            tag = self._get_tag_name(symbol.tag)
            if tag == 'Symbol':
                for comp in symbol.iter():
                    comp_tag = self._get_tag_name(comp.tag)
                    if comp_tag == 'Component':
                        name = comp.get('Name', '')
                        if name:
                            # 检查是否有数组索引
                            access_modifier = None
                            for sub in comp:
                                sub_tag = self._get_tag_name(sub.tag)
                                if sub_tag == 'Access':
                                    # 嵌套的数组索引
                                    idx = self._extract_symbol_name(sub) or self._extract_constant_value(sub)
                                    if idx:
                                        access_modifier = f"[{idx}]"
                            
                            if access_modifier:
                                components.append(name + access_modifier)
                            else:
                                components.append(name)
        
        return '.'.join(components)
    
    def _extract_constant_value(self, access_elem: ET.Element) -> str:
        """从 Access 元素提取常量值"""
        for const in access_elem.iter():
            tag = self._get_tag_name(const.tag)
            if tag == 'Constant':
                for val in const:
                    val_tag = self._get_tag_name(val.tag)
                    if val_tag == 'ConstantValue' and val.text:
                        return val.text.strip()
        return ""
    
    def _analyze_wire_connections(self, wires_elem: ET.Element, parts_elem: ET.Element, 
                                   uid_map: Dict[str, str]) -> List[VariableAccess]:
        """分析 Wires 确定 READ/WRITE"""
        accesses = []
        processed_vars = {}  # 变量 -> 访问类型列表
        
        # 1. 构建 Part 类型映射 (UID -> Part类型/名称)
        part_types = {}
        for elem in parts_elem.iter():
            tag = self._get_tag_name(elem.tag)
            if tag == 'Part':
                part_uid = elem.get('UId')
                part_name = elem.get('Name', '')
                if part_uid:
                    part_types[part_uid] = part_name
            elif tag == 'Call':
                call_uid = elem.get('UId')
                if call_uid:
                    part_types[call_uid] = 'Call'
        
        # 2. 遍历 Wire，分析连接关系
        for wire in wires_elem.iter():
            tag = self._get_tag_name(wire.tag)
            if tag != 'Wire':
                continue
            
            # 查找 Wire 中的连接点
            ident_con = None
            name_con = None
            
            for child in wire:
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'IdentCon':
                    ident_con = child
                elif child_tag == 'NameCon':
                    name_con = child
            
            if ident_con is None:
                continue
            
            var_uid = ident_con.get('UId')
            if not var_uid or var_uid not in uid_map:
                continue
            
            var_name = uid_map.get(var_uid, '')
            if not var_name:
                continue
            
            # 判断访问类型
            access_type = self._determine_access_type(wire, name_con, part_types)
            
            if var_name not in processed_vars:
                processed_vars[var_name] = {'uid': var_uid, 'types': []}
            processed_vars[var_name]['types'].append(access_type)
        
        # 3. 合并同一变量的多次访问
        for var_name, info in processed_vars.items():
            types = set(info['types'])
            if 'WRITE' in types and 'READ' in types:
                final_type = 'READWRITE'
            elif 'WRITE' in types:
                final_type = 'WRITE'
            else:
                final_type = 'READ'
            
            accesses.append(VariableAccess(
                variable=var_name,
                access_type=final_type,
                uid=info['uid'],
                comment=""
            ))
        
        return accesses
    
    def _determine_access_type(self, wire: ET.Element, name_con: Optional[ET.Element], 
                                part_types: Dict[str, str]) -> str:
        """判定连接的访问类型"""
        # 写入类型的 Part
        write_parts = {'Coil', 'SetCoil', 'ResetCoil', 'SCoil', 'RCoil', 'Move', 'TrCoil', 'SvCoil', 'IlCoil'}
        # 输出参数名（通常表示写入）
        output_params = {'out', 'eno', 'Q', 'Q1', 'Q0', 'OUT', 'OUT0', 'OUT1', 'OUTP', 'ret_val'}
        # 输入参数名（通常表示读取）
        input_params = {'in', 'en', 'operand', 'IN', 'IN1', 'IN2', 'IN0', 'INPVAL', 'PT', 'E1', 'E2'}
        
        if name_con is None:
            # 没有 NameCon，检查 Wire 结构
            # 如果 IdentCon 是第二个元素（目标），通常是写入
            children = list(wire)
            for i, child in enumerate(children):
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'IdentCon':
                    if i > 0:
                        # IdentCon 不是第一个，可能是写入目标
                        return 'WRITE'
                    break
            return 'READ'
        
        target_uid = name_con.get('UId')
        param_name = name_con.get('Name', '')
        
        # 检查目标 Part 类型
        target_part = part_types.get(target_uid, '')
        
        if target_part in write_parts:
            return 'WRITE'
        
        # 检查参数名
        if param_name.lower() in [p.lower() for p in output_params]:
            return 'WRITE'
        if param_name.lower() in [p.lower() for p in input_params]:
            return 'READ'
        
        # 对于 Call（FB/FC 调用），检查参数方向
        if target_part == 'Call':
            # 这里简化处理，根据参数名推断
            # 实际上需要查看 CallInfo 中的 Parameter Section
            if 'out' in param_name.lower() or 'ret' in param_name.lower():
                return 'WRITE'
            return 'READ'
        
        return 'READ'  # 默认读取
    
    def _build_cross_references(self, logic_units: List[LogicUnit], block_name: str) -> CrossReferences:
        """构建交叉引用索引"""
        cross_refs = CrossReferences()
        
        for unit in logic_units:
            for access in unit.variable_access:
                ref = {"block": block_name, "network": unit.unit_id, "unit_type": unit.unit_type}
                
                if access.access_type in ('WRITE', 'READWRITE'):
                    if access.variable not in cross_refs.writers:
                        cross_refs.writers[access.variable] = []
                    cross_refs.writers[access.variable].append(ref)
                
                if access.access_type in ('READ', 'READWRITE'):
                    if access.variable not in cross_refs.readers:
                        cross_refs.readers[access.variable] = []
                    cross_refs.readers[access.variable].append(ref)
        
        return cross_refs
    
    def generate_ai_prompt(self, result: ParseResult, network_id: int, iot_data: Dict[str, Any] = None) -> str:
        """生成带实时值的 AI 诊断 Prompt"""
        iot_data = iot_data or {}
        
        # 查找指定的 LogicUnit
        target_unit = None
        for unit in result.logic_units:
            if unit.unit_id == network_id:
                target_unit = unit
                break
        
        if target_unit is None:
            return f"未找到 Network {network_id}"
        
        # 构建变量映射表
        mapping_lines = []
        for uid, var_name in target_unit.uid_symbol_map.items():
            value = iot_data.get(var_name, "N/A")
            mapping_lines.append(f"UID {uid} -> {var_name} = {value}")
        
        # 构建访问列表
        access_lines = []
        for access in target_unit.variable_access:
            value = iot_data.get(access.variable, "N/A")
            access_lines.append(f"  [{access.access_type}] {access.variable} = {value}")
        
        return f"""## Network {network_id}: {target_unit.title}
编程语言: {target_unit.programming_language}

## 信号映射
{chr(10).join(mapping_lines) if mapping_lines else '无变量'}

## 变量访问
{chr(10).join(access_lines) if access_lines else '无变量访问'}

## 原始逻辑
```xml
{target_unit.raw_content[:2000]}{'...(truncated)' if len(target_unit.raw_content) > 2000 else ''}
```

## 故障分析任务
请分析为何该 Network 输出无法得电，检查所有 READ 变量的当前值是否满足逻辑条件。
"""
    
    def _get_tag_name(self, tag: str) -> str:
        """提取标签名（移除命名空间）"""
        return tag.split('}')[-1] if '}' in tag else tag
    
    def _extract_multilingual_text(self, elem: ET.Element) -> str:
        """提取多语言文本元素中的内容"""
        texts = []
        for child in elem.iter():
            child_tag = child.tag.split('}')[-1] if '}' in child.tag else child.tag
            # 添加 MultiLanguageText 标签
            if child_tag in ['Text', 'MultilingualTextItem', 'MultiLanguageText'] and child.text:
                text = child.text.strip()
                if text and text not in texts:
                    texts.append(text)
        
        # 优先返回中文，否则返回第一个
        for t in texts:
            if any('\u4e00' <= c <= '\u9fff' for c in t):
                return t
        return texts[0] if texts else ""
    
    def _load_xml(self, file_path: str, encoding: str, config: Dict) -> Optional[ET.ElementTree]:
        """加载并预处理XML"""
        try:
            with open(file_path, 'r', encoding=encoding, errors='ignore') as f:
                content = f.read()
            
            # 命名空间处理
            preprocess_config = config.get('preprocessing', {})
            ns_handling = preprocess_config.get('namespace_handling', 'strip')
            
            if ns_handling == 'strip':
                # 移除命名空间声明和前缀
                content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
                content = re.sub(r'<(\/?)[a-zA-Z0-9_]+:', r'<\1', content)
            
            return ET.ElementTree(ET.fromstring(content))
        except Exception as e:
            print(f"XML load error: {e}", file=sys.stderr)
            return None
    
    def _find_block_elements(self, root: ET.Element, blocks_config: Dict) -> List[ET.Element]:
        """查找所有程序块元素"""
        selector = blocks_config.get('selector', '')
        
        # DEBUG 输出
        if os.environ.get('PLC_DEBUG'):
            print(f"DEBUG: blocks_config keys = {list(blocks_config.keys())}", file=sys.stderr)
            print(f"DEBUG: selector = '{selector[:50] if selector else 'EMPTY'}'", file=sys.stderr)
            print(f"DEBUG: root.tag = {root.tag}", file=sys.stderr)
        
        if not selector:
            if os.environ.get('PLC_DEBUG'):
                print("DEBUG: Returning empty - no selector", file=sys.stderr)
            return []
        
        elements = []
        
        # 支持的块标签名列表 (排除 CompileUnit 等内部结构)
        block_tags = [
            'SW.Blocks.FB', 'SW.Blocks.FC', 'SW.Blocks.DB', 
            'SW.Blocks.OB', 'SW.Blocks.GlobalDB', 'SW.Blocks.InstanceDB'
        ]
        
        # 排除列表 - 这些不是独立的程序块
        exclude_tags = [
            'SW.Blocks.CompileUnit', 'SW.Blocks.Access', 'SW.Blocks.Part',
            'SW.Blocks.Call', 'SW.Blocks.Comment'
        ]
        
        # 遍历所有元素，查找匹配的块
        all_tags_found = set()
        for elem in root.iter():
            all_tags_found.add(elem.tag)
            # 跳过排除列表中的标签
            if elem.tag in exclude_tags:
                continue
            # 直接匹配完整标签名
            if elem.tag in block_tags:
                elements.append(elem)
            # 支持其他 SW.Blocks.* 标签（但排除已知的内部结构）
            elif '.' in elem.tag and elem.tag.startswith('SW.Blocks.'):
                # 检查是否是顶级块（有 AttributeList/Name 子元素）
                name_elem = elem.find('AttributeList/Name')
                if name_elem is not None:
                    elements.append(elem)
        
        # DEBUG 输出
        if os.environ.get('PLC_DEBUG'):
            sw_tags = [t for t in all_tags_found if 'SW' in t or 'Block' in t]
            print(f"DEBUG: SW/Block related tags found = {sw_tags}", file=sys.stderr)
            print(f"DEBUG: elements found = {len(elements)}", file=sys.stderr)
        
        return list(set(elements))
    
    def _find_block_by_name(self, root: ET.Element, name: str, blocks_config: Dict) -> Optional[ET.Element]:
        """按名称查找块元素"""
        for elem in self._find_block_elements(root, blocks_config):
            name_elem = elem.find('.//AttributeList/Name')
            if name_elem is not None and name_elem.text == name:
                return elem
        return None
    
    def _extract_blocks(self, root: ET.Element, blocks_config: Dict) -> List[BlockInfo]:
        """提取程序块信息"""
        blocks = []
        type_map = blocks_config.get('type_map', {})
        fields = blocks_config.get('fields', {})
        
        for elem in self._find_block_elements(root, blocks_config):
            block_type = type_map.get(elem.tag, elem.tag)
            
            block = BlockInfo(
                name=self._get_field_value(elem, fields.get('name', {})) or "",
                type=block_type,
                number=int(self._get_field_value(elem, fields.get('number', {})) or 0),
                language=self._get_field_value(elem, fields.get('programming_language', {})) or "",
                networks_count=len(list(elem.iter('CompileUnit'))),
                comment=self._get_field_value(elem, fields.get('comment', {})) or "",
                author=self._get_field_value(elem, fields.get('author', {})) or "",
                version=self._get_field_value(elem, fields.get('version', {})) or "",
            )
            blocks.append(block)
        
        return blocks
    
    def _extract_variables(self, block_elem: ET.Element, vars_config: Dict, block_name: str) -> List[VariableInfo]:
        """提取变量信息"""
        variables = []
        fields = vars_config.get('fields', {})
        scope_config = vars_config.get('scope_detection', {})
        scope_mapping = scope_config.get('mapping', {})
        nested_config = vars_config.get('nested', {})
        
        def extract_member(member: ET.Element, parent_path: str = "", depth: int = 0):
            max_depth = nested_config.get('max_depth', 10)
            if depth > max_depth:
                return
            
            name = member.get('Name', '')
            if not name:
                return
            
            path_sep = nested_config.get('path_separator', '.')
            full_path = f"{parent_path}{path_sep}{name}" if parent_path else name
            
            # 确定作用域
            scope = ""
            section = member.find('./ancestor::Section')
            if section is None:
                # 向上查找Section
                parent = member
                while parent is not None:
                    if parent.tag == 'Section':
                        scope = scope_mapping.get(parent.get('Name', ''), parent.get('Name', ''))
                        break
                    parent = self._get_parent(block_elem, parent)
            
            # 如果仍未找到scope，尝试从父元素路径推断
            if not scope:
                for sec_name, sec_scope in scope_mapping.items():
                    if f'/Section[@Name="{sec_name}"]' in str(ET.tostring(member)):
                        scope = sec_scope
                        break
            
            var = VariableInfo(
                name=name,
                path=full_path,
                datatype=member.get('Datatype', ''),
                scope=scope,
                comment=self._get_text(member.find('.//Comment//MultiLanguageText')) or "",
                initial_value=self._get_text(member.find('./StartValue')) or "",
                remanence=member.get('Remanence', 'NonRetain')
            )
            variables.append(var)
            
            # 递归处理嵌套结构
            if nested_config.get('enabled', True):
                for child in member.findall('.//Member'):
                    if child != member:
                        extract_member(child, full_path, depth + 1)
        
        # 查找所有顶层Section
        for section in block_elem.iter('Section'):
            section_name = section.get('Name', '')
            scope = scope_mapping.get(section_name, section_name)
            
            for member in section.findall('./Member'):
                # 直接在member上设置scope
                name = member.get('Name', '')
                if not name:
                    continue
                
                var = VariableInfo(
                    name=name,
                    path=name,
                    datatype=member.get('Datatype', ''),
                    scope=scope,
                    comment=self._get_text(member.find('.//Comment//MultiLanguageText')) or "",
                    initial_value=self._get_text(member.find('./StartValue')) or "",
                    remanence=member.get('Remanence', 'NonRetain')
                )
                variables.append(var)
                
                # 递归处理嵌套
                if nested_config.get('enabled', True):
                    self._extract_nested_members(member, name, scope, variables, nested_config, 1)
        
        return variables
    
    def _extract_nested_members(self, parent: ET.Element, parent_path: str, scope: str, 
                                 variables: List[VariableInfo], nested_config: Dict, depth: int):
        """递归提取嵌套成员"""
        max_depth = nested_config.get('max_depth', 10)
        if depth > max_depth:
            return
        
        path_sep = nested_config.get('path_separator', '.')
        
        # 查找直接子Member
        for section in parent.findall('./Sections/Section'):
            for member in section.findall('./Member'):
                name = member.get('Name', '')
                if not name:
                    continue
                
                full_path = f"{parent_path}{path_sep}{name}"
                
                var = VariableInfo(
                    name=name,
                    path=full_path,
                    datatype=member.get('Datatype', ''),
                    scope=scope,
                    comment=self._get_text(member.find('.//Comment//MultiLanguageText')) or "",
                    initial_value=self._get_text(member.find('./StartValue')) or "",
                    remanence=member.get('Remanence', 'NonRetain')
                )
                variables.append(var)
                
                # 继续递归
                self._extract_nested_members(member, full_path, scope, variables, nested_config, depth + 1)
    
    def _extract_calls(self, root: ET.Element, calls_config: Dict, blocks: List[BlockInfo]) -> List[CallInfo]:
        """提取调用关系"""
        calls = []
        
        # 为每个块提取其调用
        for block_elem in root.iter():
            if not block_elem.tag.startswith('SW.Blocks.'):
                continue
            
            caller_name = ""
            name_elem = block_elem.find('.//AttributeList/Name')
            if name_elem is not None:
                caller_name = name_elem.text or ""
            
            # 查找所有Call
            for call_info in block_elem.iter('CallInfo'):
                callee = call_info.get('Name', '')
                if callee:
                    # 获取网络ID
                    network_id = ""
                    parent = call_info
                    while parent is not None:
                        if parent.tag == 'CompileUnit':
                            network_id = parent.get('ID', '')
                            break
                        parent = self._find_parent_in_tree(root, parent)
                    
                    calls.append(CallInfo(
                        caller=caller_name,
                        callee=callee,
                        callee_type=call_info.get('BlockType', ''),
                        network_id=network_id
                    ))
        
        return calls
    
    def _extract_graph(self, block_elem: ET.Element, graphs_config: Dict) -> Tuple[List[GraphStep], List[GraphTransition]]:
        """提取GRAPH信息"""
        steps = []
        transitions = []
        
        # 提取步
        for step in block_elem.iter('Step'):
            step_info = GraphStep(
                number=int(step.get('Number', 0)),
                name=step.get('Name', ''),
                is_init=step.get('InitStep', 'false').lower() == 'true',
                comment=self._get_text(step.find('.//Comment//Text')) or "",
            )
            
            # 提取动作
            for action in step.findall('.//Action'):
                qualifier = action.get('Qualifier', '')
                action_text = self._get_text(action.find('.//Text'))
                if action_text:
                    step_info.actions.append(f"{qualifier}: {action_text}")
            
            # 提取时间
            max_time = step.find('.//MaximumTime')
            if max_time is not None:
                step_info.max_time = max_time.text or ""
            
            steps.append(step_info)
        
        # 提取转换
        for trans in block_elem.iter('Transition'):
            trans_info = GraphTransition(
                name=trans.get('Name', ''),
                comment=self._get_text(trans.find('.//Comment//Text')) or "",
            )
            
            lang_elem = trans.find('.//ProgrammingLanguage')
            if lang_elem is not None:
                trans_info.condition_type = lang_elem.text or "LAD"
            
            transitions.append(trans_info)
        
        return steps, transitions
    
    def _get_field_value(self, elem: ET.Element, field_config) -> str:
        """根据字段配置提取值"""
        if isinstance(field_config, str):
            # 简单字符串：属性名或简单XPath
            if field_config.startswith('@'):
                return elem.get(field_config[1:], '')
            else:
                found = elem.find(field_config)
                return self._get_text(found) if found is not None else ''
        
        if isinstance(field_config, dict):
            # 字典配置
            xpath = field_config.get('xpath', '')
            attribute = field_config.get('attribute', '')
            default = field_config.get('default', '')
            fallback = field_config.get('fallback', '')
            transform = field_config.get('transform', '')
            
            value = ''
            
            if attribute:
                value = elem.get(attribute, '')
            elif xpath:
                # 处理XPath - ElementTree只支持有限的XPath语法
                search_path = xpath
                # 去掉开头的 ./ 因为 find() 默认就是相对路径
                if search_path.startswith('./'):
                    search_path = search_path[2:]
                # ElementTree 不支持复杂的 XPath，对于包含属性选择器的路径，使用简化处理
                if '[@' in search_path:
                    # 复杂XPath降级处理：尝试提取基本路径
                    base_path = search_path.split('[@')[0]
                    if base_path.startswith('.//'):
                        base_path = base_path[3:]
                    elif base_path.startswith('//'):
                        base_path = base_path[2:]
                    search_path = base_path
                # 将 // 替换为 .// 用于递归查找
                elif search_path.startswith('//'):
                    search_path = '.' + search_path
                
                try:
                    found = elem.find(search_path)
                    if found is None and fallback:
                        fallback_path = fallback
                        if fallback_path.startswith('./'):
                            fallback_path = fallback_path[2:]
                        if '[@' in fallback_path:
                            base_path = fallback_path.split('[@')[0]
                            if base_path.startswith('.//'):
                                base_path = base_path[3:]
                            fallback_path = base_path
                        found = elem.find(fallback_path)
                    value = self._get_text(found) if found is not None else ''
                except Exception as e:
                    if os.environ.get('PLC_DEBUG'):
                        print(f"DEBUG: XPath error for '{xpath}' -> '{search_path}': {e}", file=sys.stderr)
                    value = ''
            
            # 应用转换
            if transform and value:
                if transform == 'int()':
                    try:
                        value = str(int(value))
                    except:
                        value = default
                elif transform == 'bool()':
                    value = 'true' if value.lower() in ['true', '1', 'yes'] else 'false'
                elif transform == 'text()':
                    pass  # 已经是文本
            
            return value if value else default
        
        return ''
    
    def _get_text(self, elem: Optional[ET.Element]) -> str:
        """获取元素文本"""
        if elem is None:
            return ''
        if elem.text:
            return elem.text.strip()
        # 尝试获取所有子文本
        text_parts = []
        for child in elem.iter():
            if child.text:
                text_parts.append(child.text.strip())
        return ' '.join(text_parts)
    
    def _get_parent(self, root: ET.Element, child: ET.Element) -> Optional[ET.Element]:
        """获取父元素（ET不直接支持）"""
        parent_map = {c: p for p in root.iter() for c in p}
        return parent_map.get(child)
    
    def _find_parent_in_tree(self, root: ET.Element, target: ET.Element) -> Optional[ET.Element]:
        """在树中查找元素的父节点"""
        for parent in root.iter():
            if target in parent:
                return parent
        return None
    
    def analyze_full(self, file_path: str) -> Dict:
        """完整分析：预处理 + 提取 + 摘要"""
        result = self.extract(file_path)
        # 添加语义分析
        analyzer = SemanticAnalyzer()
        analyzer.analyze(result, self.root if hasattr(self, 'root') else None)
        return result.to_dict()


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


# ============================================================
# 输出格式化
# ============================================================

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


# ============================================================
# 命令行接口
# ============================================================

def main():
    parser = argparse.ArgumentParser(description='PLC程序分析引擎')
    parser.add_argument('--file', '-f', required=True, help='PLC文件路径')
    parser.add_argument('--action', '-a', choices=['preprocess', 'extract', 'full'], 
                        default='full', help='操作类型')
    parser.add_argument('--output', '-o', help='输出文件路径')
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
        import uuid
        from datetime import datetime
        
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
    elif args.format == 'json-v2':
        # V2.0 格式 - 面向 AI 诊断
        result = engine.extract(args.file)
        output_v2 = result.to_dict_v2()
        json_str = json.dumps(output_v2, ensure_ascii=False, indent=2)
        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(json_str)
        else:
            print(json_str)
    else:
        json_str = json.dumps(output, ensure_ascii=False, indent=2)
        if args.output:
            with open(args.output, 'w', encoding='utf-8') as f:
                f.write(json_str)
        else:
            print(json_str)


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


if __name__ == '__main__':
    main()
