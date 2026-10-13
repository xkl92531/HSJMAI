# 把前提复核表（review/preconditions-<项目>.md）写回标准答案的 Preconditions 字段。
# 用法：python tools/logic_eval_set_preconditions.py <truth.json> <preconditions.md> [--overrides overrides.json]
# 复核表每行：| Key | 动作 | 前提 | 依据 |，动作 ∈ 前提 / 无 / 移除 / 拿不准；Key 中的 | 写作 \|。
# overrides.json：{"Remove": [Key…], "RemovePrefix": [前缀…], "ClearPreconditions": true/false, "Uncertain": "无|移除"}
#   ClearPreconditions=true：所有「前提」视为无（例：前提全是未接线输入，按接口默认值视为常数）
#   Uncertain：对「拿不准」行的统一处理（默认 无）
# 前提名与评测工具一致：去空白、转大写、去重、按序数排序。
import io, json, re, sys

truth_path, md_path = sys.argv[1], sys.argv[2]
overrides = {}
if '--overrides' in sys.argv:
    overrides = json.load(io.open(sys.argv[sys.argv.index('--overrides') + 1], encoding='utf-8'))

rows = {}
for line in io.open(md_path, encoding='utf-8'):
    if not line.startswith('|') or line.startswith('| Key') or line.startswith('|---'):
        continue
    cells = [c.strip() for c in re.split(r'(?<!\\)\|', line.strip())[1:-1]]
    if len(cells) < 3:
        continue
    key = cells[0].replace('\\|', '|')
    rows[key] = (cells[1], [p.strip() for p in cells[2].split(';') if p.strip()])

raw = open(truth_path, 'rb').read()
crlf = b'\r\n' in raw
truth = json.loads(raw.decode('utf-8-sig'))
remove = set(overrides.get('Remove', []))
prefixes = overrides.get('RemovePrefix', [])
uncertain = overrides.get('Uncertain', '无')
missing, kept, removed = [], [], 0
for fact in truth['Facts']:
    key = fact['Key']
    if key in remove or any(key.startswith(p) for p in prefixes):
        removed += 1
        continue
    action, pre = rows.get(key, (None, []))
    if action is None:
        missing.append(key)
        action = '无'
    if action == '拿不准':
        action = uncertain
    if action == '移除':
        removed += 1
        continue
    if action == '前提' and not overrides.get('ClearPreconditions'):
        fact['Preconditions'] = sorted({p.upper() for p in pre})
    else:
        fact['Preconditions'] = []
    kept.append(fact)
truth['Facts'] = kept
truth['Source'] += f'；前提已整理（{md_path}，移除 {removed}）'
text = json.dumps(truth, ensure_ascii=False, indent=2)
if crlf:
    text = text.replace('\n', '\r\n')
open(truth_path, 'wb').write(text.encode('utf-8'))
print(f'保留 {len(kept)}，移除 {removed}，复核表未列出（按无前提处理）{len(missing)}')
for k in missing[:20]:
    print('  未列出：', k)
