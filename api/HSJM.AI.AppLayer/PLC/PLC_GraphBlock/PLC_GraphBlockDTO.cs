using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.PLC_GraphBlock
{
    /// <summary>
    /// GRAPH 程序块解析溯源信息（来源段）
    /// </summary>
    public class PLC_GraphBlockSourceDTO
    {
        /// <summary>关联的 PLC 项目 Id（ai_plc_block_new.projectid）</summary>
        public long PlcProjectId { get; set; }

        /// <summary>程序块 Id（ai_plc_block_new.id，本次解析生成）</summary>
        public long PlcBlockId { get; set; }

        /// <summary>本次上传解析任务ID（plc_upload_{projectId}_{时间戳}_{GUID}，溯源用）</summary>
        public string ParseRunId { get; set; }

        /// <summary>GRAPH 内容 SHA256（graph-v1|块名|各GRAPH类LogicUnit拼接；仅 GRAPH 块非空）</summary>
        public string SourceHash { get; set; }
    }

    /// <summary>
    /// GRAPH 程序块基本信息（块段）
    /// </summary>
    public class PLC_GraphBlockInfoDTO
    {
        /// <summary>程序块名称（如 FB260_CacheGraph）</summary>
        public string BlockName { get; set; }

        /// <summary>程序块类型（FB/FC/OB/DB/UDT）</summary>
        public string BlockType { get; set; }

        /// <summary>程序块编号（如 260，部分品牌可能无编号）</summary>
        public int? BlockNumber { get; set; }

        /// <summary>编程语言（GRAPH）</summary>
        public string ProgrammingLanguage { get; set; }
    }

    /// <summary>
    /// GRAPH 程序块解析信息信封（source + block 两段，序列化为 camelCase JSON）
    /// </summary>
    public class PLC_GraphBlockDTO
    {
        /// <summary>溯源信息段</summary>
        public PLC_GraphBlockSourceDTO Source { get; set; }

        /// <summary>块基本信息段</summary>
        public PLC_GraphBlockInfoDTO Block { get; set; }

        /// <summary>该块是否已在 GRAPH 智能导入工作台生成过预览（存在 PreviewReady 批次）</summary>
        public bool HasPreviewBatch { get; set; }
    }
}
