# 把人工复核结果（review/*.json）合并进由 truth-formal 导出的标准答案。
# 复核文件：{"Add":[{Key,Kind,Category,Block,Network,Display,Note}], "Remove":[{Key,Note}]}
# 用法：python tools/logic_eval_apply_review.py <truth.json> <review.json>
import json, sys
truth_path, review_path = sys.argv[1], sys.argv[2]
truth = json.load(open(truth_path, encoding='utf-8'))
review = json.load(open(review_path, encoding='utf-8'))
facts = truth['Facts']
keys = {f['Key'] for f in facts}
removed = 0
for r in review.get('Remove', []):
    before = len(facts)
    facts[:] = [f for f in facts if f['Key'] != r['Key']]
    if len(facts) == before: sys.exit(f"要移除的 Key 不在标准答案里：{r['Key']}")
    removed += before - len(facts)
added = 0
for a in review.get('Add', []):
    if a['Key'] in keys: continue
    facts.append({'Kind': a['Kind'], 'Key': a['Key'], 'Display': a['Display'], 'Category': a['Category'],
                  'Block': a['Block'], 'Network': a['Network'], 'Preconditions': None,
                  'Origin': 'reviewed', 'Note': a.get('Note', '')})
    keys.add(a['Key']); added += 1
truth['Source'] += f"；人工复核 {review_path}（补 {added}，移除 {removed}）"
open(truth_path, 'w', encoding='utf-8').write(json.dumps(truth, ensure_ascii=False, indent=2))
print(f"补 {added} 条，移除 {removed} 条，现有 {len(facts)} 条")
