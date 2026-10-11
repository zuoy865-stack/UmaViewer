import sys, os, json
sys.path.insert(0, os.path.join(os.getcwd(), ".agents", "skills", "live-analyzer", "scripts"))
from live_reader import LiveReader

mid = int(sys.argv[1])
r = LiveReader()
ta = r.extract_text_assets_from_bundle(f"live/musicscores/m{mid}/m{mid}_part")
key = f"m{mid}_part"
text = ta.get(key) or next(iter(ta.values()))
lines = [l.strip() for l in text.splitlines() if l.strip()]
print("=== FILE: %s ===" % key)
print("total lines:", len(lines))
print("HEADER:", lines[0])
hdr = lines[0].split(",")
print("ncols:", len(hdr))
print("FOOTER(last line):", lines[-1])
print("--- first 6 data rows ---")
for l in lines[1:7]:
    print(l)
print("--- distinct values per column (first 40 columns) ---")
for i, h in enumerate(hdr[:40]):
    vals = set()
    for l in lines[1:]:
        v = l.split(",")
        if i < len(v):
            vals.add(v[i])
    s = sorted(vals, key=lambda x: (len(x), x))
    print(f"  [{i}] {h!r}: {s[:14]}{' ...' if len(s) > 14 else ''}")
