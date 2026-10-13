# -*- coding: utf-8 -*-
"""udt_index.json 查找分支的回归测试。

运行: cd api/HSJM.AI/wwwroot/skills/plc-analyzer/scripts && python -m pytest test_udt_index.py -v
"""
import json
import os
import tempfile

from parser import PLCAnalyzerEngine


def _make_udt_dir(tmp, index=None, files=()):
    d = os.path.join(tmp, 'udt')
    os.makedirs(d, exist_ok=True)
    for fn in files:
        path = os.path.join(d, fn)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as f:
            f.write('<Document />')
    if index is not None:
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            json.dump({'types': index}, f, ensure_ascii=False)
    return d


def test_index_resolves_name_that_does_not_match_the_file_name():
    """类型名含斜杠时文件名被清洗成下划线，只有索引能把两者对上。"""
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index={'A/B': 'A_B.xml'}, files=['A_B.xml'])
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'A/B') == os.path.join(d, 'A_B.xml')


def test_index_supports_nested_group_directories():
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index={'T': 'Sub/T.xml'}, files=[os.path.join('Sub', 'T.xml')])
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') == os.path.join(d, 'Sub', 'T.xml')


def test_returns_none_when_index_is_absent_so_old_packages_keep_working():
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_type_is_not_in_the_index():
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index={'Other': 'Other.xml'}, files=['Other.xml'])
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_the_indexed_file_is_missing():
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index={'T': 'gone.xml'}, files=[])
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_broken_index_is_ignored_rather_than_raising():
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            f.write('{not json')
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_index_json_is_top_level_null():
    """有效 JSON 但形状错误（顶级 null）应被视为无索引。"""
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            f.write('null')
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_index_json_is_top_level_array():
    """有效 JSON 但形状错误（顶级数组）应被视为无索引。"""
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            f.write('[]')
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_index_json_is_top_level_string():
    """有效 JSON 但形状错误（顶级字符串）应被视为无索引。"""
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            f.write('"invalid"')
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_returns_none_when_index_json_is_top_level_number():
    """有效 JSON 但形状错误（顶级数字）应被视为无索引。"""
    with tempfile.TemporaryDirectory() as tmp:
        d = _make_udt_dir(tmp, index=None, files=['T.xml'])
        with open(os.path.join(d, 'udt_index.json'), 'w', encoding='utf-8') as f:
            f.write('0')
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'T') is None


def test_index_resolves_when_file_has_a_utf8_bom():
    """导出端用 UTF8Encoding(false) 写 udt_index.json（无 BOM）；
    这条测试防回归——万一将来又带上了 BOM，索引仍必须能被解析（utf-8-sig 兼容有无 BOM）。
    plain 'utf-8' 编解码器不会剥离 BOM，会让 json.load 抛 JSONDecodeError，
    被 except (OSError, ValueError) 吞掉后整个索引静默失效，回退到按文件名匹配。
    """
    with tempfile.TemporaryDirectory() as tmp:
        d = os.path.join(tmp, 'udt')
        os.makedirs(d, exist_ok=True)
        with open(os.path.join(d, 'A_B.xml'), 'w', encoding='utf-8') as f:
            f.write('<Document />')
        index_path = os.path.join(d, 'udt_index.json')
        payload = json.dumps({'types': {'A/B': 'A_B.xml'}}, ensure_ascii=False)
        with open(index_path, 'w', encoding='utf-8-sig') as f:
            f.write(payload)
        engine = PLCAnalyzerEngine()
        assert engine._udt_index_lookup(d, 'A/B') == os.path.join(d, 'A_B.xml')
