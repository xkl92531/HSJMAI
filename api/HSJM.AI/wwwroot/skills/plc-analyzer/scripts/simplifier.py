#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC Analysis Engine - Logic Simplifier
将复杂的 XML LAD/FBD 逻辑简化为可读字符串格式
"""

import xml.etree.ElementTree as ET
from typing import Dict, List, Optional, Tuple
from collections import defaultdict
from models import SimplifiedLogic

class LogicSimplifier:
    """Simplifies complex XML network logic into human/LLM readable string format"""
    
    def __init__(self, uid_map: Dict[str, str], constants_map: Dict[str, str] = None):
        self.uid_map = uid_map  # UId -> Variable Name
        self.constants_map = constants_map or {}  # UId -> Constant Value
        self.parts_map = {}  # UId -> Part Element
        self.wire_connections = defaultdict(list)  # "UId:Pin" -> List of connected nodes
        self.open_cons = set()  # UIDs of OpenCon (unconnected pins)
        
    def _get_tag(self, elem) -> str:
        return elem.tag.split('}')[-1]

    def simplify(self, network_source: ET.Element) -> SimplifiedLogic:
        """Main entry: simplify network to readable string"""
        self._build_connectivity_graph(network_source)
        self._extract_constants(network_source)
        
        lines = []
        parameters = {}
        outputs = {}
        explainable_chains_lines = []
        calls = []
        coils = []
        
        # Find main operations (Calls, Coils)
        main_ops = self._find_main_operations(network_source)
        
        for op in main_ops:
            op_type = self._get_part_type(op)
            op_uid = op.get('UId')
            
            if op_type == 'Call':
                block_name, instance_name = self._get_call_info(op)
                lines.append(f"[CALL] {block_name} (Instance: {instance_name})")
                call_parameters = {}
                call_outputs = {}
                
                # Process ALL parameters
                call_info = self._find_child(op, 'CallInfo')
                if call_info is not None:
                    for param in call_info:
                        if not self._get_tag(param).endswith('Parameter'):
                            continue
                        
                        p_name = param.get('Name')
                        p_section = param.get('Section')
                        p_type = param.get('Type', 'Bool')
                        
                        # Trace value
                        value = self._trace_parameter_value(op_uid, p_name, p_section)
                        
                        if p_section in ['Input', 'InOut', 'InputOutput']:
                            parameters[p_name] = value
                            call_parameters[p_name] = value
                            lines.append(f"  IN  {p_name}: {value}")
                        
                        if p_section in ['Output', 'InOut', 'InputOutput']:
                            out_target = self._trace_logic_forwards(op_uid, p_name)
                            if out_target:
                                outputs[p_name] = out_target
                                call_outputs[p_name] = out_target
                                if p_section == 'Output':
                                    lines.append(f"  OUT {p_name} -> {out_target}")
                                else:
                                    lines.append(f"  I/O {p_name}: {value} -> {out_target}")

                for out_name, out_target in call_outputs.items():
                    explainable_chains_lines.append(
                        f"{out_target} = {block_name}.{out_name} (Instance: {instance_name})"
                    )
                    for param_name, param_value in call_parameters.items():
                        explainable_chains_lines.append(
                            f"  - depends_on {param_name}: {param_value}"
                        )

                calls.append({
                    "name": block_name,
                    "instance": instance_name,
                    "inputs": call_parameters,
                    "outputs": call_outputs
                })

            elif op_type == 'Coil':
                var_uid = self._find_connected_operand(op_uid)
                if var_uid:
                    var_name = self.uid_map.get(var_uid, f"[UID:{var_uid}]")
                else:
                    var_name = f"Coil[{op_uid}]"
                expr = self._trace_logic_backwards(op_uid, 'in')
                
                outputs[f"Coil"] = var_name
                parameters["Logic"] = expr
                lines.append(f"[COIL] {var_name} := {expr}")
                explainable_chains_lines.append(f"{var_name} = Coil")
                explainable_chains_lines.append(f"  - expression: {expr}")
                coils.append({
                    "target": var_name,
                    "expression": expr
                })

        # Build single string description
        description = "\n".join(lines)
        
        return SimplifiedLogic(
            description=description,
            parameters=parameters,
            outputs=outputs,
            explainable_chains="\n".join(explainable_chains_lines),
            calls=calls,
            coils=coils
        )

    def _build_connectivity_graph(self, network_source):
        """Build wire connectivity graph"""
    def _build_connectivity_graph(self, network_source):
        """Build wire connectivity graph"""
        all_parts = []
        all_wires = []
        
        # Traverse network source to find ALL Parts and Wires sections
        for child in network_source.iter():
            tag = self._get_tag(child)
            if tag.endswith('Parts'): 
                all_parts.append(child)
            elif tag.endswith('Wires'): 
                all_wires.append(child)
            
        for parts in all_parts:
            # Iterate ALL descendants - any element with UId could be referenced by wires
            for part in parts.iter():
                uid = part.get('UId')
                if uid:
                    self.parts_map[uid] = part
                
        for wires in all_wires:
            for wire in wires:
                connected_nodes = []
                for con in wire:
                    con_type = self._get_tag(con)
                    uid = con.get('UId')
                    name = con.get('Name')
                    
                    if con_type == 'OpenCon':
                        self.open_cons.add(uid)
                    
                    connected_nodes.append({
                        'type': con_type, 
                        'uid': uid, 
                        'name': name
                    })
                
                # Build bidirectional connections
                for i in range(len(connected_nodes)):
                    for j in range(len(connected_nodes)):
                        if i == j: continue
                        src = connected_nodes[i]
                        dst = connected_nodes[j]
                        key = f"{src['uid']}:{src['name'] or ''}"
                        self.wire_connections[key].append(dst)

    def _extract_constants(self, network_source):
        """Extract literal constants from Access elements"""
        for elem in network_source.iter():
            if self._get_tag(elem).endswith('Access'):
                scope = elem.get('Scope')
                uid = elem.get('UId')
                
                if scope == 'LiteralConstant' and uid:
                    # Find ConstantValue
                    for child in elem.iter():
                        if self._get_tag(child).endswith('ConstantValue'):
                            self.constants_map[uid] = child.text or "?"
                            break

    def _find_main_operations(self, network_source) -> List:
        """Find main operations (Calls, Coils)"""
        ops = []
        for elem in network_source.iter():
            if self._get_tag(elem).endswith('Parts'):
                for part in elem:
                    ptype = self._get_part_type(part)
                    if ptype in ['Call', 'Coil']:
                        ops.append(part)
        return ops

    def _get_part_type(self, part) -> str:
        name = part.get('Name')
        coil_names = {
            'Coil', 'TrCoil', 'SCoil', 'RCoil', 'IlCoil', 'SvCoil',
            'SetCoil', 'ResetCoil'
        }

        if name == 'Call': return 'Call'
        if name in coil_names: return 'Coil'
        if name == 'Contact': return 'Contact'
        if name == 'O': return 'OR'
        
        for child in part:
            if self._get_tag(child).endswith('CallInfo'): 
                return 'Call'
        return name or 'Unknown'

    def _get_call_info(self, part) -> Tuple[str, str]:
        """Extract block name and instance name from Call"""
        call_info = self._find_child(part, 'CallInfo')
        if call_info is None:
            return ("UnknownBlock", "?")
        
        block_name = call_info.get('Name', 'UnknownBlock')
        instance_name = "?"
        
        # Find Instance
        for child in call_info:
            if self._get_tag(child).endswith('Instance'):
                instance_name = self._extract_symbol_path(child)
                break
        
        return (block_name, instance_name)

    def _find_child(self, elem, tag_suffix) -> Optional[ET.Element]:
        for child in elem:
            if self._get_tag(child).endswith(tag_suffix):
                return child
        return None

    def _extract_symbol_path(self, elem) -> str:
        """Extract full symbol path from element"""
        comps = []
        for child in elem.iter():
            if self._get_tag(child).endswith('Component'):
                name = child.get('Name')
                if name:
                    # Check for array index
                    idx = self._find_array_index(child)
                    if idx:
                        comps.append(f"{name}[{idx}]")
                    else:
                        comps.append(name)
        return ".".join(comps) if comps else "?"

    def _find_array_index(self, component) -> Optional[str]:
        """Find array index from nested Access/Constant"""
        for child in component.iter():
            if self._get_tag(child).endswith('ConstantValue'):
                return child.text
        return None

    def _trace_parameter_value(self, part_uid: str, pin_name: str, section: str) -> str:
        """Trace what value is connected to a parameter pin"""
        key = f"{part_uid}:{pin_name}"
        peers = self.wire_connections.get(key, [])
        
        for peer in peers:
            p_type = peer['type']
            p_uid = peer['uid']
            
            if p_type == 'OpenCon':
                return "[NC]"
            elif p_type == 'IdentCon':
                # Check if it's a constant
                if p_uid in self.constants_map:
                    return self.constants_map[p_uid]
                # Otherwise it's a variable
                return self.uid_map.get(p_uid, f"[UID:{p_uid}]")
            elif p_type == 'Powerrail':
                return "TRUE"
            elif p_type == 'NameCon':
                # Connected to another part's output - trace backwards
                return self._trace_logic_backwards(part_uid, pin_name)
        
        # No connection found - for Input params, trace backwards
        if section in ['Input', 'InOut', 'InputOutput']:
            expr = self._trace_logic_backwards(part_uid, pin_name)
            if expr:
                return expr
        
        return "[NC]"  # Not Connected

    def _trace_logic_backwards(self, part_uid: str, pin_name: str, depth: int = 0) -> str:
        """Recursively trace logic feeding into a pin"""
        if depth > 30:  # Prevent infinite recursion
            return "[MAX_DEPTH]"
        
        key = f"{part_uid}:{pin_name}"
        peers = self.wire_connections.get(key, [])
        expressions = []
        
        for peer in peers:
            p_uid = peer['uid']
            p_type = peer['type']
            p_name = peer.get('name', '')
            
            if p_type == 'IdentCon':
                # Direct variable or constant
                if p_uid in self.constants_map:
                    expressions.append(self.constants_map[p_uid])
                else:
                    var_name = self.uid_map.get(p_uid, f"[UID:{p_uid}]")
                    expressions.append(var_name)
                    
            elif p_type == 'Powerrail':
                expressions.append("TRUE")
                
            elif p_type == 'OpenCon':
                expressions.append("[NC]")  # Not Connected
                
            elif p_type == 'NameCon':
                # Connected to another part's output - get the source part
                source_part = self.parts_map.get(p_uid)
                if source_part is None: 
                    expressions.append(f"[Part:{p_uid}]")
                    continue
                
                sp_type = self._get_part_type(source_part)
                
                if sp_type == 'Contact':
                    # Contact reads a variable
                    var_uid = self._find_operand(source_part)
                    var_name = self.uid_map.get(var_uid, "[?]") if var_uid else "[?]"
                    
                    # Trace what feeds into this contact
                    prev_expr = self._trace_logic_backwards(p_uid, 'in', depth + 1)
                    
                    if prev_expr == "TRUE" or not prev_expr:
                        expressions.append(var_name)
                    else:
                        expressions.append(f"({prev_expr} AND {var_name})")
                         
                elif sp_type == 'OR':
                    # OR gate - trace all inputs (the output pin is 'out')
                    or_inputs = []
                    for i in range(1, 9):
                        in_expr = self._trace_logic_backwards(p_uid, f"in{i}", depth + 1)
                        if in_expr and in_expr != "[NC]":
                            or_inputs.append(in_expr)
                    
                    if or_inputs:
                        if len(or_inputs) == 1:
                            expressions.append(or_inputs[0])
                        else:
                            expressions.append(f"({' OR '.join(or_inputs)})")
                    else:
                        expressions.append(f"[OR:{p_uid}]")
                
                elif sp_type == 'Call':
                    # Output from another Call block - try to find which output pin
                    if p_name:
                        expressions.append(f"[{sp_type}.{p_name}]")
                    else:
                        expressions.append(f"[{sp_type}]")
                    
                else:
                    # Other part types
                    expressions.append(f"[{sp_type}]")
        
        if not expressions:
            return ""
        
        return " OR ".join(expressions) if len(expressions) > 1 else expressions[0]

    def _trace_logic_forwards(self, part_uid: str, pin_name: str) -> str:
        """Trace where an output pin writes to"""
        key = f"{part_uid}:{pin_name}"
        peers = self.wire_connections.get(key, [])
        
        for peer in peers:
            if peer['type'] == 'IdentCon':
                p_uid = peer['uid']
                return self.uid_map.get(p_uid, f"[UID:{p_uid}]")
            elif peer['type'] == 'OpenCon':
                return "[未连接]"
        return "[未连接]"

    def _find_operand(self, part_elem) -> Optional[str]:
        """Find the operand UID connected to a Contact/Coil"""
        uid = part_elem.get('UId')
        key = f"{uid}:operand"
        peers = self.wire_connections.get(key, [])
        for peer in peers:
            if peer['type'] == 'IdentCon': 
                return peer['uid']
        return None
        
    def _find_connected_operand(self, part_uid: str) -> Optional[str]:
        return self._find_operand(self.parts_map.get(part_uid))
