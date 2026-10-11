---
name: live-analyzer
description: >-
  UmaViewer 赛马娘 Live 资产与演出数据解析工具。负责脱离 Unity 引擎快速解密并读取本地 Live
  文件，支持按需查询歌曲基础信息、人数站位、默认演出服装、分词开唱分轨排布、时间轴歌词、打 call 律动与荧光棒配色、舞台摄像机节点以及专属独立马娘声轨。
---

# UmaViewer Live 资产与演出解析技能 (live-analyzer)

本技能为 UmaViewer 项目专用的项目级 Live 资产解析工具。通过结合本地配置、加密数据库原生解密及 AssetBundle 内存流解析，智能体能够以极低开销快速获取 Live 演出相关的各类关键信息。

## 适用场景

当用户或智能体需要了解以下信息时触发：
- 查询某首 Live 歌曲的基本配置（参演人数、站位、默认演出服、舞台背景）。
- 查看歌曲的时间轴歌词（精确到毫秒/秒）。
- 分析歌曲分词开唱分轨（判断指定时间点由哪些站位的角色演唱、统计各角色站位开唱频次）。
- 分析打 call 律动与灯光色彩（move_type、BPM、荧光棒颜色模式）。
- 查询舞台骨架结构、站位节点分布与摄像机挂载点。
- 检索哪些马娘拥有该歌曲的专属独立演唱声轨（chara vocal）。
- 搜索或遍历本地客户端已收录的 Live 歌曲。

## 工具调用指南

核心解析工具位于当前项目的 `.agents/skills/live-analyzer/scripts/live_reader.py`。
使用命令执行工具 `run_command` 调用 Python 脚本完成查询：

```powershell
# 1. 列出或搜索 Live 歌曲
python .agents/skills/live-analyzer/scripts/live_reader.py --list
python .agents/skills/live-analyzer/scripts/live_reader.py -l "うまぴょい"

# 2. 全量综合查询（输出基础信息、歌词摘要、分词站位、打 call 律动、舞台节点与独立声轨）
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id>

# 3. 按需自主选择查询维度
# 查询歌曲基础信息
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --info

# 查询完整时间轴歌词
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --lyrics

# 查询角色分词与开唱站位分配
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --part

# 查询荧光棒打 call 律动与灯光配色
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --cyalume

# 查询舞台节点与摄像机挂载
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --cutt

# 查询收录了专属独立演唱声轨的马娘名单
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --vocals

# 4. JSON 结构化输出（供 Agent 解析与推导）
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --part --json
python .agents/skills/live-analyzer/scripts/live_reader.py <music_id> --all --json
```

## 数据源与实现原理

1. **配置读取**：自动解析根目录 `Config.json` 中的 `MainPath` 与解密密钥。
2. **元数据定位**：通过 `Assets/Plugins/sqlite3mc_x64.dll` 读取并解密本地 `meta` 数据库，动态索引 AssetBundle 物理路径与密钥参数。
3. **歌曲与角色名查询**：直接由 SQLite 读取 `master/master.mdb`，关联 `text_data` 获取曲名（id=16）、服装名（id=14）与马娘角色名（id=6）。
4. **内存安全解密**：利用 `FKey` 异或解密 AssetBundle，由 `UnityPy` 在内存中抽取 `TextAsset` 与 `GameObject` 树，零临时文件产生。
