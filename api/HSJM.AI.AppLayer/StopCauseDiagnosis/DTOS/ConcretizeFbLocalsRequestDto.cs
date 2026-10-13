using System.Collections.Generic;

namespace HSJM.AI.AppLayer.StopCauseDiagnosis.DTOS
{
    /// <summary>
    /// 批量具体化 FB/FC 内部局部变量的请求（LAD 梯形图展示用，2026-10-07）。
    /// localPaths 为去 # 前缀的局部路径（如 S_AutoRunMode / I_Mode.DryRunMode），
    /// 响应 map 只含可唯一具体化的项，失败路径不返回（前端按原样显示）。
    /// </summary>
    public class ConcretizeFbLocalsRequestDto
    {
        public long ProjectId { get; set; }

        /// <summary>局部变量所属的 FB/FC 块名（如 FB150_OP1030_电芯上线扫码枪自动流程）</summary>
        public string BlockName { get; set; }

        /// <summary>待具体化的局部路径列表（去 # 前缀）</summary>
        public List<string> LocalPaths { get; set; } = new List<string>();
    }
}
