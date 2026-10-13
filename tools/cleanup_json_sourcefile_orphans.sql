-- 清理 .json 孤儿源文件行（2026-09-21）
--
-- 背景：PLC_UploadFileClassifier.IsGeneralPlcSource 曾放行 .json，但解析器只认 .scl/.awl 与 XML，
-- .json 走 XML 分支必然识别失败；而 PLC_UploadApp 在 blocks 判定**之前**就构建 PLC_SourceFileEntity，
-- 于是导出包里的 manifest.json / udt_index.json 每次上传各留一行 ParseStatus=1 的孤儿。
-- manifest.json 内容每次都变 → 文件级 hash 跳过永不命中 → 孤儿行只增不减。
-- 代码侧已在 2026-09-21 去掉 .json 分类，本脚本只回收存量。
--
-- 幂等：可重复执行。默认软删（与本仓库其它清理脚本一致）。
-- 先跑第 1 段看清单，确认无误再跑第 2 段。

-- 1) 先看：有多少、都是什么
SELECT filetype,
       filename,
       count(*)            AS rows,
       count(DISTINCT projectid) AS projects,
       min(parsetime)      AS first_seen,
       max(parsetime)      AS last_seen
FROM "AI_PLC_SourceFile"
WHERE isdeleted = false
  AND lower(filetype) = 'json'
GROUP BY filetype, filename
ORDER BY rows DESC;

-- 2) 软删（确认第 1 段清单后再执行）
-- 只删 filetype=json 的行；这类行不挂任何块/接口/逻辑单元，删除不影响解析产物。
UPDATE "AI_PLC_SourceFile"
SET isdeleted = true
WHERE isdeleted = false
  AND lower(filetype) = 'json';

-- 3) 核对：应返回 0
SELECT count(*) AS remaining_json_rows
FROM "AI_PLC_SourceFile"
WHERE isdeleted = false
  AND lower(filetype) = 'json';
