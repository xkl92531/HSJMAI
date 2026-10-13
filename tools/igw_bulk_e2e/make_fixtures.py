# -*- coding: utf-8 -*-
"""
生成 IGW 自动注册规划器的真实数据夹具（sub-project B / Task 3）。

数据源：PLC文件/xml.rar（两份 TIA 导出：HJRD2025001_展示线三代PLC1 / WJRD2024001_展示线二代PLC2）。
做法尽量贴近生产上传链路（PLC_UploadApp）：
  1. 解压后在原目录结构里逐文件调用解析器
     `python engine.py --file X --format json-v2 --output X.parsed.json`
     —— 解析器按文件所在目录向上查找 udt/DataType 目录解析 UDT 尺寸（与上传解压目录一致），
     因此 Standard 且偏移可信的块才有 byte_offset>=0，优化块/偏移不可信块为 -1。
  2. 只保留 blocks[0].language == "DB" 的块（与注册任务读 DB 块 `ProgrammingLanguage=="DB"` 一致）；
     块号取 blocks[0].number（C# 落库 BlockNumber = number ?? 0）。
  3. 接口行按 AddInterfaceRecursively 同规则映射：VariablePath=path、DataType=datatype 原样、
     OffsetByte/OffsetBit 仅 byte_offset>=0 时有值、Comment=comment；
     只输出叶子行（不是其它行的父路径；库里 ParentId 恒为空、无 is_structure 列，只能按路径判定）——
     规划器输入是「非容器行」；解析器未展开的 UDT/数组行（无子行）按叶子保留，由规划器计 unmapped_type。
  4. IO 标签按 ParseTagCSV 同规则解析 plcTags/*.csv：BOM → UTF-8，否则 GB2312(GBK)；
     逐字符切分，`"` 只切换引号状态不输出（因此 `""PD_TEL105_IN""` → `PD_TEL105_IN`），值 Trim；
     TagName 列越界的行跳过。

输出：api/HSJM.AI.IgwRegistration.Tests/Fixtures/{plc1,plc2}.json.gz
  {"project": 名称,
   "blocks": [{"name": BlockName, "number": BlockNumber,
               "rows": [[前缀长度, 路径后缀, DataType, OffsetByte|null, OffsetBit|null, Comment], ...]}, ...],
   "ioTags": [[TableName, TagName, Address, DataType, Comment], ...]}
  VariablePath = 上一行 VariablePath[:前缀长度] + 路径后缀（块内前缀压缩，体积约为明文的 1/4）；
  行尾的 null/空串省略。blocks 按块名（序数）排序、块内保持解析器输出顺序；ioTags 保持 CSV 顺序。

用法：python tools/igw_bulk_e2e/make_fixtures.py [--src 已解压的 xml 目录] [--jobs 8]
"""
import argparse
import concurrent.futures
import gzip
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
RAR = os.path.join(REPO, "PLC文件", "xml.rar")
ENGINE = os.path.join(REPO, "api", "HSJM.AI", "wwwroot", "skills", "plc-analyzer", "scripts", "engine.py")
OUT_DIR = os.path.join(REPO, "api", "HSJM.AI.IgwRegistration.Tests", "Fixtures")
SEVEN_ZIP = r"C:\Program Files\7-Zip\7z.exe"

# 工程目录名关键字 → 夹具名
PROJECTS = [("PLC1", "plc1"), ("PLC2", "plc2")]

DB_ROOT_RE = re.compile(rb"<SW\.Blocks\.(GlobalDB|InstanceDB|ArrayDB)\b")


def extract(src):
    if src:
        return src
    target = os.path.join(tempfile.gettempdir(), "igw_fixture_xml")
    if not os.path.isdir(target):
        subprocess.run([SEVEN_ZIP, "x", "-y", RAR, "-o" + target], check=True, stdout=subprocess.DEVNULL)
    # rar 顶层目录名为 xml
    inner = os.path.join(target, "xml")
    return inner if os.path.isdir(inner) else target


def is_db_file(path):
    with open(path, "rb") as f:
        return DB_ROOT_RE.search(f.read()) is not None


def parse_file(path, out_dir):
    """与 PLC_UploadApp.ParseSingleFileAsync 相同的解析器调用（工作目录 = 脚本目录）"""
    out = os.path.join(out_dir, hashlib.md5(path.encode("utf-8")).hexdigest() + ".parsed.json")
    proc = subprocess.run(
        [sys.executable, ENGINE, "--file", path, "--format", "json-v2", "--output", out],
        cwd=os.path.dirname(ENGINE), stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    if proc.returncode != 0 or not os.path.isfile(out):
        return path, None, proc.stderr.decode("utf-8", "replace")[:300]
    with open(out, encoding="utf-8") as f:
        data = json.load(f)
    os.remove(out)
    return path, data, None


def flatten(items):
    """AddInterfaceRecursively 的遍历顺序（先父后子，深度优先）"""
    for item in items or []:
        yield item
        yield from flatten(item.get("members"))


def offset_value(value):
    """C# decimal → JSON：整数值输出 int，保持夹具紧凑"""
    if value is None:
        return None
    return int(value) if float(value).is_integer() else float(value)


def db_rows_of(data):
    blocks = data.get("blocks") or []
    if not blocks or (blocks[0].get("language") or "") != "DB":
        return None, []
    info = blocks[0]
    name = info.get("name")
    number = info.get("number") or 0
    rows = list(flatten(data.get("interface")))
    paths = [r.get("path") or "" for r in rows]
    parent_paths = set()
    for p in paths:
        # 父路径：按 `.` 逐级截断（数组下标段 `X[3]` 的容器 `X` 也算父）
        parts = p.split(".")
        for i in range(1, len(parts)):
            parent_paths.add(".".join(parts[:i]))
        head = parts[-1]
        if "[" in head:
            parent_paths.add(".".join(parts[:-1] + [head[:head.index("[")]]))
    result = []
    for r in rows:
        path = r.get("path") or ""
        if path in parent_paths:
            continue
        byte_offset = r.get("byte_offset", -1)
        has_offset = byte_offset is not None and byte_offset >= 0
        result.append([
            path, r.get("datatype"),
            offset_value(byte_offset) if has_offset else None,
            offset_value(r.get("bit_offset", 0)) if has_offset else None,
            r.get("comment") or "",
        ])
    return (name, number), result


def compress_rows(rows):
    """[path, type, ob, obit, comment] → [前缀长度, 路径后缀, type, ob, obit, comment]，去掉行尾 null/空串"""
    out, prev = [], ""
    for path, *rest in rows:
        # 前缀长度由 C# 端按 UTF-16 下标 Substring 还原：只允许 BMP 字符，保证码点数 == UTF-16 单元数
        if any(ord(c) > 0xFFFF for c in path):
            raise SystemExit("变量路径含 BMP 外字符，前缀压缩与 C# 还原不一致: %r" % path)
        k = len(os.path.commonprefix([prev, path]))
        prev = path
        row = [k, path[k:]] + rest
        while row and (row[-1] is None or row[-1] == ""):
            row.pop()
        out.append(row)
    return out


def parse_csv_line(line):
    """PLC_UploadApp.ParseCsvLine 的逐字符等价实现"""
    result, current, in_quotes = [], [], False
    for c in line:
        if c == '"':
            in_quotes = not in_quotes
        elif c == "," and not in_quotes:
            result.append("".join(current).strip())
            current = []
        else:
            current.append(c)
    result.append("".join(current).strip())
    return result


def parse_tag_csv(path):
    with open(path, "rb") as f:
        raw = f.read()
    text = raw[3:].decode("utf-8", "replace") if raw.startswith(b"\xef\xbb\xbf") else raw.decode("gbk", "replace")
    lines = text.splitlines()
    if not lines or not lines[0]:
        return []
    headers = [h.strip() for h in lines[0].split(",")]

    def idx(name):
        return headers.index(name) if name in headers else -1

    def get(values, i):
        return values[i] if 0 <= i < len(values) else None

    i_table, i_tag, i_addr, i_type, i_comment = (idx(n) for n in ("TableName", "TagName", "Address", "DataType", "Comment"))
    tags = []
    for line in lines[1:]:
        if not line.strip():
            continue
        values = parse_csv_line(line)
        tag = get(values, i_tag)
        if tag is None:
            continue
        tags.append([get(values, i_table), tag, get(values, i_addr), get(values, i_type), get(values, i_comment) or ""])
    return tags


def build_project(project_dir, jobs, work_dir):
    files = []
    for root, _, names in os.walk(os.path.join(project_dir, "blocks")):
        for n in names:
            if n.lower().endswith((".xml", ".ml")):
                p = os.path.join(root, n)
                if is_db_file(p):
                    files.append(p)
    files.sort()
    rows_by_block = {}
    failures = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as pool:
        for path, data, err in pool.map(lambda p: parse_file(p, work_dir), files):
            if data is None:
                failures.append((path, err))
                continue
            key, rows = db_rows_of(data)
            if key is not None and rows:
                rows_by_block.setdefault(key, []).extend(rows)
    blocks = [{"name": key[0], "number": key[1], "rows": compress_rows(rows_by_block[key])}
              for key in sorted(rows_by_block)]

    io_tags = []
    tag_dir = os.path.join(project_dir, "plcTags")
    if os.path.isdir(tag_dir):
        for n in sorted(os.listdir(tag_dir)):
            if n.lower().endswith(".csv"):
                io_tags.extend(parse_tag_csv(os.path.join(tag_dir, n)))
    return {"project": os.path.basename(project_dir.rstrip("\\/")), "blocks": blocks, "ioTags": io_tags}, len(files), failures


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", help="已解压的 xml 目录（含两个工程目录）；缺省用 7z 解压 PLC文件/xml.rar")
    ap.add_argument("--jobs", type=int, default=8)
    args = ap.parse_args()

    src = extract(args.src)
    os.makedirs(OUT_DIR, exist_ok=True)
    work_dir = tempfile.mkdtemp(prefix="igw_fixture_parse_")
    for key, fixture in PROJECTS:
        dirs = [d for d in os.listdir(src) if key in d and os.path.isdir(os.path.join(src, d))]
        if len(dirs) != 1:
            raise SystemExit("找不到唯一的工程目录 %s: %s" % (key, dirs))
        data, db_files, failures = build_project(os.path.join(src, dirs[0]), args.jobs, work_dir)
        out = os.path.join(OUT_DIR, fixture + ".json.gz")
        payload = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        # mtime=0 保证可重复生成（字节一致）
        with open(out, "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as gz:
            gz.write(payload)
        rows = [r for b in data["blocks"] for r in b["rows"]]
        with_offset = sum(1 for r in rows if len(r) > 3 and r[3] is not None)
        print("%s: DB 文件 %d，块 %d，接口叶子行 %d（有偏移 %d），IO %d，解析失败 %d → %s (%d 字节)"
              % (fixture, db_files, len(data["blocks"]), len(rows), with_offset, len(data["ioTags"]),
                 len(failures), out, os.path.getsize(out)))
        for path, err in failures:
            print("  解析失败: %s %s" % (path, err))


if __name__ == "__main__":
    main()
