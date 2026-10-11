using System;
using System.Collections.Generic;
using Gallop.Live.Cutt;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// 舞台监视器驱动分部类（绑定解析与状态数据）：
    /// 负责管理监视器材质绑定状态、时间轴名称匹配算法、点唱机屏幕别名解析与服装条件判断，
    /// 将数据结构与匹配运算从主驱动逻辑中解耦，严格保持单文件低于 1000 行。
    /// </summary>
    public partial class StageMonitorDriver
    {
        /// <summary>
        /// 监视器网格材质绑定对象：封装单个网格材质、名称索引、基准参数以及点唱机屏幕标识
        /// </summary>
        private sealed class MonitorMaterialBinding
        {
            public Renderer renderer;
            public Material material;
            public string rendererKey, materialKey, rendererCompact, materialCompact, groupKey;
            public Vector2 baseFilterScale = Vector2.one, baseFilterOffset = Vector2.zero;
            public float baseAlpha = 1f;
            public Color baseColor = Color.white, baseColorFade = Color.clear;
            public bool hasSrcBlendMode, hasDstBlendMode, hasAppliedState;
            public float baseSrcBlendMode, baseDstBlendMode;
            public MonitorShaderState appliedState;

            // 适配点唱机屏幕节点：标记是否属于点唱机屏幕并记录其原始贴图与 UV 参数，用于平滑回退
            public bool isAudioMonitor;
            public Texture baseMainTex;
            public Vector2 baseMainScale = Vector2.one, baseMainOffset = Vector2.zero;
        }

        /// <summary>
        /// 监视器单通道纹理播放状态
        /// </summary>
        private struct MonitorTextureState
        {
            public Texture texture, maskTexture;
            public int imageIndex;
            public Vector2 offset, scale;
        }

        /// <summary>
        /// 监视器着色器当前帧应用参数快照
        /// </summary>
        private struct MonitorShaderState
        {
            public MonitorTextureState main, fade;
            public Texture filterTexture;
            public float alpha, width, height, crossFadeRate, filterTexScale;
            public Color colorFade, baseColor;
            public int srcBlendMode, dstBlendMode, renderQueue;
            public bool hasRenderQueue, hasMainTexture, hasFadeTexture, useBlendMode, useBaseColor;
        }

        /// <summary>
        /// 将时间轴轨道名称解析为实际舞台场景中的监视器材质绑定列表
        /// </summary>
        private List<MonitorMaterialBinding> ResolveBindings(string timelineName)
        {
            string normalized = NormalizeName(timelineName);
            if (string.IsNullOrEmpty(normalized)) return EmptyBindingList;
            if (_bindingCache.TryGetValue(normalized, out List<MonitorMaterialBinding> cached)) return cached;

            _resolveBuffer.Clear();
            string compact = CompactName(normalized);
            AddMatchesExact(normalized, compact, _resolveBuffer);
            if (_resolveBuffer.Count == 0) AddMatchesContains(normalized, compact, _resolveBuffer);
            if (_resolveBuffer.Count == 0 && TryExtractMonitorIndex(normalized, out int numericIndex)) AddMatchesByNumericIndex(numericIndex, _resolveBuffer);
            if (_resolveBuffer.Count == 0 && TryExtractMonitorLetterIndex(normalized, out int letterIndex)) AddMatchesByOrdinal(letterIndex, _resolveBuffer);

            List<MonitorMaterialBinding> resolved = new List<MonitorMaterialBinding>(_resolveBuffer.Count);
            for (int i = 0; i < _resolveBuffer.Count; i++)
            {
                MonitorMaterialBinding binding = _resolveBuffer[i];
                if (binding != null && !resolved.Contains(binding)) resolved.Add(binding);
            }
            _bindingCache[normalized] = resolved;
            return resolved;
        }

        /// <summary>
        /// 精确匹配查找绑定目标（支持点唱机屏幕别名 monitor 与 monitoraudio 精确命中）
        /// </summary>
        private void AddMatchesExact(string normalized, string compact, List<MonitorMaterialBinding> result)
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                MonitorMaterialBinding b = _bindings[i];
                if (b == null) continue;
                if (b.materialKey == normalized || b.rendererKey == normalized || b.materialCompact == compact || b.rendererCompact == compact || b.groupKey == normalized || b.groupKey == compact)
                {
                    result.Add(b);
                }
                else if (b.isAudioMonitor && (normalized == "monitor" || normalized == "monitoraudio" || compact == "monitor" || compact == "monitoraudio"))
                {
                    result.Add(b);
                }
            }
        }

        /// <summary>
        /// 包含关系模糊匹配（支持点唱机屏幕节点包含关系命中）
        /// </summary>
        private void AddMatchesContains(string normalized, string compact, List<MonitorMaterialBinding> result)
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                MonitorMaterialBinding b = _bindings[i];
                if (b == null) continue;
                if (b.materialKey.Contains(normalized) || b.rendererKey.Contains(normalized) || (!string.IsNullOrEmpty(compact) && (b.materialCompact.Contains(compact) || b.rendererCompact.Contains(compact))))
                {
                    result.Add(b);
                }
                else if (b.isAudioMonitor && (normalized.Contains("monitor") || compact.Contains("monitor")))
                {
                    result.Add(b);
                }
            }
        }

        /// <summary>
        /// 根据数字序号（如 monitor000、monitor1 等）匹配对应的屏幕部件
        /// </summary>
        private void AddMatchesByNumericIndex(int monitorIndex, List<MonitorMaterialBinding> result)
        {
            string groupKey = $"monitor{monitorIndex:D3}", compactKey = CompactName(groupKey), relaxedKey = $"monitor{monitorIndex}";
            for (int i = 0; i < _bindings.Count; i++)
            {
                MonitorMaterialBinding b = _bindings[i];
                if (b == null) continue;
                if (b.groupKey == groupKey || b.materialKey.Contains(groupKey) || b.rendererKey.Contains(groupKey) ||
                    b.materialCompact.Contains(compactKey) || b.rendererCompact.Contains(compactKey) ||
                    b.materialCompact.Contains(relaxedKey) || b.rendererCompact.Contains(relaxedKey))
                    result.Add(b);
            }
        }

        /// <summary>
        /// 按字母序号顺位匹配屏幕部件
        /// </summary>
        private void AddMatchesByOrdinal(int ordinal, List<MonitorMaterialBinding> result)
        {
            if (ordinal < 0 || _bindings.Count == 0) return;
            List<string> groups = new List<string>(_bindings.Count);
            for (int i = 0; i < _bindings.Count; i++)
            {
                string groupKey = _bindings[i]?.groupKey;
                if (!string.IsNullOrEmpty(groupKey) && !groups.Contains(groupKey)) groups.Add(groupKey);
            }
            groups.Sort(StringComparer.OrdinalIgnoreCase);
            if (ordinal >= groups.Count) return;

            string targetGroup = groups[ordinal];
            for (int i = 0; i < _bindings.Count; i++)
            {
                MonitorMaterialBinding b = _bindings[i];
                if (b != null && b.groupKey == targetGroup) result.Add(b);
            }
        }

        /// <summary>
        /// 校验多角色服装条件是否与当前舞台登场马娘一致
        /// </summary>
        private bool DoesChangeConditionMatchCurrentCharacters(LiveTimelineMonitorDressCondition[] conditions)
        {
            if (conditions == null || conditions.Length == 0) return true;
            for (int i = 0; i < conditions.Length; i++)
            {
                LiveTimelineMonitorDressCondition condition = conditions[i];
                if (condition != null && condition.IsEnabled && !DoesSingleConditionMatchCurrentCharacters(condition))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 校验单个服装条件是否在当前马娘容器中匹配
        /// </summary>
        private bool DoesSingleConditionMatchCurrentCharacters(LiveTimelineMonitorDressCondition condition)
        {
            if (condition == null || !condition.IsEnabled) return true;
            Director director = Director.instance;
            if (director?.CharaContainerScript == null || director.CharaContainerScript.Count == 0) return false;

            for (int i = 0; i < director.CharaContainerScript.Count; i++)
            {
                UmaContainerCharacter container = director.CharaContainerScript[i];
                if (container == null) continue;
                int charaId = GetContainerCharaId(container);
                int dressId = GetContainerDressId(container);
                if ((condition.CharaId <= 0 || condition.CharaId == charaId) && (condition.DressId <= 0 || condition.DressId == dressId))
                    return true;
            }
            return false;
        }

        private static int GetContainerCharaId(UmaContainerCharacter container)
        {
            if (container == null) return 0;
            if (container.CharaEntry != null && container.CharaEntry.Id > 0) return container.CharaEntry.Id;
            if (container.CharaData != null)
            {
                try
                {
                    object idValue = container.CharaData["id"];
                    if (idValue != null && int.TryParse(idValue.ToString(), out int charaId)) return charaId;
                }
                catch { }
            }
            return 0;
        }

        private static int GetContainerDressId(UmaContainerCharacter container)
        {
            if (container == null) return 0;
            if (TryParseDressIdPrefix(container.VarCostumeIdLong, out int dressId)) return dressId;
            if (TryParseDressIdPrefix(container.VarCostumeIdShort, out dressId)) return dressId;
            return 0;
        }

        private static bool TryParseDressIdPrefix(string costumeId, out int dressId)
        {
            dressId = 0;
            if (string.IsNullOrWhiteSpace(costumeId)) return false;
            string[] parts = costumeId.Split('_');
            return parts.Length > 0 && int.TryParse(parts[0], out dressId);
        }

        private static bool HasTextureProperty(Material material, string propertyName) => material != null && !string.IsNullOrEmpty(propertyName) && material.HasProperty(propertyName);
        private static bool TryHasProperty(Material material, string propertyName) => material != null && !string.IsNullOrEmpty(propertyName) && material.HasProperty(propertyName);
        private static bool TrySetFloat(Material material, string propertyName, float value) { if (!TryHasProperty(material, propertyName)) return false; material.SetFloat(propertyName, value); return true; }
        private static bool TrySetColor(Material material, string propertyName, Color value) { if (!TryHasProperty(material, propertyName)) return false; material.SetColor(propertyName, value); return true; }
        private static bool IsColorEffectivelyClear(Color value) => value.a <= 0.0001f && value.r <= 0.0001f && value.g <= 0.0001f && value.b <= 0.0001f;
        private static bool Approximately(float a, float b) => Mathf.Abs(a - b) <= 0.0001f;
        private static bool Approximately(Vector2 a, Vector2 b) => Approximately(a.x, b.x) && Approximately(a.y, b.y);
        private static bool Approximately(Color a, Color b) => Approximately(a.r, b.r) && Approximately(a.g, b.g) && Approximately(a.b, b.b) && Approximately(a.a, b.a);

        private static string NormalizeName(string value) => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("(Instance)", string.Empty).Replace("(Clone)", string.Empty).Trim().ToLowerInvariant();

        private static string CompactName(string value)
        {
            string normalized = NormalizeName(value);
            if (string.IsNullOrEmpty(normalized)) return string.Empty;
            char[] buffer = new char[normalized.Length];
            int count = 0;
            for (int i = 0; i < normalized.Length; i++) { char c = normalized[i]; if (char.IsLetterOrDigit(c)) buffer[count++] = c; }
            return count > 0 ? new string(buffer, 0, count) : string.Empty;
        }

        private static string BuildGroupKey(MonitorMaterialBinding binding)
        {
            if (binding == null) return string.Empty;
            if (TryExtractMonitorIndex(binding.materialKey, out int matIdx)) return $"monitor{matIdx:D3}";
            if (TryExtractMonitorIndex(binding.rendererKey, out int renIdx)) return $"monitor{renIdx:D3}";
            if (!string.IsNullOrEmpty(binding.materialCompact) && binding.materialCompact.Contains("monitor")) return binding.materialCompact;
            if (!string.IsNullOrEmpty(binding.rendererCompact) && binding.rendererCompact.Contains("monitor")) return binding.rendererCompact;
            return !string.IsNullOrEmpty(binding.materialCompact) ? binding.materialCompact : binding.rendererCompact;
        }

        private static bool TryExtractMonitorIndex(string value, out int index)
        {
            index = -1;
            string compact = CompactName(value);
            if (string.IsNullOrEmpty(compact)) return false;
            int monitorIndex = compact.IndexOf("monitor", StringComparison.OrdinalIgnoreCase);
            if (monitorIndex < 0) return false;
            monitorIndex += "monitor".Length;
            int start = monitorIndex;
            while (monitorIndex < compact.Length && char.IsDigit(compact[monitorIndex])) monitorIndex++;
            return monitorIndex > start && int.TryParse(compact.Substring(start, monitorIndex - start), out index);
        }

        private static bool TryExtractMonitorLetterIndex(string value, out int index)
        {
            index = -1;
            string compact = CompactName(value);
            if (string.IsNullOrEmpty(compact) || !compact.StartsWith("monitor", StringComparison.OrdinalIgnoreCase) || compact.Length != "monitor".Length + 1) return false;
            char c = compact[compact.Length - 1];
            if (c < 'a' || c > 'z') return false;
            index = c - 'a';
            return true;
        }
    }
}
