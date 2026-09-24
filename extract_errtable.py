import json
import sys

PATH = r"C:\Users\c\AppData\Local\Programs\Qoder\resources\app.asar解包\out\main\chunks\chatSessionMessageQueries-BOa6qznm.js"
s = open(PATH, encoding="utf-8", errors="replace").read()

pos = s.find('"10605"')

i = pos
depth = 0
while i > 0:
    c = s[i]
    if c == "}":
        depth += 1
    elif c == "{":
        if depth == 0:
            break
        depth -= 1
    i -= 1
start = i

j = start
depth = 0
instr = False
esc = False
BACKSLASH = chr(92)
while j < len(s):
    c = s[j]
    if esc:
        esc = False
    elif c == BACKSLASH:
        esc = True
    elif c == '"':
        instr = not instr
    elif not instr:
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                break
    j += 1

d = json.loads(s[start : j + 1])

out = []
out.append(f"# Qoder 客户端错误码表（{len(d)} 个）")
out.append("# 提取自 out/main/chunks/chatSessionMessageQueries-BOa6qznm.js")
out.append("")

keys = sorted(d.keys(), key=lambda x: (0, int(x)) if x.isdigit() else (1, x))
for k in keys:
    v = d[k]
    if not isinstance(v, dict):
        out.append(f"[{k}] {v}")
        continue
    title = (v.get("title") or {}).get("zh-CN") or (v.get("title") or {}).get("en-US") or ""
    msg = (v.get("message") or {}).get("zh-CN") or (v.get("message") or {}).get("en-US") or ""
    acts = ", ".join(a.get("id", "?") for a in (v.get("actions") or []))
    out.append(f"[{k}] {title}")
    out.append(f"     动作: {acts}")
    out.append(f"     {msg}")
    out.append("")

open("qoder_errtable.txt", "w", encoding="utf-8").write("\n".join(out))
print("已写出 qoder_errtable.txt，码数:", len(d))
