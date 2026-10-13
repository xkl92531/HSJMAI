-- IGW 注册台账补 fingerprint 列（2026-09-22 只追加注册）
-- 与 PLC_IgwRegistrationSchemaDdl.AddFingerprintColumnSql 同源；幂等，可重复执行。
-- 库：192.168.6.75:5432 / hsjm_ai_new_test
--   psql -h 192.168.6.75 -p 5432 -U postgres -d hsjm_ai_new_test -f tools/mig_20260922_igw_root_fingerprint.sql
--
-- 也可以不跑：应用启动时 IgwLedgerSchemaInitializer / 台账服务的 EnsureLedgerSchemaAsync 会自动补，幂等等价。
--
-- ⚠️ 补出来的列全是 NULL，而 NULL 语义是「没记过 ⇒ 必须重发」。
--    所以本列上线后的第一次注册会把所有根重发一遍，之后才进入稳态（不再整组删光重写）。
--    这不是缺陷：反过来把 NULL 当成"一致"的话，跳过机制从第一天起就是死的，而且不报错。
--    顺带的好处是，那一次重发会把 2026-09-22 加的工步块 100ms 扫描率写到点位上。

ALTER TABLE ai_plc_igwregistration ADD COLUMN IF NOT EXISTS fingerprint varchar(64) NULL;

-- 核对
SELECT column_name, data_type, character_maximum_length
FROM information_schema.columns
WHERE table_name = 'ai_plc_igwregistration' AND column_name = 'fingerprint';
