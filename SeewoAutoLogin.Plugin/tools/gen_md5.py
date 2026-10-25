"""等效于 ClassIsland.PluginSdk 的 generate-md5.ps1（无需 PowerShell Core）。

用法: python gen_md5.py <cipx目录>
行为: 删除目录内 *.md5sum，对目录内每个文件计算 MD5（大写），
      写入 checksums.md，内容为 "\n<!-- CLASSISLAND_PKG_MD5 {...} -->"
"""
import hashlib
import json
import os
import sys

path = sys.argv[-1]
if not os.path.isdir(path):
    print(f"目录不存在: {path}", file=sys.stderr)
    sys.exit(1)

hashes = {}
for name in sorted(os.listdir(path)):
    full = os.path.join(path, name)
    if not os.path.isfile(full):
        continue
    if name.endswith(".md5sum"):
        os.remove(full)
        continue
    with open(full, "rb") as f:
        hashes[name] = hashlib.md5(f.read()).hexdigest().upper()

summary = "\n<!-- CLASSISLAND_PKG_MD5 " + json.dumps(hashes, separators=(",", ":")) + " -->"
with open(os.path.join(path, "checksums.md"), "w", encoding="utf-8", newline="\r\n") as f:
    f.write(summary + "\n")
print(summary)
