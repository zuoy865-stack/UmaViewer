using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Gallop.Live
{
    [Serializable]
    public class StageObjectUnit
    {
        public string UnitName;
        public GameObject[] ChildObjects;
        public string[] _childObjectNames;
    }

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

    public class StageController : MonoBehaviour
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

        int _logEvery = 30;
        int _logCount = 0;
        public List<GameObject> _stageObjects;
        public StageObjectUnit[] _stageObjectUnits;
        public Dictionary<string, StageObjectUnit> StageObjectUnitMap = new Dictionary<string, StageObjectUnit>();
        public Dictionary<string, GameObject> StageObjectMap = new Dictionary<string, GameObject>();
        public Dictionary<string, Transform> StageParentMap = new Dictionary<string, Transform>();
        [SerializeField] private bool _autoAddBlinkDriver = true;

        [Header("Official-like BgColor2 material sources")]
        [SerializeField] private Material[] _washLightMaterials;
        [SerializeField] private Material[] _laserMaterials;
        [SerializeField] private Material[] _footLightMaterials;
        [SerializeField] private NeonMaterialInfo[] _neonMaterialInfos;

        [Header("BgColor direct driver")]
        [SerializeField] private bool _enableBgColorDriver = true;
        [SerializeField] private bool _bgColorFallbackToAllEligible = true;
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

        [Header("Environment mirror update")]
        [SerializeField] private bool _enableEnvironmentMirrorUpdate = true;
        [SerializeField] private List<MirrorReflection> _environmentMirrorTargets = new List<MirrorReflection>(8);
        [SerializeField, HideInInspector] private float _currentMirrorReflectionRate = 1f;

        [Header("Mirror reflection specific update")]
        [SerializeField] private bool _enableMirrorReflectionSpecificUpdate = true;
        [SerializeField] private int _mirrorBgLayerMask = 0;
        [SerializeField] private int _mirror3dLayerMask = 0;
        [SerializeField] private bool _autoAttachMirrorReflection = true;
        [SerializeField] private bool _mirrorAutoAttachLog = true;
        private readonly Dictionary<int, MirrorReflection> _mirrorByTimelineHash = new Dictionary<int, MirrorReflection>();
        private LiveTimelineControl _boundTimelineControl;

        private void Awake()
        {
            AutoAddDriver("StageBlinkLightDriver");
            AutoAddDriver("StageUVScrollLightDriver");
            AutoAddDriver("StageLaserDriver");
            AutoAddDriver("StageLensFlareDriver");

            InitializeStage();
            RebuildMirrorReflectionCache();
            RebuildBgColorCache();

            Debug.Log("[StageController] stage parts = " +
                string.Join(", ", _stageObjects.ConvertAll(o => o ? o.name : "<null>")));

            if (Director.instance)
                Director.instance._stageController = this;

            TryBindTimelineCallbacks();
        }

        private void OnEnable()
        {
            if (Director.instance)
                Director.instance._stageController = this;

            TryBindTimelineCallbacks();
        }

        private void LateUpdate()
        {
            var dir = Director.instance;
            var ctl = dir ? dir._liveTimelineControl : null;
            if (_boundTimelineControl != ctl)
                TryBindTimelineCallbacks();
        }

        private void TryBindTimelineCallbacks()
        {
            var dir = Director.instance;
            if (!dir)
                return;

            dir._stageController = this;

            var ctl = dir._liveTimelineControl;
            if (ctl == null)
                return;

            if (_boundTimelineControl == ctl)
                return;

            UnbindTimelineCallbacks(_boundTimelineControl);
            _boundTimelineControl = ctl;

            ctl.OnUpdateTransform -= UpdateTransform;
            ctl.OnUpdateObject -= UpdateObject;
            ctl.OnUpdateBgColor1 -= UpdateBgColor1;
            ctl.OnUpdateBgColor2 -= UpdateBgColor2;
            ctl.OnEnvironmentMirror -= UpdateEnvironemntMirror;
            ctl.OnUpdateMirrorReflection -= UpdateMirrorReflection;

            ctl.OnUpdateTransform += UpdateTransform;
            ctl.OnUpdateObject += UpdateObject;
            ctl.OnUpdateBgColor1 += UpdateBgColor1;
            ctl.OnUpdateBgColor2 += UpdateBgColor2;
            ctl.OnEnvironmentMirror += UpdateEnvironemntMirror;
            ctl.OnUpdateMirrorReflection += UpdateMirrorReflection;
        }

        private void AutoAddDriver(string shortTypeName)
        {
            var t = Type.GetType($"{shortTypeName}, Assembly-CSharp")
                 ?? Type.GetType($"Gallop.Live.{shortTypeName}, Assembly-CSharp");

            if (t != null && GetComponent(t) == null)
                gameObject.AddComponent(t);
        }

        private void OnDisable()
        {
            UnbindTimelineCallbacks(_boundTimelineControl);
            _boundTimelineControl = null;
        }

        private void OnDestroy()
        {
            UnbindTimelineCallbacks(_boundTimelineControl);
            _boundTimelineControl = null;
        }

        private void UnbindTimelineCallbacks(LiveTimelineControl ctl)
        {
            if (ctl == null)
                return;

            ctl.OnUpdateTransform -= UpdateTransform;
            ctl.OnUpdateObject -= UpdateObject;
            ctl.OnUpdateBgColor1 -= UpdateBgColor1;
            ctl.OnUpdateBgColor2 -= UpdateBgColor2;
            ctl.OnEnvironmentMirror -= UpdateEnvironemntMirror;
            ctl.OnUpdateMirrorReflection -= UpdateMirrorReflection;
        }

        private void AutoAttachMirrorReflectionComponents()
        {
            if (!_autoAttachMirrorReflection)
                return;

            var renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return;

            int attachedCount = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (!ShouldAutoAttachMirrorReflection(renderer))
                    continue;

                renderer.gameObject.AddComponent<MirrorReflection>();
                attachedCount++;

                if (_mirrorAutoAttachLog)
                    Debug.Log($"[StageController] Auto attached MirrorReflection -> {GetTransformPath(renderer.transform)}");
            }

            if (attachedCount > 0)
                Debug.Log($"[StageController] Auto attached {attachedCount} MirrorReflection component(s).");
        }

        private bool ShouldAutoAttachMirrorReflection(Renderer renderer)
        {
            if (renderer == null)
                return false;

            if (renderer.GetComponent<MirrorReflection>() != null)
                return false;

            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                return false;

            return HasRenderableMirrorMaterial(renderer) || HasMirrorLikeName(renderer.transform);
        }

        private bool HasRenderableMirrorMaterial(Renderer renderer)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0)
                return false;

            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null)
                    continue;

                if (mat.HasProperty("_ReflectionRate") || mat.HasProperty("_ReflectionTex"))
                    return true;

                if (mat.name.IndexOf("mirror", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private bool HasMirrorLikeName(Transform tr)
        {
            while (tr != null && tr != transform)
            {
                string candidate = StripCloneSuffix(tr.name);
                if (candidate.IndexOf("mirror", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                tr = tr.parent;
            }

            return false;
        }

        private string GetTransformPath(Transform tr)
        {
            if (tr == null)
                return "<null>";

            var names = new List<string>(8);
            while (tr != null && tr != transform)
            {
                names.Add(tr.name);
                tr = tr.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private void RebuildMirrorReflectionCache()
        {
            _environmentMirrorTargets.Clear();
            _mirrorByTimelineHash.Clear();
            AutoAttachMirrorReflectionComponents();

            var localMirrors = GetComponentsInChildren<MirrorReflection>(true);
            if (localMirrors != null)
            {
                for (int i = 0; i < localMirrors.Length; i++)
                {
                    RegisterMirrorReflection(localMirrors[i]);
                }
            }

            if (_environmentMirrorTargets.Count == 0)
            {
                var allMirrors = FindObjectsOfType<MirrorReflection>(true);
                if (allMirrors != null)
                {
                    for (int i = 0; i < allMirrors.Length; i++)
                    {
                        RegisterMirrorReflection(allMirrors[i]);
                    }
                }
            }
        }

        private void RegisterMirrorReflection(MirrorReflection mirror)
        {
            if (mirror == null)
                return;

            if (!_environmentMirrorTargets.Contains(mirror))
                _environmentMirrorTargets.Add(mirror);

            RegisterMirrorReflectionAlias(mirror, mirror.name);

            Transform current = mirror.transform.parent;
            while (current != null && current != transform)
            {
                RegisterMirrorReflectionAlias(mirror, current.name);
                current = current.parent;
            }
        }

        private void RegisterMirrorReflectionAlias(MirrorReflection mirror, string alias)
        {
            RegisterMirrorReflectionHash(mirror, GenerateMirrorTimelineHash(alias));

            string normalizedAlias = StripCloneSuffix(alias);
            if (!string.Equals(normalizedAlias, alias, StringComparison.Ordinal))
                RegisterMirrorReflectionHash(mirror, GenerateMirrorTimelineHash(normalizedAlias));
        }

        private void RegisterMirrorReflectionHash(MirrorReflection mirror, int hash)
        {
            if (mirror == null || hash == 0 || _mirrorByTimelineHash.ContainsKey(hash))
                return;

            _mirrorByTimelineHash.Add(hash, mirror);
        }

        private static string StripCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            return name.Replace("(Clone)", "");
        }

        private static int GenerateMirrorTimelineHash(string timelineName)
        {
            if (string.IsNullOrEmpty(timelineName))
                return 0;

            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;

                uint hash = offset;
                for (int i = 0; i < timelineName.Length; i++)
                {
                    hash ^= timelineName[i];
                    hash *= prime;
                }
                return (int)hash;
            }
        }

        private void UpdateEnvironemntMirror(ref EnvironmentMirrorUpdateInfo updateInfo)
        {
            if (!_enableEnvironmentMirrorUpdate)
                return;

            if (!updateInfo.isValid)
                return;

            if (_environmentMirrorTargets == null || _environmentMirrorTargets.Count == 0)
                RebuildMirrorReflectionCache();

            _currentMirrorReflectionRate = updateInfo.mirrorReflectionRate;

            for (int i = 0; i < _environmentMirrorTargets.Count; i++)
            {
                var mirror = _environmentMirrorTargets[i];
                if (mirror == null)
                    continue;

                mirror.SetActive(updateInfo.mirror || updateInfo.bgMirror || updateInfo.IsMirrorBg3d);
                mirror.SetLightMirrorShader(!updateInfo.IsToonMirror);
                mirror.MirrorReflectionRate = updateInfo.mirrorReflectionRate;
            }
        }

        private void UpdateMirrorReflection(in LiveTimelineControl.MirrorReflectionUpdateInfo updateInfo)
        {
            if (!_enableMirrorReflectionSpecificUpdate)
                return;

            if (_mirrorByTimelineHash.Count == 0)
                RebuildMirrorReflectionCache();

            if (!_mirrorByTimelineHash.TryGetValue(updateInfo.TimelineNameHash, out var mirror))
            {
                RebuildMirrorReflectionCache();
                if (!_mirrorByTimelineHash.TryGetValue(updateInfo.TimelineNameHash, out mirror))
                    return;
            }

            if (mirror == null)
                return;

            var mainCamera = Camera.main;
            if (mainCamera != null)
                mirror.SetBaseCamera(mainCamera);

            mirror.SetActive(updateInfo.EnableMirror);
            mirror.SetLightMirrorShader(!updateInfo.IsToonMirror);
            mirror.MirrorReflectionRate = updateInfo.MirrorReflectionRate;

            mirror.ResetCullingMask();

            if (_mirrorBgLayerMask != 0)
            {
                if (updateInfo.EnableBgLayer) mirror.AddCullingMask(_mirrorBgLayerMask);
                else mirror.RemoveCullingMask(_mirrorBgLayerMask);
            }

            if (_mirror3dLayerMask != 0)
            {
                if (updateInfo.Enable3dLayer) mirror.AddCullingMask(_mirror3dLayerMask);
                else mirror.RemoveCullingMask(_mirror3dLayerMask);
            }
        }

        public void InitializeStage()
        {
            foreach (GameObject stage_part in _stageObjects)
            {
                if (stage_part == null)
                {
                    Debug.LogWarning("[StageController] 跳过 _stageObjects 中的 null 条目");
                    continue;
                }

                var instance = Instantiate(stage_part, transform);

                int missingCount = CountMissingScripts(instance);
                if (missingCount > 0)
                {
                    Debug.LogWarning($"[StageController] '{stage_part.name}' 实例化后有 {missingCount} 个 missing script 组件");
                }

                foreach (var child in instance.GetComponentsInChildren<Transform>(true))
                {
                    if (!StageObjectMap.ContainsKey(child.name))
                    {
                        if (child.name.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            child.gameObject.SetActive(true);
                        }

                        var tmp_name = child.name.Replace("(Clone)", "");
                        StageObjectMap[tmp_name] = child.gameObject;
                    }
                }
            }

            foreach (var unit in _stageObjectUnits)
            {
                if (unit == null || string.IsNullOrEmpty(unit.UnitName))
                    continue;

                if (!StageObjectUnitMap.ContainsKey(unit.UnitName))
                {
                    StageObjectUnitMap.Add(unit.UnitName, unit);
                }
            }

            AutoAttachMirrorReflectionComponents();
        }

        public void UpdateObject(ref ObjectUpdateInfo updateInfo)
        {
            if (updateInfo.data == null)
                return;

            if (StageObjectMap.TryGetValue(updateInfo.data.name, out GameObject gameObject))
            {
                gameObject.SetActive(updateInfo.renderEnable);

                Transform attach_transform = null;
                switch (updateInfo.AttachTarget)
                {
                    case AttachType.None:
                        if (StageParentMap.TryGetValue(updateInfo.data.name, out Transform parentTransform))
                            attach_transform = parentTransform;
                        break;
                    case AttachType.Character:
                        var chara = Director.instance.CharaContainerScript[updateInfo.CharacterPosition];
                        if (chara)
                            attach_transform = chara.transform;
                        break;
                    case AttachType.Camera:
                        attach_transform = Director.instance.MainCameraTransform;
                        break;
                }
                if (gameObject.transform.parent != attach_transform)
                    gameObject.transform.SetParent(attach_transform);

                if (updateInfo.data.enablePosition)
                    gameObject.transform.localPosition = updateInfo.updateData.position;
                if (updateInfo.data.enableRotate)
                    gameObject.transform.localRotation = updateInfo.updateData.rotation;
                if (updateInfo.data.enableScale)
                    gameObject.transform.localScale = updateInfo.updateData.scale;
            }
        }

        public void UpdateTransform(ref TransformUpdateInfo updateInfo)
        {
            if (updateInfo.data == null)
                return;

            if (StageObjectUnitMap.TryGetValue(updateInfo.data.name, out StageObjectUnit objectUnit) &&
                objectUnit.ChildObjects != null && objectUnit.ChildObjects.Length > 0)
            {
                foreach (var child in objectUnit.ChildObjects)
                {
                    if (child == null) continue;

                    if (StageObjectMap.TryGetValue(child.name, out GameObject go) && go != null)
                    {
                        ApplyTransformTo(go.transform, updateInfo);
                    }
                }
                return;
            }

            if (StageObjectMap.TryGetValue(updateInfo.data.name, out GameObject directGo) && directGo != null)
            {
                ApplyTransformTo(directGo.transform, updateInfo);
                return;
            }

            string key2 = updateInfo.data.name.Replace("(Clone)", "");
            if (StageObjectMap.TryGetValue(key2, out GameObject directGo2) && directGo2 != null)
            {
                ApplyTransformTo(directGo2.transform, updateInfo);
                return;
            }
        }

        private static void ApplyTransformTo(Transform tr, TransformUpdateInfo updateInfo)
        {
            if (updateInfo.data.enablePosition)
                tr.localPosition = updateInfo.updateData.position;

            if (updateInfo.data.enableRotate)
                tr.localRotation = updateInfo.updateData.rotation;

            if (updateInfo.data.enableScale)
                tr.localScale = updateInfo.updateData.scale;
        }

        private void UpdateBgColor1(ref BgColor1UpdateInfo updateInfo)
        {
            if (!_enableBgColorDriver)
                return;

            var targets = ResolveBgColorRenderers(updateInfo.TimelineName, wantBgColor2Style: false);
            if (targets == null || targets.Count == 0)
                return;

            for (int i = 0; i < targets.Count; i++)
            {
                var r = targets[i];
                if (r == null) continue;

                Material[] mats;
                try { mats = r.materials; }
                catch { continue; }
                if (mats == null) continue;

                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null) continue;

                    if (mat.HasProperty("_CharaColor")) mat.SetColor("_CharaColor", updateInfo.color);
                    if (mat.HasProperty("_ToonDarkColor")) mat.SetColor("_ToonDarkColor", updateInfo.toonDarkColor);
                    if (mat.HasProperty("_ToonBrightColor")) mat.SetColor("_ToonBrightColor", updateInfo.toonBrightColor);
                    if (mat.HasProperty("_OutlineColor")) mat.SetColor("_OutlineColor", updateInfo.outlineColor);
                    if (mat.HasProperty("_Saturation")) mat.SetFloat("_Saturation", updateInfo.Saturation);
                    if (mat.HasProperty("_ColorPower") && updateInfo.colorPower > 0f) mat.SetFloat("_ColorPower", updateInfo.colorPower);
                }
            }
        }

        private void UpdateBgColor2(ref BgColor2UpdateInfo updateInfo)
        {
            if (!_enableBgColorDriver)
                return;

            float extra = ResolveBgColorExtraValue(updateInfo.randomTableIndex);

            var groups = ResolveBgColor2Groups(updateInfo.TimelineName);
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

                        Material[] mats;
                        try { mats = r.materials; }
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

            

        

        private List<BgColor2RuntimeBinding> CollectBindingsBySourceMaterial(Material sourceMaterial)
        {
            var list = new List<BgColor2RuntimeBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (sourceMaterial == null)
                return list;

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
            if (dst == null || seen == null || src == null)
                return;

            for (int i = 0; i < src.Count; i++)
            {
                var binding = src[i];
                if (binding == null || binding.renderer == null)
                    continue;

                string key = binding.renderer.GetInstanceID().ToString() + ":" + binding.materialIndex.ToString();
                if (!seen.Add(key))
                    continue;

                dst.Add(binding);
            }
        }

        private List<BgColor2RuntimeGroup> ResolveBgColor2Groups(string timelineName)
        {
            string key = NormalizeKey(timelineName);
            if (string.IsNullOrEmpty(key))
                return null;

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
                    if (score <= 0)
                        continue;

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
                if (group == null || group.sourceIndex != sourceIndex)
                    continue;

                switch (family)
                {
                    case "wash":
                        if (group.kind == BgColor2RuntimeKind.Wash)
                            list.Add(group);
                        break;
                    case "foot":
                        if (group.kind == BgColor2RuntimeKind.Foot)
                            list.Add(group);
                        break;
                    case "laser":
                        if (group.kind == BgColor2RuntimeKind.Laser)
                            list.Add(group);
                        break;
                    case "neon":
                        if (group.kind == BgColor2RuntimeKind.NeonMain || group.kind == BgColor2RuntimeKind.NeonBack)
                            list.Add(group);
                        break;
                }
            }

            return list.Count > 0 ? list : null;
        }

        private static bool TryParseBgColor2TimelineIndex(string normalizedTimelineKey, out string family, out int sourceIndex)
        {
            family = null;
            sourceIndex = -1;

            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgwash", out sourceIndex))
            {
                family = "wash";
                return true;
            }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgfoot", out sourceIndex))
            {
                family = "foot";
                return true;
            }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "bgneon", out sourceIndex))
            {
                family = "neon";
                return true;
            }
            if (TryParseTimelineLetterIndex(normalizedTimelineKey, "laser", out sourceIndex) ||
                TryParseTimelineLetterIndex(normalizedTimelineKey, "bglaser", out sourceIndex))
            {
                family = "laser";
                return true;
            }

            return false;
        }

        private static bool TryParseTimelineLetterIndex(string normalizedTimelineKey, string prefix, out int sourceIndex)
        {
            sourceIndex = -1;
            if (string.IsNullOrEmpty(normalizedTimelineKey) || string.IsNullOrEmpty(prefix))
                return false;
            if (!normalizedTimelineKey.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            if (normalizedTimelineKey.Length != prefix.Length + 1)
                return false;

            char suffix = normalizedTimelineKey[normalizedTimelineKey.Length - 1];
            if (suffix < 'a' || suffix > 'z')
                return false;

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
                if (rule == null) continue;
                if (NormalizeKey(rule.timelineName) != key) continue;

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
                            var token = rule.materialNameContains[m];
                            if (!string.IsNullOrEmpty(token) && group.sourceName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
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
                            var rn = rr.name ?? string.Empty;
                            for (int t = 0; t < rule.rendererNameContains.Length; t++)
                            {
                                var token = rule.rendererNameContains[t];
                                if (!string.IsNullOrEmpty(token) && rn.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
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

            if (sourceKey == normalizedTimelineKey)
                return 1000;
            if (sourceKey.Contains(normalizedTimelineKey) || normalizedTimelineKey.Contains(sourceKey))
                return 700;

            int score = 0;
            var tokens = SplitNameTokens(timelineName);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = NormalizeKey(tokens[i]);
                if (string.IsNullOrEmpty(token)) continue;
                if (sourceKey.Contains(token))
                    score += 50;
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
            string key = NormalizeKey(timelineName);
            if (string.IsNullOrEmpty(key))
                return (_bgColorFallbackToAllEligible && allowAllEligibleFallback) ? _bgColorAllEligibleRenderers : null;

            if (_bgColorRendererCache.TryGetValue(key, out var cached))
                return cached;

            var set = new HashSet<Renderer>();

            if (TryAddRenderersFromStageObjectMap(timelineName, key, set) | TryAddRenderersFromUnitMap(timelineName, key, set))
            {
            }

            ApplyBindingOverrides(timelineName, key, set);

            var all = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                if (wantBgColor2Style && !RendererHasBgColor2Props(r)) continue;
                if (!wantBgColor2Style && !RendererHasBgColor1Props(r)) continue;
                if (RendererMatchesTimeline(r, timelineName, key))
                    set.Add(r);
            }

            var resolved = set.ToList();
            if (resolved.Count == 0 && _bgColorFallbackToAllEligible && allowAllEligibleFallback)
            {
                resolved = _bgColorAllEligibleRenderers.Where(r => r != null && (wantBgColor2Style ? RendererHasBgColor2Props(r) : RendererHasBgColor1Props(r))).ToList();
            }

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

            var all = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < _bgColorBindingOverrides.Length; i++)
            {
                var rule = _bgColorBindingOverrides[i];
                if (rule == null) continue;
                if (NormalizeKey(rule.timelineName) != key) continue;

                for (int j = 0; j < all.Length; j++)
                {
                    var r = all[j];
                    if (r == null) continue;
                    if (MatchRule(r, rule))
                        set.Add(r);
                }
            }
        }

        private static bool MatchRule(Renderer r, BgColorBindingOverride rule)
        {
            if (r == null || rule == null)
                return false;

            bool rendererMatched = rule.rendererNameContains == null || rule.rendererNameContains.Length == 0;
            if (!rendererMatched)
            {
                string rn = r.name ?? string.Empty;
                for (int i = 0; i < rule.rendererNameContains.Length; i++)
                {
                    string token = rule.rendererNameContains[i];
                    if (!string.IsNullOrEmpty(token) && rn.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
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
                        string mn = m.name ?? string.Empty;
                        for (int j = 0; j < rule.materialNameContains.Length; j++)
                        {
                            string token = rule.materialNameContains[j];
                            if (!string.IsNullOrEmpty(token) && mn.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
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
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(normalizedKey))
                return false;

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

        private static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            char[] tmp = new char[s.Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c))
                    tmp[n++] = char.ToLowerInvariant(c);
            }
            return n > 0 ? new string(tmp, 0, n) : string.Empty;
        }

        private static string CleanMaterialName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            string cleaned = s.Replace("(Instance)", string.Empty)
                              .Replace("(Clone)", string.Empty)
                              .Trim();
            return cleaned;
        }

        private void AddRenderersRecursive(GameObject go, HashSet<Renderer> set)
        {
            if (go == null) return;
            var rs = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r != null)
                    set.Add(r);
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

        public static int CountMissingScripts(GameObject root)
        {
            if (root == null) return 0;
            int count = 0;

            var components = root.GetComponents<Component>();
            foreach (var c in components)
            {
                if (c == null) count++;
            }

            foreach (Transform child in root.transform)
            {
                count += CountMissingScripts(child.gameObject);
            }
            return count;
        }
    }
}
