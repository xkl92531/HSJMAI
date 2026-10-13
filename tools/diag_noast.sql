-- 「一键排查」工位显示「无AST」的根因定位（2026-09-22）
-- 背景：DiagnoseStationForLineAsync 的 Phase 2 一旦查不到 AST 行就直接 return，
--       OverallStatus 保持初始值 "NoAst"。该分支在 Phase 3（读实时值）之前，
--       所以与 IGW / PLC 采集完全无关，纯粹是 ai_plc_logicunit_ast 的数据状态问题。
-- 库：192.168.6.75:5432 / hsjm_ai_new_test（2026-09-22 核实）
--     旧记录里的 192.168.6.123 / hsjm_ai_plc 已作废——那台机器没了，照它连必失败。
-- psql -h 192.168.6.75 -p 5432 -U postgres -d hsjm_ai_new_test -f tools/diag_noast.sql
-- 用法：把 <PID> 换成项目 id 后整个文件跑一遍，从 1 往下看，第一个为 0 的就是原因。

\set PID 0   -- ← 改成实际项目 id

-- 1) 后端实际执行的那条查询（四个条件缺一不可）。返回 0 行 = 页面显示「无AST」
SELECT stationcode, count(*) AS compiled_rows
FROM ai_plc_logicunit_ast
WHERE isdeleted = false
  AND projectid = :PID
  AND compilestatus = 'Compiled'
GROUP BY stationcode
ORDER BY stationcode;

-- 2) 若上面某些工位缺失：看该项目 AST 表到底有什么（含未编译成功的）
--    compilestatus 取值：Pending / Compiled / PartiallyCompiled / Unsupported / Failed
SELECT compilestatus, count(*) AS rows, count(DISTINCT stationcode) AS stations
FROM ai_plc_logicunit_ast
WHERE isdeleted = false AND projectid = :PID
GROUP BY compilestatus
ORDER BY rows DESC;

-- 3) 有 AST 行但 stationcode 为空 → 锚点发现时没带工位，查询永远命中不到
SELECT count(*) AS rows_without_station
FROM ai_plc_logicunit_ast
WHERE isdeleted = false AND projectid = :PID
  AND (stationcode IS NULL OR btrim(stationcode) = '');

-- 4) 对账：诊断会遍历哪些工位 vs 这些工位有没有 AST
--    左边有、右边 compiled_rows 为 NULL 的，就是会显示「无AST」的工位
SELECT s."SubStationCode" AS station, a.compiled_rows
FROM "Basic_SubStation" s
LEFT JOIN (
    SELECT stationcode, count(*) AS compiled_rows
    FROM ai_plc_logicunit_ast
    WHERE isdeleted = false AND projectid = :PID AND compilestatus = 'Compiled'
    GROUP BY stationcode
) a ON a.stationcode = s."SubStationCode"
WHERE s."IsDeleted" = false AND s."PlcID" = :PID
ORDER BY a.compiled_rows NULLS FIRST, s."SubStationCode";

-- 5) 前置条件：AST 是从 ai_plc_logicunit.rawcontent 编译来的。
--    若该项目连 LU 都没有，那是上传/解析环节的问题，不是 AST 环节。
SELECT count(*) AS logic_units
FROM ai_plc_logicunit l
JOIN ai_plc_block_new b ON b.id = l.blockid AND b.isdeleted = false
WHERE l.isdeleted = false AND b.projectid = :PID;

-- ─────────────────────────────────────────────────────────────────────────────
-- 追加（2026-09-22）：「信号心跳」页 7 个工位全是 0核心/0上下文、状态"未知"时配套查
-- 页面上那几个数来自 Redis 里的工位状态（采集跑完才写），显示"未知"= state 为 null，
-- 说明采集从来没跑完过一轮。两种可能，下面三段用来区分：
--   (a) 监控信号没播种 → 采集每轮在 NoSignals 早退（读取之前就 return，不会发 IGW 请求）
--   (b) StationShadow 采集 worker 根本没跑
-- 两者都跟"取 IGW 值的方法对不对"无关——那条路径压根没走到。

-- 6) 监控信号有没有播种（0 → 命中 (a)；这也是 0核心/0上下文 的直接解释）
SELECT stationcode,
       count(*)                                  AS signals,
       count(*) FILTER (WHERE iscore = true)     AS core,
       count(*) FILTER (WHERE iscontext = true)  AS context,
       count(*) FILTER (WHERE enabled = true)    AS enabled
FROM ai_plc_stationshadowsignal
WHERE isdeleted = false AND projectid = :PID
GROUP BY stationcode
ORDER BY stationcode;

-- 7) 播种的上游：锚点绑定。它为 0 说明「一键分析并入库」没跑过／没产出，
--    那么 AST 缺失（第 1~4 段）和监控信号缺失就是同一个根因，不是两件事。
SELECT stationcode, count(*) AS anchor_bindings
FROM ai_plc_stationanchorbinding
WHERE isdeleted = false AND projectid = :PID
GROUP BY stationcode
ORDER BY stationcode;

-- 8) 工位影子配置（采集周期等）。没有配置行，采集任务也无从跑起
SELECT stationcode, coreintervalms, contextintervalms, silencethresholdseconds
FROM ai_plc_stationshadowconfig
WHERE isdeleted = false AND projectid = :PID
ORDER BY stationcode;

-- 9) 采集任务在不在跑（Hangfire 现在与业务数据同库，不用换库）
--    SELECT id, statename, createdat FROM hangfire.job
--    WHERE invocationdata::text LIKE '%StationShadow%' ORDER BY createdat DESC LIMIT 10;
