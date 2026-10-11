using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 舞台运行时渲染与材质诊断工具类。
    /// 专门用于在实机运行 Live 时，对天空、草地、监视器等关键舞台渲染器进行快照诊断，
    /// 并追踪 BgColor1 / BgColor2 动画轨道对渲染器的实际命中与染色情况，杜绝盲目猜修。
    /// </summary>
    public static class StageRuntimeDiagnostics
    {
        /// <summary>
        /// 全局诊断开关，默认关闭以彻底消灭热循环中的高频 LogBgColorHit 日志输出与 ExtractStackTrace 反射堆栈回溯。
        /// 若需排查舞台材质染色问题，可在运行时临时置为 true。
        /// </summary>
        public static bool EnableDiagnostics = false;

        /// <summary>
        /// 背景色驱动日志节流字典：记录每个轨道名称上一次输出日志的时间，避免高频刷新刷屏
        /// </summary>
        private static readonly Dictionary<string, float> _lastBgColorLogTimes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 针对当前舞台层级中关注的关键渲染器（天空、草地、监视器、舞台主体等）执行全面快照诊断
        /// </summary>
        /// <param name="stageRoot">舞台根节点</param>
        public static void SnapshotStageRenderers(GameObject stageRoot)
        {
            if (!EnableDiagnostics || stageRoot == null)
                return;

            StringBuilder sb = new StringBuilder(1024);
            sb.AppendLine($"\n[StageRuntimeDiagnostics] ========== 舞台关键渲染器诊断快照 (Root: {stageRoot.name}) ==========");

            // 1. 检查主相机及天空盒清屏状态
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                sb.AppendLine($"[Camera] 主相机: {mainCam.name}, ClearFlags: {mainCam.clearFlags}, BgColor: {mainCam.backgroundColor}, CullingMask: 0x{mainCam.cullingMask:X}");
            }
            else
            {
                sb.AppendLine("[Camera] 未找到 Camera.main 主相机");
            }
            sb.AppendLine($"[RenderSettings] Skybox: {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")}, AmbientLight: {RenderSettings.ambientLight}");

            // 2. 遍历并诊断关键部件渲染器
            Renderer[] allRenderers = stageRoot.GetComponentsInChildren<Renderer>(true);
            int matchedCount = 0;

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer r = allRenderers[i];
                if (r == null) continue;

                string rName = r.name.ToLowerInvariant();
                string goName = r.gameObject.name.ToLowerInvariant();

                // 筛选我们重点关注的天空、草地、大屏幕以及特定背景部件
                bool isTarget = rName.Contains("sky") || rName.Contains("grass") || rName.Contains("monitor") ||
                               goName.Contains("sky") || goName.Contains("grass") || goName.Contains("monitor");

                if (!isTarget)
                    continue;

                matchedCount++;
                string path = GetHierarchyPath(r.transform);
                Material[] sharedMats = r.sharedMaterials;

                sb.AppendLine($"\n  -> [{r.GetType().Name}] 节点路径: {path}");
                sb.AppendLine($"     GameObject激活: {r.gameObject.activeInHierarchy}, 组件启用: {r.enabled}, 层级Layer: {r.gameObject.layer}, 包围盒: {r.bounds}");

                if (sharedMats == null || sharedMats.Length == 0)
                {
                    sb.AppendLine("     [警告] sharedMaterials 为空或长度为 0！");
                    continue;
                }

                for (int m = 0; m < sharedMats.Length; m++)
                {
                    Material mat = sharedMats[m];
                    if (mat == null)
                    {
                        sb.AppendLine($"     材质 [{m}]: NULL！");
                        continue;
                    }

                    Shader s = mat.shader;
                    string shaderName = s != null ? s.name : "null";
                    bool isSupported = s != null && s.isSupported;
                    bool isInternalError = shaderName == "Hidden/InternalErrorShader";

                    // 检查主纹理是否绑定
                    bool hasMainTex = mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null;
                    bool hasBaseMap = mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") != null;

                    // 检查常规颜色与混合状态属性
                    string colDesc = $" queue={mat.renderQueue}";
                    if (mat.HasProperty("_SrcBlend")) colDesc += $" _SrcBlend={mat.GetFloat("_SrcBlend")}";
                    if (mat.HasProperty("_DstBlend")) colDesc += $" _DstBlend={mat.GetFloat("_DstBlend")}";
                    if (mat.HasProperty("_ZWrite")) colDesc += $" _ZWrite={mat.GetFloat("_ZWrite")}";
                    if (mat.HasProperty("_Cull")) colDesc += $" _Cull={mat.GetFloat("_Cull")}";
                    if (mat.HasProperty("_Color")) colDesc += $" _Color={mat.GetColor("_Color")}";
                    if (mat.HasProperty("_BaseColor")) colDesc += $" _BaseColor={mat.GetColor("_BaseColor")}";
                    if (mat.HasProperty("_MulColor0")) colDesc += $" _MulColor0={mat.GetColor("_MulColor0")}";
                    if (mat.HasProperty("_ColorPower")) colDesc += $" _ColorPower={mat.GetFloat("_ColorPower")}";

                    sb.AppendLine($"     材质 [{m}]: '{mat.name}', Shader: '{shaderName}' (支持: {isSupported}, 错误着色器: {isInternalError}), 贴图绑定: (MainTex={hasMainTex}, BaseMap={hasBaseMap}), 属性:{colDesc}");
                }
            }

            sb.AppendLine($"\n[StageRuntimeDiagnostics] 快照诊断完成，共捕获 {matchedCount} 个关键目标渲染器。");
            sb.AppendLine("==========================================================================================");

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 记录背景色驱动（BgColor1 或 BgColor2）的实际命中与染色情况，并做 2 秒节流
        /// </summary>
        public static void LogBgColorHit(string timelineName, int type, List<Renderer> targets, Color color, float power)
        {
            if (!EnableDiagnostics)
                return;

            string key = $"type{type}_{timelineName}";
            float now = Time.realtimeSinceStartup;

            if (_lastBgColorLogTimes.TryGetValue(key, out float lastTime) && (now - lastTime < 2.0f))
            {
                return; // 节流抑制刷屏
            }
            _lastBgColorLogTimes[key] = now;

            int targetCount = targets != null ? targets.Count : 0;
            StringBuilder sb = new StringBuilder(256);
            sb.Append($"[StageRuntimeDiagnostics] BgColor{type} 触发 | 轨道: '{timelineName}', 命中数: {targetCount}, 目标颜色: {color}, 强度: {power}");

            if (targets != null && targetCount > 0)
            {
                sb.Append(" | 命中对象: [");
                for (int i = 0; i < Mathf.Min(5, targetCount); i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(targets[i] != null ? targets[i].name : "null");
                }
                if (targetCount > 5) sb.Append($", ...共{targetCount}个");
                sb.Append("]");
            }

            // 重点预警：若当前轨道名称非天空或草地，却意外命中了天空或草地，输出警告
            if (targets != null && !string.IsNullOrEmpty(timelineName))
            {
                string tlLower = timelineName.ToLowerInvariant();
                bool isSkyTrack = tlLower.Contains("sky");
                bool isGrassTrack = tlLower.Contains("grass");

                for (int i = 0; i < targetCount; i++)
                {
                    var r = targets[i];
                    if (r == null) continue;
                    string rn = r.name.ToLowerInvariant();
                    if (!isSkyTrack && rn.Contains("sky"))
                    {
                        sb.Append($" [警告: 非天空轨道 '{timelineName}' 命中了天空网格 '{r.name}'！]");
                        break;
                    }
                    if (!isGrassTrack && rn.Contains("grass"))
                    {
                        sb.Append($" [警告: 非草地轨道 '{timelineName}' 命中了草地网格 '{r.name}'！]");
                        break;
                    }
                }
            }

            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 获取 Transform 从根到当前节点的完整路径
        /// </summary>
        private static string GetHierarchyPath(Transform tr)
        {
            if (tr == null) return "null";
            string path = tr.name;
            while (tr.parent != null)
            {
                tr = tr.parent;
                path = tr.name + "/" + path;
            }
            return path;
        }
    }
}
