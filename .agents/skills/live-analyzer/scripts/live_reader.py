#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
UmaViewer 项目级 Live 资源与演出时序读取解析工具
负责从本地赛马娘客户端数据中解密读取 Live 文件（配置、歌词、分轨分词、荧光棒律动、舞台站位与独立人声），
供智能体和开发者按需查询。
"""

import argparse
import ctypes
import io
import json
import os
import struct
import sys
from typing import Any, Dict, List, Optional, Tuple

try:
    import UnityPy
except ImportError:
    UnityPy = None


class LiveReader:
    """Live 资源读取器，负责数据库连接、密钥派生、AssetBundle 解密与数据解析"""

    def __init__(self, config_path: Optional[str] = None):
        # 定位 Config.json
        if not config_path:
            script_dir = os.path.dirname(os.path.abspath(__file__))
            # 向上查找项目根目录的 Config.json
            cur = script_dir
            for _ in range(5):
                candidate = os.path.join(cur, "Config.json")
                if os.path.exists(candidate):
                    config_path = candidate
                    break
                cur = os.path.dirname(cur)

        if not config_path or not os.path.exists(config_path):
            raise FileNotFoundError(f"未找到 Config.json 配置文件: {config_path}")

        self.config_path = config_path
        self.project_root = os.path.dirname(config_path)

        # 读取配置内容
        with open(config_path, "r", encoding="utf-8") as f:
            self.config = json.load(f)

        self.main_path = self.config.get("MainPath", "")
        self.db_base_key_hex = self.config.get("DBBaseKeyText", "")
        self.db_key_hex = self.config.get("DBKeyText", "")
        self.ab_key_hex = self.config.get("ABKeyText", "")

        # 校验必要的运行路径
        self.meta_path = os.path.join(self.main_path, "meta")
        self.master_path = os.path.join(self.main_path, "master", "master.mdb")
        self.dll_path = os.path.join(self.project_root, "Assets", "Plugins", "sqlite3mc_x64.dll")

        self.ab_base_keys = bytes.fromhex(self.ab_key_hex) if self.ab_key_hex else b""
        self._cached_meta_db = None
        self._sqlite_dll = None

    def _get_sqlite3mc(self):
        """动态加载 sqlite3mc 原生动态链接库"""
        if self._sqlite_dll is None:
            if not os.path.exists(self.dll_path):
                raise FileNotFoundError(f"未找到 sqlite3mc 解密库: {self.dll_path}")
            self._sqlite_dll = ctypes.CDLL(self.dll_path)
        return self._sqlite_dll

    def _open_meta_db(self):
        """打开并解密 meta SQLite 数据库"""
        if not os.path.exists(self.meta_path):
            raise FileNotFoundError(f"未找到 meta 数据库: {self.meta_path}")

        sqlite = self._get_sqlite3mc()
        db = ctypes.c_void_p()
        # 只读方式打开
        rc = sqlite.sqlite3_open_v2(self.meta_path.encode("utf-8"), ctypes.byref(db), 1, None)
        if rc != 0:
            raise RuntimeError(f"打开 meta 数据库失败，错误代码: {rc}")

        # 配置使用 SQLCipher 模式 (cipher=3)
        sqlite.sqlite3mc_config.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
        sqlite.sqlite3mc_config(db, b"cipher", 3)

        # 派生最终密钥：db_key ^ (base_key[i % 13])
        base_key = bytes.fromhex(self.db_base_key_hex)
        db_key = bytearray(bytes.fromhex(self.db_key_hex))
        for i in range(len(db_key)):
            db_key[i] ^= base_key[i % 13]

        sqlite.sqlite3_key.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]
        key_rc = sqlite.sqlite3_key(db, bytes(db_key), len(db_key))
        if key_rc != 0:
            sqlite.sqlite3_close(db)
            raise RuntimeError(f"设置 meta 解密密钥失败，错误代码: {key_rc}")

        return db

    def query_meta_entries(self, pattern: str) -> List[Tuple[str, str, int]]:
        """从 meta 数据库按名称通配查询记录，返回列表 [(name, hash, e), ...]"""
        sqlite = self._get_sqlite3mc()
        db = self._open_meta_db()
        results = []
        try:
            stmt = ctypes.c_void_p()
            sql = f"SELECT n, h, e FROM a WHERE n LIKE '{pattern}'".encode("utf-8")
            sqlite.sqlite3_prepare_v2.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p]
            sqlite.sqlite3_prepare_v2.restype = ctypes.c_int

            if sqlite.sqlite3_prepare_v2(db, sql, -1, ctypes.byref(stmt), None) == 0:
                sqlite.sqlite3_step.argtypes = [ctypes.c_void_p]
                sqlite.sqlite3_column_text.argtypes = [ctypes.c_void_p, ctypes.c_int]
                sqlite.sqlite3_column_text.restype = ctypes.c_char_p
                sqlite.sqlite3_column_int64.argtypes = [ctypes.c_void_p, ctypes.c_int]
                sqlite.sqlite3_column_int64.restype = ctypes.c_int64

                while sqlite.sqlite3_step(stmt) == 100:  # SQLITE_ROW
                    n = sqlite.sqlite3_column_text(stmt, 0).decode("utf-8", errors="ignore")
                    h = sqlite.sqlite3_column_text(stmt, 1).decode("utf-8", errors="ignore")
                    e = sqlite.sqlite3_column_int64(stmt, 2)
                    results.append((n, h, e))
                sqlite.sqlite3_finalize(stmt)
        finally:
            sqlite.sqlite3_close(db)
        return results

    def query_meta_single(self, asset_name: str) -> Optional[Tuple[str, int]]:
        """按准确资源名查询 meta 记录，返回 (hash, e)"""
        sqlite = self._get_sqlite3mc()
        db = self._open_meta_db()
        try:
            stmt = ctypes.c_void_p()
            sql = f"SELECT h, e FROM a WHERE n = '{asset_name}' LIMIT 1".encode("utf-8")
            sqlite.sqlite3_prepare_v2.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p]
            if sqlite.sqlite3_prepare_v2(db, sql, -1, ctypes.byref(stmt), None) == 0:
                sqlite.sqlite3_step.argtypes = [ctypes.c_void_p]
                sqlite.sqlite3_column_text.argtypes = [ctypes.c_void_p, ctypes.c_int]
                sqlite.sqlite3_column_text.restype = ctypes.c_char_p
                sqlite.sqlite3_column_int64.argtypes = [ctypes.c_void_p, ctypes.c_int]
                sqlite.sqlite3_column_int64.restype = ctypes.c_int64

                if sqlite.sqlite3_step(stmt) == 100:
                    h = sqlite.sqlite3_column_text(stmt, 0).decode("utf-8", errors="ignore")
                    e = sqlite.sqlite3_column_int64(stmt, 1)
                    sqlite.sqlite3_finalize(stmt)
                    return h, e
                sqlite.sqlite3_finalize(stmt)
        finally:
            sqlite.sqlite3_close(db)
        return None

    def decrypt_asset_bundle(self, bundle_hash: str, entry_key_int: int) -> bytes:
        """从本地磁盘读取加密 AssetBundle 并进行逐字节异或解密"""
        file_path = os.path.join(self.main_path, "dat", bundle_hash[:2], bundle_hash)
        if not os.path.exists(file_path):
            raise FileNotFoundError(f"本地未找到 AssetBundle 文件: {file_path}")

        with open(file_path, "rb") as f:
            data = bytearray(f.read())

        # 当 entry_key_int 为 0 时无需解密
        if entry_key_int == 0 or not self.ab_base_keys:
            return bytes(data)

        # 派生 FKey：baseKeys[i] ^ entryKeyBytes[j]
        base_len = len(self.ab_base_keys)
        key_bytes = struct.pack("<q", entry_key_int)
        fkey = bytearray(base_len * 8)
        for i in range(base_len):
            b = self.ab_base_keys[i]
            base_offset = i * 8
            for j in range(8):
                fkey[base_offset + j] = b ^ key_bytes[j]

        # 前 256 字节为明文头部，之后循环异或
        header_size = 256
        fkey_len = len(fkey)
        for idx in range(header_size, len(data)):
            data[idx] ^= fkey[idx % fkey_len]

        return bytes(data)

    def extract_text_assets_from_bundle(self, asset_name: str) -> Dict[str, str]:
        """解密指定 AssetBundle 并提取其中所有的 TextAsset 内容"""
        if UnityPy is None:
            raise ImportError("未安装 UnityPy 依赖，请执行 pip install UnityPy")

        meta_info = self.query_meta_single(asset_name)
        if not meta_info:
            return {}

        h, e = meta_info
        decrypted_bytes = self.decrypt_asset_bundle(h, e)
        env = UnityPy.load(io.BytesIO(decrypted_bytes))

        text_assets = {}
        for obj in env.objects:
            if obj.type.name == "TextAsset":
                ta = obj.read()
                text_assets[ta.m_Name] = ta.m_Script
        return text_assets

    def get_live_master_info(self, music_id: int) -> Optional[Dict[str, Any]]:
        """从 master.mdb 中读取歌曲基础元数据与服装设定"""
        import sqlite3

        if not os.path.exists(self.master_path):
            return None

        conn = sqlite3.connect(self.master_path)
        cur = conn.cursor()

        sql = """
        SELECT L.music_id, T.songname, L.live_member_number, L.default_main_dress,
               L.backdancer_order, L.backdancer_dress, D.dressname
        FROM live_data L
        LEFT JOIN (SELECT [index] as songid, text as songname FROM text_data WHERE id = 16) T
            ON L.music_id = T.songid
        LEFT JOIN (SELECT [index] as dressid, text as dressname FROM text_data WHERE id = 14) D
            ON L.default_main_dress = D.dressid
        WHERE L.music_id = ?
        """
        cur.execute(sql, (music_id,))
        row = cur.fetchone()
        conn.close()

        if not row:
            return None

        return {
            "music_id": row[0],
            "songname": row[1] or "",
            "member_count": row[2],
            "default_dress_id": row[3],
            "backdancer_order": row[4],
            "backdancer_dress": row[5],
            "dress_name": row[6] or "",
        }

    def list_all_lives(self, keyword: Optional[str] = None) -> List[Dict[str, Any]]:
        """列出所有 Live 歌曲，支持通过名称或 ID 进行模糊过滤"""
        import sqlite3

        if not os.path.exists(self.master_path):
            return []

        conn = sqlite3.connect(self.master_path)
        cur = conn.cursor()

        sql = """
        SELECT L.music_id, T.songname, L.live_member_number, L.default_main_dress
        FROM live_data L
        LEFT JOIN (SELECT [index] as songid, text as songname FROM text_data WHERE id = 16) T
            ON L.music_id = T.songid
        ORDER BY L.music_id ASC
        """
        cur.execute(sql)
        rows = cur.fetchall()
        conn.close()

        results = []
        for r in rows:
            music_id = r[0]
            songname = r[1] or f"Unknown Song ({music_id})"
            member_count = r[2]
            default_dress = r[3]

            if keyword:
                kw = str(keyword).lower()
                if kw not in str(music_id) and kw not in songname.lower():
                    continue

            results.append({
                "music_id": music_id,
                "songname": songname,
                "member_count": member_count,
                "default_dress": default_dress,
            })
        return results

    def get_live_settings(self, music_id: int) -> Optional[Dict[str, Any]]:
        """读取 livesettings bundle 中对应歌曲的背景 ID、舞台特效等配置"""
        text_assets = self.extract_text_assets_from_bundle("livesettings")
        key = str(music_id)
        if key not in text_assets:
            return None

        content = text_assets[key]
        lines = [line.strip() for line in content.splitlines() if line.strip()]
        if len(lines) < 2:
            return None

        header = lines[0].split(",")
        entries = []
        background_id = ""

        for line in lines[1:]:
            parts = line.split(",")
            entries.append(parts)
            # 根据经验与 UmaDataTypes，通常第 2 行的第 3 列为背景编号
            if not background_id and len(parts) >= 3 and parts[2].isdigit():
                background_id = parts[2]

        return {
            "header": header,
            "raw_entries": entries,
            "background_id": background_id,
            "entry_count": len(entries)
        }

    def get_lyrics(self, music_id: int) -> List[Dict[str, Any]]:
        """获取歌曲的时间轴歌词数据"""
        asset_name = f"live/musicscores/m{music_id}/m{music_id}_lyrics"
        text_assets = self.extract_text_assets_from_bundle(asset_name)
        target_name = f"m{music_id}_lyrics"

        script = text_assets.get(target_name)
        if not script:
            # 尝试取第一个
            if text_assets:
                script = next(iter(text_assets.values()))
            else:
                return []

        lyrics_list = []
        lines = script.splitlines()
        for idx, line in enumerate(lines):
            if idx == 0 and "time" in line.lower():
                continue
            parts = line.split(",")
            if len(parts) >= 2:
                try:
                    time_ms = float(parts[0])
                    text = parts[1].strip()
                    lyrics_list.append({
                        "time_ms": time_ms,
                        "time_sec": round(time_ms / 1000.0, 3),
                        "text": text
                    })
                except ValueError:
                    continue
        return lyrics_list

    def get_part(self, music_id: int) -> Dict[str, Any]:
        """解析歌曲分词分轨数据，说明各时间段哪个角色站位开唱"""
        asset_name = f"live/musicscores/m{music_id}/m{music_id}_part"
        text_assets = self.extract_text_assets_from_bundle(asset_name)
        target_name = f"m{music_id}_part"

        script = text_assets.get(target_name)
        if not script:
            if text_assets:
                script = next(iter(text_assets.values()))
            else:
                return {}

        lines = [l.strip() for l in script.splitlines() if l.strip()]
        if not lines:
            return {}

        headers = lines[0].split(",")
        timeline = []
        position_activity = {h: 0 for h in headers if h != "time"}

        for line in lines[1:]:
            vals = line.split(",")
            if len(vals) < len(headers):
                continue
            try:
                t_ms = float(vals[0])
            except ValueError:
                continue

            active_positions = []
            for col_idx in range(1, len(headers)):
                h = headers[col_idx]
                try:
                    active = float(vals[col_idx]) > 0
                except ValueError:
                    active = False
                if active:
                    active_positions.append(h)
                    position_activity[h] += 1

            timeline.append({
                "time_ms": t_ms,
                "time_sec": round(t_ms / 1000.0, 3),
                "singers": active_positions
            })

        return {
            "positions": [h for h in headers if h != "time"],
            "total_keyframes": len(timeline),
            "position_activity_counts": position_activity,
            "timeline": timeline
        }

    def get_cyalume(self, music_id: int) -> List[Dict[str, Any]]:
        """获取观众荧光棒与打 call 律动配置"""
        asset_name = f"live/musicscores/m{music_id}/m{music_id}_cyalume"
        text_assets = self.extract_text_assets_from_bundle(asset_name)
        target_name = f"m{music_id}_cyalume"

        script = text_assets.get(target_name)
        if not script:
            if text_assets:
                script = next(iter(text_assets.values()))
            else:
                return []

        lines = [l.strip() for l in script.splitlines() if l.strip()]
        if len(lines) < 2:
            return []

        headers = lines[0].split(",")
        cyalume_events = []

        for line in lines[1:]:
            parts = line.split(",")
            if len(parts) < 3:
                continue
            try:
                t_ms = float(parts[0])
                item = {
                    "time_ms": t_ms,
                    "time_sec": round(t_ms / 1000.0, 3),
                    "move_type": parts[1] if len(parts) > 1 else "",
                    "bpm": parts[2] if len(parts) > 2 else "",
                    "color_pattern": parts[3] if len(parts) > 3 else "",
                    "colors": [c for c in parts[4:9] if c]
                }
                cyalume_events.append(item)
            except ValueError:
                continue

        return cyalume_events

    def get_cutt_stage_structure(self, music_id: int) -> Dict[str, Any]:
        """解析 cutt_son bundle，提取舞台上的站位与相机骨架节点"""
        asset_name = f"cutt/cutt_son{music_id}/cutt_son{music_id}"
        meta_info = self.query_meta_single(asset_name)
        if not meta_info:
            return {}

        h, e = meta_info
        decrypted_bytes = self.decrypt_asset_bundle(h, e)
        env = UnityPy.load(io.BytesIO(decrypted_bytes))

        game_objects = []
        for obj in env.objects:
            if obj.type.name == "GameObject":
                go = obj.read()
                name = getattr(go, "m_Name", "")
                if name:
                    game_objects.append(name)

        # 整理常见舞台节点分类
        character_places = [n for n in game_objects if "Place" in n or n in ["Center", "Left1", "Right1", "Left2", "Right2", "Left3", "Right3"]]
        camera_nodes = [n for n in game_objects if "Camera" in n or "LookAt" in n]

        return {
            "total_game_objects": len(game_objects),
            "character_stand_places": character_places,
            "camera_nodes": camera_nodes,
            "all_nodes": game_objects
        }

    def get_available_vocals(self, music_id: int) -> List[Dict[str, Any]]:
        """从 meta 数据库查询该歌曲拥有的独立录音马娘声轨名单并映射姓名"""
        import sqlite3

        pattern = f"sound/l/{music_id}/snd_bgm_live_{music_id}_chara_%_01.awb"
        entries = self.query_meta_entries(pattern)

        # 提取 chara_id 集合
        chara_ids = []
        for n, _, _ in entries:
            # 格式类似 sound/l/1001/snd_bgm_live_1001_chara_1001_01.awb
            base = os.path.basename(n)
            parts = base.split("_")
            if len(parts) >= 6:
                try:
                    cid = int(parts[5])
                    if cid not in chara_ids:
                        chara_ids.append(cid)
                except ValueError:
                    pass

        # 查 master.mdb 获取马娘名字
        name_map = {}
        if os.path.exists(self.master_path) and chara_ids:
            conn = sqlite3.connect(self.master_path)
            cur = conn.cursor()
            placeholders = ",".join(["?"] * len(chara_ids))
            sql = f"SELECT [index], text FROM text_data WHERE id = 6 AND [index] IN ({placeholders})"
            cur.execute(sql, chara_ids)
            for cid, cname in cur.fetchall():
                name_map[cid] = cname
            conn.close()

        vocal_list = []
        for cid in sorted(chara_ids):
            vocal_list.append({
                "chara_id": cid,
                "name": name_map.get(cid, f"未知马娘 ({cid})")
            })

        return vocal_list


def main():
    parser = argparse.ArgumentParser(
        description="UmaViewer 项目级 Live 文件与演出时序读取工具",
        formatter_class=argparse.RawDescriptionHelpFormatter
    )

    parser.add_argument("music_id", nargs="?", type=int, help="Live 歌曲 ID（例如 1001）")
    parser.add_argument("--list", "-l", nargs="?", const="", help="列出或模糊搜索所有 Live 歌曲")
    parser.add_argument("--info", "-i", action="store_true", help="查询歌曲基础信息、人数与默认服装")
    parser.add_argument("--lyrics", action="store_true", help="查询歌词与时序")
    parser.add_argument("--part", action="store_true", help="查询分词与角色开唱分轨分配")
    parser.add_argument("--cyalume", action="store_true", help="查询荧光棒打 call 律动与灯光色彩")
    parser.add_argument("--cutt", action="store_true", help="查询舞台站位节点与摄像机结构")
    parser.add_argument("--vocals", action="store_true", help="查询拥有专属独立唱腔声轨的马娘名单")
    parser.add_argument("--all", "-a", action="store_true", help="查询该歌曲的所有 Live 数据汇总")
    parser.add_argument("--json", "-j", action="store_true", help="以 JSON 格式输出结果")
    parser.add_argument("--config", "-c", type=str, help="自定义 Config.json 路径")

    args = parser.parse_args()

    try:
        reader = LiveReader(config_path=args.config)
    except Exception as e:
        print(f"初始化 LiveReader 失败: {e}", file=sys.stderr)
        sys.exit(1)

    # 1. 处理歌曲列表查询
    if args.list is not None:
        kw = args.list.strip() if args.list else None
        lives = reader.list_all_lives(keyword=kw)
        if args.json:
            print(json.dumps(lives, ensure_ascii=False, indent=2))
        else:
            print(f"共找到 {len(lives)} 首 Live 歌曲:")
            for l in lives:
                print(f"  [{l['music_id']}] {l['songname']} (参演人数: {l['member_count']}, 默认演出服: {l['default_dress']})")
        return

    # 必须指定 music_id 才能查询具体歌曲内容
    if not args.music_id:
        parser.print_help()
        sys.exit(1)

    mid = args.music_id
    output_data: Dict[str, Any] = {"music_id": mid}

    # 判断是否默认启用所有模块
    show_all = args.all or not (args.info or args.lyrics or args.part or args.cyalume or args.cutt or args.vocals)

    # 始终获取基础曲名，以确保标题与概况显示友好
    master_info = reader.get_live_master_info(mid) or {}
    song_title = master_info.get("songname", f"Live {mid}")

    # 读取基础信息
    if show_all or args.info:
        settings = reader.get_live_settings(mid) or {}
        master_info["background_id"] = settings.get("background_id", "")
        output_data["info"] = master_info

    # 读取歌词
    if show_all or args.lyrics:
        output_data["lyrics"] = reader.get_lyrics(mid)

    # 读取分词分轨
    if show_all or args.part:
        output_data["part"] = reader.get_part(mid)

    # 读取荧光棒打 call
    if show_all or args.cyalume:
        output_data["cyalume"] = reader.get_cyalume(mid)

    # 读取舞台 CUTT 节点
    if show_all or args.cutt:
        output_data["cutt"] = reader.get_cutt_stage_structure(mid)

    # 读取独立声轨
    if show_all or args.vocals:
        output_data["vocals"] = reader.get_available_vocals(mid)

    # JSON 模式输出
    if args.json:
        print(json.dumps(output_data, ensure_ascii=False, indent=2))
        return

    # 人类可读格式化文本输出
    print(f"==================================================")
    print(f"Live 歌曲详细信息: [{mid}] {song_title}")
    print(f"==================================================")

    if "info" in output_data:
        inf = output_data["info"]
        print(f"曲名: {inf.get('songname', '未知')}")
        print(f"参演人数: {inf.get('member_count', '未知')} 人")
        print(f"默认演出服装: {inf.get('dress_name', '未知')} (ID: {inf.get('default_dress_id', '-')})")
        print(f"舞台背景编号: {inf.get('background_id', '未指定')}")
        print()

    if "vocals" in output_data:
        vocs = output_data["vocals"]
        print(f"独立专属声轨收录 ({len(vocs)} 位马娘):")
        names = [f"{v['name']}({v['chara_id']})" for v in vocs]
        # 每行展示 5 个名字
        chunk_size = 5
        for i in range(0, len(names), chunk_size):
            print("  " + ", ".join(names[i:i + chunk_size]))
        print()

    if "lyrics" in output_data:
        lyr = output_data["lyrics"]
        limit = None if args.lyrics and not args.all else 8
        print(f"歌词时间轴 (共 {len(lyr)} 条关键句):")
        display_items = lyr if limit is None else lyr[:limit]
        for item in display_items:
            if item['text']:
                print(f"  [{item['time_sec']:06.2f}s] {item['text']}")
        if limit is not None and len(lyr) > limit:
            print(f"  ... 剩余 {len(lyr) - limit} 句歌词 (使用 --lyrics 查看完整歌词)")
        print()

    if "part" in output_data:
        part_data = output_data["part"]
        print(f"角色开唱分轨排布 (共 {part_data.get('total_keyframes', 0)} 个分段):")
        print(f"  参演站位列表: {', '.join(part_data.get('positions', []))}")
        counts = part_data.get("position_activity_counts", {})
        count_strs = [f"{pos}: {cnt} 次开唱" for pos, cnt in counts.items() if cnt > 0]
        if count_strs:
            print("  站位开唱频次: " + ", ".join(count_strs))
        print()

    if "cyalume" in output_data:
        cya = output_data["cyalume"]
        print(f"观众打 call 与灯光色彩 (共 {len(cya)} 个律动事件):")
        for item in cya[:5]:
            cols = "/".join(item["colors"]) if item["colors"] else "默认色"
            print(f"  [{item['time_sec']:06.2f}s] 动作: {item['move_type']}, BPM: {item['bpm']}, 色彩: {cols}")
        if len(cya) > 5:
            print(f"  ... 剩余 {len(cya) - 5} 个打 call 转换节点")
        print()

    if "cutt" in output_data:
        cutt = output_data["cutt"]
        print(f"舞台骨架与站位节点:")
        print(f"  场景物体总数: {cutt.get('total_game_objects', 0)}")
        print(f"  角色站位节点: {', '.join(cutt.get('character_stand_places', [])[:10])} ...")
        print(f"  摄像机节点: {', '.join(cutt.get('camera_nodes', []))}")
        print()


if __name__ == "__main__":
    main()
