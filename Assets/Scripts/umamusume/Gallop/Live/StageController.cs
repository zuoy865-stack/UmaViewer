using Gallop.Live.Cutt;
using System;
using System.Collections;
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

    /// <summary>
    /// 舞台主控制器（分部类）：
    /// 负责舞台物件实例化、层级与Transform管理、生命周期监听、驱动组件自动挂载与反射镜面同步。
    /// </summary>
    public partial class StageController : MonoBehaviour
    {
        int _logEvery = 30;
        int _logCount = 0;
        public List<GameObject> _stageObjects = new List<GameObject>();
        public StageObjectUnit[] _stageObjectUnits;
        public Dictionary<string, StageObjectUnit> StageObjectUnitMap = new Dictionary<string, StageObjectUnit>();
        public Dictionary<string, GameObject> StageObjectMap = new Dictionary<string, GameObject>();
        public Dictionary<string, Transform> StageParentMap = new Dictionary<string, Transform>();
        // 记录舞台子物件的初始局部变换基准值，供时间轴增量模式（OffsetType.Add）运算使用
        private readonly Dictionary<Transform, Vector3> _baseLocalPositions = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Quaternion> _baseLocalRotations = new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<Transform, Vector3> _baseLocalScales = new Dictionary<Transform, Vector3>();
        [SerializeField] private bool _autoAddBlinkDriver = true;

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
            // 恢复舞台颜色驱动，使舞台光效正常驱动场景内物品
            _enableBgColorDriver = true;

            AutoAddDriver("StageBlinkLightDriver");
            AutoAddDriver("StageWashLightDriver");
            AutoAddDriver("StageUVScrollLightDriver");
            //AutoAddDriver("StageLaserDriver");
            AutoAddDriver("StageLensFlareDriver");

            InitializeStage();
            RebuildMirrorReflectionCache();
            RebuildBgColorCache();

            if (_stageObjects != null && _stageObjects.Count > 0)
            {
                Debug.Log("[StageController] stage parts = " +
                    string.Join(", ", _stageObjects.ConvertAll(o => o ? o.name : "<null>")));
            }

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

            // 仅当时间轴控制器有效且引用发生改变时，才重新绑定回调
            if (ctl != null && _boundTimelineControl != ctl)
                TryBindTimelineCallbacks();

            // 状态锁加固：仅当未初始化且时间轴数据已就绪时才尝试初始化激光，杜绝每帧重复调用
            if (!_laserSetupDone && _boundTimelineControl != null && _boundTimelineControl.data != null)
                TrySetupLaserObject(_boundTimelineControl);

            if (_laserControllerArray == null)
                return;

            for (int i = 0; i < _laserControllerArray.Length; i++)
            {
                LaserController controller = _laserControllerArray[i];
                if (controller != null && controller.IsInitialized)
                    controller.AlterLateUpdate();
            }
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
            ctl.OnUpdateLaser -= UpdateLaser;

            ctl.OnUpdateTransform += UpdateTransform;
            ctl.OnUpdateObject += UpdateObject;
            ctl.OnUpdateBgColor1 += UpdateBgColor1;
            ctl.OnUpdateBgColor2 += UpdateBgColor2;
            ctl.OnEnvironmentMirror += UpdateEnvironemntMirror;
            ctl.OnUpdateMirrorReflection += UpdateMirrorReflection;
            ctl.OnUpdateLaser += UpdateLaser;

            // 同步为天空控制器绑定时间轴事件
            var skyCtrl = GetComponent<StageSkyController>();
            if (skyCtrl != null)
            {
                skyCtrl.BindTimelineControl(ctl);
            }

            // 状态锁加固：仅当时间轴控制器实例发生变更时，才重置激光状态并尝试初始化
            if (_laserSetupTimelineControl != ctl)
            {
                _laserSetupDone = false;
                _laserSetupTimelineControl = ctl;
                _laserDataIndexMap.Clear();

                TrySetupLaserObject(ctl);
            }
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

            var skyCtrl = GetComponent<StageSkyController>();
            if (skyCtrl != null)
            {
                skyCtrl.UnbindTimelineControl();
            }

            ctl.OnUpdateTransform -= UpdateTransform;
            ctl.OnUpdateObject -= UpdateObject;
            ctl.OnUpdateBgColor1 -= UpdateBgColor1;
            ctl.OnUpdateBgColor2 -= UpdateBgColor2;
            ctl.OnEnvironmentMirror -= UpdateEnvironemntMirror;
            ctl.OnUpdateMirrorReflection -= UpdateMirrorReflection;
            ctl.OnUpdateLaser -= UpdateLaser;
        }

        private void AutoAddDriver(string shortTypeName)
        {
            Type t = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType($"Gallop.Live.{shortTypeName}") ?? asm.GetType(shortTypeName);

                if (t != null)
                    break;
            }

            if (t == null)
            {
                Debug.LogWarning($"[StageController] AutoAddDriver type not found: {shortTypeName}");
                return;
            }

            if (!typeof(MonoBehaviour).IsAssignableFrom(t))
            {
                Debug.LogWarning($"[StageController] AutoAddDriver not MonoBehaviour: {shortTypeName}");
                return;
            }

            if (GetComponent(t) == null)
            {
                gameObject.AddComponent(t);
                Debug.Log($"[StageController] AutoAddDriver added: {t.FullName}");
            }
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
            if (_stageObjects != null)
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

                    // 空材质防护检测：遍历生成的 Renderer，如果检测到 sharedMaterial == null 或 materials 包含 null，进行安全回退，杜绝裸露白色死模
                    ProtectRendererMaterials(instance, stage_part.name);

                    foreach (var child in instance.GetComponentsInChildren<Transform>(true))
                    {
                        var tmp_name = child.name.Replace("(Clone)", "");

                        // 完整记录每个子物件的初始父节点，杜绝后续更新或归位时脱离舞台层级
                        StageParentMap[child.name] = child.parent;
                        StageParentMap[tmp_name] = child.parent;

                        // 记录子物件的初始局部变换基准值，用于后续增量模式（OffsetType.Add）运算
                        _baseLocalPositions[child] = child.localPosition;
                        _baseLocalRotations[child] = child.localRotation;
                        _baseLocalScales[child] = child.localScale;

                        if (!StageObjectMap.ContainsKey(child.name))
                        {
                            if (child.name.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                child.gameObject.SetActive(true);
                            }

                            StageObjectMap[tmp_name] = child.gameObject;
                            if (!StageObjectMap.ContainsKey(child.name))
                            {
                                StageObjectMap[child.name] = child.gameObject;
                            }
                        }
                    }
                }
            }

            if (_stageObjectUnits != null)
            {
                foreach (var unit in _stageObjectUnits)
                {
                    if (unit == null || string.IsNullOrEmpty(unit.UnitName))
                        continue;

                    if (!StageObjectUnitMap.ContainsKey(unit.UnitName))
                    {
                        StageObjectUnitMap.Add(unit.UnitName, unit);
                    }
                }
            }

            AutoAttachMirrorReflectionComponents();

            // 校准舞台天空网格材质与 Alpha 混合状态，保障云层源颜色与天空底色 Alpha 隔离
            CalibrateStageSkyAndLighting();

            // 在舞台初始化完成时输出关键部件快照诊断，摸清天空、草地和监视器的真实材质与 Shader 状态
            StageRuntimeDiagnostics.SnapshotStageRenderers(gameObject);

            // 自动挂载并初始化天空与云层透明度控制器，提供官方时间轴驱动、云层透明度反相及外部天球透视多模式切换
            var skyCtrl = gameObject.GetComponent<StageSkyController>();
            if (skyCtrl == null)
            {
                skyCtrl = gameObject.AddComponent<StageSkyController>();
            }
            skyCtrl.Initialize(this);
        }

        /// <summary>
        /// 对舞台天空网格与混合状态进行校准，确保云层源颜色不被清零、混合模式正确、且天空不被写入多余 Alpha
        /// </summary>
        private void CalibrateStageSkyAndLighting()
        {
            var allRenderers = GetComponentsInChildren<Renderer>(true);
            if (allRenderers == null || allRenderers.Length == 0)
                return;

            foreach (var r in allRenderers)
            {
                if (r == null) continue;
                string rName = r.name.ToLowerInvariant();
                string goName = r.gameObject.name.ToLowerInvariant();

                bool isSky = rName.Contains("sky") || goName.Contains("sky");
                if (!isSky) continue;

                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0) continue;

                foreach (var mat in mats)
                {
                    if (mat == null) continue;

                    // 1. 对于半透明云层（如 sky_grad_00，材质通常为 DefaultTransparentNoAmbient，queue=3000）
                    // 确保其源颜色为纯白、强度为1，防止 0 * Alpha 退化为遮蔽光的黑墨蒙版
                    if (mat.renderQueue >= 2500 || rName.Contains("grad") || mat.name.Contains("grad") || mat.name.Contains("sky001"))
                    {
                        if (mat.HasProperty("_MulColor0"))
                        {
                            Color c = mat.GetColor("_MulColor0");
                            if (c.r < 0.1f && c.g < 0.1f && c.b < 0.1f)
                                mat.SetColor("_MulColor0", Color.white);
                        }
                        if (mat.HasProperty("_ColorPower"))
                        {
                            float p = mat.GetFloat("_ColorPower");
                            if (p < 0.1f) mat.SetFloat("_ColorPower", 1f);
                        }
                        // 半透明云层严禁写入深度，避免遮挡
                        if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
                    }
                    else
                    {
                        // 2. 对于不透明天空底色（如 sky_base_00，queue=2000）
                        // 确保底色正常，且 Alpha 通道输出为 0，防止作为 Framebuffer Light Mask 时被舞台加法灯光误叠加造成死白
                        if (mat.HasProperty("_Color"))
                        {
                            Color c = mat.GetColor("_Color");
                            c.a = 0f;
                            mat.SetColor("_Color", c);
                        }
                        if (mat.HasProperty("_MulColor0"))
                        {
                            Color c = mat.GetColor("_MulColor0");
                            if (c.r < 0.1f && c.g < 0.1f && c.b < 0.1f)
                                mat.SetColor("_MulColor0", Color.white);
                        }
                        if (mat.HasProperty("_ColorPower"))
                        {
                            float p = mat.GetFloat("_ColorPower");
                            if (p < 0.1f) mat.SetFloat("_ColorPower", 1f);
                        }
                    }
                }
            }

            Debug.Log("[StageController] 舞台天空网格材质与 Alpha 混合校准完成。");
        }

        public void UpdateObject(ref ObjectUpdateInfo updateInfo)
        {
            if (updateInfo.data == null || string.IsNullOrEmpty(updateInfo.data.name))
                return;

            // 优先根据原始名称查找对应物件，若未命中则剔除 (Clone) 后缀进行兜底查找
            GameObject gameObject = null;
            if (!StageObjectMap.TryGetValue(updateInfo.data.name, out gameObject) || gameObject == null)
            {
                string cleanName = updateInfo.data.name.Replace("(Clone)", "");
                StageObjectMap.TryGetValue(cleanName, out gameObject);
            }

            if (gameObject != null)
            {
                // 严格依照 updateInfo.renderEnable 设置显隐状态（包括开场 renderEnable=0 时及时隐藏 monitor_000 等物件）
                gameObject.SetActive(updateInfo.renderEnable);

                // 目标父节点默认严格保留其现有的 gameObject.transform.parent，绝不调用 SetParent(null)
                Transform targetParent = gameObject.transform.parent;

                switch (updateInfo.AttachTarget)
                {
                    case AttachType.None:
                        // 当 AttachTarget 为 None 时：如果 StageParentMap 找到了 parentTransform，则将物体挂回 parentTransform；
                        // 如果字典中未找到，严格保留其现有的 gameObject.transform.parent，绝不能调用 SetParent(null)！杜绝物体脱离舞台被孤立在世界根节点
                        if (StageParentMap.TryGetValue(updateInfo.data.name, out Transform parentTransform) && parentTransform != null)
                        {
                            targetParent = parentTransform;
                        }
                        else
                        {
                            string cleanName = updateInfo.data.name.Replace("(Clone)", "");
                            if (StageParentMap.TryGetValue(cleanName, out Transform cleanParent) && cleanParent != null)
                            {
                                targetParent = cleanParent;
                            }
                        }
                        break;

                    case AttachType.Character:
                        // 挂载至指定位置马娘角色的手腕/手持骨骼 Transform，而非直接赋予脚底根节点 chara.transform
                        if (Director.instance != null && Director.instance.CharaContainerScript != null &&
                            updateInfo.CharacterPosition >= 0 && updateInfo.CharacterPosition < Director.instance.CharaContainerScript.Count)
                        {
                            var chara = Director.instance.CharaContainerScript[updateInfo.CharacterPosition];
                            if (chara != null)
                            {
                                targetParent = chara.FindAttachBone("Hand_Attach_R");
                            }
                        }
                        break;

                    case AttachType.Camera:
                        // 挂载至主摄像机 Transform
                        if (Director.instance != null && Director.instance.MainCameraTransform != null)
                        {
                            targetParent = Director.instance.MainCameraTransform;
                        }
                        break;
                }

                // 仅当目标父节点有效且与当前父节点不同时进行重新挂载，严禁调用 SetParent(null)
                if (targetParent != null && gameObject.transform.parent != targetParent)
                {
                    gameObject.transform.SetParent(targetParent);
                }

                Transform tr = gameObject.transform;

                // 获取并记录物体的初始局部位姿基准值，供增量模式计算
                if (!_baseLocalPositions.TryGetValue(tr, out Vector3 basePos))
                {
                    basePos = tr.localPosition;
                    _baseLocalPositions[tr] = basePos;
                    _baseLocalRotations[tr] = tr.localRotation;
                    _baseLocalScales[tr] = tr.localScale;
                }
                _baseLocalRotations.TryGetValue(tr, out Quaternion baseRot);
                _baseLocalScales.TryGetValue(tr, out Vector3 baseScale);

                // 修复层级坐标空间混淆：
                // 时间轴下发的 position 与 rotation 是基于舞台根空间的全局变换。
                // 当受控物体在场景中的当前父级 targetParent 不为 null 且不是舞台根节点时，
                // 必须通过父节点的逆变换进行换算，消除深层父级平移与旋转叠加导致的位移二次放大和歪斜偏转！
                // 如果父级就是舞台根节点或无父级，则直接赋给 localPosition/localRotation。
                Vector3 targetLocalPos;
                Quaternion targetLocalRot;
                Vector3 targetLocalScale = updateInfo.updateData.scale;

                if (targetParent != null && targetParent != transform)
                {
                    targetLocalPos = targetParent.InverseTransformPoint(updateInfo.updateData.position);
                    targetLocalRot = Quaternion.Inverse(targetParent.rotation) * updateInfo.updateData.rotation;
                }
                else
                {
                    targetLocalPos = updateInfo.updateData.position;
                    targetLocalRot = updateInfo.updateData.rotation;
                }

                // 支持增量模式（OffsetType.Add，在初始基准值基础上相加）与绝对值模式（OffsetType.Direct）
                if (updateInfo.OffsetType == OffsetType.Add)
                {
                    if (updateInfo.data.enablePosition)
                        tr.localPosition = basePos + targetLocalPos;
                    if (updateInfo.data.enableRotate)
                        tr.localRotation = baseRot * targetLocalRot;
                    if (updateInfo.data.enableScale)
                        tr.localScale = Vector3.Scale(baseScale, targetLocalScale);
                }
                else
                {
                    if (updateInfo.data.enablePosition)
                        tr.localPosition = targetLocalPos;
                    if (updateInfo.data.enableRotate)
                        tr.localRotation = targetLocalRot;
                    if (updateInfo.data.enableScale)
                        tr.localScale = targetLocalScale;
                }
            }
        }

        public void UpdateTransform(ref TransformUpdateInfo updateInfo)
        {
            if (updateInfo.data == null || string.IsNullOrEmpty(updateInfo.data.name))
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

        internal static string CleanMaterialName(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            string cleaned = s.Replace("(Instance)", string.Empty)
                              .Replace("(Clone)", string.Empty)
                              .Trim();
            return cleaned;
        }

        internal static string NormalizeKey(string s)
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
    }
}
