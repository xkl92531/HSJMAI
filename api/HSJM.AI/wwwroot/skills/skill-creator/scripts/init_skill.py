#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Skill Initializer - 创建新技能模板

Usage:
    python init_skill.py <skill-name> [--resources scripts,references,assets]

注意：
- 技能将自动创建在 wwwroot/skills/ 目录下
- 如果同名目录已存在，会自动添加数字后缀 (如 csv-analyzer-2)

Examples:
    python init_skill.py csv-analyzer --resources scripts
    python init_skill.py data-processor --resources scripts,references
"""

import argparse
import re
import sys
import os
from pathlib import Path
from datetime import datetime
import json
import base64

# 获取 skills 根目录（相对于此脚本）
SCRIPT_DIR = Path(__file__).parent.resolve()
SKILLS_ROOT = SCRIPT_DIR.parent.parent  # skill-creator/scripts -> skill-creator -> skills

SKILL_TEMPLATE = '''---
name: {skill_name}
description: {description_short}
---

# {skill_title}

## 概述

{description_long}

## 使用方法

{usage_guide}

## 工作流程

{workflow_steps}

## 资源说明

{resources_section}

---
*由 skill-creator 生成于 {timestamp}*
'''

SCRIPT_TEMPLATE = '''#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
{skill_title} - 主入口脚本

用法:
    python main.py --input-base64 <base64编码的JSON>
    python main.py --file-path <文件路径>
"""

import json
import base64
import argparse
import sys

def main():
    parser = argparse.ArgumentParser(description='{skill_title}')
    parser.add_argument('--input-base64', help='Base64 编码的 JSON 输入')
    parser.add_argument('--file-path', help='要处理的文件路径')
    args = parser.parse_args()
    
    # 解析输入
    params = {{}}
    if args.input_base64:
        try:
            params = json.loads(base64.b64decode(args.input_base64).decode('utf-8'))
        except Exception as e:
            print(json.dumps({{"success": False, "error": f"输入解析失败: {{e}}"}}))
            return
    
    if args.file_path:
        params['file_path'] = args.file_path
    
    try:
        # TODO: 实现核心逻辑
        result = {{
            "success": True,
            "data": None,
            "message": "处理完成"
        }}
    except Exception as e:
        result = {{"success": False, "error": str(e)}}
    
    print(json.dumps(result, ensure_ascii=False, indent=2))

if __name__ == '__main__':
    main()
'''


def normalize_name(name):
    """标准化技能名称为 kebab-case"""
    name = name.strip().lower()
    name = re.sub(r'[^a-z0-9]+', '-', name)
    name = name.strip('-')
    return re.sub(r'-{2,}', '-', name)


def title_case(name):
    """转换为标题格式"""
    return ' '.join(word.capitalize() for word in name.split('-'))


def get_unique_skill_name(base_name, output_path):
    """获取唯一的技能名称，如果存在则自动加数字后缀"""
    skill_dir = Path(output_path) / base_name
    
    if not skill_dir.exists():
        return base_name, skill_dir
    
    # 目录已存在，尝试添加数字后缀
    counter = 2
    while True:
        new_name = f"{base_name}-{counter}"
        skill_dir = Path(output_path) / new_name
        if not skill_dir.exists():
            return new_name, skill_dir
        counter += 1
        if counter > 100:  # 安全限制
            raise ValueError(f"无法创建唯一目录名，已尝试到 {base_name}-100")


def create_skill(skill_name, output_path, resources, skill_details=None):
    """创建技能目录和文件"""
    if skill_details is None:
        skill_details = {}

    # 获取唯一名称
    original_name = skill_name
    skill_name, skill_dir = get_unique_skill_name(skill_name, output_path)
    
    if skill_name != original_name:
        # 仅在调试时输出，防止干扰 JSON 输出
        pass
        # print(f"[注意] 目录 '{original_name}' 已存在，使用新名称: {skill_name}")
    
    # 创建目录
    skill_dir.mkdir(parents=True)
    # print(f"[OK] 创建目录: {skill_dir}")
    
    # 确定资源部分内容
    resources_section = ""
    if resources:
        if 'scripts' in resources:
            resources_section += "### scripts/\n可执行脚本（Python/Shell等）\n\n"
        if 'references' in resources:
            resources_section += "### references/\n参考文档\n\n"
        if 'assets' in resources:
            resources_section += "### assets/\n模板和资源文件\n\n"
    else:
        resources_section = "无额外资源"
    
    # 获取详细信息或默认值
    description_short = skill_details.get('description') or '[TODO: 说明技能功能和触发条件]'
    if len(description_short) > 100:
        description_long = description_short
        description_short = description_short[:100] + "..."
    else:
        description_long = description_short
        
    usage_guide = skill_details.get('usage') or '[TODO: 说明如何使用此技能，包括输入参数和期望输出]'
    workflow_steps = skill_details.get('workflow') or '[TODO: 分步骤说明技能执行流程]\n\n1. 步骤一\n2. 步骤二\n3. 步骤三'
    
    # 创建 SKILL.md
    skill_title = title_case(skill_name)
    skill_content = SKILL_TEMPLATE.format(
        skill_name=skill_name,
        skill_title=skill_title,
        description_short=description_short,
        description_long=description_long,
        usage_guide=usage_guide,
        workflow_steps=workflow_steps,
        resources_section=resources_section,
        timestamp=datetime.now().strftime('%Y-%m-%d %H:%M:%S')
    )
    (skill_dir / 'SKILL.md').write_text(skill_content, encoding='utf-8')
    # print("[OK] 创建 SKILL.md")
    
    # 创建资源目录
    for res in resources:
        res_dir = skill_dir / res
        res_dir.mkdir(exist_ok=True)
        # print(f"[OK] 创建 {res}/")
        
        # 如果是 scripts，创建 main.py 和 requirements.txt 模板
        if res == 'scripts':
            script_content = SCRIPT_TEMPLATE.format(skill_title=skill_title)
            (res_dir / 'main.py').write_text(script_content, encoding='utf-8')
            # print("[OK] 创建 scripts/main.py")
            
            # 创建 requirements.txt 占位符
            requirements_content = "# Python dependencies\n# 示例:\n# requests==2.31.0\n# pandas==2.1.0\n"
            (res_dir / 'requirements.txt').write_text(requirements_content, encoding='utf-8')
            # print("[OK] 创建 scripts/requirements.txt")
    
    # print(f"\n[完成] 技能 '{skill_name}' 已创建于 {skill_dir}")
    return True


def main():
    # 强制 stdout 使用 UTF-8 编码，防止 Windows 下乱码
    if sys.platform == 'win32':
        sys.stdout.reconfigure(encoding='utf-8')

    parser = argparse.ArgumentParser(description='创建新技能模板（自动保存到 wwwroot/skills/ 目录）')
    parser.add_argument('skill_name', nargs='?', default=None, help='技能名称（自动转为 kebab-case）')
    parser.add_argument('--path', default=None, help='输出目录（默认: wwwroot/skills/）')
    parser.add_argument('--resources', default='scripts', help='资源目录（逗号分隔: scripts,references,assets，默认: scripts）')
    # 支持 ScriptExecutorTool 的 --input-base64 参数
    parser.add_argument('--input-base64', dest='input_base64', help='Base64 编码的 JSON 输入')
    
    args = parser.parse_args()
    
    # 解析 --input-base64 参数（如果有）
    skill_name = args.skill_name
    resources_str = args.resources
    output_path_str = args.path
    skill_details = {}
    
    if args.input_base64:
        try:
            params = json.loads(base64.b64decode(args.input_base64).decode('utf-8'))
            
            # 从 JSON 中提取参数，支持多种命名风格
            # skill_name (Python风格) / skillName (驼峰) / name (通用) / requirement (从任务描述提取)
            skill_name = (
                params.get('skill_name') or 
                params.get('skillName') or 
                params.get('name') or 
                params.get('requirement') or 
                skill_name
            )
            
            resources_val = params.get('resources', resources_str)
            if isinstance(resources_val, list):
                # 如果是列表 ['scripts', 'assets'] -> 'scripts,assets'
                resources_str = ",".join([str(x) for x in resources_val])
            elif resources_val:
                resources_str = str(resources_val)
            
            output_path_str = params.get('path', output_path_str)
            
            # 提取额外的技能详情
            skill_details = {
                'description': params.get('description'),
                'usage': params.get('usage') or params.get('usage_guide'),
                'workflow': params.get('workflow') or params.get('workflow_steps')
            }
            
        except Exception as e:
            result = {"success": False, "error": f"解析 input-base64 失败: {e}"}
            print(json.dumps(result, ensure_ascii=False))
            sys.exit(1)
    
    # 验证技能名称
    if not skill_name:
        result = {"success": False, "error": "缺少技能名称参数 (skill_name 或 skillName)"}
        print(json.dumps(result, ensure_ascii=False))
        sys.exit(1)
    
    # 标准化参数
    skill_name = normalize_name(skill_name)
    resources = [r.strip() for r in resources_str.split(',') if r.strip()]
    
    # 默认路径处理
    if not output_path_str:
        output_path = SKILLS_ROOT
    else:
        output_path = Path(output_path_str)
        if not output_path.is_absolute():
            # 智能识别 "skills/" 路径，避免在 skill-creator 内部创建
            # 如果路径以 "skills" 开头，相对于 wwwroot (SKILLS_ROOT.parent) 解析
            if output_path_str.strip('/\\').startswith('skills'):
                output_path = SKILLS_ROOT.parent / output_path
            else:
                # 其他相对路径，相对于 SKILLS_ROOT 解析
                output_path = SKILLS_ROOT / output_path
    
    # 确保 scripts 始终存在
    if 'scripts' not in resources:
        resources.append('scripts')
        
    try:
        if not output_path.exists():
            output_path.mkdir(parents=True, exist_ok=True)
            
        create_skill(skill_name, output_path, resources, skill_details)
        
        # 返回 JSON 结果供调用方解析
        result = {
            "success": True, 
            "message": f"技能 {skill_name} 创建成功",
            "path": str(output_path / skill_name),
            "skill_name": skill_name
        }
        print(json.dumps(result, ensure_ascii=False))
        
    except Exception as e:
        result = {"success": False, "error": f"创建技能失败: {e}"}
        print(json.dumps(result, ensure_ascii=False))
        sys.exit(1)

if __name__ == '__main__':
    main()
