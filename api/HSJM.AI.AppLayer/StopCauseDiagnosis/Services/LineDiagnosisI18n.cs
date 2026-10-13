using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.Services
{
    /// <summary>
    /// 产线诊断国际化：语言归一（4 语）+ 出口固定文案字典（第 2 层）。
    /// 语言码规范：zh-CN / zh-TW / en-US / ja-JP。
    /// LLM 自由文本走第 1 层直生（prompt 语言指令），
    /// 本字典只负责后端拼接的固定模板文案（ack / 错误 / 超时 / LLM 兜底）。
    /// </summary>
    public static class LineDiagnosisI18n
    {
        public const string ZhCn = "zh-CN";
        public const string ZhTw = "zh-TW";
        public const string EnUs = "en-US";
        public const string JaJp = "ja-JP";

        /// <summary>
        /// 把前端传来的各种语言写法归一为 4 个规范值之一（缺省 zh-CN）。
        /// </summary>
        public static string Normalize(string outputLanguage)
        {
            var value = (outputLanguage ?? string.Empty).Trim();
            if (value.Length == 0) return ZhCn;

            if (value.Equals("en", StringComparison.OrdinalIgnoreCase)
                || value.Equals("en-US", StringComparison.OrdinalIgnoreCase)
                || value.Equals("en_US", StringComparison.OrdinalIgnoreCase)
                || value.Equals("en-us", StringComparison.OrdinalIgnoreCase)
                || value.Equals("english", StringComparison.OrdinalIgnoreCase))
            {
                return EnUs;
            }

            if (value.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
                || value.Equals("zh_TW", StringComparison.OrdinalIgnoreCase)
                || value.Equals("zh-tw", StringComparison.OrdinalIgnoreCase)
                || value.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase)
                || value.Equals("zh-Hant-TW", StringComparison.OrdinalIgnoreCase))
            {
                return ZhTw;
            }

            if (value.Equals("ja", StringComparison.OrdinalIgnoreCase)
                || value.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)
                || value.Equals("ja_JP", StringComparison.OrdinalIgnoreCase)
                || value.Equals("ja-jp", StringComparison.OrdinalIgnoreCase)
                || value.Equals("jp", StringComparison.OrdinalIgnoreCase)
                || value.Equals("japanese", StringComparison.OrdinalIgnoreCase))
            {
                return JaJp;
            }

            return ZhCn;
        }

        /// <summary>
        /// LLM 输出语言指令用的语言自称（目标语言的自名）。
        /// </summary>
        public static string LanguageName(string normalizedLanguage)
        {
            switch (Normalize(normalizedLanguage))
            {
                case EnUs: return "English";
                case ZhTw: return "繁體中文（台灣用語）";
                case JaJp: return "日本語";
                default: return "简体中文";
            }
        }

        /// <summary>
        /// 固定文案字典：key → 4 语。{0} 为 string.Format 参数位。
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Messages
            = new Dictionary<string, Dictionary<string, string>>
        {
            ["ack.started"] = new Dictionary<string, string>
            {
                [ZhCn] = "全线诊断已启动，请通过 SignalR 接收实时进度",
                [ZhTw] = "全線診斷已啟動，請透過 SignalR 接收即時進度",
                [EnUs] = "Line diagnosis started. Real-time progress will arrive via SignalR.",
                [JaJp] = "ライン診断を開始しました。リアルタイム進捗は SignalR で届きます。",
            },
            ["err.requestNull"] = new Dictionary<string, string>
            {
                [ZhCn] = "请求不能为空",
                [ZhTw] = "請求不能為空",
                [EnUs] = "The request body must not be empty.",
                [JaJp] = "リクエストが空です。",
            },
            ["err.noProjects"] = new Dictionary<string, string>
            {
                [ZhCn] = "系统中未找到任何 PLC 项目",
                [ZhTw] = "系統中未找到任何 PLC 專案",
                [EnUs] = "No PLC project was found in the system.",
                [JaJp] = "システムに PLC プロジェクトが見つかりません。",
            },
            ["err.noStations"] = new Dictionary<string, string>
            {
                [ZhCn] = "未找到任何工位",
                [ZhTw] = "未找到任何工位",
                [EnUs] = "No station was found to diagnose.",
                [JaJp] = "診断対象の工位が見つかりません。",
            },
            ["warn.timeout"] = new Dictionary<string, string>
            {
                [ZhCn] = "诊断超时（{0}秒），已返回已完成部分的结果",
                [ZhTw] = "診斷逾時（{0}秒），已回傳已完成部分的結果",
                [EnUs] = "Diagnosis timed out after {0}s. Partial results are returned.",
                [JaJp] = "診断がタイムアウトしました（{0} 秒）。完了済みの部分結果を返します。",
            },
            ["err.timeoutPartial"] = new Dictionary<string, string>
            {
                [ZhCn] = "诊断超时（{0}秒），已返回部分结果",
                [ZhTw] = "診斷逾時（{0}秒），已回傳部分結果",
                [EnUs] = "Diagnosis timed out after {0}s. Partial results are returned.",
                [JaJp] = "診断がタイムアウトしました（{0} 秒）。部分結果を返します。",
            },
            ["llm.noData"] = new Dictionary<string, string>
            {
                [ZhCn] = "无工位诊断数据",
                [ZhTw] = "無工位診斷資料",
                [EnUs] = "No station diagnosis data is available.",
                [JaJp] = "工位の診断データがありません。",
            },
            ["llm.callFailed"] = new Dictionary<string, string>
            {
                [ZhCn] = "LLM 调用失败: {0}",
                [ZhTw] = "LLM 呼叫失敗: {0}",
                [EnUs] = "LLM call failed: {0}",
                [JaJp] = "LLM 呼び出しに失敗しました: {0}",
            },
            ["llm.analysisFailed"] = new Dictionary<string, string>
            {
                [ZhCn] = "分析异常: {0}",
                [ZhTw] = "分析異常: {0}",
                [EnUs] = "Analysis failed: {0}",
                [JaJp] = "分析で例外が発生しました: {0}",
            },
        };

        /// <summary>
        /// 取固定文案。args 会做 string.Format 替换（{0}）。
        /// </summary>
        public static string T(string key, string outputLanguage, params object[] args)
        {
            var lang = Normalize(outputLanguage);
            if (!Messages.TryGetValue(key, out var table)
                || !table.TryGetValue(lang, out var text))
            {
                return key;
            }

            return args != null && args.Length > 0
                ? string.Format(text, args)
                : text;
        }
    }
}
