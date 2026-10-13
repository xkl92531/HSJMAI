namespace HSJM.AI.IDbService
{
    /// <summary>
    /// IGW <c>/Tag</c> 读接口的路径（2026-09-22 契约变更）。
    ///
    /// IGW 把「取值」和「进轮询」拆开了：六个读接口**默认只取值、不把点送进扫描计划**；
    /// 需要保持轮询的调用方显式加 <c>?activate=true</c>。
    ///
    /// 原先是无条件 Touch 每个请求点，按 activeWindowSec（默认 30 秒）判为活动送进扫描计划——
    /// 于是一次约束诊断读上千个 IO 点、读完就走，却让这批点白白被轮询 30 秒，挤占正常采集窗口。
    ///
    /// 我们两类读诉求相反，且**共用同一条读链**（<c>PLC_VariableAccessService.ReadExactViaIgwAsync</c>），
    /// 所以意图必须由调用方透下来：
    /// <list type="bullet">
    /// <item>约束诊断 / 停机诊断快照：读一次就走 → 默认（不带参数）</item>
    /// <item>LineUp 设备监控（工步监控）：100ms 持续盯同一批点 → <c>keepPolling: true</c></item>
    /// </list>
    ///
    /// 两点边界（IGW 侧确认）：①不带参数时，不在采集的点**仍会现场补读 PLC** 再返回，
    /// 拿到的是最新值不是陈旧缓存，诊断语义一点没变；②带 activate 时，IGW 是"先判 idle 再 Touch"，
    /// 所以**第一次调用仍会现场补读**，从第二次起才命中轮询缓存——别把"第一轮偏慢"当 bug 查。
    /// </summary>
    public static class IgwReadPath
    {
        private const string Activate = "?activate=true";

        /// <param name="keepPolling">
        /// true = 读完仍保持在活动窗口内（持续盯同一批点的调用方）；
        /// false（默认）= 只取值，不产生 30 秒轮询副作用。
        /// </param>
        public static string TagsAsList(bool keepPolling = false) =>
            keepPolling ? "Tag/ReadTagsAsList" + Activate : "Tag/ReadTagsAsList";

        /// <inheritdoc cref="TagsAsList"/>
        public static string TagsWithQuality(bool keepPolling = false) =>
            keepPolling ? "Tag/ReadTagsWithQuality" + Activate : "Tag/ReadTagsWithQuality";
    }
}
