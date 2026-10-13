#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 数据模型定义

定义所有用于PLC解析的数据结构
"""

from dataclasses import dataclass, field, asdict
from typing import Dict, List, Any

# ============================================================
# 基础数据模型
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
    instance_of_name: str = ""    # InstanceDB 绑定的 FB/UDT 类型名（如 FB161_HomeGraph），非 InstanceDB 为空
    instance_of_type: str = ""    # 绑定类型（如 "FB" / "UDT"）

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
    offset: str = ""              # S7偏移地址，格式 "字节.位"，如 "0.0", "4.0"
    byte_offset: int = -1         # 纯字节偏移（用于程序化访问）
    bit_offset: int = 0           # 位偏移 (0-7)
    size_in_bytes: float = 0      # 变量占用字节数
    is_structure: bool = False     # 是否为结构/文件夹节点（非叶子变量）
    memory_layout: str = ""        # 内存布局：Standard (标准) / Optimized (优化)

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

@dataclass
class BlockCallPinBinding:
    """FB/FC调用处的引脚绑定关系 (Formal-to-Actual映射)
    
    这是跨边界AST展平的基础数据。例如，当外层Network调用FB_Cylinder时：
      input_pins  = {"IN_Enable": "I0.1"}   # 形参 → 实参
      output_pins = {"OUT_Push": "Q0.5"}    # 形参 → 实参
    展平时，FB内部的 #OUT_Push 被替换为 Q0.5，
    #Stat_Ready 被替换为 {instance_db}.Stat_Ready。
    """
    callee_name: str                                            # FB/FC类型名, e.g. "FB_Cylinder"
    callee_block_type: str                                      # "FB" / "FC" / "OB"
    instance_db: str                                            # 背景数据块名 (FC调用时为空)
    call_uid: str                                               # Call节点的UId (用于溯源)
    input_pins: Dict[str, str] = field(default_factory=dict)    # 形式IN参数→实际变量
    output_pins: Dict[str, str] = field(default_factory=dict)   # 形式OUT参数→实际变量
    inout_pins: Dict[str, str] = field(default_factory=dict)    # 形式InOut参数→实际变量

# ============================================================
# V2.0 数据模型 - 支持 AI 诊断的精细化结构
# ============================================================

@dataclass
class VariableAccess:
    """变量访问记录（V2.0）"""
    variable: str          # 完整变量名（如 DB420_OP40Manual.General.AutoMod）
    access_type: str       # "READ" | "WRITE" | "READWRITE" | "CALL" | "OPEN"
    uid: str               # 对应的 UId
    comment: str = ""      # 变量注释
    instruction: str = ""  # STL指令码（如 L/T/A/OPN），SCL中为操作符（如 :=）
    address_mode: str = "" # 寻址模式: symbolic/absolute/indirect/pointer/constant
    variable_scope: str = "" # 变量作用域: GlobalVariable/LocalVariable/LiteralConstant/TypedConstant

@dataclass  
class LogicUnit:
    """逻辑单元 - Network/Transition（V2.0）
    
    unit_type 取值:
    - "NETWORK"          : LAD/FBD/STL XML 中的 CompileUnit
    - "TRANSITION"       : GRAPH 转换条件
    - "STEP_ACTION"      : GRAPH 步动作
    - "STL_NETWORK"      : STL 源文件(.awl)中的 NETWORK 段
    - "SCL_BODY"         : SCL 源文件(.scl)中的完整代码体
    - "SCL_INTERFACE"    : SCL 源文件中的接口定义（无代码时的降级模式）
    """
    unit_id: int                                    # Network 编号或 Transition 编号
    unit_type: str                                  # 见上方取值说明
    title: str                                      # 标题
    raw_content: str = ""                           # 原始内容（XML 或源代码文本）
    uid_symbol_map: Dict[str, str] = field(default_factory=dict)   # UID -> 变量名映射（STL/SCL中为变量索引映射）
    variable_access: List[VariableAccess] = field(default_factory=list)  # 变量访问列表
    comment: str = ""                               # 注释
    programming_language: str = "LAD"               # 编程语言（LAD/FBD/SCL/STL/GRAPH）
    compile_unit_id: str = ""                       # CompileUnit 原始 ID（十六进制），用于与 BlockCall 关联
    calls: List[str] = field(default_factory=list)  # 逻辑单元内的调用列表（SCL/STL 源码中提取）
    call_pin_bindings: List['BlockCallPinBinding'] = field(default_factory=list)  # FB/FC调用引脚绑定（跨边界AST展平）
    outgoing_transition_numbers: List[int] = field(default_factory=list)  # GRAPH Step 出口转移编号列表（来自 <Connections>）

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

# ============================================================
# 解析结果
# ============================================================

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
                "instance_of_name": b.instance_of_name,
                "instance_of_type": b.instance_of_type,
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
                "compile_unit_id": lu.compile_unit_id,
                "calls": lu.calls,
                "call_pin_bindings": [asdict(b) for b in lu.call_pin_bindings],
                "outgoing_transition_numbers": lu.outgoing_transition_numbers,
            } for lu in self.logic_units],
            "cross_references": {
                "writers": self.cross_references.writers,
                "readers": self.cross_references.readers,
            },
            "calls": [asdict(c) for c in self.calls],
            "graph_steps": [asdict(s) for s in self.graph_steps],
            "graph_transitions": [asdict(t) for t in self.graph_transitions],
            "networks": [asdict(n) for n in self.networks],
            "semantic_hints": asdict(self.semantic_hints),
            "errors": self.errors,
        }
