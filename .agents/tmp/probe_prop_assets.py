import sys, os, ctypes, re, collections
sys.path.insert(0, os.path.join(os.getcwd(), ".agents", "skills", "live-analyzer", "scripts"))
import live_reader as lr

r = lr.LiveReader()
sqlite = r._get_sqlite3mc()
db = r._open_meta_db()

sqlite.sqlite3_prepare_v2.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p]
sqlite.sqlite3_step.argtypes = [ctypes.c_void_p]
sqlite.sqlite3_column_text.argtypes = [ctypes.c_void_p, ctypes.c_int]
sqlite.sqlite3_column_text.restype = ctypes.c_char_p
sqlite.sqlite3_finalize.argtypes = [ctypes.c_void_p]

def query(where, limit=0):
    out = []
    sql = f"SELECT n FROM a WHERE {where}".encode("utf-8")
    stmt = ctypes.c_void_p()
    if sqlite.sqlite3_prepare_v2(db, sql, -1, ctypes.byref(stmt), None) != 0:
        return out
    while sqlite.sqlite3_step(stmt) == 100:
        v = sqlite.sqlite3_column_text(stmt, 0)
        if v:
            out.append(v.decode("utf-8", "replace"))
        if limit and len(out) >= limit:
            break
    sqlite.sqlite3_finalize(stmt)
    return out

# 1) 所有含 chr_prop 的
for pat in ("%chr_prop%", "%rich_prop%", "%toon_prop%", "%/prop/%"):
    rows = query(f"n LIKE '{pat}'")
    print(f"=== LIKE '{pat}'  -> {len(rows)} 条 ===")
    for s in sorted(rows)[:18]:
        print("   ", s)
    print()

# 2) 统计所有 prop 相关路径的“目录形态”
rows = query("n LIKE '%prop%'")
shape = collections.Counter()
for s in rows:
    d = os.path.dirname(s)
    shape[re.sub(r'\d+', '#', d)] += 1
print("=== 目录形态统计（数字归一为 #）top 25 ===")
for k, v in shape.most_common(25):
    print(f"  {v:5d}  {k}")

sqlite.sqlite3_close(db)
