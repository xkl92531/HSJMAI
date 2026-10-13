using System;
using System.ComponentModel;

namespace HSJM.AI.AppLayer.Agent.Tools
{
    /// <summary>
    /// 内置工具集（用于PLC解析工厂和通用场景）
    /// </summary>
    public static class BuiltInTools
    {
        #region 通用工具

        [Description("获取当前系统时间")]
        public static string GetCurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss dddd");

        [Description("执行数学表达式计算")]
        public static string Calculate([Description("数学表达式，如 2+3*4")] string expression)
        {
            try
            {
                var result = new System.Data.DataTable().Compute(expression, null);
                return $"计算结果: {result}";
            }
            catch (Exception ex)
            {
                return $"计算错误: {ex.Message}";
            }
        }

        [Description("生成UUID")]
        public static string GenerateUuid() => Guid.NewGuid().ToString();

        #endregion

       
    }
}
