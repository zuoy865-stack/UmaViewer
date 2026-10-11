import sys, os
sys.path.insert(0, os.path.join(os.getcwd(), ".agents", "skills", "live-analyzer", "scripts"))
from live_reader import LiveReader


def normalize(name: str) -> str:
    key = name.strip()
    key = key.replace("llleft", "left3")
    key = key.replace("rrright", "right3")
    key = key.replace("lleft", "left2")
    key = key.replace("rright", "right2")
    return key


def is_key_used(keys, candidate, skip):
    return any(j != skip and k == candidate for j, k in enumerate(keys))


def resolve_duplicated_suffix_columns(keys):
    base = [k for k in keys if k != "time" and "_" not in k]
    if not base:
        return
    for suffix in ("_vol", "_pan"):
        block_start = next((j for j, k in enumerate(keys) if k.endswith(suffix)), -1)
        if block_start < 0:
            continue
        for j in range(block_start, len(keys)):
            if not keys[j].endswith(suffix):
                continue
            ordinal = j - block_start
            if ordinal >= len(base):
                continue
            expected = base[ordinal] + suffix
            if expected == keys[j] or is_key_used(keys, expected, j):
                continue
            keys[j] = expected


TAGS = ["center", "left", "right", "left2", "right2", "left3", "right3"]

r = LiveReader()
for mid in (1001, 1030, 1032, 1034, 1036):
    ta = r.extract_text_assets_from_bundle(f"live/musicscores/m{mid}/m{mid}_part")
    text = ta.get(f"m{mid}_part") or next(iter(ta.values()))
    lines = [l for l in text.split("\n")]
    header_raw = lines[0].strip().split(",")

    keys = [normalize(n) for n in header_raw]
    before = list(keys)
    resolve_duplicated_suffix_columns(keys)

    rows = [l.strip() for l in lines[1:] if l.strip()]
    active_tags = []
    for tag in TAGS:
        if tag in keys:
            idx = keys.index(tag)
            if any(float(rw.split(",")[idx]) > 0 for rw in rows):
                active_tags.append(tag)

    dups_before = [k for k in set(before) if before.count(k) > 1]
    dups_after = [k for k in set(keys) if keys.count(k) > 1]

    print(f"--- m{mid}  rows={len(rows)}  cols={len(header_raw)} ---")
    print(f"    raw header : {header_raw}")
    print(f"    keys       : {keys}")
    print(f"    dup(before): {dups_before}   dup(after): {dups_after}")
    if before != keys:
        print(f"    RELOCATED  : {[(a, b) for a, b in zip(before, keys) if a != b]}")
    print(f"    singing tags present as keys: {active_tags}")
    missing = [t for t in active_tags if t not in keys]
    print(f"    MISSING (would throw / stay silent): {missing}")
    # every singing tag must be resolvable; every _vol/_pan key must be unique
    assert not dups_after, f"m{mid}: duplicate keys remain"
    assert not missing, f"m{mid}: singing tag missing from keys"
print("\nALL OK")
