using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;

namespace HSJM.AI.AppLayer.PLC.DTOS
{
    public class PLC_ParseResultDTO
    {
        public PLC_MetadataDTO metadata { get; set; }
        public List<PLC_BlockInfoDTO> blocks { get; set; }  // Changed: Python outputs array
        public List<PLC_InterfaceDTO> @interface { get; set; }
        public List<PLC_LogicUnitDTO> logic_units { get; set; }

        public List<PLC_IOTagEntity> tags { get; set; }
        public PLC_CrossReferencesDTO cross_references { get; set; }
        
        // 块调用关系
        public List<PLC_CallInfoDTO> calls { get; set; }
        
        // GRAPH程序元素（步序和转换）
        public PLC_GraphElementsDTO graph_elements { get; set; }
        
        // 语义提示（用于AI分析）
        public PLC_SemanticHintsDTO semantic_hints { get; set; }
        
        // 解析错误信息
        public List<string> errors { get; set; }
    }
    
    /// <summary>
    /// 块调用关系
    /// </summary>
    public class PLC_CallInfoDTO
    {
        public string caller { get; set; }
        public string callee { get; set; }
        public string callee_type { get; set; }
        public string network_id { get; set; }
    }
    
    /// <summary>
    /// GRAPH程序元素
    /// </summary>
    public class PLC_GraphElementsDTO
    {
        public List<PLC_GraphStepDTO> steps { get; set; }
        public List<PLC_GraphTransitionDTO> transitions { get; set; }
    }
    
    public class PLC_GraphStepDTO
    {
        public int number { get; set; }
        public string name { get; set; }
        public bool is_init { get; set; }
        public string comment { get; set; }
        public List<string> actions { get; set; }
        public string max_time { get; set; }
        public string warning_time { get; set; }
    }
    
    public class PLC_GraphTransitionDTO
    {
        public string name { get; set; }
        public string comment { get; set; }
        public string condition_type { get; set; }
    }
    
    /// <summary>
    /// 语义提示（AI分析辅助）
    /// </summary>
    public class PLC_SemanticHintsDTO
    {
        public string station_type { get; set; }
        public string function_category { get; set; }
        public List<string> keywords_detected { get; set; }
        public double confidence { get; set; }
        public string inferred_purpose { get; set; }
    }

    public class PLC_MetadataDTO
    {
        public string brand { get; set; }
        public string product { get; set; }
        public string version { get; set; }
        public string station_id { get; set; }
        public string parse_config { get; set; }
    }

    public class PLC_BlockInfoDTO
    {
        public string name { get; set; }
        public string type { get; set; }
        public int? number { get; set; }
        public string language { get; set; }
        public string description { get; set; }
        public bool is_potential_entry { get; set; }
        /// <summary>InstanceDB 绑定的 FB/UDT 类型名（如 FB161_HomeGraph），非 InstanceDB 为空</summary>
        public string instance_of_name { get; set; }
        /// <summary>绑定类型（如 "FB" / "UDT"）</summary>
        public string instance_of_type { get; set; }
    }

    public class PLC_InterfaceDTO
    {
        public string name { get; set; }
        public string path { get; set; }
        public string datatype { get; set; }
        public string scope { get; set; }
        public string comment { get; set; }
        public string initial_value { get; set; }
        public string remanence { get; set; }
        // For hierarchical structure if needed, or flat list
        public List<PLC_InterfaceDTO> members { get; set; }
        public string array_info { get; set; }
        
        // ===== Python解析器预计算的S7偏移量字段 =====
        /// <summary>S7偏移地址，格式 "字节.位"，如 "0.0", "4.0"</summary>
        public string offset { get; set; }
        /// <summary>纯字节偏移（用于程序化访问）</summary>
        public decimal byte_offset { get; set; } = -1;
        /// <summary>位偏移 (0-7)</summary>
        public decimal bit_offset { get; set; }
        /// <summary>变量占用字节数</summary>
        public decimal size_in_bytes { get; set; }
        /// <summary>是否为结构/文件夹节点（非叶子变量）</summary>
        public bool is_structure { get; set; }
    }

    /// <summary>
    /// FB/FC调用处的引脚绑定（形式参数→实际变量）
    /// </summary>
    public class PLC_BlockCallPinBindingDTO
    {
        /// <summary>FB/FC类型名, e.g. "FB_Cylinder"</summary>
        public string callee_name { get; set; }
        /// <summary>"FB" / "FC" / "OB"</summary>
        public string callee_block_type { get; set; }
        /// <summary>背景数据块名 (FC调用时为空)</summary>
        public string instance_db { get; set; }
        /// <summary>Call节点的UId (用于溯源到raw_content)</summary>
        public string call_uid { get; set; }
        /// <summary>形式IN参数→实际变量</summary>
        public Dictionary<string, string> input_pins { get; set; } = new Dictionary<string, string>();
        /// <summary>形式OUT参数→实际变量</summary>
        public Dictionary<string, string> output_pins { get; set; } = new Dictionary<string, string>();
        /// <summary>形式InOut参数→实际变量</summary>
        public Dictionary<string, string> inout_pins { get; set; } = new Dictionary<string, string>();
    }

    public class PLC_LogicUnitDTO
    {
        public string unit_type { get; set; }
        public int network_id { get; set; }
        public string title { get; set; }
        public string comment { get; set; }
        public string raw_content { get; set; }
        public Dictionary<string, string> uid_symbol_map { get; set; }
        public List<PLC_VariableAccessDTO> variable_access { get; set; }
        public string programming_language { get; set; }
        /// <summary>
        /// CompileUnit 原始 ID（十六进制字符串），用于与 BlockCall 关联
        /// </summary>
        public string compile_unit_id { get; set; }
        /// <summary>
        /// 逻辑单元内的调用列表（SCL/STL 源码中提取的被调用块名）
        /// </summary>
        public List<string> calls { get; set; }
        /// <summary>
        /// FB/FC调用引脚绑定（跨边界AST展平基础数据）
        /// </summary>
        public List<PLC_BlockCallPinBindingDTO> call_pin_bindings { get; set; }
        /// <summary>
        /// GRAPH Step Action 单元的出口 Transition 编号列表（来自 &lt;Connections&gt;）
        /// </summary>
        public List<int> outgoing_transition_numbers { get; set; }
    }

    public class PLC_VariableAccessDTO
    {
        public string variable { get; set; } // VariableName
        public string access_type { get; set; } // READ, WRITE, READWRITE
        public string uid { get; set; }
        public string comment { get; set; }
        /// <summary>
        /// STL指令码（如 L/T/A/AN/OPN），SCL中为空
        /// </summary>
        public string instruction { get; set; }
        /// <summary>
        /// 寻址模式（symbolic/absolute/indirect/pointer/constant）
        /// </summary>
        public string address_mode { get; set; }
        /// <summary>
        /// 变量作用域（GlobalVariable/LocalVariable/LiteralConstant/TypedConstant）
        /// </summary>
        public string variable_scope { get; set; }
    }

    public class PLC_CrossReferencesDTO
    {
        // "writers": { "variable": [{ "block", "network", "unit_type" }] }
        public Dictionary<string, List<PLC_CrossReferenceItemDTO>> writers { get; set; }
        public Dictionary<string, List<PLC_CrossReferenceItemDTO>> readers { get; set; }
    }

    public class PLC_CrossReferenceItemDTO
    {
        public string block { get; set; }
        public int network { get; set; }
        public string unit_type { get; set; }
    }
}
