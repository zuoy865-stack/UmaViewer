# Uma Viewer

[English](README.md) | [简体中文](README.zh-CN.md)

⚠️ 如果你看到 **“Failed to load il2cpp”**、DLL 加载失败，或者程序无法在 Windows 上启动，可能是被 **Windows Smart App Control（智能应用控制）** 阻止了。

如果启动失败，请关闭 Smart App Control：

`Windows Security → App & browser control → Smart App Control settings → Off`

也就是：

`Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置 → 关闭`

这个问题通常是因为 Windows 阻止了未签名的程序。将程序移动到本地文件夹，或者关闭 Smart App Control 后，程序通常就可以正常启动。

Uma Viewer 是一个 Unity 应用程序，可以方便地查看《赛马娘 Pretty Derby》的游戏资源。

| 版本 | 支持情况 |
|------|----------|
| JP (DMM) | ✅ |
| JP (Steam) | ✅ |
| KR | ✅ |
| TW (台湾服) | ✅ |
| Global | ✅ |

---

## ⚠️ 🌍 EN / Global 用户 ⚠️

在 UmaViewer 的 **Other** 设置页面中，将 **WorkMode** 设置为 **Default**，并将 **Region** 设置为 **Global**。

目前只支持 Default 工作模式。你需要先在游戏设置中使用 **Download All** 按钮下载全部资源，Viewer 才能正常工作。

---

## 🇹🇼 台湾服 / TW 用户

UmaViewer 的**默认地区为日本（JP）**。使用台湾服时，需要手动切换地区：

1. 打开 UmaViewer 的 **Settings → Other** 设置页面。
2. 将 **Region** 从默认的 **Japan (JP)** 更改为 **Taiwan**。
3. 如果程序没有自动识别台湾版游戏数据，请通过 **Settings → Other → Change DataPath** 手动选择台湾版游戏数据所在的文件夹。

台湾版的游戏路径取决于你自己的安装位置。例如，某台电脑上的安装路径为：

```text
D:\KOMOE Game\komoemumamusume\komoemumamusume Game
```

**注意：这只是路径示例，并不是所有台湾服玩家都安装在 D 盘。** 你的游戏也可能安装在 C 盘、E 盘或其他位置。请根据自己的实际安装目录选择相应的游戏数据文件夹，不要直接照抄示例路径。

---

### 要求 / 安装

1. 运行 Viewer 前，需要安装 [Uma Musume: Pretty Derby](https://dmg.umamusume.jp/) 并完整下载游戏数据。台湾服用户应使用自己安装的台湾版游戏客户端和对应数据。

2. 根据你的游戏版本和更新情况，游戏数据可能存放在 **不同的位置**：

   - **DMM / Steam 旧版安装：**

     `C:\Users\<你的用户名>\AppData\LocalLow\Cygames\umamusume(?)\`

   - **DMM / Steam 新版安装：**

     `...\<Umamusume 安装目录>\Umamusume_Data\Persistent(?)\`

   - **台湾版安装路径示例：**

     `D:\KOMOE Game\komoemumamusume\komoemumamusume Game`

     具体路径由你安装台湾版游戏时选择的位置决定。若 Viewer 无法自动识别，请手动定位实际游戏数据目录。

   - 无论是哪一种情况，请确认所选目标文件夹包含 Viewer 所需的游戏数据。常见的目录结构大致如下：

     ```text
     Target Folder\
       meta
       master\
         master.mdb
       dat\
         2A\
         2B\
         ...
     ```

3. 在 [Releases](https://github.com/zuoy865-stack/UmaViewer/releases) 页面下载最新的 `UmaViewer.zip`。

4. 将压缩包解压到任意位置，也可以直接覆盖旧版本。

5. 运行 `UmaViewer.exe`。

6. UmaViewer 会尝试自动检测游戏数据文件夹。

   - 如果自动检测失败或出现错误，请前往：

     **Settings → Other → Change DataPath**

     然后手动选择目标文件夹。

---

## 开发者 / Contributors

1. 推荐使用 [Unity Hub](https://unity3d.com/get-unity/download) 和 [Unity Engine 2022.3.62f1](https://unity.com/releases/editor/archive)。

   较新的 Unity 2022.3.X 版本理论上也应该可以运行。

2. Clone 本仓库，或者下载并解压本仓库。

3. 使用 Unity Hub 导入并打开项目，缺失的文件通常会自动修复。

4. 打开：

   `Assets/Scenes/Version2`

   - 注意：如果 Console 中出现错误，可能需要安装：

     [JSON .NET For Unity](https://assetstore.unity.com/packages/tools/input-management/json-net-for-unity-11347)

PMX 导出器的维护者还应该阅读：

[PMX Export Standard and Postmortem](docs/pmx-export-standard-and-postmortem.md)

> 脚本里面有错误的单词不要改！  
> （Cy 拼错一堆单词，我都无语了 😓）

---

### 功能

| 状态 | 含义 |
|------|------|
| ✓ | 已完成 / 可用 |
| / | 未完成 |
| x | 不支持 |

| 功能 | 状态 |
|------|------|
| 查看主要角色模型 / 动画 | ✓ |
| 在不同角色之间交换服装 / 动画 | ✓ |
| 查看 Q 版角色模型 / 动画 | ✓ |
| 查看 Mob（NPC）模型 | ✓ |
| 播放面部动画、自定义滑块 | ✓ |
| 衣服 / 头发物理 | ✓ |
| 播放 Live 音频和歌词 | ✓ |
| 导出动画到 MMD | ✓ |
| 录制动画（.gif）、截图 | ✓ |
| 查看道具、场景、Live 舞台 | / |
| 导出模型 | / |

支持所有角色和动画。

<img src="https://user-images.githubusercontent.com/59540382/222418271-a6e4ce82-b3a5-47ba-9fc9-4d85120218ec.png" height="350" />

也支持 Mob / 背景角色。

<img src="https://user-images.githubusercontent.com/32562737/219174232-7d0a0eec-8b1c-4571-9c08-8474e06dd3a8.png" height="350" />

支持混合角色服装和动画。

<img src="https://user-images.githubusercontent.com/59540382/222420757-609e1f77-d762-4b39-a7d0-d1fb2d3b79a3.png" height="350" />

支持截图和 `.gif` 录制。

<img src="https://user-images.githubusercontent.com/59540382/222421579-582be5db-5839-4f7c-bf1b-80efc812c4e0.gif" height="350" />

以及更多功能。

<img src="https://user-images.githubusercontent.com/59540382/222422871-12e80e0b-778b-4f42-b581-5e4af5cd6df9.png" height="350" />

---

### 其他推荐项目

[UmaChat by kagari](https://github.com/kagari-bi/UmaChat)

这是一个模型查看器的 Fork，可以让你通过 AI + TTS 和赛马娘角色聊天。

---

### 特别感谢

MarshmallowAndroid：

[UmaMusumeExplorer](https://github.com/MarshmallowAndroid/UmaMusumeExplorer)

感谢该项目提供的 acb / awb 解码器。

