#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC分析数据持久化模块

将engine.py提取的JSON数据持久化到数据库
支持：
1. 直接从JSON导入
2. 从文件导入（先调用engine提取）
3. 批量导入目录下所有PLC文件

使用:
    python persist.py --file <PLC文件> --db-conn <连接串>
    python persist.py --json <JSON文件> --db-conn <连接串>
    python persist.py --dir <目录> --db-conn <连接串>
"""

import os
import sys
import json
import hashlib
import argparse
from pathlib import Path
from typing import Dict, List, Optional
from datetime import datetime

# 导入解析引擎 - 使用延迟导入避免循环导入
# from engine import PLCAnalyzerEngine, ConfigLoader

# 其他函数保持不变...
def load_plc_data(file_path: str) -> Dict:
    """从文件加载PLC数据"""
    with open(file_path, 'r', encoding='utf-8') as f:
        return json.load(f)

def save_to_json(data: Dict, output_file: str):
    """保存数据到JSON文件"""
    with open(output_file, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

# 其余代码保持不变...