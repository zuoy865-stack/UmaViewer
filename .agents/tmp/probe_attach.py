import sys, os, re, io, json
sys.path.insert(0, os.path.join(os.getcwd(), ".agents", "skills", "live-analyzer", "scripts"))
import live_reader as lr
import UnityPy

mid = int(sys.argv[1]) if len(sys.argv) > 1 else 1157
r = lr.LiveReader()

for suffix in ("cutt", "3d"):
    if suffix == "cutt":
        asset_name = f"cutt/cutt_son{mid}/cutt_son{mid}"
    else:
        asset_name = f"live/musicscores/m{mid}/m{mid}_{suffix}"
    meta = r.query_meta_single(asset_name)
    if not meta:
        print(f"[{suffix}] meta not found")
        continue
    h, e = meta
    env = UnityPy.load(io.BytesIO(r.decrypt_asset_bundle(h, e)))

    hits = {}
    types = {}
    for obj in env.objects:
        tname = obj.type.name
        types[tname] = types.get(tname, 0) + 1
        try:
            raw = obj.get_raw_data()
        except Exception:
            continue
        for m in re.finditer(rb"[A-Za-z0-9_]{3,40}", raw):
            s = m.group().decode("ascii", "ignore")
            low = s.lower()
            if ("attach" in low) or ("hand" in low) or ("prop" in low) or ("mic" in low) or ("wrist" in low):
                hits[s] = hits.get(s, 0) + 1

    print(f"=== {suffix} === object types: {types}")
    for k in sorted(hits, key=lambda x: (-hits[x], x))[:70]:
        print(f"   {hits[k]:5d}  {k}")
    print()
