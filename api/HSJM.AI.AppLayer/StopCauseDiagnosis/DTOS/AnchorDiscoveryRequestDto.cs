using System;
using System.Collections.Generic;
using HSJM.AI.AppLayer.StopCauseDiagnosis.Models;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 锚点发现请求
    /// </summary>
    public class AnchorDiscoveryRequestDto
    {
        /// <summary>
        /// 项目ID
        /// </summary>
        public long ProjectId { get; set; }

        /// <summary>
        /// 可选工位过滤，例如 OP080
        /// </summary>
        public string StationCode { get; set; }

        /// <summary>
        /// 可选块过滤；传入后仅分析指定 BlockId，未传则分析项目内全部块。
        /// </summary>
        public long? BlockId { get; set; }

        /// <summary>
        /// 是否包含辅助输出
        /// </summary>
        public bool IncludeAuxiliary { get; set; }

        /// <summary>
        /// 是否识别物理 IOTag Q 点。默认开启。
        /// 关闭后可在测试时屏蔽大量硬件输出，只保留虚拟 Q 点/握手位。
        /// </summary>
        public bool IncludePhysicalAnchors { get; set; } = true;

        /// <summary>
        /// 是否识别虚拟 Q 点。默认开启。
        /// </summary>
        public bool IncludeVirtualAnchors { get; set; } = true;

        /// <summary>
        /// 是否识别握手位。默认开启。
        /// 若只想观察纯虚拟 Q 点，可将其关闭。
        /// </summary>
        public bool IncludeHandshakeAnchors { get; set; } = true;

        /// <summary>
        /// 是否识别 I 点输入锚点（物理输入：传感器、按钮、开关等）。
        /// 默认关闭。开启后会扫描 %I 地址的 IOTag 并建立到 AST 的反向索引。
        /// </summary>
        public bool IncludeInputAnchors { get; set; }

        /// <summary>
        /// 是否同时返回 AST 种子
        /// </summary>
        public bool IncludeAstSeeds { get; set; } = true;

        /// <summary>
        /// 是否同时返回 AST 编译预览
        /// </summary>
        public bool IncludeAstCompileResults { get; set; }

        /// <summary>
        /// RawContent 摘要最大长度；小于等于 0 表示不限长
        /// </summary>
        public int RawContentMaxLength { get; set; } = 1200;

        /// <summary>
        /// 是否在物理点工位推断阶段启用 LLM 增强。
        /// 注意：虚拟核心Q点和握手位发现现在默认优先走 LLM 分类，
        /// 规则仅作为结构预筛和 LLM 不可用时的兜底；
        /// 本开关主要影响物理 IOTag 与变量工位归属的低置信度 LLM 兜底。
        /// </summary>
        public bool UseLlmAnnotation { get; set; }

        /// <summary>
        /// 是否执行工位染色，将锚点工位传播到关联 LogicUnit。
        /// 会额外产生数据库查询，建议在全量分析时开启。
        /// </summary>
        public bool IncludeStationColoring { get; set; }

        /// <summary>
        /// 是否将工位地图与绑定结果持久化到新表。
        /// 默认关闭，避免测试阶段误写库。
        /// </summary>
        public bool PersistStationMap { get; set; }

        /// <summary>
        /// 是否将 AST 编译结果与语义识别结果持久化。
        /// </summary>
        public bool PersistAstResults { get; set; }

        /// <summary>
        /// AST 语义识别进度键。前端可用该键轮询当前工位语义识别进度。
        /// </summary>
        public string SemanticProgressKey { get; set; }

        /// <summary>
        /// 预提取的物理 IOTag 工位结果，仅供 AppLayer 内部复用，避免重复推断。
        /// </summary>
        internal IReadOnlyDictionary<long, StationInferenceResult> PrecomputedPhysicalTagStations { get; set; }

        /// <summary>
        /// 预提取的变量工位结果，仅供 AppLayer 内部复用，避免重复推断。
        /// </summary>
        internal IReadOnlyDictionary<string, StationInferenceResult> PrecomputedVariableStations { get; set; }
    }
}
