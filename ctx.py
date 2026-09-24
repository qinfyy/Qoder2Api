"""按关键词提取 Qoder 客户端 bundle 的上下文片段。"""
import re
import sys

BASE = r"C:\Users\c\AppData\Local\Programs\Qoder\resources\app.asar解包"
FILES = [
    BASE + r"\out\main\index.js",
    BASE + r"\out\main\chunks\chatSessionMessageQueries-BOa6qznm.js",
]


def show(path, kw, before=1800, after=1800, limit=4, out=None):
    s = open(path, encoding="utf-8", errors="replace").read()
    hits = [m.start() for m in re.finditer(re.escape(kw), s)]
    if not hits:
        return
    w = out or sys.stdout
    w.write(f"\n{'='*72}\n{path.split(chr(92))[-1]}  ::  {kw}  ({len(hits)} 处)\n{'='*72}\n")
    for p in hits[:limit]:
        w.write(f"\n--- @{p} ---\n")
        w.write(s[max(0, p - before) : p + after])
        w.write("\n")


if __name__ == "__main__":
    kw = sys.argv[1]
    before = int(sys.argv[2]) if len(sys.argv) > 2 else 1800
    after = int(sys.argv[3]) if len(sys.argv) > 3 else 1800
    limit = int(sys.argv[4]) if len(sys.argv) > 4 else 4
    f = open("ctx_out.txt", "w", encoding="utf-8")
    for path in FILES:
        show(path, kw, before, after, limit, f)
    f.close()
    print("已写出 ctx_out.txt")
