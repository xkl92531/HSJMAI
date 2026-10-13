using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace HSJM.AI.AppLayer.DiagnosticStrategyFactory.Services
{
    /// <summary>
    /// 西门子Transition条件解析器
    /// 解析TransitionInfo中的西门子XML格式，提取逻辑条件并计算结果
    /// </summary>
    public class SiemensTransitionParser
    {
        /// <summary>
        /// 解析Transition XML并计算条件结果
        /// </summary>
        /// <param name="transitionXml">Transition XML内容</param>
        /// <param name="actualValues">实际变量值字典</param>
        /// <returns>解析结果</returns>
        public TransitionParseResult ParseAndEvaluate(string transitionXml, Dictionary<string, string> actualValues)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(transitionXml))
                {
                    return new TransitionParseResult
                    {
                        Success = false,
                        ErrorMessage = "Transition XML为空"
                    };
                }

                // 解析XML
                XDocument doc;
                try
                {
                    doc = XDocument.Parse(transitionXml);
                }
                catch (Exception ex)
                {
                    return new TransitionParseResult
                    {
                        Success = false,
                        ErrorMessage = $"XML解析失败: {ex.Message}"
                    };
                }

                // 提取条件表达式
                var conditions = ExtractConditions(doc);
                if (conditions.Count == 0)
                {
                    return new TransitionParseResult
                    {
                        Success = false,
                        ErrorMessage = "未找到任何条件表达式"
                    };
                }

                // 计算条件结果
                var evaluationResult = EvaluateConditions(conditions, actualValues);

                return new TransitionParseResult
                {
                    Success = true,
                    IsPassed = evaluationResult.Result,
                    Conditions = conditions,
                    EvaluationDetails = evaluationResult.Details,
                    MissingVariables = evaluationResult.MissingVariables
                };
            }
            catch (Exception ex)
            {
                return new TransitionParseResult
                {
                    Success = false,
                    ErrorMessage = $"解析过程异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 从XML中提取条件表达式
        /// </summary>
        private List<ConditionExpression> ExtractConditions(XDocument doc)
        {
            var conditions = new List<ConditionExpression>();

            // 查找所有逻辑元件
            var parts = doc.Descendants("Part");
            foreach (var part in parts)
            {
                var type = part.Attribute("Type")?.Value;
                var name = part.Attribute("Name")?.Value;

                if (string.IsNullOrEmpty(type))
                    continue;

                var condition = new ConditionExpression
                {
                    Name = name,
                    Type = type,
                    Inputs = new Dictionary<string, string>()
                };

                // 提取输入
                var inputs = part.Elements("Input");
                foreach (var input in inputs)
                {
                    var inputName = input.Attribute("Name")?.Value;
                    var variable = input.Attribute("Variable")?.Value;
                    var constant = input.Attribute("Constant")?.Value;

                    if (!string.IsNullOrEmpty(inputName))
                    {
                        condition.Inputs[inputName] = variable ?? constant ?? "";
                    }
                }

                conditions.Add(condition);
            }

            // 如果没有找到Part元素，尝试查找其他常见格式
            if (conditions.Count == 0)
            {
                conditions.AddRange(ExtractLadderLogicConditions(doc));
            }

            return conditions;
        }

        /// <summary>
        /// 提取梯形图逻辑条件
        /// </summary>
        private List<ConditionExpression> ExtractLadderLogicConditions(XDocument doc)
        {
            var conditions = new List<ConditionExpression>();

            // 查找梯形图中的触点（Contact）
            var contacts = doc.Descendants("Contact");
            foreach (var contact in contacts)
            {
                var variable = contact.Attribute("Variable")?.Value;
                var negated = contact.Attribute("Negated")?.Value == "true";

                if (!string.IsNullOrEmpty(variable))
                {
                    conditions.Add(new ConditionExpression
                    {
                        Name = variable,
                        Type = negated ? "NOT" : "CONTACT",
                        Inputs = new Dictionary<string, string>
                        {
                            { "Variable", variable }
                        }
                    });
                }
            }

            // 查找比较指令（Compare）
            var compares = doc.Descendants("Compare");
            foreach (var compare in compares)
            {
                var operation = compare.Attribute("Operator")?.Value ?? "EQ";
                var operand1 = compare.Element("Operand1")?.Value;
                var operand2 = compare.Element("Operand2")?.Value;

                conditions.Add(new ConditionExpression
                {
                    Name = $"{operand1}_{operation}_{operand2}",
                    Type = operation,
                    Inputs = new Dictionary<string, string>
                    {
                        { "IN1", operand1 ?? "" },
                        { "IN2", operand2 ?? "" }
                    }
                });
            }

            return conditions;
        }

        /// <summary>
        /// 计算条件表达式
        /// </summary>
        private EvaluationResult EvaluateConditions(
            List<ConditionExpression> conditions,
            Dictionary<string, string> actualValues)
        {
            var details = new List<string>();
            var missingVariables = new List<string>();
            bool overallResult = true;

            foreach (var condition in conditions)
            {
                var result = EvaluateSingleCondition(condition, actualValues, out var detail, out var missing);
                
                details.Add(detail);
                missingVariables.AddRange(missing);

                // 默认使用AND逻辑组合所有条件
                overallResult = overallResult && result;
            }

            return new EvaluationResult
            {
                Result = overallResult,
                Details = details,
                MissingVariables = missingVariables.Distinct().ToList()
            };
        }

        /// <summary>
        /// 计算单个条件
        /// </summary>
        private bool EvaluateSingleCondition(
            ConditionExpression condition,
            Dictionary<string, string> actualValues,
            out string detail,
            out List<string> missingVariables)
        {
            missingVariables = new List<string>();
            detail = $"[{condition.Type}] {condition.Name}: ";

            try
            {
                switch (condition.Type?.ToUpper())
                {
                    case "GT":  // Greater Than (>)
                    case "GE":  // Greater or Equal (>=)
                    case "LT":  // Less Than (<)
                    case "LE":  // Less or Equal (<=)
                    case "EQ":  // Equal (==)
                    case "NE":  // Not Equal (!=)
                        return EvaluateComparisonCondition(condition, actualValues, out detail, out missingVariables);

                    case "AND":
                    case "OR":
                    case "XOR":
                        return EvaluateLogicalCondition(condition, actualValues, out detail, out missingVariables);

                    case "NOT":
                        return EvaluateNotCondition(condition, actualValues, out detail, out missingVariables);

                    case "CONTACT":
                        return EvaluateContactCondition(condition, actualValues, out detail, out missingVariables);

                    default:
                        detail += $"未知类型 '{condition.Type}'";
                        return false;
                }
            }
            catch (Exception ex)
            {
                detail += $"计算异常: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 计算比较条件
        /// </summary>
        private bool EvaluateComparisonCondition(
            ConditionExpression condition,
            Dictionary<string, string> actualValues,
            out string detail,
            out List<string> missingVariables)
        {
            missingVariables = new List<string>();

            var in1 = condition.Inputs.GetValueOrDefault("IN1", "");
            var in2 = condition.Inputs.GetValueOrDefault("IN2", "");

            // 获取实际值
            var value1 = GetVariableValue(in1, actualValues, out var missing1);
            var value2 = GetVariableValue(in2, actualValues, out var missing2);

            if (missing1 != null) missingVariables.Add(missing1);
            if (missing2 != null) missingVariables.Add(missing2);

            if (missingVariables.Any())
            {
                detail = $"[{condition.Type}] {in1} vs {in2}: 缺少变量值 [{string.Join(", ", missingVariables)}]";
                return false;
            }

            // 尝试转换为数字进行比较
            if (double.TryParse(value1, out var num1) && double.TryParse(value2, out var num2))
            {
                bool result = condition.Type?.ToUpper() switch
                {
                    "GT" => num1 > num2,
                    "GE" => num1 >= num2,
                    "LT" => num1 < num2,
                    "LE" => num1 <= num2,
                    "EQ" => Math.Abs(num1 - num2) < 0.0001,
                    "NE" => Math.Abs(num1 - num2) >= 0.0001,
                    _ => false
                };

                var symbol = condition.Type?.ToUpper() switch
                {
                    "GT" => ">",
                    "GE" => ">=",
                    "LT" => "<",
                    "LE" => "<=",
                    "EQ" => "==",
                    "NE" => "!=",
                    _ => "?"
                };

                detail = $"[{condition.Type}] {in1}({num1}) {symbol} {in2}({num2}) = {result}";
                return result;
            }
            else
            {
                // 字符串比较
                bool result = condition.Type?.ToUpper() switch
                {
                    "EQ" => value1 == value2,
                    "NE" => value1 != value2,
                    _ => false
                };

                detail = $"[{condition.Type}] '{in1}'({value1}) vs '{in2}'({value2}) = {result}";
                return result;
            }
        }

        /// <summary>
        /// 计算逻辑条件（AND/OR/XOR）
        /// </summary>
        private bool EvaluateLogicalCondition(
            ConditionExpression condition,
            Dictionary<string, string> actualValues,
            out string detail,
            out List<string> missingVariables)
        {
            missingVariables = new List<string>();

            var inputs = condition.Inputs.Values.ToList();
            var results = new List<bool>();

            foreach (var input in inputs)
            {
                var value = GetVariableValue(input, actualValues, out var missing);
                if (missing != null)
                {
                    missingVariables.Add(missing);
                    continue;
                }

                if (bool.TryParse(value, out var boolValue))
                {
                    results.Add(boolValue);
                }
            }

            if (missingVariables.Any())
            {
                detail = $"[{condition.Type}] 缺少变量值 [{string.Join(", ", missingVariables)}]";
                return false;
            }

            bool result = condition.Type?.ToUpper() switch
            {
                "AND" => results.All(r => r),
                "OR" => results.Any(r => r),
                "XOR" => results.Count(r => r) == 1,
                _ => false
            };

            detail = $"[{condition.Type}] {string.Join($" {condition.Type} ", results)} = {result}";
            return result;
        }

        /// <summary>
        /// 计算NOT条件
        /// </summary>
        private bool EvaluateNotCondition(
            ConditionExpression condition,
            Dictionary<string, string> actualValues,
            out string detail,
            out List<string> missingVariables)
        {
            missingVariables = new List<string>();

            var variable = condition.Inputs.GetValueOrDefault("Variable", "");
            var value = GetVariableValue(variable, actualValues, out var missing);

            if (missing != null)
            {
                missingVariables.Add(missing);
                detail = $"[NOT] {variable}: 缺少变量值";
                return false;
            }

            if (bool.TryParse(value, out var boolValue))
            {
                var result = !boolValue;
                detail = $"[NOT] {variable}({boolValue}) = {result}";
                return result;
            }

            detail = $"[NOT] {variable}: 无法解析为布尔值";
            return false;
        }

        /// <summary>
        /// 计算触点条件
        /// </summary>
        private bool EvaluateContactCondition(
            ConditionExpression condition,
            Dictionary<string, string> actualValues,
            out string detail,
            out List<string> missingVariables)
        {
            missingVariables = new List<string>();

            var variable = condition.Inputs.GetValueOrDefault("Variable", "");
            var value = GetVariableValue(variable, actualValues, out var missing);

            if (missing != null)
            {
                missingVariables.Add(missing);
                detail = $"[CONTACT] {variable}: 缺少变量值";
                return false;
            }

            if (bool.TryParse(value, out var boolValue))
            {
                detail = $"[CONTACT] {variable} = {boolValue}";
                return boolValue;
            }

            detail = $"[CONTACT] {variable}: 无法解析为布尔值";
            return false;
        }

        /// <summary>
        /// 获取变量的实际值
        /// </summary>
        private string GetVariableValue(
            string variableOrConstant,
            Dictionary<string, string> actualValues,
            out string? missingVariable)
        {
            missingVariable = null;

            // 如果是常量（数字或字符串），直接返回
            if (string.IsNullOrEmpty(variableOrConstant))
                return "";

            // 尝试作为数字
            if (double.TryParse(variableOrConstant, out _))
                return variableOrConstant;

            // 尝试作为布尔值
            if (bool.TryParse(variableOrConstant, out _))
                return variableOrConstant;

            // 作为变量查找
            if (actualValues.TryGetValue(variableOrConstant, out var value))
                return value;

            // 未找到变量
            missingVariable = variableOrConstant;
            return "";
        }
    }

    /// <summary>
    /// Transition解析结果
    /// </summary>
    public class TransitionParseResult
    {
        /// <summary>
        /// 解析是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 条件是否通过
        /// </summary>
        public bool IsPassed { get; set; }

        /// <summary>
        /// 提取的条件表达式列表
        /// </summary>
        public List<ConditionExpression> Conditions { get; set; } = new();

        /// <summary>
        /// 计算详情
        /// </summary>
        public List<string> EvaluationDetails { get; set; } = new();

        /// <summary>
        /// 缺少的变量列表
        /// </summary>
        public List<string> MissingVariables { get; set; } = new();

        /// <summary>
        /// 错误消息
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 条件表达式
    /// </summary>
    public class ConditionExpression
    {
        /// <summary>
        /// 条件名称
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// 条件类型（GT, LT, EQ, AND, OR等）
        /// </summary>
        public string? Type { get; set; }

        /// <summary>
        /// 输入参数
        /// </summary>
        public Dictionary<string, string> Inputs { get; set; } = new();
    }

    /// <summary>
    /// 计算结果
    /// </summary>
    internal class EvaluationResult
    {
        public bool Result { get; set; }
        public List<string> Details { get; set; } = new();
        public List<string> MissingVariables { get; set; } = new();
    }
}
