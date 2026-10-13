using HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HSJM.AI.Examples
{
    /// <summary>
    /// 西门子Transition XML解析示例
    /// </summary>
    public class SiemensTransitionParserExample
    {
        private readonly SiemensTransitionParser _parser;

        public SiemensTransitionParserExample()
        {
            _parser = new SiemensTransitionParser();
        }

        /// <summary>
        /// 示例1：简单的温度比较
        /// </summary>
        public void Example1_SimpleTemperatureCheck()
        {
            Console.WriteLine("=== 示例1：简单温度比较 ===\n");

            var transitionXml = @"
<Transition>
  <Parts>
    <Part Name='Temperature_Check' Type='GE'>
      <Input Name='IN1' Variable='DB1.DBD0'/>
      <Input Name='IN2' Constant='100.0'/>
    </Part>
  </Parts>
</Transition>";

            // 场景A：温度足够
            var actualValues1 = new Dictionary<string, string>
            {
                { "DB1.DBD0", "105.5" }
            };

            var result1 = _parser.ParseAndEvaluate(transitionXml, actualValues1);
            PrintResult("温度105.5度（足够）", result1);

            // 场景B：温度不足
            var actualValues2 = new Dictionary<string, string>
            {
                { "DB1.DBD0", "95.5" }
            };

            var result2 = _parser.ParseAndEvaluate(transitionXml, actualValues2);
            PrintResult("温度95.5度（不足）", result2);
        }

        /// <summary>
        /// 示例2：多条件组合（AND逻辑）
        /// </summary>
        public void Example2_MultipleConditions()
        {
            Console.WriteLine("\n=== 示例2：多条件组合 ===\n");

            var transitionXml = @"
<Transition>
  <Parts>
    <!-- 温度检查 -->
    <Part Name='Temp_Check' Type='GT'>
      <Input Name='IN1' Variable='DB1.DBD0'/>
      <Input Name='IN2' Constant='80.0'/>
    </Part>
    
    <!-- 压力范围检查 -->
    <Part Name='Pressure_Min' Type='GE'>
      <Input Name='IN1' Variable='DB2.DBW10'/>
      <Input Name='IN2' Constant='100'/>
    </Part>
    <Part Name='Pressure_Max' Type='LE'>
      <Input Name='IN1' Variable='DB2.DBW10'/>
      <Input Name='IN2' Constant='200'/>
    </Part>
    
    <!-- 启动信号 -->
    <Part Name='Start_Signal' Type='CONTACT'>
      <Input Name='Variable' Variable='M0.0'/>
    </Part>
  </Parts>
</Transition>";

            // 场景A：所有条件满足
            var actualValues1 = new Dictionary<string, string>
            {
                { "DB1.DBD0", "85.5" },
                { "DB2.DBW10", "150" },
                { "M0.0", "true" }
            };

            var result1 = _parser.ParseAndEvaluate(transitionXml, actualValues1);
            PrintResult("所有条件满足", result1);

            // 场景B：温度不足
            var actualValues2 = new Dictionary<string, string>
            {
                { "DB1.DBD0", "75.0" },
                { "DB2.DBW10", "150" },
                { "M0.0", "true" }
            };

            var result2 = _parser.ParseAndEvaluate(transitionXml, actualValues2);
            PrintResult("温度不足", result2);

            // 场景C：压力过高
            var actualValues3 = new Dictionary<string, string>
            {
                { "DB1.DBD0", "85.5" },
                { "DB2.DBW10", "250" },
                { "M0.0", "true" }
            };

            var result3 = _parser.ParseAndEvaluate(transitionXml, actualValues3);
            PrintResult("压力过高", result3);
        }

        /// <summary>
        /// 示例3：布尔触点条件
        /// </summary>
        public void Example3_BooleanContacts()
        {
            Console.WriteLine("\n=== 示例3：布尔触点 ===\n");

            var transitionXml = @"
<Transition>
  <Parts>
    <!-- 启动按钮必须为true -->
    <Part Name='Start_Button' Type='CONTACT'>
      <Input Name='Variable' Variable='M0.0'/>
    </Part>
    
    <!-- 停止按钮必须为false -->
    <Part Name='Stop_Button' Type='NOT'>
      <Input Name='Variable' Variable='M0.1'/>
    </Part>
    
    <!-- 急停按钮必须为false -->
    <Part Name='Emergency_Stop' Type='NOT'>
      <Input Name='Variable' Variable='M0.2'/>
    </Part>
  </Parts>
</Transition>";

            // 场景A：可以启动
            var actualValues1 = new Dictionary<string, string>
            {
                { "M0.0", "true" },
                { "M0.1", "false" },
                { "M0.2", "false" }
            };

            var result1 = _parser.ParseAndEvaluate(transitionXml, actualValues1);
            PrintResult("可以启动", result1);

            // 场景B：急停触发
            var actualValues2 = new Dictionary<string, string>
            {
                { "M0.0", "true" },
                { "M0.1", "false" },
                { "M0.2", "true" }  // 急停触发
            };

            var result2 = _parser.ParseAndEvaluate(transitionXml, actualValues2);
            PrintResult("急停触发", result2);
        }

        /// <summary>
        /// 示例4：缺少变量值
        /// </summary>
        public void Example4_MissingVariables()
        {
            Console.WriteLine("\n=== 示例4：缺少变量值 ===\n");

            var transitionXml = @"
<Transition>
  <Parts>
    <Part Name='Temp_Check' Type='GE'>
      <Input Name='IN1' Variable='DB1.DBD0'/>
      <Input Name='IN2' Constant='100.0'/>
    </Part>
    <Part Name='Pressure_Check' Type='GT'>
      <Input Name='IN1' Variable='DB2.DBW10'/>
      <Input Name='IN2' Constant='100'/>
    </Part>
  </Parts>
</Transition>";

            // 只提供了温度值，缺少压力值
            var actualValues = new Dictionary<string, string>
            {
                { "DB1.DBD0", "105.5" }
                // DB2.DBW10 缺失
            };

            var result = _parser.ParseAndEvaluate(transitionXml, actualValues);
            PrintResult("缺少压力值", result);
        }

        /// <summary>
        /// 示例5：梯形图格式（Contact元素）
        /// </summary>
        public void Example5_LadderLogicFormat()
        {
            Console.WriteLine("\n=== 示例5：梯形图格式 ===\n");

            var transitionXml = @"
<Transition>
  <Contact Variable='M0.0' Negated='false'/>
  <Contact Variable='M0.1' Negated='true'/>
  <Compare Operator='GT'>
    <Operand1>DB1.DBD0</Operand1>
    <Operand2>75.5</Operand2>
  </Compare>
</Transition>";

            var actualValues = new Dictionary<string, string>
            {
                { "M0.0", "true" },
                { "M0.1", "false" },
                { "DB1.DBD0", "85.5" }
            };

            var result = _parser.ParseAndEvaluate(transitionXml, actualValues);
            PrintResult("梯形图格式", result);
        }

        /// <summary>
        /// 示例6：等值比较
        /// </summary>
        public void Example6_EqualityComparison()
        {
            Console.WriteLine("\n=== 示例6：等值比较 ===\n");

            var transitionXml = @"
<Transition>
  <Parts>
    <!-- 检查模式是否为自动模式(1) -->
    <Part Name='Mode_Check' Type='EQ'>
      <Input Name='IN1' Variable='DB1.DBW0'/>
      <Input Name='IN2' Constant='1'/>
    </Part>
    
    <!-- 检查速度是否不等于0 -->
    <Part Name='Speed_Check' Type='NE'>
      <Input Name='IN1' Variable='DB1.DBD10'/>
      <Input Name='IN2' Constant='0'/>
    </Part>
  </Parts>
</Transition>";

            // 场景A：自动模式，速度50
            var actualValues1 = new Dictionary<string, string>
            {
                { "DB1.DBW0", "1" },
                { "DB1.DBD10", "50.0" }
            };

            var result1 = _parser.ParseAndEvaluate(transitionXml, actualValues1);
            PrintResult("自动模式，速度50", result1);

            // 场景B：手动模式（模式=0）
            var actualValues2 = new Dictionary<string, string>
            {
                { "DB1.DBW0", "0" },
                { "DB1.DBD10", "50.0" }
            };

            var result2 = _parser.ParseAndEvaluate(transitionXml, actualValues2);
            PrintResult("手动模式", result2);

            // 场景C：自动模式，但速度为0
            var actualValues3 = new Dictionary<string, string>
            {
                { "DB1.DBW0", "1" },
                { "DB1.DBD10", "0" }
            };

            var result3 = _parser.ParseAndEvaluate(transitionXml, actualValues3);
            PrintResult("速度为0", result3);
        }

        /// <summary>
        /// 打印解析结果
        /// </summary>
        private void PrintResult(string scenario, TransitionParseResult result)
        {
            Console.WriteLine($"场景: {scenario}");
            Console.WriteLine($"解析成功: {result.Success}");

            if (result.Success)
            {
                var passedSymbol = result.IsPassed ? "√ 是" : "× 否";
                Console.WriteLine($"条件通过: {passedSymbol}");
                Console.WriteLine($"找到条件数: {result.Conditions.Count}");

                if (result.EvaluationDetails.Any())
                {
                    Console.WriteLine("详细结果:");
                    foreach (var detail in result.EvaluationDetails)
                    {
                        Console.WriteLine($"  {detail}");
                    }
                }

                if (result.MissingVariables.Any())
                {
                    Console.WriteLine($"[警告] 缺少变量: {string.Join(", ", result.MissingVariables)}");
                }
            }
            else
            {
                Console.WriteLine($"[错误] {result.ErrorMessage}");
            }

            Console.WriteLine(new string('-', 60));
            Console.WriteLine();
        }

        /// <summary>
        /// 运行所有示例
        /// </summary>
        public void RunAllExamples()
        {
            Console.WriteLine("===================================================================");
            Console.WriteLine("   西门子Transition XML解析器 - 示例集合");
            Console.WriteLine("===================================================================\n");

            try
            {
                Example1_SimpleTemperatureCheck();
                Example2_MultipleConditions();
                Example3_BooleanContacts();
                Example4_MissingVariables();
                Example5_LadderLogicFormat();
                Example6_EqualityComparison();

                Console.WriteLine("\n[成功] 所有示例运行完成！");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[失败] 示例运行失败: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
