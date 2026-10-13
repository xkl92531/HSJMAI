# -*- coding: utf-8 -*-
"""Transition 解析的 variable_scope 回归测试（2026-10-07）

回归背景：_parse_graph_transition 此前把 _build_uid_symbol_map 返回的 uid_scope_map
丢弃（`uid_symbol_map, _ = ...`），导致 GRAPH 转换条件里 FB 接口变量
（#S_AutoRunMode / #I_Mode.DryRunMode）入库 ai_plc_variableaccess.variable_scope 全空
（实测泰兴项目全库 1060 行 TRANSITION 访问行 scope 为空），下游无法区分局部形参与全局变量。
"""
import os
import sys
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from parser import PLCAnalyzerEngine  # noqa: E402


def make_engine():
    # _parse_graph_transition 依赖的方法均为无状态方法，
    # 跳过 __init__（其需要 ConfigLoader 读品牌配置文件）
    return object.__new__(PLCAnalyzerEngine)


# 结构取自 FB150_OP1030_电芯上线扫码枪自动流程 T37「传感器判断」的真实 XML（简化）
TRANSITION_XML = """<Transition IsMissing="false" Name="传感器判断" Number="37" ProgrammingLanguage="LAD">
  <FlgNet>
    <Parts>
      <Access Scope="LocalVariable" UId="21">
        <Symbol><Component Name="S_AutoRunMode" /></Symbol>
      </Access>
      <Access Scope="GlobalVariable" UId="22">
        <Symbol>
          <Component Name="M010_2_Action" />
          <Component Name="Sensor_On" AccessModifier="Array">
            <Access Scope="LiteralConstant" UId="23">
              <Constant><ConstantValue>37</ConstantValue></Constant>
            </Access>
          </Component>
        </Symbol>
      </Access>
      <Access Scope="LocalVariable" UId="28">
        <Symbol><Component Name="I_Mode" /><Component Name="DryRunMode" /></Symbol>
      </Access>
      <Part Name="Contact" UId="29" />
      <Part Name="Contact" UId="30" />
      <Part Name="Contact" UId="31" />
    </Parts>
    <Wires>
      <Wire UId="40"><NameCon UId="29" Name="operand" /><IdentCon UId="21" /></Wire>
      <Wire UId="41"><NameCon UId="30" Name="operand" /><IdentCon UId="22" /></Wire>
      <Wire UId="42"><NameCon UId="31" Name="operand" /><IdentCon UId="28" /></Wire>
    </Wires>
  </FlgNet>
</Transition>"""


class TransitionScopeTests(unittest.TestCase):
    def setUp(self):
        self.engine = make_engine()
        trans = ET.fromstring(TRANSITION_XML)
        self.unit = self.engine._parse_graph_transition(trans, 37, "传感器判断")

    def test_unit_type_is_transition(self):
        self.assertIsNotNone(self.unit)
        self.assertEqual(self.unit.unit_type, "TRANSITION")
        self.assertEqual(self.unit.unit_id, 37)

    def test_variable_access_carries_scope(self):
        scopes = {va.variable: va.variable_scope for va in self.unit.variable_access}
        self.assertEqual(scopes.get("S_AutoRunMode"), "LocalVariable",
                         "FB STAT 镜像位应标记 LocalVariable")
        self.assertEqual(scopes.get("I_Mode.DryRunMode"), "LocalVariable",
                         "FB 形参成员访问应标记 LocalVariable")
        self.assertEqual(scopes.get("M010_2_Action.Sensor_On[37]"), "GlobalVariable",
                         "全局 DB 变量应标记 GlobalVariable")

    def test_uid_symbol_map_names(self):
        # 名字本身不带 #、不带实例 DB 前缀（转换在下游具体化器做，解析层只保真）
        self.assertEqual(self.unit.uid_symbol_map.get("21"), "S_AutoRunMode")
        self.assertEqual(self.unit.uid_symbol_map.get("28"), "I_Mode.DryRunMode")
        self.assertEqual(self.unit.uid_symbol_map.get("22"), "M010_2_Action.Sensor_On[37]")


if __name__ == "__main__":
    unittest.main()
