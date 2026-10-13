using System;
using System.Collections.Generic;
using System.Linq;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Services;
using Xunit;

namespace HSJM.AI.StopCauseDiagnosis.Tests
{
    /// <summary>
    /// FbLocalVariableConcretizer 纯函数单测（2026-10-07）。
    /// 场景数据取自泰兴项目实测链路：
    ///   FB150_OP1030_电芯上线扫码枪自动流程.I_Mode.DryRunMode
    ///     → FC150_Graph_Main 调用绑定 input_pins["I_Mode"]="Mode"（形参转发）
    ///     → FC100_M010_Main 调用绑定 input_pins["Mode"]="DB10050_Mode.M010_Mode"（全局真源）
    ///     ⇒ DB10050_Mode.M010_Mode.DryRunMode
    ///   FB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode
    ///     → ai_plc_dbfbinding 唯一实例 DB IDB150_OP1030_电芯上线扫码枪自动流程
    ///     ⇒ IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode
    /// </summary>
    public class FbLocalVariableConcretizerTests
    {
        private const string Fb150 = "FB150_OP1030_电芯上线扫码枪自动流程";
        private const string Fc150 = "FC150_Graph_Main";
        private const string Fc100 = "FC100_M010_Main";
        private const string Idb150 = "IDB150_OP1030_电芯上线扫码枪自动流程";
        private const string ModeSource = "DB10050_Mode.M010_Mode";

        private static FbLocalVariableConcretizer.CallSiteRecord Site(
            string caller,
            string callee,
            Dictionary<string, string>? input = null,
            Dictionary<string, string>? inout = null,
            string instanceDb = "")
            => new(caller, callee, instanceDb, "74", input, inout);

        [Fact]
        public void FormalStep_GlobalActual_ResolvesDirectly()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc100, Fc150, new Dictionary<string, string> { ["Mode"] = ModeSource }),
            };

            var step = FbLocalVariableConcretizer.ResolveFormalStep(sites, "Mode.DryRunMode");

            Assert.NotNull(step);
            Assert.Equal(ModeSource + ".DryRunMode", step!.ResolvedGlobal);
            Assert.Null(step.MoveToCaller);
        }

        [Fact]
        public void FormalStep_FormalActual_MovesUpToCaller()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc150, Fb150, new Dictionary<string, string> { ["I_Mode"] = "Mode" }),
            };

            var step = FbLocalVariableConcretizer.ResolveFormalStep(sites, "I_Mode.DryRunMode");

            Assert.NotNull(step);
            Assert.Null(step.ResolvedGlobal);
            Assert.Equal(Fc150, step!.MoveToCaller);
            Assert.Equal("Mode.DryRunMode", step.MoveNextRemaining);
        }

        [Fact]
        public void FormalChain_TwoSteps_ResolvesToGlobalSource()
        {
            // 第一步：FB150.I_Mode → FC150 的形参 Mode
            var step1 = FbLocalVariableConcretizer.ResolveFormalStep(
                new List<FbLocalVariableConcretizer.CallSiteRecord>
                {
                    Site(Fc150, Fb150, new Dictionary<string, string> { ["I_Mode"] = "Mode" }),
                },
                "I_Mode.DryRunMode");
            Assert.NotNull(step1);
            Assert.Null(step1!.ResolvedGlobal);

            // 第二步：FC150.Mode → FC100 给的 DB10050_Mode.M010_Mode
            var step2 = FbLocalVariableConcretizer.ResolveFormalStep(
                new List<FbLocalVariableConcretizer.CallSiteRecord>
                {
                    Site(Fc100, Fc150, new Dictionary<string, string> { ["Mode"] = ModeSource }),
                },
                step1.MoveNextRemaining!);
            Assert.NotNull(step2);
            Assert.Equal(ModeSource + ".DryRunMode", step2!.ResolvedGlobal);
        }

        [Fact]
        public void FormalStep_ConflictingActuals_ReturnsNull()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc100, Fb150, new Dictionary<string, string> { ["I_Mode"] = "DB10050_Mode.M010_Mode" }),
                Site(Fc150, Fb150, new Dictionary<string, string> { ["I_Mode"] = "DB10050_Mode.M020_Mode" }),
            };

            Assert.Null(FbLocalVariableConcretizer.ResolveFormalStep(sites, "I_Mode.DryRunMode"));
        }

        [Fact]
        public void FormalStep_UnboundPin_ReturnsNull()
        {
            // STAT 场景：S_AutoRunMode 不在任何 input/inout 绑定里
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc150, Fb150, new Dictionary<string, string> { ["S_SEL"] = "M010_1_HMI.GraphMode[1].PB.I_SelectStepNo" }),
            };

            Assert.Null(FbLocalVariableConcretizer.ResolveFormalStep(sites, "S_AutoRunMode"));
        }

        [Fact]
        public void FormalStep_LiteralActual_ReturnsNull()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc150, Fb150, new Dictionary<string, string> { ["SW_TOP"] = "0" }),
            };

            Assert.Null(FbLocalVariableConcretizer.ResolveFormalStep(sites, "SW_TOP"));
        }

        [Fact]
        public void FormalStep_AbsoluteAddress_ScalarOnly()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site(Fc150, Fb150, new Dictionary<string, string> { ["Enable"] = "%M0.0" }),
            };

            var ok = FbLocalVariableConcretizer.ResolveFormalStep(sites, "Enable");
            Assert.NotNull(ok);
            Assert.Equal("%M0.0", ok!.ResolvedGlobal);

            // 标量直连但局部路径还有成员 → 类型不匹配，不可解析
            Assert.Null(FbLocalVariableConcretizer.ResolveFormalStep(sites, "Enable.Sub"));
        }

        [Fact]
        public void FormalStep_MultipleDistinctCallers_ReturnsNull()
        {
            var sites = new List<FbLocalVariableConcretizer.CallSiteRecord>
            {
                Site("FC100_M010_Main", Fc150, new Dictionary<string, string> { ["Mode"] = "Mode" }),
                Site("FC200_M020_Main", Fc150, new Dictionary<string, string> { ["Mode"] = "Mode" }),
            };

            Assert.Null(FbLocalVariableConcretizer.ResolveFormalStep(sites, "Mode.Reset"));
        }

        [Fact]
        public void ParseCallSitesJson_FiltersCalleeAndParsesPins()
        {
            // 形状取自 FC150_Graph_Main 实测 callpinbindingsjson
            var json = "[{\"callee_name\":\"" + Fb150 + "\",\"callee_block_type\":\"FB\","
                       + "\"instance_db\":\"" + Idb150 + "\",\"call_uid\":\"74\","
                       + "\"input_pins\":{\"S_SEL\":\"M010_1_HMI.GraphMode[1].PB.I_SelectStepNo\","
                       + "\"S_ON\":\"M010_1_HMI.GraphMode[1].PB.I_ActivationStep\","
                       + "\"T_PUSH\":\"M010_1_HMI.GraphMode[1].PB.I_NextStep\","
                       + "\"I_Mode\":\"Mode\","
                       + "\"I_SingleStationDebug\":\"M010_1_HMI.GraphMode[1].PB.I_SingleStationDebug\"},"
                       + "\"output_pins\":{},\"inout_pins\":{}}]";

            var sites = FbLocalVariableConcretizer.ParseCallSitesJson(json, Fb150, Fc150);

            var site = Assert.Single(sites);
            Assert.Equal(Fc150, site.CallerBlockName);
            Assert.Equal(Idb150, site.InstanceDb);
            Assert.Equal("Mode", site.InputPins!["I_Mode"]);
        }

        [Fact]
        public void ParseCallSitesJson_BadJson_ReturnsEmpty()
        {
            Assert.Empty(FbLocalVariableConcretizer.ParseCallSitesJson("{not-json", Fb150, Fc150));
            Assert.Empty(FbLocalVariableConcretizer.ParseCallSitesJson(null, Fb150, Fc150));
        }

        [Fact]
        public void InstanceDbPathExists_MatchesStaticPrefixAndArrayExpansion()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "S_AutoRunMode",
                "Static.S_On",
                "Sensor_On.Sensor_On[37]",
            };

            Assert.True(FbLocalVariableConcretizer.InstanceDbPathExists(paths, "S_AutoRunMode"));
            Assert.True(FbLocalVariableConcretizer.InstanceDbPathExists(paths, "S_On"));          // Static. 前缀
            Assert.True(FbLocalVariableConcretizer.InstanceDbPathExists(paths, "Sensor_On[37]")); // 数组展开
            Assert.False(FbLocalVariableConcretizer.InstanceDbPathExists(paths, "S_ManualMode"));
        }

        [Fact]
        public void ExpandArrayPath_ShortFormToHierarchyForm()
        {
            Assert.Equal("Sensor_On.Sensor_On[37]", FbLocalVariableConcretizer.ExpandArrayPath("Sensor_On[37]"));
            Assert.Equal("V90.V90[1].CTRL_IN.X", FbLocalVariableConcretizer.ExpandArrayPath("V90[1].CTRL_IN.X"));
            Assert.Equal("Plain.Path", FbLocalVariableConcretizer.ExpandArrayPath("Plain.Path"));
        }

        [Fact]
        public void IsLiteralActual_DetectsConstants()
        {
            Assert.True(FbLocalVariableConcretizer.IsLiteralActual("0"));
            Assert.True(FbLocalVariableConcretizer.IsLiteralActual("true"));
            Assert.True(FbLocalVariableConcretizer.IsLiteralActual("'ENUM#A'"));
            Assert.False(FbLocalVariableConcretizer.IsLiteralActual("Mode"));
            Assert.False(FbLocalVariableConcretizer.IsLiteralActual(ModeSource));
        }
    }
}
