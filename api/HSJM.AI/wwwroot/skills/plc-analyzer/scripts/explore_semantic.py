#!/usr/bin/env python3
"""探索XML中的语义信息"""

import xml.etree.ElementTree as ET
import sys

def analyze_program_blocks(file_path: str):
    """分析程序块信息"""
    # 实现程序块分析逻辑
    return {"blocks": [], "total": 0}

def analyze_variables(file_path: str):
    """分析变量信息"""
    # 实现变量分析逻辑
    return {"variables": [], "total": 0}

def analyze_calls(file_path: str):
    """分析调用关系"""
    # 实现调用分析逻辑
    return {"calls": [], "total": 0}

def analyze_networks(file_path: str):
    """分析Network段落"""
    # 实现Network分析逻辑
    return {"networks": [], "total": 0}

def analyze_graph_steps(file_path: str):
    """分析GRAPH步序"""
    # 实现GRAPH步序分析逻辑
    return {"steps": [], "total": 0}

def analyze_comments(file_path: str):
    """分析关键注释"""
    # 实现注释分析逻辑
    return {"comments": [], "total": 0}

def analyze_architecture(file_path: str):
    """分析程序架构"""
    # 实现架构分析逻辑
    return {"architecture": {}, "total": 0}

def analyze_monitors(file_path: str):
    """分析变量监控信息"""
    # 实现监控分析逻辑
    return {"monitors": [], "total": 0}

def explore_semantic_info(file_path: str):
    """探索PLC文件中可用于语义理解的信息"""
    
    tree = ET.parse(file_path)
    root = tree.getroot()
    
    # 获取命名空间
    ns = {}
    for event, elem in ET.iterparse(file_path, events=['start-ns']):
        if event == 'start-ns':
            prefix, uri = elem
            if prefix:
                ns[prefix] = uri
            else:
                ns['default'] = uri
        break
    
    # 重新解析
    tree = ET.parse(file_path)
    root = tree.getroot()
    
    # 查找所有命名空间
    ns_map = {
        'si': 'http://www.siemens.com/automation/Openness/SW/Interface/v5',
        'default': 'http://www.siemens.com/automation/Openness/SW/Interface/v5'
    }
    
    print("=" * 60)
    print(f"文件: {file_path}")
    print("=" * 60)
    
    # 1. 查找块标题/注释
    print("\n【1. 块级注释】")
    for title_elem in root.iter():
        if 'Title' in title_elem.tag or 'Comment' in title_elem.tag:
            # 获取文本内容
            texts = []
            for text_elem in title_elem.iter():
                if text_elem.text and text_elem.text.strip():
                    texts.append(text_elem.text.strip())
            if texts:
                print(f"  {title_elem.tag}: {' '.join(texts)[:100]}")
    
    # 2. 查找Network标题
    print("\n【2. Network/段落标题】")
    network_count = 0
    for elem in root.iter():
        tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
        if tag in ['CompileUnit', 'NetworkSource']:
            network_count += 1
            # 查找标题
            for child in elem:
                child_tag = child.tag.split('}')[-1] if '}' in child.tag else child.tag
                if child_tag in ['Title', 'Comment']:
                    texts = [t.text for t in child.iter() if t.text and t.text.strip()]
                    if texts:
                        print(f"  Network {network_count}: {' '.join(texts)[:80]}")
                        break
    print(f"  共 {network_count} 个Network")
    
    # 3. 查找所有MultilingualText（注释来源）
    print("\n【3. 多语言文本（注释/标题）】")
    seen_texts = set()
    text_count = 0
    for elem in root.iter():
        tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
        if tag == 'MultilingualText':
            for text_item in elem.iter():
                item_tag = text_item.tag.split('}')[-1] if '}' in text_item.tag else text_item.tag
                if item_tag == 'Text' and text_item.text and text_item.text.strip():
                    content = text_item.text.strip()
                    if content not in seen_texts and len(content) > 3:
                        seen_texts.add(content)
                        text_count += 1
                        if text_count <= 15:
                            print(f"  [{text_count}] {content[:80]}")
    print(f"  共 {len(seen_texts)} 个独特文本")
    
    # 4. 分析变量命名模式
    print("\n【4. 变量命名模式分析】")
    var_names = []
    for elem in root.iter():
        tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
        if tag == 'Member':
            name = elem.get('Name', '')
            if name:
                var_names.append(name)
    
    # 统计前缀
    prefix_counts = {}
    for name in var_names:
        if '_' in name:
            prefix = name.split('_')[0]
            prefix_counts[prefix] = prefix_counts.get(prefix, 0) + 1
    
    print(f"  总变量数: {len(var_names)}")
    print("  前缀分布:")
    for prefix, count in sorted(prefix_counts.items(), key=lambda x: -x[1])[:10]:
        print(f"    {prefix}_*: {count}个")
    
    # 5. 识别工位特征关键词
    print("\n【5. 工位/功能关键词检测】")
    keywords = {
        '机器人': ['Robot', 'ABB', 'KUKA', 'Fanuc', 'UR', 'Arm', 'robot', 'gripper', 'Gripper'],
        '涂胶': ['Glue', 'Dispens', 'Coating', 'Seal', 'glue', 'dispens'],
        '焊接': ['Weld', 'Spot', 'Arc', 'weld'],
        '装配': ['Assembly', 'Press', 'Screw', 'assembly', 'press'],
        '输送': ['Conveyor', 'Transfer', 'Belt', 'conveyor', 'transfer'],
        '检测': ['Vision', 'Camera', 'Sensor', 'Check', 'Inspect', 'vision', 'camera'],
        '安全': ['Safety', 'Door', 'EStop', 'Emergency', 'Guard', 'safety'],
        '气缸': ['Cylinder', 'Valve', 'Air', 'cylinder', 'valve'],
        '伺服': ['Servo', 'Motor', 'Axis', 'servo', 'motor', 'axis'],
        'GRAPH': ['Step', 'Transition', 'Graph', 'Home', 'Init'],
        '手动': ['Manual', 'Jog', 'Hand', 'manual'],
        '自动': ['Auto', 'Cycle', 'Sequence', 'auto'],
    }
    
    # 获取所有文本内容
    all_text = []
    for elem in root.iter():
        if elem.text:
            all_text.append(elem.text)
        if elem.tail:
            all_text.append(elem.tail)
        for attr_val in elem.attrib.values():
            all_text.append(str(attr_val))
    
    full_text = ' '.join(all_text)
    
    detected = {}
    for category, kw_list in keywords.items():
        matches = []
        for kw in kw_list:
            if kw in full_text:
                # 统计出现次数
                count = full_text.count(kw)
                matches.append(f"{kw}({count})")
        if matches:
            detected[category] = matches
    
    for category, matches in detected.items():
        print(f"  {category}: {', '.join(matches[:5])}")
    
    # 6. 调用的功能块（语义来源）
    print("\n【6. 调用的功能块】")
    called_blocks = set()
    for elem in root.iter():
        tag = elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag
        if tag == 'Call':
            block_type = None
            for child in elem:
                child_tag = child.tag.split('}')[-1] if '}' in child.tag else child.tag
                if child_tag == 'CallInfo':
                    name = child.get('Name', '')
                    block_type = child.get('BlockType', '')
                    if name:
                        called_blocks.add(f"{block_type}:{name}")
    
    for block in sorted(called_blocks)[:15]:
        print(f"  → {block}")
    print(f"  共调用 {len(called_blocks)} 个不同块")
    
    print("\n" + "=" * 60)
    print("语义总结建议")
    print("=" * 60)
    
    # 基于检测到的信息生成功能推断
    func_hints = []
    if 'GRAPH' in detected:
        func_hints.append("顺序控制/步序逻辑")
    if '手动' in detected:
        func_hints.append("手动操作模式")
    if '自动' in detected:
        func_hints.append("自动循环控制")
    if '安全' in detected:
        func_hints.append("安全联锁处理")
    if '机器人' in detected:
        func_hints.append("机器人控制/交互")
    if '涂胶' in detected:
        func_hints.append("涂胶工艺")
    if '气缸' in detected:
        func_hints.append("气动执行器控制")
    
    print(f"可能的功能: {', '.join(func_hints) if func_hints else '需要更多信息'}")


if __name__ == "__main__":
    test_files = [
        r"e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\LAD_FB720_OP70Manual.xml",
        r"e:\AI效率优化\AI应用平台\HSJM_AI_PLC\docs\demoimg\FB461_HomeGraph.xml",
    ]
    
    for f in test_files:
        try:
            explore_semantic_info(f)
            print("\n")
        except Exception as e:
            print(f"Error processing {f}: {e}")