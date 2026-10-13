#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 核心解析器

负责PLC文件的解析和数据提取
"""

import json
import os
import re
import sys
import html
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Dict, List, Any, Optional, Tuple

import chardet

# 设置标准输出为 UTF-8
sys.stdout.reconfigure(encoding="utf-8")

from models import (
    FileInfo, BlockInfo, VariableInfo, CallInfo, NetworkInfo,
    GraphStep, GraphTransition, VariableAccess, LogicUnit,
    CrossReferences, ParseResult, BlockCallPinBinding
)
from config_loader import ConfigLoader
from analyzers import SemanticAnalyzer


class PLCAnalyzerEngine:
    """PLC程序分析引擎"""
    
    def __init__(self, config_loader: ConfigLoader = None):
        self.config_loader = config_loader or ConfigLoader()
        self.current_config: Optional[Dict] = None
        self._udt_cache: Dict[str, Any] = {}      # UDT名 -> {'size': int, 'fields': list, 'unresolved': bool}
        self._udt_search_dirs: List[str] = []      # UDT搜索目录列表
        self._current_file_path: str = ""           # 当前处理的文件路径
        # 偏移污染跟踪（块级）：尺寸未解析的成员无法推进偏移，
        # 其自身及后续所有成员的偏移都不可信 → 不保存（byte_offset=-1）
        self._taint_vars: Optional[List[Any]] = None
        self._taint_start: Optional[int] = None
        self._taint_reason: str = ""
    
    def detect_encoding(self, file_path: str) -> str:
        """检测文件编码"""
        with open(file_path, 'rb') as f:
            raw = f.read(10000)
        result = chardet.detect(raw)
        encoding = result.get('encoding')
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
        else:
            # 对于 .scl/.awl 文件，即使没有匹配到配置也设置品牌
            file_ext = path.suffix.lower()
            if file_ext in ('.scl', '.awl'):
                brand = "Siemens"
                product = "TIA Portal"
        
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
        
        # 检测文件类型：源文本 (.scl/.awl) vs XML
        file_ext = Path(file_path).suffix.lower()
        if file_ext in ('.scl', '.awl'):
            return self._extract_from_source_text(file_path, file_info, file_ext)
        
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
            
            # 初始化 UDT 搜索路径
            self._init_udt_search_dirs(file_path)
            
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
            
            # ============================================================
            # 自动关联：SCL/STL XML 无代码体时，查找同目录下的 .scl/.awl 源文件
            # ============================================================
            has_meaningful_logic = any(
                lu.variable_access or lu.calls for lu in result.logic_units
            )
            if not has_meaningful_logic:
                for block in result.blocks:
                    if block.language in ('SCL', 'STL'):
                        source_units, source_calls = self._try_load_companion_source(
                            file_path, block.name, block.language)
                        if source_units:
                            result.logic_units = source_units  # 用源文件解析结果替换空的XML逻辑单元
                            for callee in source_calls:
                                result.calls.append(CallInfo(
                                    caller=block.name, callee=callee,
                                    callee_type="", network_id=""
                                ))
                            break  # 已找到，无需继续
            
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
                # 获取 CompileUnit 的 ID（十六进制字符串）
                compile_unit_id = elem.get('ID', '')
                logic_unit = self._parse_compile_unit(elem, network_idx, "NETWORK", compile_unit_id)
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
        
        # 构建 GRAPH 拓扑：Step编号 -> 出口Transition编号列表（解析 <Connections>）
        step_to_outgoing = self._build_graph_step_outgoing(root)
        
        # 处理 GRAPH 的 Step Actions、Supervisions、Interlocks
        for step_elem in root.iter():
            tag = self._get_tag_name(step_elem.tag)
            if tag == 'Step':
                step_number = step_elem.get('Number', '0')
                step_name = step_elem.get('Name', '')
                step_units = self._parse_graph_step_logic(step_elem, int(step_number), step_name)
                # 把出口 Transition 编号附加到 ACTION 类型的逻辑单元上
                outgoing = step_to_outgoing.get(int(step_number), [])
                for su in step_units:
                    if su.unit_type.endswith('_ACTION') and outgoing:
                        su.outgoing_transition_numbers = outgoing
                logic_units.extend(step_units)
        
        return logic_units

    def _build_graph_step_outgoing(self, root: ET.Element) -> dict:
        """
        解析 GRAPH <Connections> 段，返回 {step_number: [outgoing_transition_number, ...]}。

        TIA Portal GRAPH XML 拓扑（简化）：
          直连：  Step N  →  Transition M
          分支：  Step N  →  Branch(AltBegin/SimBegin) → Transition M1, M2, ...
        所有 Transition 编号均直接出现在 TransitionRef.Number 属性里。
        """
        step_outgoing: dict = {}  # step_number(int) -> List[int]

        for connections_elem in root.iter():
            if self._get_tag_name(connections_elem.tag) != 'Connections':
                continue

            # 每个 <Connection> 代表一条有向边
            for conn in connections_elem:
                if self._get_tag_name(conn.tag) != 'Connection':
                    continue

                from_elem = conn.find('NodeFrom')
                to_elem   = conn.find('NodeTo')
                if from_elem is None or to_elem is None:
                    continue

                from_step = self._get_connection_node_step(from_elem)
                to_trans  = self._get_connection_node_transition(to_elem)

                if from_step is not None and to_trans is not None:
                    step_outgoing.setdefault(from_step, [])
                    if to_trans not in step_outgoing[from_step]:
                        step_outgoing[from_step].append(to_trans)
                    continue

                # Branch 中转：若 From=Step → Branch，To=Branch → Trans，需两次查找
                # 先收集所有 Branch 出口映射
                from_branch_out = self._get_connection_node_branch_out(from_elem)
                to_branch_in    = self._get_connection_node_branch_in(to_elem)
                # (Branch 自身的中转在下面的第二遍扫描里完成)

        # 第二遍：处理 Step→Branch→Transition 两跳路径
        # 收集所有 Branch 出口 -> Transition 映射，以及 Step -> Branch 映射
        branch_in_to_step: dict  = {}   # (branch_number, in_port) -> step_number
        branch_out_to_trans: dict = {}  # (branch_number, out_port) -> trans_number

        for connections_elem in root.iter():
            if self._get_tag_name(connections_elem.tag) != 'Connections':
                continue
            for conn in connections_elem:
                if self._get_tag_name(conn.tag) != 'Connection':
                    continue
                from_elem = conn.find('NodeFrom')
                to_elem   = conn.find('NodeTo')
                if from_elem is None or to_elem is None:
                    continue

                # Step → Branch(In)
                from_step   = self._get_connection_node_step(from_elem)
                to_branch_in = self._get_connection_node_branch_in(to_elem)
                if from_step is not None and to_branch_in is not None:
                    b_num, b_in = to_branch_in
                    branch_in_to_step[(b_num, b_in)] = from_step

                # Branch(Out) → Transition
                from_branch_out = self._get_connection_node_branch_out(from_elem)
                to_trans        = self._get_connection_node_transition(to_elem)
                if from_branch_out is not None and to_trans is not None:
                    b_num, b_out = from_branch_out
                    branch_out_to_trans[(b_num, b_out)] = to_trans

        # 合并：Step → Branch(In=0) + Branch(Out=k) → Transition
        for (b_num, b_in), step_num in branch_in_to_step.items():
            for (b_num2, b_out), trans_num in branch_out_to_trans.items():
                if b_num == b_num2:
                    step_outgoing.setdefault(step_num, [])
                    if trans_num not in step_outgoing[step_num]:
                        step_outgoing[step_num].append(trans_num)

        return step_outgoing

    def _get_connection_node_step(self, node_elem: ET.Element):
        """从 NodeFrom/NodeTo 中取 StepRef.Number，不存在则返回 None"""
        step_ref = node_elem.find('StepRef')
        if step_ref is not None:
            try:
                return int(step_ref.get('Number', ''))
            except (ValueError, TypeError):
                pass
        return None

    def _get_connection_node_transition(self, node_elem: ET.Element):
        """从 NodeFrom/NodeTo 中取 TransitionRef.Number，不存在则返回 None"""
        trans_ref = node_elem.find('TransitionRef')
        if trans_ref is not None:
            try:
                return int(trans_ref.get('Number', ''))
            except (ValueError, TypeError):
                pass
        return None

    def _get_connection_node_branch_in(self, node_elem: ET.Element):
        """从 NodeTo 中取 BranchRef (In port)，返回 (branch_number, in_port) 或 None"""
        branch_ref = node_elem.find('BranchRef')
        if branch_ref is not None and branch_ref.get('In') is not None:
            try:
                return (int(branch_ref.get('Number', '')), int(branch_ref.get('In', '0')))
            except (ValueError, TypeError):
                pass
        return None

    def _get_connection_node_branch_out(self, node_elem: ET.Element):
        """从 NodeFrom 中取 BranchRef (Out port)，返回 (branch_number, out_port) 或 None"""
        branch_ref = node_elem.find('BranchRef')
        if branch_ref is not None and branch_ref.get('Out') is not None:
            try:
                return (int(branch_ref.get('Number', '')), int(branch_ref.get('Out', '0')))
            except (ValueError, TypeError):
                pass
        return None

    
    def _parse_compile_unit(self, elem: ET.Element, network_idx: int, unit_type: str, compile_unit_id: str = "") -> Optional[LogicUnit]:
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

        if network_source is None:
            # 可能是直接的 StructuredText 结构
            for child in elem.iter():
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'StructuredText':
                    network_source = child
                    try:
                        raw_content = ET.tostring(child, encoding='unicode')
                    except:
                        raw_content = ""
                    break

        # 构建 UID-Symbol 映射
        uid_symbol_map = {}
        uid_scope_map = {}
        variable_access = []
        calls = []
        parts_elem = None
        wires_elem = None
        
        if network_source is not None:
            # 查找 Parts 和 Wires
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
                uid_symbol_map, uid_scope_map = self._build_uid_symbol_map(parts_elem)
            
            if parts_elem is not None and wires_elem is not None:
                variable_access = self._analyze_wire_connections(wires_elem, parts_elem, uid_symbol_map, uid_scope_map)

            if not variable_access and self._get_tag_name(network_source.tag) == 'StructuredText':
                structured_text = self._extract_structured_text_content(network_source)
                variable_access, uid_symbol_map, calls = self._extract_scl_style_accesses(structured_text)

        # 提取FB/FC引脚绑定（仅LAD/FBD有Parts+Wires结构，且包含Call元素）
        call_pin_bindings = []
        if parts_elem is not None and wires_elem is not None:
            call_pin_bindings = self._extract_call_pin_bindings(parts_elem, wires_elem, uid_symbol_map)

        return LogicUnit(
            unit_id=network_idx,
            unit_type=unit_type,
            title=title,
            raw_content=raw_content,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment=comment,
            programming_language=programming_language,
            compile_unit_id=compile_unit_id,  # 保存原始的 CompileUnit ID 用于关联
            calls=calls,
            call_pin_bindings=call_pin_bindings,
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
                    # 2026-10-07：保留 scope map 并传给 _analyze_wire_connections，
                    # 让 Transition 的变量访问落 variable_scope（LocalVariable/GlobalVariable）。
                    # 此前 `uid_symbol_map, _ = ...` 丢弃 scope，导致 GRAPH 转换条件里
                    # #S_AutoRunMode / #I_Mode.DryRunMode 这类 FB 接口变量入库后作用域为空，
                    # 下游（诊断/具体化）无法区分局部形参与全局变量（实测全库 1060 行 TRANSITION scope 全空）。
                    uid_symbol_map, uid_scope_map = self._build_uid_symbol_map(parts_elem)

                if parts_elem is not None and wires_elem is not None:
                    variable_access = self._analyze_wire_connections(wires_elem, parts_elem, uid_symbol_map, uid_scope_map)
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
        """解析 GRAPH Step 中的逻辑（Actions, Supervisions, Interlocks）
        
        unit_id 使用 Step 原始编号，通过 unit_type 区分子类型：
        - STEP_{N}_SUPERVISION  
        - STEP_{N}_INTERLOCK
        unit_type 中已包含 Step 编号，不再把编号编码到 unit_id 中
        """
        units = []

        # 解析 Actions
        has_action_unit = False
        for actions_elem in step_elem.findall('./Actions'):
            unit = self._parse_graph_step_actions(actions_elem, step_number, step_name)
            if unit:
                units.append(unit)
                has_action_unit = True

        if not has_action_unit:
            units.append(self._build_empty_graph_step_action(step_elem, step_number, step_name))
        
        # 解析 Supervisions（可能有多个，用子索引区分）
        supervision_idx = 0
        for supervision in step_elem.iter():
            tag = self._get_tag_name(supervision.tag)
            if tag == 'Supervision':
                supervision_idx += 1
                prog_lang = supervision.get('ProgrammingLanguage', 'LAD')
                unit = self._parse_flgnet_to_logic_unit(
                    supervision, step_number, 
                    f"STEP_{step_number}_SUPERVISION", step_name, prog_lang
                )
                if unit:
                    units.append(unit)
        
        # 解析 Interlocks（可能有多个，用子索引区分）
        interlock_idx = 0
        for interlock in step_elem.iter():
            tag = self._get_tag_name(interlock.tag)
            if tag == 'Interlock':
                interlock_idx += 1
                prog_lang = interlock.get('ProgrammingLanguage', 'LAD')
                unit = self._parse_flgnet_to_logic_unit(
                    interlock, step_number,
                    f"STEP_{step_number}_INTERLOCK", step_name, prog_lang
                )
                if unit:
                    units.append(unit)
        
        return units

    def _parse_graph_step_actions(self, actions_elem: ET.Element, step_number: int, step_name: str) -> Optional[LogicUnit]:
        """解析 GRAPH Step 的 Actions，生成单独的 LogicUnit"""
        action_lines = []
        action_code_lines = []
        
        for action_elem in actions_elem.findall('./Action'):
            qualifier = action_elem.get('Qualifier', '')
            action_text = self._extract_graph_action_text(action_elem)
            if not action_text:
                continue

            action_code_lines.append(action_text)

            if qualifier:
                action_lines.append(f"{qualifier}: {action_text}")
            else:
                action_lines.append(action_text)

        raw_content = ""
        try:
            raw_content = ET.tostring(actions_elem, encoding='unicode')
        except:
            raw_content = "\n".join(action_lines)

        if not action_lines:
            action_lines.append(f"GRAPH step S{step_number} has no action statements")

        variable_access, uid_symbol_map, calls = self._extract_scl_style_accesses("\n".join(action_code_lines))

        return LogicUnit(
            unit_id=step_number,
            unit_type=f"STEP_{step_number}_ACTION",
            title=step_name,
            raw_content=raw_content,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment="\n".join(action_lines),
            programming_language="GRAPH",
            calls=calls
        )

    def _build_empty_graph_step_action(self, step_elem: ET.Element, step_number: int, step_name: str) -> LogicUnit:
        """Build a placeholder StepAction so GRAPH step indexes stay aligned."""
        raw_content = f'<Actions StepNumber="{step_number}" Empty="true" />'

        return LogicUnit(
            unit_id=step_number,
            unit_type=f"STEP_{step_number}_ACTION",
            title=step_name,
            raw_content=raw_content,
            uid_symbol_map={},
            variable_access=[],
            comment=f"GRAPH step S{step_number} has no action statements",
            programming_language="GRAPH",
            calls=[]
        )
    
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
            uid_symbol_map, _ = self._build_uid_symbol_map(parts_elem)
        
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

    def _extract_graph_action_text(self, action_elem: ET.Element) -> str:
        """拼接 GRAPH Action 下多个 Token 的文本"""
        parts = []

        for token in action_elem.iter():
            if self._get_tag_name(token.tag) != 'Token':
                continue

            token_text = token.get('Text', '')
            if token_text:
                parts.append(token_text)

        action_text = ''.join(parts).replace('\r\n', '\n').replace('\r', '\n').strip()
        return action_text

    def _extract_structured_text_content(self, structured_text_elem: ET.Element) -> str:
        """将 StructuredText XML 还原为近似源码文本"""
        parts = []

        for elem in structured_text_elem.iter():
            tag = self._get_tag_name(elem.tag)

            if tag == 'Access':
                symbol_name = self._extract_symbol_name(elem)
                if not symbol_name:
                    symbol_name = self._extract_constant_value(elem)
                if symbol_name:
                    parts.append(symbol_name)
            elif tag == 'Token':
                token_text = elem.get('Text', '')
                if token_text:
                    parts.append(html.unescape(token_text))
            elif tag == 'LineComment':
                comment_text = self._extract_multilingual_text(elem)
                if comment_text:
                    parts.append(f"//{comment_text}")
            elif tag == 'NewLine':
                num = int(elem.get('Num', '1') or '1')
                parts.append('\n' * max(1, num))
            elif tag == 'Blank':
                num = int(elem.get('Num', '1') or '1')
                parts.append(' ' * max(1, num))

        return ''.join(parts)

    def _extract_scl_style_accesses(self, text: str) -> Tuple[List[VariableAccess], Dict[str, str], List[str]]:
        """从 SCL/StructuredText/GRAPH Action 文本中提取变量访问"""
        variable_access = []
        uid_symbol_map = {}
        calls = []
        seen_vars = {}
        var_idx = 0

        for raw_line in text.split('\n'):
            stripped = raw_line.strip()
            if not stripped or stripped.startswith('//'):
                continue

            line_comment = ""
            comment_match = re.search(r'//(.*)$', stripped)
            if comment_match:
                line_comment = comment_match.group(1).strip()
                code_part = stripped[:comment_match.start()].strip()
            else:
                code_part = stripped

            if not code_part:
                continue

            call_matches = re.finditer(r'#?(\w+(?:\.\w+)*)\s*\(', code_part)
            for cm in call_matches:
                call_name = cm.group(1)
                calls.append(call_name)

                call_start = cm.end()
                paren_depth = 1
                call_end = call_start
                for ci in range(call_start, len(code_part)):
                    if code_part[ci] == '(':
                        paren_depth += 1
                    elif code_part[ci] == ')':
                        paren_depth -= 1
                        if paren_depth == 0:
                            call_end = ci
                            break

                params_text = code_part[call_start:call_end]
                for pm in re.finditer(r'(\w+)\s*(=>|:=)\s*([^,]+)', params_text):
                    direction = pm.group(2)
                    value_expr = pm.group(3).strip()
                    refs = self._extract_scl_variable_references(value_expr)
                    for ref in refs:
                        if direction == ':=':
                            self._scl_record_access(ref, 'READ', line_comment, seen_vars)
                        elif direction == '=>':
                            self._scl_record_access(ref, 'WRITE', line_comment, seen_vars)

            assign_match = re.match(r'^(.+?)\s*:=\s*(.+?)\s*;?\s*$', code_part)
            if assign_match and '(' not in assign_match.group(0).split(':=')[0]:
                left_side = assign_match.group(1).strip()
                right_side = assign_match.group(2).strip()

                for ref in self._extract_scl_variable_references(left_side):
                    self._scl_record_access(ref, 'WRITE', line_comment, seen_vars)

                for ref in self._extract_scl_variable_references(right_side):
                    self._scl_record_access(ref, 'READ', line_comment, seen_vars)
                continue

            cond_match = re.match(r'^(?:IF|ELSIF|WHILE|UNTIL|CASE|FOR)\b\s+(.+)', code_part, re.IGNORECASE)
            if cond_match:
                for ref in self._extract_scl_variable_references(cond_match.group(1)):
                    self._scl_record_access(ref, 'READ', line_comment, seen_vars)
                continue

            bare_refs = self._extract_scl_variable_references(code_part)
            if bare_refs and not re.search(r'\b(?:AND|OR|NOT|XOR|MOD|TO|THEN|DO|OF)\b', code_part, re.IGNORECASE):
                for ref in bare_refs:
                    self._scl_record_access(ref, 'WRITE', line_comment, seen_vars)

        for var_name, info in seen_vars.items():
            types = info['types']
            if 'WRITE' in types and 'READ' in types:
                final_type = 'READWRITE'
            elif 'WRITE' in types:
                final_type = 'WRITE'
            else:
                final_type = 'READ'

            var_idx += 1
            uid_symbol_map[str(var_idx)] = var_name
            variable_access.append(VariableAccess(
                variable=var_name,
                access_type=final_type,
                uid=str(var_idx),
                comment=info['comment'],
                instruction=':=',
                address_mode='symbolic',
                variable_scope=info.get('scope', '')
            ))

        return variable_access, uid_symbol_map, calls

    def _extract_scl_variable_references(self, text: str) -> List[str]:
        """提取 SCL/StructuredText 文本中的变量引用，支持数组下标和全局符号路径"""
        refs = []

        local_pattern = r'#([\w\u4e00-\u9fff]+(?:\[\d+\])?(?:\.[\w\u4e00-\u9fff]+(?:\[\d+\])?)*)'
        # 支持 TIA Portal 完整路径：引号段 + 任意数量的点分隔段（每段可以是引号或裸词）
        # 例如 "DB515_AxisCombo".Status."1#CreateCamExecute"
        quoted_global_pattern = r'"([^"]+)"((?:\.(?:"[^"]+"|[\w\u4e00-\u9fff]+(?:\[\d+\])?))*)'
        bare_global_pattern = r'(?<![#"\w\.])([A-Za-z_][\w\u4e00-\u9fff]*(?:\[\d+\])?(?:\.[\w\u4e00-\u9fff]+(?:\[\d+\])?)+)'

        # 先处理引号全局路径（最高优先级），确保 "DB.."."1#Foo" 这类整体被正确提取
        for match in re.finditer(quoted_global_pattern, text):
            base = match.group(1)
            suffix_raw = match.group(2) or ''
            if suffix_raw:
                # 解析 suffix 中每个段（可能有引号包裹），重建完整路径
                segments = re.findall(r'\.(?:"([^"]+)"|([\w\u4e00-\u9fff]+(?:\[\d+\])?))', suffix_raw)
                suffix_parts = [quoted or unquoted for quoted, unquoted in segments]
                refs.append(base + '.' + '.'.join(suffix_parts))
            else:
                refs.append(base)

        # 屏蔽所有引号内容后再扫描 local/bare，避免 "1#Foo" 中的 #Foo 被误识别为局部变量
        masked = re.sub(r'"[^"]*"', lambda m: ' ' * len(m.group()), text)

        for match in re.finditer(local_pattern, masked):
            refs.append('#' + match.group(1))

        for match in re.finditer(bare_global_pattern, masked):
            candidate = match.group(1)
            if candidate.upper() in {'TRUE', 'FALSE'}:
                continue
            refs.append(candidate)

        return list(dict.fromkeys(refs))
    
    def _build_uid_symbol_map(self, parts_elem: ET.Element) -> tuple:
        """构建 UID -> 变量名映射，同时返回 UID -> Scope 映射"""
        uid_map = {}
        uid_scope_map = {}
        
        for elem in parts_elem.iter():
            tag = self._get_tag_name(elem.tag)
            if tag == 'Access':
                uid = elem.get('UId')
                scope = elem.get('Scope', '')
                
                if not uid:
                    continue
                
                # 记录 scope
                if scope:
                    uid_scope_map[uid] = scope
                
                # 提取变量名
                var_name = self._extract_symbol_name(elem)
                if var_name:
                    uid_map[uid] = var_name
                elif scope == 'LiteralConstant' or scope == 'TypedConstant':
                    # 常量，提取值
                    const_val = self._extract_constant_value(elem)
                    if const_val:
                        uid_map[uid] = const_val
        
        return uid_map, uid_scope_map
    
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
                                   uid_map: Dict[str, str], uid_scope_map: Dict[str, str] = None) -> List[VariableAccess]:
        """分析 Wires 确定 READ/WRITE"""
        accesses = []
        processed_vars = {}  # 变量 -> 访问类型列表
        if uid_scope_map is None:
            uid_scope_map = {}
        
        # 1. 构建 Part 类型映射 (UID -> Part类型/名称)
        part_types = {}
        call_param_sections = {}
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
                    call_param_sections[call_uid] = self._extract_call_parameter_sections(elem)
        
        # 2. 遍历 Wire，分析连接关系
        for wire in wires_elem.iter():
            tag = self._get_tag_name(wire.tag)
            if tag != 'Wire':
                continue

            ident_cons = []
            name_cons = []
            for child in wire:
                child_tag = self._get_tag_name(child.tag)
                if child_tag == 'IdentCon':
                    ident_cons.append(child)
                elif child_tag == 'NameCon':
                    name_cons.append(child)

            if not ident_cons:
                continue

            for ident_con in ident_cons:
                var_uid = ident_con.get('UId')
                if not var_uid or var_uid not in uid_map:
                    continue

                var_name = uid_map.get(var_uid, '')
                if not var_name:
                    continue

                access_type = self._determine_access_type(
                    wire,
                    ident_con,
                    name_cons,
                    part_types,
                    call_param_sections
                )

                if var_name not in processed_vars:
                    processed_vars[var_name] = {'uid': var_uid, 'types': [], 'scope': uid_scope_map.get(var_uid, '')}
                if access_type == 'READWRITE':
                    processed_vars[var_name]['types'].extend(['READ', 'WRITE'])
                else:
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
                comment="",
                variable_scope=info.get('scope', '')
            ))
        
        return accesses

    def _extract_call_parameter_sections(self, call_elem: ET.Element) -> Dict[str, str]:
        """提取 Call 参数方向信息，避免仅靠参数名猜测输入/输出。"""
        param_sections = {}

        for elem in call_elem.iter():
            if self._get_tag_name(elem.tag) != 'Parameter':
                continue

            param_name = elem.get('Name', '')
            if not param_name:
                continue

            param_sections[param_name.lower()] = (elem.get('Section', '') or '').lower()

        return param_sections

    def _extract_call_pin_bindings(self, parts_elem: ET.Element, wires_elem: ET.Element,
                                    uid_symbol_map: Dict[str, str]) -> List[BlockCallPinBinding]:
        """提取Network中所有FB/FC调用的引脚绑定 (Formal-to-Actual映射)。
        
        解析原理：
          Call元素 (Parts内) 标识一次FB/FC调用，其CallInfo子元素含有：
            - Name        : FB/FC类型名         (e.g. "FB_Cylinder")
            - BlockType   : "FB" / "FC"
            - Instance    : 背景数据块 Symbol   (仅FB)
            - Parameter[] : 形参名+Section方向
          Wires中以 NameCon.UId==Call.UId 标识连接到该调用的引线：
            - NameCon.Name : 形参名
            - IdentCon.UId : 实参 Access 元素的UID -> uid_symbol_map 得到实际变量名
        """
        bindings: List[BlockCallPinBinding] = []

        # 1. 扫描所有 Call 元素，建立元数据
        for call_elem in parts_elem.iter():
            if self._get_tag_name(call_elem.tag) != 'Call':
                continue

            call_uid = call_elem.get('UId', '')
            if not call_uid:
                continue

            call_info_elem = None
            for child in call_elem:
                if self._get_tag_name(child.tag) == 'CallInfo':
                    call_info_elem = child
                    break
            if call_info_elem is None:
                continue

            callee_name = call_info_elem.get('Name', '')
            callee_type = call_info_elem.get('BlockType', 'FB')

            # 背景数据块 (FB实例)
            instance_db = ''
            instance_elem = call_info_elem.find('.//Instance')
            if instance_elem is not None:
                # Instance 下有 Symbol/Component 结构，复用 _extract_symbol_name
                instance_db = self._extract_symbol_name(instance_elem)
                if not instance_db:
                    # Instance 下直接挂 Component（无 Symbol 包装，GlobalVariable 单组件形态）：
                    # <Instance Scope="GlobalVariable"><Component Name="DB160_MainGraph"/></Instance>
                    direct_comps = []
                    for comp in instance_elem.iter():
                        if self._get_tag_name(comp.tag) == 'Component':
                            comp_name = comp.get('Name', '')
                            if comp_name:
                                direct_comps.append(comp_name)
                    instance_db = '.'.join(direct_comps)
                if not instance_db:
                    # 有些格式直接把 DB 名写在 Instance 的 Name 属性上
                    instance_db = instance_elem.get('Name', '')

            # 形参 → Section 方向map (不区分大小写)
            param_sections: Dict[str, str] = {}
            for param in call_info_elem.findall('.//Parameter'):
                pname = param.get('Name', '')
                section = (param.get('Section', '') or '').lower()
                if pname:
                    param_sections[pname] = section

            # 2. 遍历 Wires，找连接到此 Call 的引线
            input_pins: Dict[str, str] = {}
            output_pins: Dict[str, str] = {}
            inout_pins: Dict[str, str] = {}

            for wire in wires_elem.iter():
                if self._get_tag_name(wire.tag) != 'Wire':
                    continue

                name_con = None
                ident_uid = None
                for child in wire:
                    ctag = self._get_tag_name(child.tag)
                    if ctag == 'NameCon' and child.get('UId') == call_uid:
                        name_con = child
                    elif ctag == 'IdentCon':
                        ident_uid = child.get('UId', '')

                if name_con is None or not ident_uid:
                    continue

                pin_name = name_con.get('Name', '')
                actual_var = uid_symbol_map.get(ident_uid, '')
                if not pin_name or not actual_var:
                    continue

                section = param_sections.get(pin_name, '').lower()
                if section == 'output':
                    output_pins[pin_name] = actual_var
                elif section in ('inout', 'inputoutput'):
                    inout_pins[pin_name] = actual_var
                elif section == 'input':
                    input_pins[pin_name] = actual_var
                else:
                    # 无 Section 信息时按 TIA Portal 惯例猜测
                    plo = pin_name.lower()
                    if plo.startswith('out') or plo in {'q', 'eno', 'ret_val'}:
                        output_pins[pin_name] = actual_var
                    else:
                        input_pins[pin_name] = actual_var

            if callee_name:
                bindings.append(BlockCallPinBinding(
                    callee_name=callee_name,
                    callee_block_type=callee_type,
                    instance_db=instance_db,
                    call_uid=call_uid,
                    input_pins=input_pins,
                    output_pins=output_pins,
                    inout_pins=inout_pins,
                ))

        return bindings

    def _determine_access_type(self, wire: ET.Element, ident_con: ET.Element,
                                name_cons: List[ET.Element], part_types: Dict[str, str],
                                call_param_sections: Dict[str, Dict[str, str]]) -> str:
        """判定 IdentCon 变量在当前 Wire 上的访问类型。"""
        pin_accesses = []

        for name_con in name_cons:
            target_uid = name_con.get('UId')
            param_name = name_con.get('Name', '')
            access_type = self._classify_namecon_access_type(target_uid, param_name, part_types, call_param_sections)
            if access_type == 'READWRITE':
                pin_accesses.extend(['READ', 'WRITE'])
            elif access_type:
                pin_accesses.append(access_type)

        if pin_accesses:
            access_set = set(pin_accesses)
            if 'READ' in access_set and 'WRITE' in access_set:
                return 'READWRITE'
            if 'WRITE' in access_set:
                return 'WRITE'
            return 'READ'

        children = list(wire)
        for i, child in enumerate(children):
            if child is ident_con:
                if i > 0:
                    return 'WRITE'
                break

        return 'READ'

    def _classify_namecon_access_type(self, target_uid: Optional[str], param_name: str,
                                      part_types: Dict[str, str],
                                      call_param_sections: Dict[str, Dict[str, str]]) -> str:
        """根据目标 part 和引脚名判定变量是读还是写。"""
        target_part = part_types.get(target_uid or '', '')
        pin_name = (param_name or '').strip()
        pin_lower = pin_name.lower()
        timer_parts = {'TON', 'TOF', 'TP'}
        common_output_pins = {
            'q', 'q0', 'q1', 'eno', 'ret_val', 'et', 'cv', 'done', 'busy',
            'error', 'status', 'valid', 'ndr', 'dr', 'ok'
        }

        if target_part == 'Call':
            param_section = call_param_sections.get(target_uid or '', {}).get(pin_lower, '')
            if param_section == 'output':
                return 'WRITE'
            if param_section in {'inout', 'inputoutput'}:
                return 'READWRITE'
            if param_section == 'input':
                return 'READ'
            if pin_lower.startswith('out') or pin_lower in {'q', 'eno', 'ret_val'} or 'ret' in pin_lower:
                return 'WRITE'
            return 'READ'

        if target_part in {'Coil', 'PCoil', 'SetCoil', 'ResetCoil', 'SCoil', 'RCoil', 'TrCoil', 'SvCoil', 'IlCoil'}:
            if pin_lower == 'operand':
                return 'WRITE'
            if pin_lower.startswith('out') or pin_lower in common_output_pins:
                return 'WRITE'
            return 'READ'

        if target_part in timer_parts:
            if pin_lower.startswith('out') or pin_lower in common_output_pins:
                return 'WRITE'
            return 'READ'

        if target_part == 'Move':
            if pin_lower.startswith('out') or pin_lower in common_output_pins:
                return 'WRITE'
            return 'READ'

        if target_part in {'Contact', 'PContact', 'NContact'}:
            return 'READ'

        if target_part in {'PBox', 'NBox'}:
            if pin_lower.startswith('out') or pin_lower in common_output_pins:
                return 'WRITE'
            return 'READ'

        if pin_lower.startswith('out') or pin_lower in common_output_pins:
            return 'WRITE'
        if pin_lower in {'operand', 'bit', 'in', 'en', 's', 'r', 'pt'} or re.match(r'^(in|e)\d+$', pin_lower):
            return 'READ'

        return 'READ'
    
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
            'SW.Blocks.OB', 'SW.Blocks.GlobalDB', 'SW.Blocks.InstanceDB',
            'SW.Types.PlcStruct'   # UDT 类型定义
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
            
            # InstanceDB 的绑定信息（西门子固定结构，不依赖品牌配置）：
            #   <AttributeList><InstanceOfName>FB161_HomeGraph</InstanceOfName>
            #                 <InstanceOfType>FB</InstanceOfType></AttributeList>
            instance_of_name = self._get_text(elem.find('AttributeList/InstanceOfName')) or ""
            instance_of_type = self._get_text(elem.find('AttributeList/InstanceOfType')) or ""

            block = BlockInfo(
                name=self._get_field_value(elem, fields.get('name', {})) or "",
                type=block_type,
                number=int(self._get_field_value(elem, fields.get('number', {})) or 0),
                language=self._get_field_value(elem, fields.get('programming_language', {})) or "",
                networks_count=len(list(elem.iter('CompileUnit'))),
                comment=self._get_field_value(elem, fields.get('comment', {})) or "",
                author=self._get_field_value(elem, fields.get('author', {})) or "",
                version=self._get_field_value(elem, fields.get('version', {})) or "",
                instance_of_name=instance_of_name.strip(),
                instance_of_type=instance_of_type.strip(),
            )
            blocks.append(block)
        
        return blocks
    
    # ============================================================
    # S7 偏移量计算相关
    # ============================================================
    
    # S7 基础数据类型大小映射 (字节数)
    S7_TYPE_SIZES = {
        'Bool': 0.125,       # 1 bit = 0.125 bytes
        'Byte': 1, 'USInt': 1, 'SInt': 1, 'Char': 1,
        'Int': 2, 'UInt': 2, 'Word': 2, 'Date': 2, 'Database': 2,
        'DInt': 4, 'UDInt': 4, 'DWord': 4, 'Real': 4, 'Time': 4,
        'Time_Of_Day': 4, 'TOD': 4,
        'Date_And_Time': 8,  # DT = 8 bytes BCD
        'LInt': 8, 'ULInt': 8, 'LWord': 8, 'LReal': 8, 'LTime': 8,
        'LDT': 8, 'LTOD': 8,
        'DTL': 12,
        'Pointer': 6, 'Any': 10, 'ANY': 10,
        'DB_ANY': 2, 'Variant': 0,
        # 硬件标识符 (均为 UInt / 2字节)
        'HW_ANY': 2, 'HW_INTERFACE': 2, 'HW_IO': 2, 'HW_IOSYSTEM': 2,
        'HW_SUBMODULE': 2, 'HW_DEVICE': 2, 'HW_HSC': 2, 'HW_PWM': 2, 'HW_PTO': 2,
        # 工艺对象引用 (DB 编号)
        'TO_Axis': 2, 'TO_Cam': 2, 'TO_CamBase': 2, 'TO_Object': 2,
        'Remote': 4,
    }

    # InOut 区中按指针（6字节）处理的简单判定：
    # 零尺寸 S7 通信/系统 SFB 类型：在 DB 地址空间中不占偏移
    # (TIA Portal 不展示其实际物理地址，内部状态由操作系统管理)
    _ZERO_SIZE_TYPES = {
        # 数据记录读写
        'RDREC', 'WRREC',
        # S7通信 (SFB8-15/52-54, MPI/PROFIBUS)
        'USEND', 'URCV', 'BSEND', 'BRCV', 'GET', 'PUT',
        # PROFINET / TCP-IP 通信
        'TSEND', 'TRCV', 'TSEND_C', 'TRCV_C', 'TCON', 'TDISCON',
        'OPEN_DB', 'CLOSE_DB',
        # PROFIBUS DP 数据交换
        'DPRD_DAT', 'DPWR_DAT', 'DP_SEND', 'DP_RECV',
        'AG_SEND', 'AG_RECV', 'AG_LSEND', 'AG_LRECV',
        # AS-i / IO-Link
        'RALRM',
        # 报警
        'ALARM_8', 'ALARM_8P', 'ALARM', 'NOTIFY',
        # Variant 无固定大小
        'Variant',
        # S7-1500 OUC / Open User Communication
        'TMAILC', 'FTP_CMD',
    }

    # 内层小类型（InOut 区不按指针处理，按实际大小）
    # 除基本标量类型和已有固定大小的指针/Any外，都视为复杂类型
    _INOUT_SIMPLE_TYPES = {
        'Bool', 'Byte', 'USInt', 'SInt', 'Char',
        'Int', 'UInt', 'Word', 'Date', 'Database',
        'DInt', 'UDInt', 'DWord', 'Real', 'Time', 'Time_Of_Day', 'TOD',
        'Date_And_Time',
        'LInt', 'ULInt', 'LWord', 'LReal', 'LTime', 'LDT', 'LTOD',
        'DTL',
        'HW_ANY', 'HW_INTERFACE', 'HW_IO', 'HW_IOSYSTEM', 'HW_SUBMODULE',
        'HW_DEVICE', 'HW_HSC', 'HW_PWM', 'HW_PTO',
        'Database', 'DB_ANY',
        'TO_Axis', 'TO_Cam', 'TO_CamBase', 'TO_Object',
        'Pointer', 'Any', 'ANY', 'Remote',
    }
    INOUT_POINTER_SIZE = 6  # 复杂类型在 InOut 区占 6 字节 (S7 area pointer)
    
    # IEC 系统结构体的内部字段定义
    IEC_SYSTEM_TYPES = {
        'IEC_TIMER': {
            'size': 16,
            'fields': [
                ('PT', 'Time', 4),
                ('ET', 'Time', 4),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'IEC_COUNTER': {
            'size': 6,
            'fields': [
                ('CU', 'Bool', 0.125),
                ('CD', 'Bool', 0.125),
                ('R', 'Bool', 0.125),
                ('LD', 'Bool', 0.125),
                ('QU', 'Bool', 0.125),
                ('QD', 'Bool', 0.125),
                ('PV', 'Int', 2),
                ('CV', 'Int', 2),
            ]
        },
        'IEC_DCOUNTER': {
            'size': 12,
            'fields': [
                ('CU', 'Bool', 0.125),
                ('CD', 'Bool', 0.125),
                ('R', 'Bool', 0.125),
                ('LD', 'Bool', 0.125),
                ('QU', 'Bool', 0.125),
                ('QD', 'Bool', 0.125),
                ('PV', 'DInt', 4),
                ('CV', 'DInt', 4),
            ]
        },
        'IEC_LTIMER': {
            'size': 24,
            'fields': [
                ('PT', 'LTime', 8),
                ('ET', 'LTime', 8),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'IEC_SCOUNTER': {
            'size': 4,
            'fields': [
                ('CU', 'Bool', 0.125),
                ('CD', 'Bool', 0.125),
                ('R', 'Bool', 0.125),
                ('LD', 'Bool', 0.125),
                ('QU', 'Bool', 0.125),
                ('QD', 'Bool', 0.125),
                ('PV', 'SInt', 1),
                ('CV', 'SInt', 1),
            ]
        },
        'IEC_UCOUNTER': {
            'size': 6,
            'fields': [
                ('CU', 'Bool', 0.125),
                ('CD', 'Bool', 0.125),
                ('R', 'Bool', 0.125),
                ('LD', 'Bool', 0.125),
                ('QU', 'Bool', 0.125),
                ('QD', 'Bool', 0.125),
                ('PV', 'UInt', 2),
                ('CV', 'UInt', 2),
            ]
        },
        'IEC_UDCOUNTER': {
            'size': 12,
            'fields': [
                ('CU', 'Bool', 0.125),
                ('CD', 'Bool', 0.125),
                ('R', 'Bool', 0.125),
                ('LD', 'Bool', 0.125),
                ('QU', 'Bool', 0.125),
                ('QD', 'Bool', 0.125),
                ('PV', 'UDInt', 4),
                ('CV', 'UDInt', 4),
            ]
        },
        # ── S7-1500 SFB 定时器类型 (4 字节隐藏前缀 + 1 个隐藏 Bool) ──
        'TON_TIME': {
            'size': 16,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'Time', 4),
                ('ET', 'Time', 4),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TOF_TIME': {
            'size': 16,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'Time', 4),
                ('ET', 'Time', 4),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TP_TIME': {
            'size': 16,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'Time', 4),
                ('ET', 'Time', 4),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TONR_TIME': {
            'size': 16,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'Time', 4),
                ('ET', 'Time', 4),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TON_LTIME': {
            'size': 24,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'LTime', 8),
                ('ET', 'LTime', 8),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TOF_LTIME': {
            'size': 24,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'LTime', 8),
                ('ET', 'LTime', 8),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TP_LTIME': {
            'size': 24,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'LTime', 8),
                ('ET', 'LTime', 8),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
        'TONR_LTIME': {
            'size': 24,
            'fields': [
                (None, 'DWord', 4),
                ('PT', 'LTime', 8),
                ('ET', 'LTime', 8),
                (None, 'Bool', 0.125),
                ('IN', 'Bool', 0.125),
                ('Q', 'Bool', 0.125),
            ]
        },
    }
    
    def _clean_type_name(self, datatype: str) -> str:
        """清理数据类型名称中的引号和实体"""
        dt = datatype.strip('"').strip("'")
        dt = dt.replace('&quot;', '').strip()
        return dt
    
    def _get_type_size(self, datatype: str) -> float:
        """获取数据类型的字节大小"""
        dt = self._clean_type_name(datatype)
        
        # 基础类型
        if dt in self.S7_TYPE_SIZES:
            return self.S7_TYPE_SIZES[dt]
        
        # IEC 系统结构体
        if dt in self.IEC_SYSTEM_TYPES:
            return self.IEC_SYSTEM_TYPES[dt]['size']
        
        # String[n] → 2 + n (最大长度字节 + 当前长度字节 + n字符)
        m = re.match(r'String\[(\d+)\]', dt, re.IGNORECASE)
        if m:
            return 2 + int(m.group(1))
        if dt.lower() == 'string':
            return 256  # 默认 String = String[254] → 256 bytes
        
        # WString[n] → 4 + 2*n
        m = re.match(r'WString\[(\d+)\]', dt, re.IGNORECASE)
        if m:
            return 4 + 2 * int(m.group(1))
        
        # Array[lo..hi] of Type
        m = re.match(r'Array\[(-?\d+)\.\.("?[^"\]]+"?)\]\s+of\s+(.+)', dt, re.IGNORECASE)
        if m:
            lo_str, hi_str, element_type = m.group(1), m.group(2), m.group(3).strip()
            try:
                lo = int(lo_str)
                hi = int(hi_str)
            except ValueError:
                # 上界是常量名（如 "CamProfileNum"），无法计算精确大小
                return 0
            count = hi - lo + 1
            elem_size = self._get_type_size(element_type)
            if elem_size == 0.125:  # Array of Bool
                # Bool数组按字节对齐，每8个Bool占1字节
                return (count + 7) // 8
            if elem_size > 0:
                # 对象类型数组，每个元素需要对齐到偶字节
                if elem_size > 1 and elem_size % 2 != 0:
                    aligned_elem_size = elem_size + 1
                else:
                    aligned_elem_size = elem_size
                return count * aligned_elem_size
            return 0  # 元素大小未知
        
        # 已缓存的 UDT
        if dt in self._udt_cache:
            return self._udt_cache[dt]['size']
        
        # 尝试解析 UDT
        udt_info = self._resolve_udt(dt)
        if udt_info:
            return udt_info['size']
        
        # UDT或FB实例 - 返回0，需要递归计算
        return 0
    
    # ============================================================
    # UDT 解析器和缓存
    # ============================================================
    
    # UDT 导出文件可能存放的目录名（TIA 导出 zip 常见结构：blocks/{DB,GRAPH,DataType,...}）
    _UDT_DIR_NAMES = {'udt', 'datatype', 'datatypes', 'userdatatype'}

    def _init_udt_search_dirs(self, file_path: str):
        """从文件路径向上建立 UDT 搜索目录列表：
        - 各级祖先目录下名为 udt/DataType/DataTypes/UserDataType 的子目录
        - 当前文件所在目录的兄弟目录中同名者（覆盖 blocks/DB 与 blocks/DataType 平级布局）
        """
        self._current_file_path = file_path
        self._udt_search_dirs = []

        def _add_dir(d: Path):
            s = str(d)
            if d.is_dir() and s not in self._udt_search_dirs:
                self._udt_search_dirs.append(s)

        current = Path(file_path).parent
        for _ in range(6):  # 最多向上6级
            for sub in current.iterdir():
                if sub.is_dir() and sub.name.lower() in self._UDT_DIR_NAMES:
                    _add_dir(sub)
            current = current.parent
            if current == current.parent:  # 到达根目录
                break
    
    # udt_index.json 缓存：{udt_dir: {类型名: 相对路径} 或 None}
    _udt_index_cache = None

    def _udt_index_lookup(self, udt_dir: str, clean_name: str) -> Optional[str]:
        """按导出端生成的 udt_index.json 查找 UDT 文件。

        导出端必须把文件名里的非法字符清洗掉（A/B -> A_B.xml），
        而这里是按**类型名**查找的，两者对不上就会让整个 UDT 解析不出来。
        索引把类型名映射回实际相对路径，解决这个错位。

        没有索引文件、索引损坏、或索引里没有该类型时一律返回 None，
        调用方继续走原有的按文件名匹配逻辑 —— 旧导出包行为完全不变。
        """
        if self._udt_index_cache is None:
            self._udt_index_cache = {}

        if udt_dir not in self._udt_index_cache:
            index_path = os.path.join(udt_dir, 'udt_index.json')
            mapping = None
            if os.path.isfile(index_path):
                try:
                    # utf-8-sig：导出端不写 BOM（避免 Python 'utf-8' 编解码器把 BOM 当成
                    # 数据首字节导致 json.load 抛 JSONDecodeError），但用 utf-8-sig 打开
                    # 能兼容"万一将来又带上了 BOM"的情况——有 BOM 会被剥掉，没有也能正常读。
                    with open(index_path, 'r', encoding='utf-8-sig') as f:
                        data = json.load(f)
                    if isinstance(data, dict):
                        types = data.get('types')
                        if isinstance(types, dict):
                            mapping = types
                except (OSError, ValueError):
                    # 索引文件读不了或不是合法 JSON：视为无索引，走原有匹配逻辑
                    mapping = None
            self._udt_index_cache[udt_dir] = mapping

        mapping = self._udt_index_cache[udt_dir]
        if not mapping:
            return None

        relative = mapping.get(clean_name)
        if not relative:
            return None

        full = os.path.join(udt_dir, *str(relative).replace('\\', '/').split('/'))
        return full if os.path.isfile(full) else None

    def _resolve_udt(self, type_name: str) -> Optional[Dict]:
        """解析UDT类型，返回 {'size': int, 'fields': list} 或 None

        fields 中每项: (name, datatype, size, sub_fields)
        sub_fields 仅在UDT/IEC/Struct时有值
        """
        clean_name = self._clean_type_name(type_name)
        
        # 已缓存
        if clean_name in self._udt_cache:
            return self._udt_cache[clean_name]
        
        # IEC 系统类型直接返回
        if clean_name in self.IEC_SYSTEM_TYPES:
            iec = self.IEC_SYSTEM_TYPES[clean_name]
            result = {'size': iec['size'], 'fields': [(n, t, s, None) for n, t, s in iec['fields']]}
            self._udt_cache[clean_name] = result
            return result
        
        # 在搜索目录中查找 UDT 文件：兼容多种命名与扩展
        # 1) 精确名：{类型名}.xml / {类型名}.ml
        # 2) 工程后缀名：{类型名}_{工程...}.xml/.ml（TIA 导出批量命名，如 UDT_General_WJRD2024001_...ml）
        #    前缀必须紧跟下划线，避免 UDT_General 误配 UDT_General2
        for udt_dir in self._udt_search_dirs:
            candidates = []
            indexed = self._udt_index_lookup(udt_dir, clean_name)
            if indexed:
                candidates.append(indexed)
            candidates.append(os.path.join(udt_dir, f"{clean_name}.xml"))
            candidates.append(os.path.join(udt_dir, f"{clean_name}.ml"))
            try:
                for fn in os.listdir(udt_dir):
                    low = fn.lower()
                    if low.endswith(('.xml', '.ml')) and low.startswith(clean_name.lower() + '_'):
                        candidates.append(os.path.join(udt_dir, fn))
            except OSError:
                pass
            for udt_path in candidates:
                if os.path.isfile(udt_path):
                    udt_info = self._parse_udt_xml(udt_path, clean_name)
                    if udt_info:
                        self._udt_cache[clean_name] = udt_info
                        return udt_info
        
        return None
    
    def _parse_udt_xml(self, xml_path: str, udt_name: str) -> Optional[Dict]:
        """解析 UDT XML 文件，提取字段列表和总大小"""
        try:
            encoding = self.detect_encoding(xml_path)
            with open(xml_path, 'r', encoding=encoding, errors='ignore') as f:
                content = f.read()
            
            # 移除命名空间
            content = re.sub(r'\s+xmlns[^=]*="[^"]*"', '', content)
            content = re.sub(r'<(\/?)[a-zA-Z0-9_]+:', r'<\1', content)
            
            root = ET.fromstring(content)
            
            # 查找 Section Name="None" 下的 Member 列表
            members = []
            for section in root.iter('Section'):
                if section.get('Name') == 'None':
                    members = section.findall('./Member')
                    break
            
            if not members:
                return None
            
            # 防止递归循环
            self._udt_cache[udt_name] = {'size': 0, 'fields': []}  # 占位
            
            fields = []
            byte_offset = 0
            bit_offset = 0
            has_unresolved = False
            
            for member in members:
                name = member.get('Name', '')
                datatype = member.get('Datatype', '')
                if not name:
                    continue
                
                type_size = self._get_type_size(datatype)
                has_children = len(member.findall('./Sections/Section/Member')) > 0
                
                # 处理内联子成员（UDT XML中可能有展开的子字段）
                if has_children and type_size == 0:
                    byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
                    struct_start = byte_offset
                    sub_fields = self._parse_udt_inline_members(member)
                    if self._fields_have_unresolved(sub_fields):
                        has_unresolved = True
                    struct_size = self._calculate_fields_size(sub_fields)
                    if struct_size % 2 != 0:
                        struct_size += 1
                    fields.append((name, datatype, struct_size, sub_fields))
                    byte_offset = struct_start + struct_size
                    bit_offset = 0
                else:
                    # 尝试解析为IEC/UDT
                    clean_dt = self._clean_type_name(datatype)
                    sub_fields = None
                    
                    if type_size == 0 and not has_children:
                        resolved = self._resolve_udt(clean_dt)
                        if resolved:
                            type_size = resolved['size']
                            sub_fields = resolved['fields']
                            if resolved.get('unresolved'):
                                has_unresolved = True
                        else:
                            has_unresolved = True
                    
                    byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, type_size)
                    fields.append((name, datatype, type_size, sub_fields))
                    byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, type_size)
            
            # 结构体总大小对齐到偶字节
            if bit_offset > 0:
                byte_offset += 1
                bit_offset = 0
            if byte_offset % 2 != 0:
                byte_offset += 1
            
            result = {'size': byte_offset, 'fields': fields, 'unresolved': has_unresolved}
            self._udt_cache[udt_name] = result
            return result
            
        except Exception as e:
            if os.environ.get('PLC_DEBUG'):
                print(f"DEBUG: UDT parse error for {xml_path}: {e}", file=sys.stderr)
            return None
    
    def _fields_have_unresolved(self, fields: list) -> bool:
        """判断模板字段列表是否含未解析（尺寸未知）成员——递归检查子字段"""
        for _name, dtype, size, sub in fields:
            if sub:
                if self._fields_have_unresolved(sub):
                    return True
            elif size is not None and size <= 0:
                # Bool=0.125>0；零尺寸SFB/未知类型尺寸为 0 → 无法验证
                return True
        return False
    
    def _parse_udt_inline_members(self, parent_member: ET.Element) -> list:
        """解析UDT XML中内联展开的子字段，返回 [(name, datatype, size, sub_fields), ...]"""
        fields = []
        for section in parent_member.findall('./Sections/Section'):
            for member in section.findall('./Member'):
                name = member.get('Name', '')
                datatype = member.get('Datatype', '')
                if not name:
                    continue
                
                type_size = self._get_type_size(datatype)
                has_children = len(member.findall('./Sections/Section/Member')) > 0
                
                if has_children and type_size == 0:
                    sub_fields = self._parse_udt_inline_members(member)
                    struct_size = self._calculate_fields_size(sub_fields)
                    if struct_size % 2 != 0:
                        struct_size += 1
                    fields.append((name, datatype, struct_size, sub_fields))
                else:
                    clean_dt = self._clean_type_name(datatype)
                    sub_fields = None
                    if type_size == 0 and not has_children:
                        resolved = self._resolve_udt(clean_dt)
                        if resolved:
                            if resolved.get('unresolved'):
                                # 模板内部未解析 → 尺寸不可信，按未解析处理
                                type_size = 0
                            else:
                                type_size = resolved['size']
                                sub_fields = resolved['fields']
                    fields.append((name, datatype, type_size, sub_fields))
        return fields
    
    def _calculate_fields_size(self, fields: list) -> int:
        """计算字段列表的总大小（带S7对齐规则）"""
        byte_offset = 0
        bit_offset = 0
        for name, datatype, type_size, sub_fields in fields:
            byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, type_size if type_size > 0 else 0)
            if type_size > 0:
                byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, type_size)
            elif sub_fields:
                # 内嵌结构体
                inner = self._calculate_fields_size(sub_fields)
                if inner % 2 != 0:
                    inner += 1
                byte_offset += inner
        if bit_offset > 0:
            byte_offset += 1
        return byte_offset
    
    def _align_offset(self, byte_offset: int, bit_offset: int, type_size: float) -> tuple:
        """S7对齐规则 - 返回 (aligned_byte, aligned_bit)
        
        规则:
        - Bool: 不需要对齐，紧接在前一个Bool后面
        - Byte/USInt/SInt/Char: 对齐到字节边界
        - >1字节的类型(Int/Word等): 对齐到偶字节（2字节）边界
        - Struct/UDT: 对齐到偶字节边界
        """
        if type_size == 0.125:  # Bool
            return byte_offset, bit_offset
        
        # 非Bool类型，先对齐到字节边界
        if bit_offset > 0:
            byte_offset += 1
            bit_offset = 0
        
        # 复杂类型 (Array, Struct, UDT, type_size==0) 或 Word 类型 (>= 2字节)
        # 在西门子标准 DB 中必须对齐到偶数字节
        if type_size == 0 or type_size >= 2.0:
            if byte_offset % 2 != 0:
                byte_offset += 1
        
        return byte_offset, bit_offset
    
    def _advance_offset(self, byte_offset: int, bit_offset: int, type_size: float) -> tuple:
        """前进偏移量 - 返回 (new_byte, new_bit)"""
        if type_size == 0.125:  # Bool
            bit_offset += 1
            if bit_offset >= 8:
                byte_offset += 1
                bit_offset = 0
        else:
            byte_offset += int(type_size)
        
        return byte_offset, bit_offset
    
    def _extract_variables(self, block_elem: ET.Element, vars_config: Dict, block_name: str) -> List[VariableInfo]:
        """提取变量信息 - 增强版：支持IEC系统类型、UDT解析、数组展开"""
        variables = []
        preserve_offsets = self._should_preserve_offsets(block_elem)
        # 重置本块的偏移污染跟踪状态（块间独立）
        self._taint_vars = variables
        self._taint_start = None
        self._taint_reason = ""
        scope_config = vars_config.get('scope_detection', {})
        scope_mapping = scope_config.get('mapping', {})
        nested_config = vars_config.get('nested', {})
        
        # 构建父子关系映射，用于判断Section是否为嵌套Section
        parent_map = {child: parent for parent in block_elem.iter() for child in parent}
        
        # 查找Interface元素
        interface_elem = block_elem.find('.//Interface')
        if interface_elem is None:
            interface_elem = block_elem
        
        # 1. 尝试从当前元素寻找 MemoryLayout
        memory_layout_elem = block_elem.find('.//MemoryLayout')
        memory_layout = memory_layout_elem.text if memory_layout_elem is not None else "Optimized"
        
        # 找到 Interface 下的 Sections 根元素 (只查找顶层，避免重复解析嵌套 Member 内的 Section)
        sections_roots = interface_elem.findall('./Sections')
        if not sections_roots:
             # 有些 XML 结构可能略有不同，尝试直接查找 Section
             sections_roots = [interface_elem] if interface_elem.find('./Section') is not None else interface_elem.findall('.//Sections')[:1]
        
        # 获取 Interface 下的所有顶级 Section
        top_sections = []
        for sections_root in sections_roots:
            # 即使 parent 是 Member 也要提取 Section，用于处理 Instance DB
            for section in sections_root.findall('./Section'):
                section_name = section.get('Name', '')
                if section_name == 'None' and parent_map.get(sections_root).tag != 'Member':
                    continue
                top_sections.append(section)
        
        # 将 memory_layout 记录进类属性以便后续使用（如果需要）
        self.current_memory_layout = memory_layout
        
        # 方案 B（2026-08-26）：优化访问块不保存偏移。
        # 优化块在 PLC 内部按符号管理，DBX 绝对地址空间中不存在该块，推算的名义偏移
        # 无物理意义——保存只会诱导采集注册拼出错误地址（绝对式读优化块可能静默读回
        # 别的数据）。延续"只保存准确的"哲学：优化块偏移一律 byte_offset=-1（C# 落 NULL）。
        # XML 缺失 MemoryLayout 元素时按默认 Optimized 处理（宁可漏存不可误存）。
        if preserve_offsets and str(memory_layout).strip().lower() == 'optimized':
            preserve_offsets = False
            print(f"[OFFSET-LAYOUT] {block_name}: MemoryLayout=Optimized，不保存偏移（优化块无 DBX 地址）",
                  file=sys.stderr)
        
        # 处理每个顶层 Section
        # DB 类块中 Input→Output→InOut→Static 共享连续地址空间
        shared_byte_offset = 0
        shared_bit_offset = 0

        for section in top_sections:
            section_name = section.get('Name', '')
            scope = scope_mapping.get(section_name, section_name)
            is_inout = section_name == 'InOut'

            if preserve_offsets:
                byte_offset = shared_byte_offset
                bit_offset = shared_bit_offset
            else:
                byte_offset = 0
                bit_offset = 0
            
            for member in section.findall('./Member'):
                name = member.get('Name', '')
                if not name:
                    continue
                
                byte_offset, bit_offset = self._process_member_variable(
                    member, name, name, scope, variables, nested_config,
                    byte_offset, bit_offset, 0, is_inout=is_inout
                )

            if preserve_offsets:
                shared_byte_offset = byte_offset
                shared_bit_offset = bit_offset

        if not preserve_offsets:
            self._clear_variable_offsets(variables)
        elif self._taint_start is not None and self._taint_vars:
            # 本块存在无法解析尺寸的成员：从首个可疑成员（含其容器）起，
            # 后续所有偏移一律不保存（byte_offset=-1 → C# 侧落 NULL，
            # 采集注册 IsNullOrEmpty(Offset) 过滤自动排除，防止错位地址被误用）
            cleared = variables[self._taint_start:]
            for v in cleared:
                v.offset = ""
                v.byte_offset = -1
                v.bit_offset = 0
            print(f"[OFFSET-TAINT] {block_name}: {self._taint_reason}；"
                  f"自 '{cleared[0].path if cleared else '?'}' 起偏移不可信，"
                  f"清空 {len(cleared)}/{len(variables)} 行偏移", file=sys.stderr)
        
        return variables

    def _mark_taint(self, reason: str):
        """标记当前块偏移不可信：从最近一条已记录变量（含）起不再保存偏移。
        尺寸未解析的成员无法推进偏移，其自身及后续所有成员的偏移都不可信。"""
        if self._taint_start is None and self._taint_vars:
            self._taint_start = max(len(self._taint_vars) - 1, 0)
            self._taint_reason = reason

    def _taint_pullback(self, container_index: int):
        """子树内出现未解析成员时，把污染起点回拉到容器变量自身（容器尺寸同样不可信）"""
        if (self._taint_start is not None and container_index is not None
                and self._taint_start > container_index):
            self._taint_start = container_index

    def _should_preserve_offsets(self, block_elem: ET.Element) -> bool:
        """仅 DB 类块保留偏移，其他块的偏移对在线直读没有意义。"""
        return block_elem.tag in {'SW.Blocks.DB', 'SW.Blocks.GlobalDB', 'SW.Blocks.InstanceDB'}

    def _clear_variable_offsets(self, variables: List[VariableInfo]):
        for variable in variables:
            variable.offset = ""
            variable.byte_offset = -1
            variable.bit_offset = 0
    
    def _is_inout_pointer_type(self, datatype: str) -> bool:
        """判断数据类型在 InOut 区是否按 6 字节指针处理（复杂类型）"""
        dt = self._clean_type_name(datatype)
        # 零尺寸类型在 InOut 也不占空间
        if dt in self._ZERO_SIZE_TYPES:
            return False
        if dt in self._INOUT_SIMPLE_TYPES:
            return False
        # String/WString → 复杂类型
        if dt.lower().startswith('string') or dt.lower().startswith('wstring'):
            return True
        # Array → 复杂类型
        if dt.lower().startswith('array'):
            return True
        # Struct/UDT/FB实例等 → 复杂类型
        return True

    def _process_member_variable(self, member: ET.Element, name: str, path: str, 
                                  scope: str, variables: List[VariableInfo], 
                                  nested_config: Dict, byte_offset: int, bit_offset: int,
                                  depth: int, is_inout: bool = False) -> tuple:
        """处理单个 Member 变量，返回 (new_byte_offset, new_bit_offset)
        
        统一处理：普通类型、数组、IEC系统类型、UDT引用、嵌套结构体
        is_inout: 当前处于 InOut 区，复杂类型按 6 字节指针处理
        """
        max_depth = nested_config.get('max_depth', 20)
        if depth > max_depth:
            return byte_offset, bit_offset
        
        path_sep = nested_config.get('path_separator', '.')
        datatype = member.get('Datatype', '')
        comment = self._get_text(member.find('.//Comment//MultiLanguageText')) or ""

        # ============================================================
        # InOut 复杂类型 → 6 字节指针，不展开内部结构
        # ============================================================
        if is_inout and self._is_inout_pointer_type(datatype):
            ptr_size = self.INOUT_POINTER_SIZE
            byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, ptr_size)
            var = VariableInfo(
                name=name, path=path, datatype=datatype, scope=scope,
                comment=comment,
                initial_value="",
                remanence=member.get('Remanence', 'NonRetain'),
                offset=f"{byte_offset}.{bit_offset}",
                byte_offset=byte_offset, bit_offset=bit_offset,
                size_in_bytes=ptr_size, is_structure=True,
                memory_layout=getattr(self, 'current_memory_layout', "")
            )
            variables.append(var)
            byte_offset += ptr_size
            return byte_offset, 0

        type_size = self._get_type_size(datatype)
        # 兼容普通 Struct (直接 .Member) 和 Instance DB (./Sections/Section/Member)
        inline_members = member.findall('./Member')
        if not inline_members:
            inline_members = member.findall('./Sections/Section/Member')
        has_children = len(inline_members) > 0

        
        # ============================================================
        # 情况1: 数组类型 - 需要展开每个下标
        # ============================================================
        clean_dt = self._clean_type_name(datatype)
        arr_match = re.match(r'Array\[(-?\d+)\.\.("?[^"\]]+"?)\]\s+of\s+(.+)', clean_dt, re.IGNORECASE)
        if arr_match:
            return self._expand_array_member(
                member, name, path, scope, variables, nested_config,
                byte_offset, bit_offset, depth, comment, datatype, arr_match
            )
        
        # ============================================================
        # 情况1.5: 已知 IEC/SFB 系统类型 — 优先使用预定义布局
        # 解决 TON_TIME 等 SFB 类型含隐藏前缀字段的问题
        # ============================================================
        if clean_dt in self.IEC_SYSTEM_TYPES:
            iec_info = self.IEC_SYSTEM_TYPES[clean_dt]
            if iec_info.get('fields'):
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
                struct_start_byte = byte_offset
                var = VariableInfo(
                    name=name, path=path, datatype=datatype, scope=scope,
                    comment=comment,
                    initial_value=self._get_text(member.find('./StartValue')) or "",
                    remanence=member.get('Remanence', 'NonRetain'),
                    offset=f"{byte_offset}.{bit_offset}",
                    byte_offset=byte_offset, bit_offset=bit_offset,
                    is_structure=True, size_in_bytes=iec_info['size'],
                    memory_layout=getattr(self, 'current_memory_layout', "")
                )
                variables.append(var)
                resolved_fields = [(n, t, s, None) for n, t, s in iec_info['fields']]
                byte_offset = self._expand_resolved_fields(
                    resolved_fields, path, scope, variables, nested_config,
                    byte_offset, bit_offset, depth + 1, path_sep
                )
                expected_end = struct_start_byte + iec_info['size']
                byte_offset = max(byte_offset, expected_end)
                if byte_offset % 2 != 0:
                    byte_offset += 1
                bit_offset = 0
                var.size_in_bytes = byte_offset - struct_start_byte
                return byte_offset, bit_offset

        # ============================================================
        # 情况1.6: 零尺寸类型 (RDREC/WRREC 等通信 SFB)
        # 不对齐、不推进偏移，仅记录当前位置
        # ============================================================
        if clean_dt in self._ZERO_SIZE_TYPES:
            var = VariableInfo(
                name=name, path=path, datatype=datatype, scope=scope,
                comment=comment,
                initial_value=self._get_text(member.find('./StartValue')) or "",
                remanence=member.get('Remanence', 'NonRetain'),
                offset=f"{byte_offset}.{bit_offset}",
                byte_offset=byte_offset, bit_offset=bit_offset,
                size_in_bytes=0, is_structure=False,
                memory_layout=getattr(self, 'current_memory_layout', "")
            )
            variables.append(var)
            # 零尺寸 SFB 假设无法验证（S7-1500 中 RDREC/WRREC 多实例实际占字节），
            # 从该成员起后续偏移不可信 → 不保存
            self._mark_taint(f"零尺寸SFB假设无法验证 {clean_dt}")
            return byte_offset, bit_offset  # 偏移不变

        # ============================================================
        # 情况2: 有内联子成员（XML中已展开的Struct/FB子字段）
        # ============================================================
        if has_children and type_size == 0:
            # 结构体起始必须对齐到偶数字节
            byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
            struct_start_byte = byte_offset
            
            var = VariableInfo(
                name=name, path=path, datatype=datatype, scope=scope,
                comment=comment,
                initial_value=self._get_text(member.find('./StartValue')) or "",
                remanence=member.get('Remanence', 'NonRetain'),
                offset=f"{byte_offset}.{bit_offset}",
                byte_offset=byte_offset, bit_offset=bit_offset,
                is_structure=True,
                memory_layout=getattr(self, 'current_memory_layout', "")
            )
            variables.append(var)
            struct_idx = len(variables) - 1
            
            new_byte, new_bit = self._extract_nested_members_v2(
                member, path, scope, variables, nested_config,
                byte_offset, bit_offset, depth + 1
            )
            
            # 结构体占用的空间必须补齐到偶数字节（2字节对齐）
            if new_bit > 0:
                new_byte += 1
            
            if new_byte % 2 != 0:
                new_byte += 1
                
            byte_offset = new_byte
            bit_offset = 0
            self._taint_pullback(struct_idx)
            var.size_in_bytes = byte_offset - struct_start_byte
            return byte_offset, bit_offset
        
        # ============================================================
        # 情况3: 无内联子成员但type_size==0，尝试IEC/UDT解析
        # ============================================================
        if type_size == 0 and not has_children:
            resolved = self._resolve_udt(clean_dt)
            if resolved and resolved['fields']:
                # 作为结构体展开
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
                struct_start_byte = byte_offset
                
                var = VariableInfo(
                    name=name, path=path, datatype=datatype, scope=scope,
                    comment=comment,
                    initial_value=self._get_text(member.find('./StartValue')) or "",
                    remanence=member.get('Remanence', 'NonRetain'),
                    offset=f"{byte_offset}.{bit_offset}",
                    byte_offset=byte_offset, bit_offset=bit_offset,
                    is_structure=True, size_in_bytes=resolved['size'],
                )
                variables.append(var)
                if resolved.get('unresolved'):
                    # UDT 模板内部含未解析成员 → 模板尺寸不可信
                    self._mark_taint(f"UDT模板含未解析成员 {clean_dt}")
                udt_idx = len(variables) - 1
                
                byte_offset = self._expand_resolved_fields(
                    resolved['fields'], path, scope, variables, nested_config,
                    byte_offset, bit_offset, depth + 1, path_sep
                )
                
                # 结构体结束对齐
                expected_end = struct_start_byte + resolved['size']
                byte_offset = max(byte_offset, expected_end)
                if byte_offset % 2 != 0:
                    byte_offset += 1
                bit_offset = 0
                self._taint_pullback(udt_idx)
                var.size_in_bytes = byte_offset - struct_start_byte
                return byte_offset, bit_offset
            elif resolved:
                # 有大小但无字段明细（不应该发生，但作为兜底）
                type_size = resolved['size']
        
        # ============================================================
        # 情况4: 普通叶子变量
        # ============================================================
        byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, type_size)
        
        var = VariableInfo(
            name=name, path=path, datatype=datatype, scope=scope,
            comment=comment,
            initial_value=self._get_text(member.find('./StartValue')) or "",
            remanence=member.get('Remanence', 'NonRetain'),
            offset=f"{byte_offset}.{bit_offset}",
            byte_offset=byte_offset, bit_offset=bit_offset,
            size_in_bytes=type_size, is_structure=False,
        )
        variables.append(var)
        if type_size == 0:
            # 未知类型 / 未解析UDT / 变长数组(Array[*])：尺寸未知，偏移无法推进
            self._mark_taint(f"类型未解析 {clean_dt or datatype}")
        elif self._udt_cache.get(clean_dt, {}).get('unresolved'):
            # 尺寸来自含未解析成员的 UDT 模板缓存 → 尺寸不可信
            self._mark_taint(f"UDT模板含未解析成员 {clean_dt}")
        if type_size == 0:
            # 未知类型 / 未解析UDT / 变长数组(Array[*])：尺寸未知，偏移无法推进
            self._mark_taint(f"类型未解析 {clean_dt or datatype}")
        
        byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, type_size)
        return byte_offset, bit_offset
    
    def _expand_array_member(self, member: ET.Element, name: str, path: str,
                              scope: str, variables: List[VariableInfo],
                              nested_config: Dict, byte_offset: int, bit_offset: int,
                              depth: int, comment: str, datatype: str,
                              arr_match) -> tuple:
        """展开数组类型成员，为每个下标创建变量记录"""
        path_sep = nested_config.get('path_separator', '.')
        lo_str, hi_str = arr_match.group(1), arr_match.group(2)
        element_type = arr_match.group(3).strip()
        
        try:
            lo = int(lo_str)
            hi = int(self._clean_type_name(hi_str))
        except ValueError:
            # 上界是常量名（如 "CamProfileNum"），无法展开
            byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
            var = VariableInfo(
                name=name, path=path, datatype=datatype, scope=scope,
                comment=comment, offset=f"{byte_offset}.{bit_offset}",
                byte_offset=byte_offset, bit_offset=bit_offset,
                is_structure=True,
            )
            variables.append(var)
            self._mark_taint(f"数组上界非数字无法解析 {datatype}")
            return byte_offset, bit_offset
        
        count = hi - lo + 1
        clean_elem_type = self._clean_type_name(element_type)
        elem_size = self._get_type_size(element_type)
        
        # Bool 数组特殊处理：按bit紧凑排列
        if elem_size == 0.125:
            total_bytes = (count + 7) // 8
            # Bool 数组起始对齐到字境界（偶数地址）
            byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
            array_start = byte_offset
            
            # 添加数组容器节点
            var = VariableInfo(
                name=name, path=path, datatype=datatype, scope=scope,
                comment=comment, offset=f"{byte_offset}.{bit_offset}",
                byte_offset=byte_offset, bit_offset=bit_offset,
                size_in_bytes=total_bytes, is_structure=True,
            )
            variables.append(var)
            
            arr_byte = byte_offset
            arr_bit = 0
            for i in range(lo, hi + 1):
                idx_path = f"{path}.{name}[{i}]"
                variables.append(VariableInfo(
                    name=f"{name}[{i}]", path=idx_path, datatype='Bool', scope=scope,
                    offset=f"{arr_byte}.{arr_bit}",
                    byte_offset=arr_byte, bit_offset=arr_bit,
                    size_in_bytes=0.125, is_structure=False,
                ))
                arr_bit += 1
                if arr_bit >= 8:
                    arr_byte += 1
                    arr_bit = 0
            
            # 数组作为一个整体对象，结束也必须对齐到偶数字节
            byte_offset = array_start + total_bytes
            if byte_offset % 2 != 0:
                byte_offset += 1
            bit_offset = 0
            var.size_in_bytes = byte_offset - array_start
            return byte_offset, bit_offset
        
        # 确定元素是否为结构体类型（IEC/UDT/Struct）
        resolved = None
        inline_members = member.findall('./Member')
        if not inline_members:
            inline_members = member.findall('./Sections/Section/Member')
        has_inline_children = len(inline_members) > 0
        
        if elem_size == 0 or has_inline_children:
            resolved = self._resolve_udt(clean_elem_type)
            if resolved:
                elem_size = resolved['size']
        
        is_struct_array = has_inline_children or (resolved and resolved.get('fields'))
        
        # 数组整体对齐
        # 在西门子标准 DB 中，数组作为一个复杂的聚合类型，始终由新字开始(2字节对齐)
        byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
        
        array_start = byte_offset
        
        # 计算数组总大小
        if is_struct_array and elem_size > 0:
            aligned_elem = elem_size if elem_size % 2 == 0 else elem_size + 1
            total_array_size = count * aligned_elem
        elif elem_size > 0:
            total_array_size = count * elem_size
        else:
            total_array_size = 0
        
        # 添加数组容器节点
        var = VariableInfo(
            name=name, path=path, datatype=datatype, scope=scope,
            comment=comment, offset=f"{byte_offset}.{bit_offset}",
            byte_offset=byte_offset, bit_offset=bit_offset,
            size_in_bytes=total_array_size, is_structure=True,
        )
        variables.append(var)
        # 元素模板含未解析成员 / 元素类型未解析 → 数组尺寸不可信
        elem_cache = self._udt_cache.get(clean_elem_type)
        if (resolved is not None and resolved.get('unresolved')) or \
                (elem_cache is not None and elem_cache.get('unresolved')):
            self._mark_taint(f"数组元素UDT模板含未解析成员 {clean_elem_type}")
        elif elem_size == 0 and not is_struct_array:
            self._mark_taint(f"数组元素类型未解析 {clean_elem_type}")
        array_idx = len(variables) - 1
        
        # 展开每个数组元素
        for i in range(lo, hi + 1):
            idx_path = f"{path}.{name}[{i}]"
            
            if is_struct_array:
                # 每个元素对齐到偶字节
                if byte_offset % 2 != 0:
                    byte_offset += 1
                bit_offset = 0
                elem_start = byte_offset
                
                # 添加元素容器
                container_var = VariableInfo(
                    name=f"{name}[{i}]", path=idx_path, datatype=clean_elem_type, scope=scope,
                    offset=f"{byte_offset}.0",
                    byte_offset=byte_offset, bit_offset=0,
                    size_in_bytes=elem_size, is_structure=True,
                )
                variables.append(container_var)
                
                # 展开子字段
                if has_inline_children:
                    new_byte, new_bit = self._extract_nested_members_v2(
                        member, idx_path, scope, variables, nested_config,
                        byte_offset, 0, depth + 1
                    )
                    struct_size = new_byte - elem_start
                    if new_bit > 0:
                        struct_size += 1
                    byte_offset = elem_start + max(struct_size, elem_size)
                    container_var.size_in_bytes = max(struct_size, elem_size)
                elif resolved and resolved['fields']:
                    byte_offset = self._expand_resolved_fields(
                        resolved['fields'], idx_path, scope, variables, nested_config,
                        byte_offset, 0, depth + 1, path_sep
                    )
                    struct_size = byte_offset - elem_start
                    byte_offset = max(byte_offset, elem_start + elem_size)
                    container_var.size_in_bytes = max(struct_size, elem_size)
                else:
                    byte_offset += elem_size
                
                # 对齐到偶字节
                if byte_offset % 2 != 0:
                    byte_offset += 1
                bit_offset = 0
            else:
                # 普通类型数组
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, elem_size)
                variables.append(VariableInfo(
                    name=f"{name}[{i}]", path=idx_path, datatype=clean_elem_type, scope=scope,
                    offset=f"{byte_offset}.{bit_offset}",
                    byte_offset=byte_offset, bit_offset=bit_offset,
                    size_in_bytes=elem_size, is_structure=False,
                ))
                byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, elem_size)
        
        self._taint_pullback(array_idx)
        return byte_offset, bit_offset
    
    def _expand_resolved_fields(self, fields: list, parent_path: str, scope: str,
                                 variables: List[VariableInfo], nested_config: Dict,
                                 byte_offset: int, bit_offset: int, depth: int,
                                 path_sep: str = '.') -> int:
        """展开已解析的字段列表（来自IEC系统类型或UDT），返回结束后的byte_offset"""
        max_depth = nested_config.get('max_depth', 20)
        if depth > max_depth:
            return byte_offset
        
        for field_name, field_type, field_size, sub_fields in fields:
            # 隐藏字段（field_name 为 None）：仅推进偏移，不创建 VariableInfo
            if field_name is None:
                effective_size = field_size if field_size > 0 else self._get_type_size(field_type)
                if effective_size <= 0:
                    self._mark_taint(f"IEC/UDT隐藏字段尺寸未知 {field_type}")
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, effective_size)
                byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, effective_size)
                continue

            full_path = f"{parent_path}{path_sep}{field_name}"
            
            if sub_fields:
                # 嵌套结构体
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, 0)
                struct_start = byte_offset
                
                variables.append(VariableInfo(
                    name=field_name, path=full_path, datatype=field_type, scope=scope,
                    offset=f"{byte_offset}.{bit_offset}",
                    byte_offset=byte_offset, bit_offset=bit_offset,
                    size_in_bytes=field_size, is_structure=True,
                ))
                field_idx = len(variables) - 1
                
                byte_offset = self._expand_resolved_fields(
                    sub_fields, full_path, scope, variables, nested_config,
                    byte_offset, bit_offset, depth + 1, path_sep
                )
                
                byte_offset = max(byte_offset, struct_start + field_size)
                if byte_offset % 2 != 0:
                    byte_offset += 1
                bit_offset = 0
                self._taint_pullback(field_idx)
            else:
                # 叶子字段
                effective_size = field_size if field_size > 0 else self._get_type_size(field_type)
                byte_offset, bit_offset = self._align_offset(byte_offset, bit_offset, effective_size)
                
                variables.append(VariableInfo(
                    name=field_name, path=full_path, datatype=field_type, scope=scope,
                    offset=f"{byte_offset}.{bit_offset}",
                    byte_offset=byte_offset, bit_offset=bit_offset,
                    size_in_bytes=effective_size, is_structure=False,
                ))
                if effective_size <= 0:
                    self._mark_taint(f"UDT/IEC字段类型未解析 {field_type}")
                elif self._udt_cache.get(self._clean_type_name(field_type or ''), {}).get('unresolved'):
                    self._mark_taint(f"UDT/IEC字段模板含未解析成员 {field_type}")
                
                byte_offset, bit_offset = self._advance_offset(byte_offset, bit_offset, effective_size)
        
        return byte_offset
    
    def _extract_nested_members_v2(self, parent: ET.Element, parent_path: str, scope: str, 
                                    variables: List[VariableInfo], nested_config: Dict,
                                    base_byte_offset: int, base_bit_offset: int, 
                                    depth: int) -> tuple:
        """递归提取嵌套成员 - 增强版：支持IEC/UDT/数组展开
        
        返回: (byte_offset, bit_offset) 结束时的偏移量
        """
        max_depth = nested_config.get('max_depth', 20)
        if depth > max_depth:
            return base_byte_offset, base_bit_offset
        
        path_sep = nested_config.get('path_separator', '.')
        
        byte_offset = base_byte_offset
        bit_offset = base_bit_offset
        
        # 查找直接子 Member (用于内联的 Struct/Array 等)
        # 或者 查找 Sections/Section/Member (用于外部传入的包含 Sections 的块)
        child_members = parent.findall('./Member')
        if not child_members:
            for section in parent.findall('./Sections/Section'):
                child_members.extend(section.findall('./Member'))
                
        for member in child_members:
            name = member.get('Name', '')
            if not name:
                continue
            
            full_path = f"{parent_path}{path_sep}{name}"
            
            byte_offset, bit_offset = self._process_member_variable(
                member, name, full_path, scope, variables, nested_config,
                byte_offset, bit_offset, depth
            )
        
        return byte_offset, bit_offset
    
    def _extract_nested_members(self, parent: ET.Element, parent_path: str, scope: str, 
                                 variables: List[VariableInfo], nested_config: Dict, depth: int):
        """递归提取嵌套成员 - 保留旧接口兼容性，内部调用新版本"""
        self._extract_nested_members_v2(
            parent, parent_path, scope, variables, nested_config,
            0, 0, depth
        )
    
    def _extract_calls(self, root: ET.Element, calls_config: Dict, blocks: List[BlockInfo]) -> List[CallInfo]:
        """提取调用关系"""
        calls = []
        
        # 预构建父子关系映射
        parent_map = {child: parent for parent in root.iter() for child in parent}
        
        # 为每个块提取其调用
        for block_elem in root.iter():
            if not block_elem.tag.startswith('SW.Blocks.'):
                continue
            
            # 排除内部结构
            if block_elem.tag in ['SW.Blocks.CompileUnit', 'SW.Blocks.Access', 'SW.Blocks.Part', 'SW.Blocks.Call']:
                continue
            
            caller_name = ""
            name_elem = block_elem.find('.//AttributeList/Name')
            if name_elem is not None:
                caller_name = name_elem.text or ""
            
            # 查找所有Call元素和CallInfo
            for call_elem in block_elem.iter():
                tag_name = self._get_tag_name(call_elem.tag)
                
                # 处理 Call 元素 (Part类型的调用)
                if tag_name == 'Call':
                    call_info = call_elem.find('.//CallInfo')
                    if call_info is not None:
                        callee = call_info.get('Name', '')
                        if callee:
                            # 向上查找 CompileUnit 获取 network_id
                            network_id = self._find_network_id(call_elem, parent_map)
                            
                            calls.append(CallInfo(
                                caller=caller_name,
                                callee=callee,
                                callee_type=call_info.get('BlockType', ''),
                                network_id=network_id
                            ))
                
                # 也处理单独的 CallInfo（GRAPH等）
                elif tag_name == 'CallInfo':
                    # 检查是否已经被 Call 处理过
                    parent = parent_map.get(call_elem)
                    if parent is not None and self._get_tag_name(parent.tag) == 'Call':
                        continue  # 跳过，已经在 Call 处理中处理过
                    
                    callee = call_elem.get('Name', '')
                    if callee:
                        network_id = self._find_network_id(call_elem, parent_map)
                        calls.append(CallInfo(
                            caller=caller_name,
                            callee=callee,
                            callee_type=call_elem.get('BlockType', ''),
                            network_id=network_id
                        ))
        
        return calls
    
    def _find_network_id(self, elem: ET.Element, parent_map: Dict) -> str:
        """向上遍历找到包含的CompileUnit并返回其ID"""
        current = elem
        while current is not None:
            tag_name = self._get_tag_name(current.tag)
            if tag_name == 'SW.Blocks.CompileUnit' or tag_name == 'CompileUnit':
                # 优先获取 ID，否则尝试 Number
                network_id = current.get('ID', '') or current.get('Number', '')
                return network_id
            current = parent_map.get(current)
        return ""
    
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
                action_text = self._extract_graph_action_text(action)
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
    
    # ============================================================
    # XML ↔ 源文件自动关联
    # ============================================================
    
    def _try_load_companion_source(self, xml_path: str, block_name: str, language: str
                                   ) -> tuple:
        """
        在 XML 文件所在目录(及相邻子目录)查找同名 .scl/.awl 源文件。
        找到后解析其代码体，返回 (logic_units, calls_list)。
        """
        from pathlib import Path as _Path
        ext = '.awl' if language == 'STL' else '.scl'
        xml_dir = _Path(xml_path).parent
        xml_stem = _Path(xml_path).stem  # e.g. FB5000_GraphMonitor
        
        # 去掉引号
        clean_name = block_name.strip('"').strip("'")
        
        # 候选名称：块名称 → XML文件名 (去重)
        names = list(dict.fromkeys([clean_name, xml_stem]))
        
        # 候选路径：同目录 → 父目录 → 同级子目录
        candidates = []
        for name in names:
            candidates.append(xml_dir / f"{name}{ext}")
        for name in names:
            candidates.append(xml_dir.parent / f"{name}{ext}")
        try:
            for sub in xml_dir.iterdir():
                if sub.is_dir():
                    for name in names:
                        candidates.append(sub / f"{name}{ext}")
        except OSError:
            pass
        
        for candidate in candidates:
            if candidate.is_file():
                try:
                    encoding = self.detect_encoding(str(candidate))
                    with open(candidate, 'r', encoding=encoding, errors='ignore') as f:
                        content = f.read()
                    lines = content.split('\n')
                    body_text, _ = self._extract_source_body(lines)
                    if not body_text.strip():
                        continue
                    
                    if ext == '.awl':
                        units = self._parse_stl_body(body_text, clean_name)
                    else:
                        units = self._parse_scl_body(body_text, clean_name)
                    
                    # 收集调用列表
                    all_calls = []
                    for lu in units:
                        all_calls.extend(lu.calls)
                    
                    return units, all_calls
                except Exception:
                    continue
        
        return [], []
    
    # ============================================================
    # 源文本文件解析 (.scl / .awl)
    # ============================================================
    
    def _extract_from_source_text(self, file_path: str, file_info: FileInfo, file_ext: str) -> ParseResult:
        """从 SCL/STL 纯文本源文件中提取结构化信息"""
        result = ParseResult(file_info=file_info)
        
        try:
            with open(file_path, 'r', encoding=file_info.encoding, errors='ignore') as f:
                content = f.read()
            
            lines = content.split('\n')
            
            # 1. 解析块头信息
            block_info = self._parse_source_block_header(lines)
            if block_info:
                result.blocks.append(block_info)
            
            # 2. 解析变量声明
            result.variables = self._parse_source_var_declarations(lines)
            
            # 3. 提取代码体
            body_text, body_start_line = self._extract_source_body(lines)
            
            # 4. 根据语言类型解析逻辑
            main_block_name = block_info.name if block_info else Path(file_path).stem
            language = block_info.language if block_info else ('STL' if file_ext == '.awl' else 'SCL')
            
            if file_ext == '.awl' or language == 'STL':
                result.logic_units = self._parse_stl_body(body_text, main_block_name)
                # 更新 networks 向后兼容
                for lu in result.logic_units:
                    result.networks.append(NetworkInfo(id=lu.unit_id, title=lu.title, comment=lu.comment))
            else:
                result.logic_units = self._parse_scl_body(body_text, main_block_name)
            
            # 5. 从逻辑单元中提取调用关系
            for lu in result.logic_units:
                for callee in lu.calls:
                    result.calls.append(CallInfo(
                        caller=main_block_name,
                        callee=callee,
                        callee_type="",
                        network_id=str(lu.unit_id)
                    ))
            
            # 6. 构建交叉引用
            result.cross_references = self._build_cross_references(result.logic_units, main_block_name)
            
            # 7. 品牌信息（硬编码为 Siemens — 因为 SCL/AWL 格式是西门子特有的）
            file_info.brand = "Siemens"
            file_info.product = "TIA Portal"
            
            # 8. 语义分析（基于文本）
            analyzer = SemanticAnalyzer()
            analyzer.analyze_from_text(result, content)
            
        except Exception as e:
            result.errors.append(f"源文件解析错误: {str(e)}")
            import traceback
            result.errors.append(traceback.format_exc())
        
        return result
    
    def _parse_source_block_header(self, lines: List[str]) -> Optional[BlockInfo]:
        """解析源文件块头（FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / ORGANIZATION_BLOCK）"""
        block_type = ""
        block_name = ""
        language = ""
        title = ""
        author = ""
        version = ""
        comment_lines = []
        
        for line in lines:
            stripped = line.strip()
            
            # 块类型声明
            m = re.match(r'^FUNCTION_BLOCK\s+"([^"]+)"', stripped)
            if m:
                block_type = "FB"
                block_name = m.group(1)
                language = "SCL"  # 默认，后续可能被覆盖
                continue
            
            m = re.match(r'^FUNCTION\s+"([^"]+)"\s*:\s*(\S+)', stripped)
            if m:
                block_type = "FC"
                block_name = m.group(1)
                language = "SCL"
                continue
            
            m = re.match(r'^DATA_BLOCK\s+"([^"]+)"', stripped)
            if m:
                block_type = "DB"
                block_name = m.group(1)
                continue
            
            m = re.match(r'^ORGANIZATION_BLOCK\s+"([^"]+)"', stripped)
            if m:
                block_type = "OB"
                block_name = m.group(1)
                continue
            
            # TITLE
            m = re.match(r'^TITLE\s*=\s*(.*)', stripped)
            if m:
                title = m.group(1).strip()
                continue
            
            # AUTHOR
            m = re.match(r'^AUTHOR\s*:\s*(.*)', stripped)
            if m:
                author = m.group(1).strip()
                continue
            
            # VERSION
            m = re.match(r'^VERSION\s*:\s*(.*)', stripped)
            if m:
                version = m.group(1).strip()
                continue
            
            # 行注释（在块头区域的）
            if stripped.startswith('//') and not block_name:
                comment_lines.append(stripped[2:].strip())
            
            # 遇到 VAR 或 BEGIN 就停止头部解析
            if stripped.startswith('VAR') or stripped == 'BEGIN':
                break
        
        if not block_name:
            return None
        
        # 确定语言：如果文件包含 STL 指令模式则为 STL
        # 简单检查：如果 BEGIN 后面有 NETWORK 关键词
        full_text = '\n'.join(lines)
        if re.search(r'^\s*NETWORK\s*$', full_text, re.MULTILINE):
            language = "STL"
        
        # 提取块编号
        number = 0
        m = re.search(r'(\d+)', block_name)
        if m:
            number = int(m.group(1))
        
        return BlockInfo(
            name=block_name,
            type=block_type,
            number=number,
            language=language,
            comment=title or '; '.join(comment_lines),
            author=author,
            version=version,
        )
    
    def _parse_source_var_declarations(self, lines: List[str]) -> List[VariableInfo]:
        """解析 VAR_INPUT / VAR_OUTPUT / VAR_IN_OUT / VAR / VAR_TEMP / VAR CONSTANT 段"""
        variables = []
        
        scope_map = {
            'VAR_INPUT': 'Input',
            'VAR_OUTPUT': 'Output',
            'VAR_IN_OUT': 'InOut',
            'VAR': 'Static',
            'VAR_TEMP': 'Temp',
            'VAR CONSTANT': 'Constant',
        }
        
        current_scope = None
        struct_stack = []  # 嵌套 Struct 路径栈
        i = 0
        
        while i < len(lines):
            stripped = lines[i].strip()
            
            # 检测 VAR 段开始
            for kw, scope in scope_map.items():
                if stripped == kw or stripped.startswith(kw + ' '):
                    current_scope = scope
                    struct_stack = []
                    break
            
            # 检测 VAR 段结束
            if stripped == 'END_VAR':
                current_scope = None
                struct_stack = []
                i += 1
                continue
            
            # 在 VAR 段内解析成员
            if current_scope is not None and stripped and not stripped.startswith('{') and stripped != 'END_VAR':
                # 跳过 VAR 行本身
                is_var_line = False
                for kw in scope_map:
                    if stripped == kw or stripped.startswith(kw + ' '):
                        is_var_line = True
                        break
                
                if not is_var_line:
                    # 检查 Struct 嵌套起始
                    m_struct = re.match(r'^(\w+)\s*(?:\{[^}]*\})?\s*:\s*Struct', stripped, re.IGNORECASE)
                    if m_struct:
                        struct_name = m_struct.group(1)
                        struct_stack.append(struct_name)
                        i += 1
                        continue
                    
                    # 检查 END_STRUCT
                    if stripped.startswith('END_STRUCT'):
                        if struct_stack:
                            struct_stack.pop()
                        i += 1
                        continue
                    
                    # 解析普通变量声明
                    var_info = self._parse_source_var_line(stripped, current_scope, struct_stack)
                    if var_info:
                        variables.append(var_info)
            
            # 遇到 BEGIN 就完全停止
            if stripped == 'BEGIN':
                break
            
            i += 1
        
        return variables
    
    def _parse_source_var_line(self, line: str, scope: str, struct_stack: List[str]) -> Optional[VariableInfo]:
        """解析单行变量声明
        
        格式示例:
          AutoRun : Bool;   // 运行模式
          Minitor { ExternalAccessible := 'False' } : Array[0..249] of Bool;
          Delay {InstructionName := 'TON_TIME'; LibVersion := '1.0'} : TON_TIME;
          PositionValueMax : LReal := 3000.0;   // 位置最大值
          StatCamProfile : "LCamHdl_typeAdvancedElement";
        """
        # 移除属性声明 { ... }
        clean_line = re.sub(r'\{[^}]*\}', '', line).strip()
        
        # 匹配: Name : DataType [:= InitValue] ;  [// Comment]
        m = re.match(
            r'^(\w+)\s*:\s*(.+?)\s*(?::=\s*(.+?))?\s*;\s*(?://\s*(.*))?$',
            clean_line
        )
        if not m:
            return None
        
        name = m.group(1)
        datatype = m.group(2).strip().strip('"')
        initial_value = m.group(3).strip() if m.group(3) else ""
        comment = m.group(4).strip() if m.group(4) else ""
        
        # 构建路径
        if struct_stack:
            path = '.'.join(struct_stack) + '.' + name
        else:
            path = name
        
        return VariableInfo(
            name=name,
            path=path,
            datatype=datatype,
            scope=scope,
            comment=comment,
            initial_value=initial_value,
        )
    
    def _extract_source_body(self, lines: List[str]) -> Tuple[str, int]:
        """提取 BEGIN ~ END_FUNCTION_BLOCK/END_FUNCTION 之间的代码体"""
        body_lines = []
        body_start = -1
        in_body = False
        
        for i, line in enumerate(lines):
            stripped = line.strip()
            if stripped == 'BEGIN':
                in_body = True
                body_start = i + 1
                continue
            
            if in_body:
                # 结束标记
                if stripped in ('END_FUNCTION_BLOCK', 'END_FUNCTION', 
                               'END_DATA_BLOCK', 'END_ORGANIZATION_BLOCK'):
                    break
                body_lines.append(line)
        
        return '\n'.join(body_lines), body_start
    
    # ============================================================
    # SCL 代码体解析
    # ============================================================
    
    def _parse_scl_body(self, body_text: str, block_name: str) -> List[LogicUnit]:
        """解析 SCL 代码体 → 单个 LogicUnit"""
        if not body_text.strip():
            return []
        
        variable_access, uid_symbol_map, calls = self._extract_scl_style_accesses(body_text)
        
        return [LogicUnit(
            unit_id=1,
            unit_type="SCL_BODY",
            title=block_name,
            raw_content=body_text,
            uid_symbol_map=uid_symbol_map,
            variable_access=variable_access,
            comment="",
            programming_language="SCL",
            calls=calls
        )]
    
    def _scl_record_access(self, var_name: str, access_type: str, comment: str, seen_vars: dict):
        """记录 SCL 变量访问（去重合并）"""
        if var_name not in seen_vars:
            # SCL 中 # 前缀的变量是本地变量，其余为全局变量
            scope = 'LocalVariable' if var_name.startswith('#') else 'GlobalVariable'
            seen_vars[var_name] = {'types': set(), 'comment': comment, 'scope': scope}
        seen_vars[var_name]['types'].add(access_type)
        if comment and not seen_vars[var_name]['comment']:
            seen_vars[var_name]['comment'] = comment
    
    # ============================================================
    # STL 代码体解析
    # ============================================================
    
    # STL 指令分类
    STL_WRITE_INSTRUCTIONS = {'T', '=', 'S', 'R'}
    STL_READ_INSTRUCTIONS = {
        'L', 'A', 'AN', 'O', 'ON', 'U', 'UN', 'X', 'XN',
        'AW', 'OW', 'XOW', 'FP', 'FN'
    }
    STL_CALL_INSTRUCTIONS = {'CALL', 'UC', 'CC'}
    STL_DB_INSTRUCTIONS = {'OPN'}
    STL_NO_OPERAND = {
        'SET', 'SAVE', 'CLR', 'NOT', 'PUSH', 'POP',
        'BE', 'BEC', 'BEU', 'NOP',
        '+I', '-I', '*I', '/I', '+D', '-D', '*D', '/D',
        '+R', '-R', '*R', '/R',
        'TAK', 'INC', 'DEC', 'INVI', 'INVD', 'NEGI', 'NEGD',
        'BTI', 'BTD', 'ITB', 'ITD', 'DTB', 'DTR', 'RND', 'TRUNC',
        'AD', 'OD', 'XOD', 'AW', 'OW', 'XOW',
        'SSD', 'SSI', 'SLW', 'SRW', 'SLD', 'SRD',
        'RLD', 'RRD', 'RLDA', 'RRDA',
        'LAR1', 'LAR2', 'TAR1', 'TAR2',
        'ENT', 'LEAVE', 'MCRA', 'MCRD',
    }
    STL_JUMP_INSTRUCTIONS = {'JC', 'JCN', 'JU', 'JCB', 'JNB', 'JBI', 'JNBI', 'JL', 'JO', 'JOS', 'JP', 'JM', 'JN', 'JZ', 'JPZ', 'JMZ', 'JUO'}
    
    def _parse_stl_body(self, body_text: str, block_name: str) -> List[LogicUnit]:
        """解析 STL 代码体 → 按 NETWORK 拆分为多个 LogicUnit"""
        if not body_text.strip():
            return []
        
        # 按 NETWORK 关键词拆分
        networks = self._split_stl_networks(body_text)
        logic_units = []
        
        for net_idx, (net_title, net_comment, net_body) in enumerate(networks, start=1):
            variable_access = []
            uid_symbol_map = {}
            calls = []
            seen_vars = {}
            var_idx = 0
            
            for line in net_body.split('\n'):
                stripped = line.strip()
                if not stripped:
                    continue
                
                # 提取行内注释 (//...)
                line_comment = ""
                comment_match = re.search(r'//(.*)$', stripped)
                if comment_match:
                    line_comment = comment_match.group(1).strip()
                    code_part = stripped[:comment_match.start()].strip()
                else:
                    code_part = stripped
                
                if not code_part:
                    continue
                
                # 去掉行尾分号
                code_part = code_part.rstrip(';').strip()
                
                # 跳过标签行 (如 M001:)
                label_match = re.match(r'^(\w+):\s*(.*)', code_part)
                if label_match:
                    # 标签后可能有指令
                    code_part = label_match.group(2).strip()
                    if not code_part:
                        continue
                
                # 解析指令
                parsed = self._parse_stl_instruction(code_part)
                if not parsed:
                    continue
                
                instruction, operand = parsed
                
                # 跳过无操作数指令和跳转
                if instruction in self.STL_NO_OPERAND or not operand:
                    continue
                if instruction in self.STL_JUMP_INSTRUCTIONS:
                    continue
                
                # 处理 NOP
                if instruction == 'NOP':
                    continue
                
                # 处理 LOOP
                if instruction == 'LOOP':
                    continue
                
                # 提取操作数中的变量
                var_info = self._parse_stl_operand(operand)
                if not var_info:
                    continue
                
                var_name = var_info['name']
                addr_mode = var_info['address_mode']
                
                # 确定访问类型
                if instruction in self.STL_WRITE_INSTRUCTIONS:
                    access_type = 'WRITE'
                elif instruction in self.STL_READ_INSTRUCTIONS:
                    access_type = 'READ'
                elif instruction in self.STL_DB_INSTRUCTIONS:
                    access_type = 'OPEN'
                elif instruction in self.STL_CALL_INSTRUCTIONS:
                    access_type = 'CALL'
                    calls.append(var_name)
                elif instruction.startswith('+AR') or instruction.startswith('-AR'):
                    access_type = 'READ'
                else:
                    access_type = 'READ'
                
                # 记录变量访问
                if var_name not in seen_vars:
                    # STL 中 # 前缀为本地变量，其余为全局变量
                    scope = 'LocalVariable' if var_name.startswith('#') else 'GlobalVariable'
                    seen_vars[var_name] = {
                        'types': set(), 'comment': line_comment,
                        'addr_mode': addr_mode, 'instruction': instruction,
                        'scope': scope
                    }
                seen_vars[var_name]['types'].add(access_type)
                if line_comment and not seen_vars[var_name]['comment']:
                    seen_vars[var_name]['comment'] = line_comment
            
            # 合并变量访问
            for var_name, info in seen_vars.items():
                types = info['types']
                if 'WRITE' in types and 'READ' in types:
                    final_type = 'READWRITE'
                elif 'WRITE' in types:
                    final_type = 'WRITE'
                elif 'OPEN' in types:
                    final_type = 'OPEN'
                elif 'CALL' in types:
                    final_type = 'CALL'
                else:
                    final_type = 'READ'
                
                var_idx += 1
                uid_symbol_map[str(var_idx)] = var_name
                variable_access.append(VariableAccess(
                    variable=var_name,
                    access_type=final_type,
                    uid=str(var_idx),
                    comment=info['comment'],
                    instruction=info['instruction'],
                    address_mode=info['addr_mode'],
                    variable_scope=info.get('scope', '')
                ))
            
            logic_units.append(LogicUnit(
                unit_id=net_idx,
                unit_type="STL_NETWORK",
                title=net_title,
                raw_content=net_body,
                uid_symbol_map=uid_symbol_map,
                variable_access=variable_access,
                comment=net_comment,
                programming_language="STL",
                calls=calls
            ))
        
        return logic_units
    
    def _split_stl_networks(self, body_text: str) -> List[Tuple[str, str, str]]:
        """将 STL 代码体按 NETWORK 拆分
        
        Returns: [(title, comment, body), ...]
        """
        lines = body_text.split('\n')
        networks = []
        current_title = ""
        current_comment = ""
        current_body_lines = []
        in_network = False
        
        for line in lines:
            stripped = line.strip()
            
            if stripped == 'NETWORK':
                # 保存上一个 NETWORK
                if in_network:
                    networks.append((current_title, current_comment, '\n'.join(current_body_lines)))
                
                in_network = True
                current_title = ""
                current_comment = ""
                current_body_lines = []
                continue
            
            if in_network and stripped.startswith('TITLE ='):
                current_title = stripped[7:].strip()
                continue
            
            if in_network:
                # 检查是否是注释行（紧跟 TITLE 后面的注释）
                if not current_body_lines and stripped.startswith('//') and not current_comment:
                    current_comment = stripped[2:].strip()
                    continue
                current_body_lines.append(line)
        
        # 最后一个 NETWORK
        if in_network:
            networks.append((current_title, current_comment, '\n'.join(current_body_lines)))
        
        # 如果没有 NETWORK 分隔符，整个 body 作为一个逻辑单元
        if not networks and body_text.strip():
            networks.append(("", "", body_text))
        
        return networks
    
    def _parse_stl_instruction(self, code: str) -> Optional[Tuple[str, str]]:
        """解析 STL 指令行 → (指令码, 操作数)
        
        示例:
          L #From         → ('L', '#From')
          T D[AR1, P#0.0] → ('T', 'D[AR1, P#0.0]')
          OPN DB[#w_Temp]  → ('OPN', 'DB[#w_Temp]')
          +AR1 P#1.0       → ('+AR1', 'P#1.0')
          SET              → ('SET', '')
        """
        code = code.strip()
        if not code:
            return None
        
        # 特殊处理 +AR1 / +AR2 / -AR1 / -AR2
        m = re.match(r'^([+-]AR[12])\s*(.*)', code)
        if m:
            return (m.group(1), m.group(2).strip())
        
        # 标准指令: OPCODE [operand]
        parts = code.split(None, 1)
        instruction = parts[0].upper()
        operand = parts[1].strip() if len(parts) > 1 else ""
        
        return (instruction, operand)
    
    def _parse_stl_operand(self, operand: str) -> Optional[Dict[str, str]]:
        """解析 STL 操作数 → {name, address_mode}
        
        支持的模式:
          #From              → 局部变量
          P##Dest            → 指针
          DB[#w_Temp]        → DB间接
          DI[#DBNO]          → DI间接
          W[AR1, P#0.0]      → 间接寻址 (Word)
          D[AR1, P#2.0]      → 间接寻址 (DWord)
          IB[AR1, P#0.0]     → 输入字节间接
          QB[AR1, P#0.0]     → 输出字节间接
          MB[AR1, P#0.0]     → 存储器字节间接
          DBB[AR2, P#0.0]    → DB字节间接
          DIB[AR2, P#0.0]    → DI字节间接
          DBB[#BytePoint]    → DB字节（局部变量索引）
          DW#16#FFFFF8       → DWord 常量
          W#16#3             → Word 常量
          DINT#1             → DInt 常量
          0                  → 数字常量
          P#0.0              → 指针常量
        """
        operand = operand.strip()
        if not operand:
            return None
        
        # 局部变量: #VarName  或 #VarName.SubField
        if operand.startswith('#'):
            return {'name': operand, 'address_mode': 'symbolic'}
        
        # 指针: P##VarName
        if operand.startswith('P##'):
            return {'name': operand, 'address_mode': 'pointer'}
        
        # DB/DI 间接开启: DB[#var] / DI[#var]
        m = re.match(r'^(DB|DI)\[\s*#(\w+)\s*\]$', operand)
        if m:
            return {'name': f'{m.group(1)}[#{m.group(2)}]', 'address_mode': 'indirect'}
        
        # 间接寻址: TYPE[ARx, P#offset] 或 TYPE[#var]
        m = re.match(r'^(\w+)\[\s*(.+?)\s*\]$', operand)
        if m:
            mem_type = m.group(1)
            index = m.group(2)
            
            # 检查是否是局部变量索引: DBB[#BytePoint]
            if index.startswith('#'):
                return {'name': f'{mem_type}[{index}]', 'address_mode': 'indirect'}
            
            # AR 间接: D[AR1, P#2.0]
            if 'AR' in index:
                return {'name': f'{mem_type}[{index}]', 'address_mode': 'indirect'}
            
            return {'name': f'{mem_type}[{index}]', 'address_mode': 'indirect'}
        
        # 常量类型: DW#16#xxx, W#16#x, DINT#x, INT#x, BOOL#x, etc.
        if re.match(r'^(DW|W|DINT|INT|BYTE|BOOL|REAL|LREAL|WSTRING|STRING)#', operand, re.IGNORECASE):
            return None  # 常量不记录为变量访问
        
        # 纯数字常量
        if re.match(r'^-?\d+(\.\d+)?$', operand):
            return None
        
        # 指针常量 P#x.y
        if re.match(r'^P#\d+\.\d+$', operand):
            return None
        
        # NOP 操作数
        if re.match(r'^\d+$', operand):
            return None
        
        # 绝对地址: M10.0, I0.0, Q0.0, MW10, DB10.DBW0 等
        m = re.match(r'^([IQME])(B|W|D)?\d', operand, re.IGNORECASE)
        if m:
            return {'name': operand, 'address_mode': 'absolute'}
        
        # DB绝对地址: DBB0, DBW0, DBD0 等
        m = re.match(r'^(DBB|DBW|DBD|DIB|DIW|DID)\d', operand, re.IGNORECASE)
        if m:
            return {'name': operand, 'address_mode': 'absolute'}
        
        # 其他未识别的操作数（可能是函数名等）
        return {'name': operand, 'address_mode': 'unknown'}

    def analyze_full(self, file_path: str) -> Dict:
        """完整分析：预处理 + 提取 + 摘要"""
        result = self.extract(file_path)
        # 添加语义分析
        analyzer = SemanticAnalyzer()
        analyzer.analyze(result, self.root if hasattr(self, 'root') else None)
        return result.to_dict()
