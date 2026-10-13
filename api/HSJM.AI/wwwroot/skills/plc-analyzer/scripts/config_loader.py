#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PLC程序分析引擎 - 配置加载器

负责加载和管理品牌配置文件
"""

import re
import sys
from pathlib import Path
from typing import Dict, Optional
import yaml


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
            
            # 对于 .scl/.awl 文件，检查 Siemens 特征
            if file_ext in ('.scl', '.awl'):
                siemens_signatures = ['S7_Optimized_Access', 'FUNCTION_BLOCK', 'FUNCTION', 
                                      'END_FUNCTION_BLOCK', 'END_FUNCTION', 'VAR_INPUT']
                for sig in siemens_signatures:
                    if sig in content_sample:
                        score += 0.15
            
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
