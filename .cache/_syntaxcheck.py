"""Crude C# balance / structure self-check (no compiler available)."""
import sys, io

path = sys.argv[1]
src = io.open(path, encoding="utf-8").read()

out = []
i = 0
n = len(src)
in_line_comment = False
in_block_comment = False
in_str = False
in_char = False
in_verbatim = False
while i < n:
    c = src[i]
    nxt = src[i + 1] if i + 1 < n else ""
    if in_line_comment:
        if c == "\n":
            in_line_comment = False
            out.append(c)
        i += 1
        continue
    if in_block_comment:
        if c == "*" and nxt == "/":
            in_block_comment = False
            i += 2
            continue
        i += 1
        continue
    if in_verbatim:
        if c == '"':
            if nxt == '"':
                i += 2
                continue
            in_verbatim = False
        i += 1
        continue
    if in_str:
        if c == "\\":
            i += 2
            continue
        if c == '"':
            in_str = False
        i += 1
        continue
    if in_char:
        if c == "\\":
            i += 2
            continue
        if c == "'":
            in_char = False
        i += 1
        continue
    if c == "/" and nxt == "/":
        in_line_comment = True
        i += 2
        continue
    if c == "/" and nxt == "*":
        in_block_comment = True
        i += 2
        continue
    if c == "@" and nxt == '"':
        in_verbatim = True
        i += 2
        continue
    if c == "$" and nxt == '"':
        in_str = True
        i += 2
        continue
    if c == '"':
        in_str = True
        i += 1
        continue
    if c == "'":
        in_char = True
        i += 1
        continue
    out.append(c)
    i += 1

code = "".join(out)
print("unterminated string/comment:", in_str or in_char or in_block_comment or in_verbatim)
for op, cl, name in (("{", "}", "brace"), ("(", ")", "paren"), ("[", "]", "bracket")):
    print("%-8s open=%d close=%d  balanced=%s" % (name, code.count(op), code.count(cl), code.count(op) == code.count(cl)))

# depth never negative
depth = 0
line = 1
for ch in code:
    if ch == "\n":
        line += 1
    elif ch == "{":
        depth += 1
    elif ch == "}":
        depth -= 1
        if depth < 0:
            print("NEGATIVE DEPTH at line", line)
            break
print("final depth:", depth)
