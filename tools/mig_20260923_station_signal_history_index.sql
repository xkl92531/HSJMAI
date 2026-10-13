-- 2026-09-23 工位信号历史表查询索引（与 StationSignalHistoryIndexInitializer 启动时自动建的完全相同，二选一即可）
-- 背景：停机诊断一键排查走 GRAPH 兜底路径的工位 GraphActiveCheck 要 34~44 秒，
--      该路径按「项目 + 工位 + 变量名列表，按 changed_at 倒序取前 N 条」查本表，此前无索引、全表扫描。
-- CONCURRENTLY：建索引期间不阻塞监控写入；不能放在事务里执行（psql 默认自动提交即可）。
-- 若中途失败会留下无效索引，先 DROP INDEX CONCURRENTLY <名字>; 再重跑。

-- 执行前可先看看表有多大、现在有哪些索引：
--   SELECT pg_size_pretty(pg_total_relation_size('ai_plc_station_signal_history')), count(*) FROM ai_plc_station_signal_history;
--   SELECT indexname, indexdef FROM pg_indexes WHERE tablename = 'ai_plc_station_signal_history';

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_station_signal_history_station_var_time
    ON ai_plc_station_signal_history (project_id, station_code, variable_name, changed_at DESC);

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_station_signal_history_station_time
    ON ai_plc_station_signal_history (project_id, station_code, changed_at DESC);

-- 验证（两行都应 indisvalid = t）：
--   SELECT c.relname, i.indisvalid FROM pg_class c JOIN pg_index i ON i.indexrelid = c.oid
--   WHERE c.relname LIKE 'ix_station_signal_history_%';
