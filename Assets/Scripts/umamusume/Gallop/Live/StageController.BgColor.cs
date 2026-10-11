using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Gallop.Live
{
    [Serializable]
    public class BgColorBindingOverride
    {
        public string timelineName;
        public string[] rendererNameContains;
        public string[] materialNameContains;
    }

    [Serializable]
    public class NeonMaterialInfo
    {
        [SerializeField] private Material _mainMaterial;
        [SerializeField] private Material _backMaterial;

        public Material MainMaterial => _mainMaterial;
        public Material BackMaterial => _backMaterial;
    }

    /// <summary>
    /// StageController 背景颜色驱动分部类：
    /// 负责 BgColor1 与 BgColor2 时间轴轨道事件驱动、舞台材质色彩匹配评分、缓存重构与运行时多材质属性下发。
    /// </summary>
    public partial class StageController
    {
        private enum BgColor2RuntimeKind
        {
            Wash,
            Laser,
            Foot,
            NeonMain,
            NeonBack,
            LegacyFallback
        }

        private sealed class BgColor2RuntimeGroup
        {
            public BgColor2RuntimeKind kind;
            public int sourceIndex;
            public Material sourceMaterial;
            public string sourceKey;
            public string sourceName;
            public readonly List<BgColor2RuntimeBinding> bindings = new List<BgColor2RuntimeBinding>(8);
            public readonly List<Renderer> renderers = new List<Renderer>(8);
        }

        private sealed class BgColor2RuntimeBinding
        {
            public Renderer renderer;
            public int materialIndex;
        }

        [Header("Official-like BgColor2 material sources")]
        [SerializeField] private Material[] _washLightMaterials;
        [SerializeField] private Material[] _footLightMaterials;
        [SerializeField] private NeonMaterialInfo[] _neonMaterialInfos;

        [Header("BgColor direct driver")]
        [SerializeField] private bool _enableBgColorDriver = false;
        [SerializeField] private bool _bgColorFallbackToAllEligible = false;
        [SerializeField] private bool _bgColorVerboseLog = false;
        [SerializeField] private float[] _bgColorExtraValueTable = new float[] { 1f };
        [SerializeField] private BgColorBindingOverride[] _bgColorBindingOverrides;

        private readonly Dictionary<string, List<Renderer>> _bgColorRendererCache = new Dictionary<string, List<Renderer>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Renderer> _bgColorAllEligibleRenderers = new List<Renderer>(256);
        private readonly HashSet<string> _bgColorMissingLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, List<BgColor2RuntimeGroup>> _bgColor2GroupCache = new Dictionary<string, List<BgColor2RuntimeGroup>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BgColor2RuntimeGroup> _bgColor2Groups = new List<BgColor2RuntimeGroup>(64);
        private readonly List<Renderer> _allStageRenderers = new List<Renderer>(256);
        private readonly Dictionary<Material, List<BgColor2RuntimeBinding>> _bindingsBySharedMaterialRef = new Dictionary<Material, List<BgColor2RuntimeBinding>>();
        private readonly Dictionary<string, List<BgColor2RuntimeBinding>> _bindingsBySharedMaterialName = new Dictionary<string, List<BgColor2RuntimeBinding>>(StringComparer.OrdinalIgnoreCase);

        private StageColorBinder _stageColorBinder;
        private readonly List<Renderer> _bgColor1ExactHits = new List<Renderer>(32);

        public void OnUpdateBgColor1(ref BgColor1UpdateInfo updateInfo)
        {
            UpdateBgColor1(ref updateInfo);
        }

        private void UpdateBgColor1(ref BgColor1UpdateInfo updateInfo)
        {
            if (!_enableBgColorDriver)
                return;

            EnsureStageColorBinder();
            StageColorLane lane = StageColorLane.FromTimeline(updateInfo.TimelineName, updateInfo.TimelineNameHash);

            // 角色色只给角色，舞台/天空/草地一律不写。
            if (lane.Kind == StageColorLaneKind.Character)
                return;

            if (lane.Kind == StageColorLaneKind.Ambient)
            {
                ApplyAmbientLane(ref updateInfo);
                return;
            }

            if (lane.Kind == StageColorLaneKind.Sky)
            {
                StageSkyController skyCtrl = GetComponent<StageSkyController>() ?? GetComponentInChildren<StageSkyController>();
                if (skyCtrl != null)
                    skyCtrl.OnUpdateBgColor1(ref updateInfo);
                return;
            }

            if (lane.Kind != StageColorLaneKind.Stage)
                return;

            // 按时间轴名字/FNV 精确命中；没命中就停，不再广播全部 eligible renderer。
            if (!_stageColorBinder.TryFillExactHits(this, in lane, _bgColor1ExactHits))
                return;

            if (StageRuntimeDiagnostics.EnableDiagnostics)
                StageRuntimeDiagnostics.LogBgColorHit(updateInfo.TimelineName, 1, _bgColor1ExactHits, updateInfo.color, updateInfo.colorPower);

            for (int i = 0; i < _bgColor1ExactHits.Count; i++)
            {
                Renderer r = _bgColor1ExactHits[i];
                if (r == null)
                    continue;
                _stageColorBinder.WriteMulColor(r, updateInfo.color, updateInfo.colorPower);
            }
        }

        private void ApplyAmbientLane(ref BgColor1UpdateInfo updateInfo)
        {
            EnsureStageColorBinder();
            float power = updateInfo.colorPower > 0f ? updateInfo.colorPower : 1f;
            Color ambient = updateInfo.color * power;
            _stageColorBinder.WriteAmbient(ambient);

            Gallop.Cyalume.MobShadowController mob = GetComponentInChildren<Gallop.Cyalume.MobShadowController>(true);
            if (mob != null)
                mob.SetAmbientColor(ambient);
        }

        private void EnsureStageColorBinder()
        {
            if (_stageColorBinder == null)
                _stageColorBinder = new StageColorBinder();
        }

        private void UpdateBgColor2(ref BgColor2UpdateInfo updateInfo)
        {
            if (!_enableBgColorDriver)
                return;

            float extra = ResolveBgColorExtraValue(updateInfo.randomTableIndex);
            var groups = ResolveBgColor2Groups(updateInfo.TimelineName);

            // 运行时诊断：彻底剔除 SelectMany.Distinct.ToList 堆分配，仅在开启诊断时以轻量方式收集
            if (StageRuntimeDiagnostics.EnableDiagnostics)
            {
                List<Renderer> logTargets = null;
                if (groups != null && groups.Count > 0)
                {
                    logTargets = new List<Renderer>();
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        var grp = groups[gi];
                        if (grp == null || grp.renderers == null) continue;
                        for (int ri = 0; ri < grp.renderers.Count; ri++)
                        {
                            var rend = grp.renderers[ri];
                            if (rend != null && !logTargets.Contains(rend))
                                logTargets.Add(rend);
                        }
                    }
                }
                StageRuntimeDiagnostics.LogBgColorHit(updateInfo.TimelineName, 2, logTargets, updateInfo.color1, updateInfo.power);
            }

            string tlNameLower = (updateInfo.TimelineName ?? "").ToLowerInvariant();
            bool timelineIsSky = tlNameLower.Contains("sky");

            if (groups != null && groups.Count > 0)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    if (group == null) continue;
                    for (int j = 0; j < group.bindings.Count; j++)
                    {
                        var binding = group.bindings[j];
                        if (binding == null) continue;

                        var r = binding.renderer;
                        if (r == null) continue;

                        // 核心安全防护：严格遵循指示，部分物品（如天空球）绝不能被非天空光效污染
                        string rName = r.name.ToLowerInvariant();
                        if (!timelineIsSky && rName.Contains("sky"))
                            continue;

                        // 核心性能优化：全面使用 sharedMaterials 替代 materials，杜绝深拷贝克隆材质与高频 GC 停顿
                        Material[] mats;
                        try { mats = r.sharedMaterials; }
                        catch { continue; }
                        if (mats == null) continue;

                        if (binding.materialIndex < 0 || binding.materialIndex >= mats.Length)
                            continue;

                        var mat = mats[binding.materialIndex];
                        if (mat == null) continue;

                        ApplyBgColor2ToRuntimeGroup(group.kind, mat, ref updateInfo, extra);
                    }
                }
                return;
            }

            // 退回到旧的名字匹配，仅作为最后兜底。
            var legacyRendererTargets = ResolveBgColorRenderers(updateInfo.TimelineName, wantBgColor2Style: true, allowAllEligibleFallback: false);
            if (legacyRendererTargets == null || legacyRendererTargets.Count == 0)
                return;

            for (int i = 0; i < legacyRendererTargets.Count; i++)
            {
                var r = legacyRendererTargets[i];
                if (r == null) continue;

                // 核心安全防护：严格遵循指示，部分物品（如天空球）绝不能被非天空光效污染
                string rName = r.name.ToLowerInvariant();
                if (!timelineIsSky && rName.Contains("sky"))
                    continue;

                // 核心性能优化：兜底分支同样使用 sharedMaterials 替代 materials
                Material[] mats;
                try { mats = r.sharedMaterials; }
                catch { continue; }
                if (mats == null) continue;

                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;
                    ApplyBgColor2ToRuntimeGroup(BgColor2RuntimeKind.LegacyFallback, mat, ref updateInfo, extra);
                }
            }
        }

        private void ApplyBgColor2ToRuntimeGroup(BgColor2RuntimeKind kind, Material mat, ref BgColor2UpdateInfo updateInfo, float extra)
        {
            if (mat == null) return;

            switch (kind)
            {
                case BgColor2RuntimeKind.Wash:
                    ApplyOrdinaryBgColor2(mat, updateInfo.color1, updateInfo.color2, updateInfo.power, setExtraMultiply: false, extraValue: extra);
                    break;
                case BgColor2RuntimeKind.Laser:
                    ApplyOrdinaryBgColor2(mat, updateInfo.color1, updateInfo.color2, updateInfo.power, setExtraMultiply: true, extraValue: extra);
                    break;
                case BgColor2RuntimeKind.Foot:
                    ApplyOrdinaryBgColor2(mat, updateInfo.color1, updateInfo.color2, updateInfo.power, setExtraMultiply: false, extraValue: extra);
                    break;
                case BgColor2RuntimeKind.NeonMain:
                    ApplyNeonMainBgColor2(mat, updateInfo.color1, updateInfo.color2, updateInfo.power);
                    break;
                case BgColor2RuntimeKind.NeonBack:
                    ApplyNeonBackBgColor2(mat, updateInfo.color1, updateInfo.power);
                    break;
                default:
                    ApplyLegacyBgColor2(mat, updateInfo.color1, updateInfo.color2, updateInfo.power, extra);
                    break;
            }
        }

        private static void ApplyOrdinaryBgColor2(Material mat, Color color1, Color color2, float power, bool setExtraMultiply, float extraValue)
        {
            bool hasAny = false;
            if (mat.HasProperty("_MulColor0")) { mat.SetColor("_MulColor0", color1); hasAny = true; }
            if (mat.HasProperty("_MulColor1")) { mat.SetColor("_MulColor1", color2); hasAny = true; }
            if (mat.HasProperty("_BlinkLightColor") && !mat.HasProperty("_MulColor0") && !mat.HasProperty("_MulColor1")) { mat.SetColor("_BlinkLightColor", color1); hasAny = true; }
            if (mat.HasProperty("_ColorPower")) { mat.SetFloat("_ColorPower", power); hasAny = true; }
            if (setExtraMultiply && mat.HasProperty("_ColorPowerMultiply")) { mat.SetFloat("_ColorPowerMultiply", extraValue); hasAny = true; }
            if (!hasAny && mat.HasProperty("_Color")) mat.SetColor("_Color", color1);
        }

        private static void ApplyNeonMainBgColor2(Material mat, Color color1, Color color2, float power)
        {
            bool wroteAny = false;
            if (mat.HasProperty("_MulColor0")) { mat.SetColor("_MulColor0", color2); wroteAny = true; }
            if (mat.HasProperty("_MulColor1")) { mat.SetColor("_MulColor1", color1); wroteAny = true; }
            if (mat.HasProperty("_BlinkLightColor") && !mat.HasProperty("_MulColor1")) { mat.SetColor("_BlinkLightColor", color1); wroteAny = true; }
            if (mat.HasProperty("_ColorPower")) { mat.SetFloat("_ColorPower", power); wroteAny = true; }
            if (!wroteAny && mat.HasProperty("_Color")) mat.SetColor("_Color", color1);
        }

        private static void ApplyNeonBackBgColor2(Material mat, Color color1, float power)
        {
            bool wroteAny = false;
            if (mat.HasProperty("_MulColor1")) { mat.SetColor("_MulColor1", color1); wroteAny = true; }
            else if (mat.HasProperty("_MulColor0")) { mat.SetColor("_MulColor0", color1); wroteAny = true; }

            if (mat.HasProperty("_BlinkLightColor")) { mat.SetColor("_BlinkLightColor", color1); wroteAny = true; }
            if (mat.HasProperty("_ColorPower")) { mat.SetFloat("_ColorPower", power); wroteAny = true; }
            if (!wroteAny && mat.HasProperty("_Color")) mat.SetColor("_Color", color1);
        }

        private static void ApplyLegacyBgColor2(Material mat, Color color1, Color color2, float power, float extra)
        {
            bool hasMul0 = mat.HasProperty("_MulColor0");
            bool hasMul1 = mat.HasProperty("_MulColor1");
            bool hasPower = mat.HasProperty("_ColorPower");
            bool hasMultiply = mat.HasProperty("_ColorPowerMultiply");
            bool hasBlinkColor = mat.HasProperty("_BlinkLightColor");

            if (!hasMul0 && !hasMul1 && !hasPower && !hasMultiply && !hasBlinkColor)
                return;

            if (hasMul0) mat.SetColor("_MulColor0", color1);
            if (hasMul1) mat.SetColor("_MulColor1", color2);
            if (hasBlinkColor && !hasMul0 && !hasMul1) mat.SetColor("_BlinkLightColor", color1);
            if (hasPower) mat.SetFloat("_ColorPower", power);
            if (hasMultiply) mat.SetFloat("_ColorPowerMultiply", extra);
        }

        private void RebuildBgColorCache()
        {
            _bgColorRendererCache.Clear();
            _bgColor2GroupCache.Clear();
            _bgColorMissingLogged.Clear();
            _bgColorAllEligibleRenderers.Clear();
            _allStageRenderers.Clear();
            _bgColor2Groups.Clear();
            _bindingsBySharedMaterialRef.Clear();
            _bindingsBySharedMaterialName.Clear();

            EnsureStageColorBinder();
            _stageColorBinder.Rebuild(this);

            var all = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                _allStageRenderers.Add(r);
                IndexRendererSharedMaterials(r);

                if (RendererHasAnyBgColorProps(r))
                    _bgColorAllEligibleRenderers.Add(r);
            }

            BuildBgColor2RuntimeGroups();

            if (_bgColorVerboseLog)
            {
                Debug.Log($"[StageController] BgColor cache rebuilt. eligibleRenderers={_bgColorAllEligibleRenderers.Count}, materialGroups={_bgColor2Groups.Count}");
            }
        }

        private void IndexRendererSharedMaterials(Renderer r)
        {
            var mats = r.sharedMaterials;
            if (mats == null) return;

            for (int i = 0; i < mats.Length; i++)
            {
                var mat = mats[i];
                if (mat == null) continue;

                var binding = new BgColor2RuntimeBinding { renderer = r, materialIndex = i };

                if (!_bindingsBySharedMaterialRef.TryGetValue(mat, out var byRef))
                {
                    byRef = new List<BgColor2RuntimeBinding>();
                    _bindingsBySharedMaterialRef[mat] = byRef;
                }
                byRef.Add(binding);

                string key = NormalizeKey(CleanMaterialName(mat.name));
                if (string.IsNullOrEmpty(key)) continue;

                if (!_bindingsBySharedMaterialName.TryGetValue(key, out var byName))
                {
                    byName = new List<BgColor2RuntimeBinding>();
                    _bindingsBySharedMaterialName[key] = byName;
                }
                byName.Add(binding);
            }
        }

        private void BuildBgColor2RuntimeGroups()
        {
            AddRuntimeGroupsFromMaterialArray(_washLightMaterials, BgColor2RuntimeKind.Wash);
            AddLaserRuntimeGroups();
            AddRuntimeGroupsFromMaterialArray(_footLightMaterials, BgColor2RuntimeKind.Foot);

            if (_neonMaterialInfos != null)
            {
                for (int i = 0; i < _neonMaterialInfos.Length; i++)
                {
                    var info = _neonMaterialInfos[i];
                    if (info == null) continue;
                    AddRuntimeGroup(info.MainMaterial, BgColor2RuntimeKind.NeonMain, i);
                    AddRuntimeGroup(info.BackMaterial, BgColor2RuntimeKind.NeonBack, i);
                }
            }
        }

        private void AddRuntimeGroupsFromMaterialArray(Material[] sourceMaterials, BgColor2RuntimeKind kind)
        {
            if (sourceMaterials == null) return;
            for (int i = 0; i < sourceMaterials.Length; i++)
            {
                AddRuntimeGroup(sourceMaterials[i], kind, i);
            }
        }

        private void AddRuntimeGroup(Material sourceMaterial, BgColor2RuntimeKind kind, int sourceIndex)
        {
            if (sourceMaterial == null) return;

            var group = new BgColor2RuntimeGroup
            {
                kind = kind,
                sourceIndex = sourceIndex,
                sourceMaterial = sourceMaterial,
                sourceName = CleanMaterialName(sourceMaterial.name),
                sourceKey = NormalizeKey(CleanMaterialName(sourceMaterial.name))
            };

            var collected = CollectBindingsBySourceMaterial(sourceMaterial);
            if (collected != null && collected.Count > 0)
            {
                group.bindings.AddRange(collected);

                var uniqueRenderers = new HashSet<Renderer>();
                for (int i = 0; i < collected.Count; i++)
                {
                    var renderer = collected[i]?.renderer;
                    if (renderer != null && uniqueRenderers.Add(renderer))
                        group.renderers.Add(renderer);
                }
            }

            _bgColor2Groups.Add(group);

            if (_bgColorVerboseLog)
            {
                Debug.Log($"[StageController] BgColor2 runtime group built. kind={kind}, src='{group.sourceName}', bindings={group.bindings.Count}, renderers={group.renderers.Count}");
            }
        }

        private List<BgColor2RuntimeBinding> CollectBindingsBySourceMaterial(Material sourceMaterial)
        {
            var list = new List<BgColor2RuntimeBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (sourceMaterial == null) return list;

            if (_bindingsBySharedMaterialRef.TryGetValue(sourceMaterial, out var byRef))
            {
                AddBindings(list, seen, byRef);
            }

            if (list.Count == 0)
            {
                string sourceKey = NormalizeKey(CleanMaterialName(sourceMaterial.name));
                if (!string.IsNullOrEmpty(sourceKey) && _bindingsBySharedMaterialName.TryGetValue(sourceKey, out var byName))
                {
                    AddBindings(list, seen, byName);
                }
            }

            return list;
        }

        private static void AddBindings(List<BgColor2RuntimeBinding> dst, HashSet<string> seen, List<BgColor2RuntimeBinding> src)
        {
            if (dst == null || seen == null || src == null) return;

            for (int i = 0; i < src.Count; i++)
            {
                var binding = src[i];
                if (binding == null || binding.renderer == null) continue;

                string key = binding.renderer.GetInstanceID().ToString() + ":" + binding.materialIndex.ToString();
                if (!seen.Add(key)) continue;

                dst.Add(binding);
            }
        }

        private List<BgColor2RuntimeGroup> ResolveBgColor2Groups(string timelineName)
        {
            string key = NormalizeKey(timelineName);
            if (string.IsNullOrEmpty(key)) return null;

            if (_bgColor2GroupCache.TryGetValue(key, out var cached))
                return cached;

            var resolved = TryResolveBgColor2GroupsByOverride(timelineName, key);
            if (resolved == null || resolved.Count == 0)
            {
                resolved = TryResolveBgColor2GroupsByTimelineIndex(key);
            }
            if (resolved == null || resolved.Count == 0)
            {
                var bestByKind = new Dictionary<BgColor2RuntimeKind, (int score, BgColor2RuntimeGroup group)>();
                for (int i = 0; i < _bgColor2Groups.Count; i++)
                {
                    var group = _bgColor2Groups[i];
                    if (group == null || group.sourceMaterial == null || group.bindings.Count == 0)
                        continue;

                    int score = ScoreTimelineAgainstSource(key, timelineName, group.sourceName, group.sourceKey);
                    if (score <= 0) continue;

                    if (!bestByKind.TryGetValue(group.kind, out var best) || score > best.score)
                    {
                        bestByKind[group.kind] = (score, group);
                    }
                }

                resolved = bestByKind.Values
                                     .OrderBy(v => v.group.kind)
                                     .Select(v => v.group)
                                     .ToList();
            }

            if ((resolved == null || resolved.Count == 0) && _bgColorVerboseLog && _bgColorMissingLogged.Add($"bg2::{timelineName}"))
            {
                Debug.LogWarning($"[StageController] BgColor2 material group not found for timeline '{timelineName}'");
            }
            else if (_bgColorVerboseLog && resolved != null && resolved.Count > 0)
            {
                Debug.Log($"[StageController] BgColor2 '{timelineName}' resolved groups={string.Join(", ", resolved.Select(g => $"{g.kind}:{g.sourceName}:{g.renderers.Count}"))}");
            }

            _bgColor2GroupCache[key] = resolved;
            return resolved;
        }

        private List<BgColor2RuntimeGroup> TryResolveBgColor2GroupsByTimelineIndex(string normalizedTimelineKey)
        {
            if (!TryParseBgColor2TimelineIndex(normalizedTimelineKey, out var family, out var sourceIndex))
                return null;

            var list = new List<BgColor2RuntimeGroup>(2);
            for (int i = 0; i < _bgColor2Groups.Count; i++)
            {
                var group = _bgColor2Groups[i];
                if (group == null || group.sourceIndex != sourceIndex) continue;

                switch (family)
                {
                    case "wash": if (group.kind == BgColor2RuntimeKind.Wash) list.Add(group); break;
                    case "foot": if (group.kind == BgColor2RuntimeKind.Foot) list.Add(group); break;
                    case "laser": if (group.kind == BgColor2RuntimeKind.Laser) list.Add(group); break;
                    case "neon": if (group.kind == BgColor2RuntimeKind.NeonMain || group.kind == BgColor2RuntimeKind.NeonBack) list.Add(group); break;
                }
            }

            return list.Count > 0 ? list : null;
        }

        private static bool TryParseBgColor2TimelineIndex(string normalizedTimelineKey, out string family, out int sourceIndex)
        {
            family = null;
            sourceIndex = -1;

            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgwash", out sourceIndex)) { family = "wash"; return true; }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgfoot", out sourceIndex)) { family = "foot"; return true; }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgneon", out sourceIndex)) { family = "neon"; return true; }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "laser", out sourceIndex) ||
                TryParseTimelineLetterIndex(normalizedTimelineKey, "bglaser", out sourceIndex)) { family = "laser"; return true; }

            return false;
        }

        private static bool TryParseTimelineLetterIndex(string normalizedTimelineKey, string prefix, out int sourceIndex)
        {
            sourceIndex = -1;
            if (string.IsNullOrEmpty(normalizedTimelineKey) || string.IsNullOrEmpty(prefix)) return false;
            if (!normalizedTimelineKey.StartsWith(prefix, StringComparison.Ordinal)) return false;
            if (normalizedTimelineKey.Length != prefix.Length + 1) return false;

            char suffix = normalizedTimelineKey[normalizedTimelineKey.Length - 1];
            if (suffix < 'a' || suffix > 'z') return false;

            sourceIndex = suffix - 'a';
            return true;
        }

        private List<BgColor2RuntimeGroup> TryResolveBgColor2GroupsByOverride(string timelineName, string key)
        {
            if (_bgColorBindingOverrides == null || _bgColorBindingOverrides.Length == 0)
                return null;

            for (int i = 0; i < _bgColorBindingOverrides.Length; i++)
            {
                var rule = _bgColorBindingOverrides[i];
                if (rule == null || NormalizeKey(rule.timelineName) != key) continue;

                var list = new List<BgColor2RuntimeGroup>();
                for (int g = 0; g < _bgColor2Groups.Count; g++)
                {
                    var group = _bgColor2Groups[g];
                    if (group == null || group.sourceMaterial == null) continue;

                    bool materialMatch = rule.materialNameContains == null || rule.materialNameContains.Length == 0;
                    if (!materialMatch)
                    {
                        for (int m = 0; m < rule.materialNameContains.Length; m++)
                        {
                            if (ContainsIgnoreCase(group.sourceName, rule.materialNameContains[m]))
                            {
                                materialMatch = true;
                                break;
                            }
                        }
                    }

                    bool rendererMatch = rule.rendererNameContains == null || rule.rendererNameContains.Length == 0;
                    if (!rendererMatch)
                    {
                        for (int r = 0; r < group.renderers.Count && !rendererMatch; r++)
                        {
                            var rr = group.renderers[r];
                            if (rr == null) continue;
                            for (int t = 0; t < rule.rendererNameContains.Length; t++)
                            {
                                if (ContainsIgnoreCase(rr.name, rule.rendererNameContains[t]))
                                {
                                    rendererMatch = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (materialMatch && rendererMatch)
                        list.Add(group);
                }

                if (list.Count > 0)
                    return list;
            }

            return null;
        }

        private static int ScoreTimelineAgainstSource(string normalizedTimelineKey, string timelineName, string sourceName, string sourceKey)
        {
            if (string.IsNullOrEmpty(normalizedTimelineKey) || string.IsNullOrEmpty(sourceKey))
                return 0;

            if (sourceKey == normalizedTimelineKey) return 1000;
            if (sourceKey.Contains(normalizedTimelineKey) || normalizedTimelineKey.Contains(sourceKey)) return 700;

            int score = 0;
            var tokens = SplitNameTokens(timelineName);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = NormalizeKey(tokens[i]);
                if (string.IsNullOrEmpty(token)) continue;
                if (sourceKey.Contains(token)) score += 50;
            }

            if (!string.IsNullOrEmpty(sourceName))
            {
                string sourceLower = sourceName.ToLowerInvariant();
                if (sourceLower.Contains("wash") && normalizedTimelineKey.Contains("wash")) score += 80;
                if (sourceLower.Contains("laser") && normalizedTimelineKey.Contains("laser")) score += 80;
                if (sourceLower.Contains("foot") && normalizedTimelineKey.Contains("foot")) score += 80;
                if (sourceLower.Contains("neon") && normalizedTimelineKey.Contains("neon")) score += 80;
                if (sourceLower.Contains("led") && normalizedTimelineKey.Contains("led")) score += 30;
            }

            return score;
        }

        private List<Renderer> ResolveBgColorRenderers(string timelineName, bool wantBgColor2Style, bool allowAllEligibleFallback = true)
        {
            // 开关只保留给 Inspector；无论它怎么设，都禁止全量回退。
            if (_bgColorFallbackToAllEligible)
                allowAllEligibleFallback = false;
            allowAllEligibleFallback = false;

            string key = NormalizeKey(timelineName);
            if (string.IsNullOrEmpty(key))
                return null;

            if (_bgColorRendererCache.TryGetValue(key, out var cached))
                return cached;

            var set = new HashSet<Renderer>();

            if (TryAddRenderersFromStageObjectMap(timelineName, key, set) | TryAddRenderersFromUnitMap(timelineName, key, set))
            {
            }

            ApplyBindingOverrides(timelineName, key, set);

            // 核心性能修复：不再每次调用 GetComponentsInChildren（开销巨大），
            // 改为复用 RebuildBgColorCache 在初始化时已填充的 _allStageRenderers 缓存列表
            for (int i = 0; i < _allStageRenderers.Count; i++)
            {
                var r = _allStageRenderers[i];
                if (r == null) continue;
                if (wantBgColor2Style && !RendererHasBgColor2Props(r)) continue;
                if (!wantBgColor2Style && !RendererHasBgColor1Props(r)) continue;
                if (RendererMatchesTimeline(r, timelineName, key))
                    set.Add(r);
            }

            var resolved = set.ToList();

            if (resolved.Count == 0)
            {
                if (_bgColorVerboseLog && !string.IsNullOrEmpty(timelineName) && _bgColorMissingLogged.Add(timelineName))
                    Debug.LogWarning($"[StageController] BgColor target not found for timeline '{timelineName}'");
            }
            else if (_bgColorVerboseLog)
            {
                Debug.Log($"[StageController] BgColor '{timelineName}' resolved renderers={resolved.Count}");
            }

            _bgColorRendererCache[key] = resolved;
            return resolved;
        }

        private bool TryAddRenderersFromStageObjectMap(string timelineName, string key, HashSet<Renderer> set)
        {
            bool added = false;
            foreach (var kv in StageObjectMap)
            {
                if (kv.Value == null) continue;
                string candidate = kv.Key ?? string.Empty;
                string candidateKey = NormalizeKey(candidate);
                if (candidateKey != key && !candidateKey.Contains(key) && !key.Contains(candidateKey))
                    continue;

                AddRenderersRecursive(kv.Value, set);
                added = true;
            }
            return added;
        }

        private bool TryAddRenderersFromUnitMap(string timelineName, string key, HashSet<Renderer> set)
        {
            bool added = false;
            foreach (var kv in StageObjectUnitMap)
            {
                if (kv.Value == null) continue;
                string candidateKey = NormalizeKey(kv.Key ?? string.Empty);
                if (candidateKey != key && !candidateKey.Contains(key) && !key.Contains(candidateKey))
                    continue;

                var children = kv.Value.ChildObjects;
                if (children == null) continue;
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i] == null) continue;
                    AddRenderersRecursive(children[i], set);
                    added = true;
                }
            }
            return added;
        }

        private void ApplyBindingOverrides(string timelineName, string key, HashSet<Renderer> set)
        {
            if (_bgColorBindingOverrides == null || _bgColorBindingOverrides.Length == 0)
                return;

            // 性能修复：复用预缓存的 _allStageRenderers，避免 GetComponentsInChildren 全场景遍历
            for (int i = 0; i < _bgColorBindingOverrides.Length; i++)
            {
                var rule = _bgColorBindingOverrides[i];
                if (rule == null || NormalizeKey(rule.timelineName) != key) continue;

                for (int j = 0; j < _allStageRenderers.Count; j++)
                {
                    var r = _allStageRenderers[j];
                    if (r != null && MatchRule(r, rule))
                        set.Add(r);
                }
            }
        }

        private static bool MatchRule(Renderer r, BgColorBindingOverride rule)
        {
            if (r == null || rule == null) return false;

            bool rendererMatched = rule.rendererNameContains == null || rule.rendererNameContains.Length == 0;
            if (!rendererMatched)
            {
                string rn = r.name ?? string.Empty;
                for (int i = 0; i < rule.rendererNameContains.Length; i++)
                {
                    if (ContainsIgnoreCase(rn, rule.rendererNameContains[i]))
                    {
                        rendererMatched = true;
                        break;
                    }
                }
            }

            bool materialMatched = rule.materialNameContains == null || rule.materialNameContains.Length == 0;
            if (!materialMatched)
            {
                var mats = r.sharedMaterials;
                if (mats != null)
                {
                    for (int i = 0; i < mats.Length && !materialMatched; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        for (int j = 0; j < rule.materialNameContains.Length; j++)
                        {
                            if (ContainsIgnoreCase(m.name, rule.materialNameContains[j]))
                            {
                                materialMatched = true;
                                break;
                            }
                        }
                    }
                }
            }

            return rendererMatched && materialMatched;
        }

        private bool RendererMatchesTimeline(Renderer r, string timelineName, string normalizedKey)
        {
            if (r == null) return false;

            if (StringMatchesTimeline(r.name, normalizedKey)) return true;
            if (r.transform != null && StringMatchesTimeline(r.transform.root.name, normalizedKey)) return true;
            if (r.transform != null && r.transform.parent != null && StringMatchesTimeline(r.transform.parent.name, normalizedKey)) return true;

            var mats = r.sharedMaterials;
            if (mats != null)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m != null && StringMatchesTimeline(m.name, normalizedKey))
                        return true;
                }
            }

            if (!string.IsNullOrEmpty(timelineName))
            {
                var tokens = SplitNameTokens(timelineName);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i];
                    if (string.IsNullOrEmpty(token)) continue;
                    if (ContainsIgnoreCase(r.name, token)) return true;
                    if (r.transform != null && ContainsIgnoreCase(r.transform.root.name, token)) return true;
                    if (mats != null)
                    {
                        for (int j = 0; j < mats.Length; j++)
                        {
                            var m = mats[j];
                            if (m != null && ContainsIgnoreCase(m.name, token))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool StringMatchesTimeline(string candidate, string normalizedKey)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(normalizedKey)) return false;
            string nk = NormalizeKey(candidate);
            return nk == normalizedKey || nk.Contains(normalizedKey) || normalizedKey.Contains(nk);
        }

        private static string[] SplitNameTokens(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
            return s.Replace("-", " ").Replace("_", " ")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 3)
                    .ToArray();
        }

        private static bool ContainsIgnoreCase(string s, string token)
        {
            return !string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(token) && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddRenderersRecursive(GameObject go, HashSet<Renderer> set)
        {
            if (go == null) return;
            var rs = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r != null) set.Add(r);
            }
        }

        private static bool RendererHasAnyBgColorProps(Renderer r)
        {
            return RendererHasBgColor1Props(r) || RendererHasBgColor2Props(r);
        }

        private static bool RendererHasBgColor1Props(Renderer r)
        {
            var mats = r != null ? r.sharedMaterials : null;
            if (mats == null) return false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty("_CharaColor") || m.HasProperty("_ToonDarkColor") || m.HasProperty("_ToonBrightColor") || m.HasProperty("_OutlineColor") || m.HasProperty("_Saturation"))
                    return true;
                // 支持天空球及通用舞台材质的颜色属性
                if (m.HasProperty("_BaseColor") || m.HasProperty("_Color") || m.HasProperty("_MulColor0"))
                    return true;
            }
            return false;
        }

        private static bool RendererHasBgColor2Props(Renderer r)
        {
            var mats = r != null ? r.sharedMaterials : null;
            if (mats == null) return false;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (m.HasProperty("_MulColor0") || m.HasProperty("_MulColor1") || m.HasProperty("_ColorPower") || m.HasProperty("_ColorPowerMultiply") || m.HasProperty("_BlinkLightColor"))
                    return true;
            }
            return false;
        }

        private float ResolveBgColorExtraValue(int index)
        {
            if (_bgColorExtraValueTable != null && index >= 0 && index < _bgColorExtraValueTable.Length)
                return _bgColorExtraValueTable[index];
            return 1f;
        }
    }
}
