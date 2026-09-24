"""定位错误表变量名并提取其使用点。"""
import re

PATH = r"C:\Users\c\AppData\Local\Programs\Qoder\resources\app.asar解包\out\main\chunks\chatSessionMessageQueries-BOa6qznm.js"
s = open(PATH, encoding="utf-8", errors="replace").read()

pos = s.find('"10605"')
# 向前找对象起点
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

print("=" * 72)
print("错误表之前 400 字符（找变量名）")
print("=" * 72)
print(s[max(0, start - 400) : start + 60])

# 向后找配平终点
j = start
depth = 0
instr = False
esc = False
BS = chr(92)
while j < len(s):
    c = s[j]
    if esc:
        esc = False
    elif c == BS:
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
end = j + 1

print()
print("=" * 72)
print("错误表之后 1200 字符（找导出名/使用点）")
print("=" * 72)
print(s[end : end + 1200])

# 找可能的变量名
m = re.search(r"([A-Za-z_$][\w$]*)\s*=\s*\{[^{]*?\"10605\"", s[max(0, start - 400) : start + 60])
print()
print("候选变量名:", m.group(1) if m else "未识别")
