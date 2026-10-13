# 把复核得出的新事实并入标准答案（按 Key 去重，已存在的不覆盖）。
# 用法：python tools/logic_eval_merge_facts.py <truth.json> <facts.json> [--md <复核表.md> --md-section <节标题> --origin reviewed --note-suffix 文本]
#   facts.json：事实数组（字段同标准答案 Facts）
#   --md：额外从复核表某一节的表格行读 TimeoutAlarm（列：Key | 前提 | Block | Network | 依据）
import io, json, re, sys

truth_path, facts_path = sys.argv[1], sys.argv[2]
args = sys.argv[3:]
opt = lambda k, d=None: args[args.index(k) + 1] if k in args else d
raw = open(truth_path, 'rb').read()
crlf = b'\r\n' in raw
truth = json.loads(raw.decode('utf-8-sig'))
new = json.load(io.open(facts_path, encoding='utf-8-sig'))
md, section = opt('--md'), opt('--md-section')
if md:
    inside = False
    for line in io.open(md, encoding='utf-8'):
        if line.startswith('## '):
            inside = line.strip().startswith('## ' + section)
            continue
        if not inside or not line.startswith('| TimeoutAlarm'):
            continue
        cells = [c.strip() for c in re.split(r'(?<!\\)\|', line.strip())[1:-1]]
        key = cells[0].replace('\\|', '|')
        pre = sorted({p.strip().upper() for p in cells[1].split(';') if p.strip() and p.strip() not in ('—', '-')})
        new.append({'Kind': 'TimeoutAlarm', 'Key': key, 'Display': key, 'Category': '阀门气缸', 'Block': cells[2],
                    'Network': int(cells[3]), 'Preconditions': pre, 'Origin': opt('--origin', 'reviewed'),
                    'Note': cells[4] + (opt('--note-suffix', '') or '')})
have = {f['Key'] for f in truth['Facts']}
added = [f for f in new if f['Key'] not in have and not have.add(f['Key'])]
truth['Facts'].extend(added)
truth['Source'] += f'；补标 {len(added)} 条（{facts_path}' + (f' + {md} {section}' if md else '') + '）'
text = json.dumps(truth, ensure_ascii=False, indent=2)
if crlf:
    text = text.replace('\n', '\r\n')
open(truth_path, 'wb').write(text.encode('utf-8'))
print(f'新增 {len(added)}，跳过已存在 {len(new) - len(added)}，现共 {len(truth["Facts"])}')
