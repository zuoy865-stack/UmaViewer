using Gallop.Live.Cutt;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// StageController 激光分部类：
    /// 负责舞台激光预制体加载、多实例实例化、时间轴 Laser 轨道驱动、激光色彩分组构建及每帧定向相机跟踪（AlterUpdate）。
    /// </summary>
    public partial class StageController
    {
        [SerializeField]
        [Tooltip("レーザーオブジェクト")]
        private GameObject[] _laserObjects;

        private LaserController[] _laserControllerArray;

        [SerializeField]
        [Tooltip("レーザーマテリアル")]
        private Material[] _laserMaterials;

        private bool _laserSetupDone = false;
        private LiveTimelineControl _laserSetupTimelineControl;
        private readonly Dictionary<object, int> _laserDataIndexMap =
            new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        // 时间轴 LaserA/LaserB... 的字母实际对应 LiveTimelineLaserData._materialIndex。
        // 必须保存“材质索引 -> 该索引创建出的运行时 Renderer”，不能按材质名去重，
        // 否则多个同名的 Laser 实例会串色或只更新第一组。
        private readonly Dictionary<int, List<Renderer>> _laserRenderersByMaterialIndex =
            new Dictionary<int, List<Renderer>>();

        // Laser 的 AlterUpdate 必须在 LiveTimelineControl 下发本帧 UpdateInfo 之后执行。
        // 由 Director.ApplyTimelineLateUpdate 显式调用，避免 MonoBehaviour.Update 顺序不确定。
        public void AlterUpdateLaserControllers()
        {
            if (_laserControllerArray == null)
                return;

            Transform currentCameraTransform = null;

            if (Director.instance != null)
            {
                currentCameraTransform = Director.instance.MainCameraTransform;
            }

            if (currentCameraTransform == null && Camera.main != null)
            {
                currentCameraTransform = Camera.main.transform;
            }

            for (int i = 0; i < _laserControllerArray.Length; i++)
            {
                LaserController controller = _laserControllerArray[i];
                if (controller == null || !controller.IsInitialized)
                    continue;

                // 项目使用多台 Camera，并在切镜头时更换 MainCameraTransform 的引用
                controller.SetTargetCameraTransform(currentCameraTransform);
                controller.AlterUpdate();
            }
        }

        private GameObject[] LoadDefaultLaserObjects()
        {
            string[] paths =
            {
                "3d/effect/live/pfb_eff_live_laser_01",
                "3d/effect/live/pfb_eff_live_laser_02",
                "3d/effect/live/pfb_eff_live_laser_03",
                "3d/effect/live/pfb_eff_live_laser_04",
            };

            var main = UmaViewerMain.Instance;
            if (main == null || main.AbList == null)
                return Array.Empty<GameObject>();

            var list = new List<GameObject>();

            foreach (string path in paths)
            {
                if (!main.AbList.TryGetValue(path, out var entry) || entry == null)
                {
                    Debug.LogWarning("[StageController] missing laser object entry: " + path);
                    continue;
                }

                AssetBundle ab = UmaAssetManager.LoadAssetBundle(entry, neverUnload: true, isRecursive: true);
                if (ab == null)
                {
                    Debug.LogWarning("[StageController] load laser bundle failed: " + path);
                    continue;
                }

                string prefabName = path.Substring(path.LastIndexOf('/') + 1);
                GameObject prefab = null;

                foreach (string assetName in ab.GetAllAssetNames())
                {
                    if (assetName.IndexOf(prefabName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        prefab = ab.LoadAsset<GameObject>(assetName);
                        if (prefab != null)
                            break;
                    }
                }

                if (prefab == null)
                {
                    foreach (GameObject go in ab.LoadAllAssets<GameObject>())
                    {
                        if (go != null && go.name.IndexOf(prefabName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prefab = go;
                            break;
                        }
                    }
                }

                if (prefab != null)
                {
                    Debug.Log("[StageController] loaded laser object: " + prefab.name);
                    list.Add(prefab);
                }
                else
                {
                    Debug.LogWarning("[StageController] prefab not found inside bundle: " + path);
                }
            }

            return list.ToArray();
        }

        private void TrySetupLaserObject(LiveTimelineControl ctl)
        {
            // 核心状态锁加固：若已完成设置且为同一时间轴控制器，立即拦截，严禁穿透重入
            if (_laserSetupDone && _laserSetupTimelineControl == ctl)
                return;

            if (ctl == null || ctl.data == null)
                return;

            // 预先加锁并锁定当前时间轴控制器引用，杜绝重入与并发穿透
            _laserSetupDone = true;
            _laserSetupTimelineControl = ctl;

            SetupLaserObject(ctl.data);

            if (_laserControllerArray != null && _laserControllerArray.Length > 0)
            {
                Debug.Log("[StageController] Laser setup done. count=" + _laserControllerArray.Length);
            }
        }

        private void SetupLaserObject(LiveTimelineData liveTimelineData)
        {
            ReleaseLaserControllers();
            _laserControllerArray = null;
            _laserDataIndexMap.Clear();
            _laserRenderersByMaterialIndex.Clear();

            // 优先使用舞台/Live 资源加载流程已经设置好的专用 Laser prefab。
            // 没有专用资源时，才回退到多个 Live 共用的通用 Laser prefab。
            if (_laserObjects == null || _laserObjects.Length == 0)
                _laserObjects = LoadDefaultLaserObjects();

            if (_laserObjects == null || _laserObjects.Length == 0)
            {
                Debug.LogWarning("[StageController] no laser prefabs loaded");
                return;
            }

            Transform cameraTransform = ResolveLaserCameraTransform();
            if (cameraTransform == null)
            {
                Debug.LogWarning("[StageController] cameraTransform is null for laser");
                return;
            }

            IList worksheets = GetWorksheetList(liveTimelineData);
            if (worksheets == null || worksheets.Count == 0)
            {
                Debug.LogWarning("[StageController] worksheetList is empty for laser");
                return;
            }

            var result = new List<LaserController>();

            for (int wsIndex = 0; wsIndex < worksheets.Count; wsIndex++)
            {
                object worksheet = worksheets[wsIndex];
                if (worksheet == null)
                    continue;

                IList laserList = GetMemberValue(worksheet, "laserList") as IList;
                if (laserList == null || laserList.Count == 0)
                    continue;

                for (int i = 0; i < laserList.Count; i++)
                {
                    object laserData = laserList[i];
                    int objectIndex = GetLaserObjectIndex(laserData);

                    if (objectIndex < 0 || objectIndex >= _laserObjects.Length)
                    {
                        Debug.LogWarning($"[StageController] invalid laser prefab index={objectIndex}, prefabCount={_laserObjects.Length}; fallback to 0");
                        DumpLaserDataIntFields(laserData);
                        objectIndex = 0;
                    }

                    GameObject prefab = _laserObjects[objectIndex];
                    if (prefab == null)
                    {
                        Debug.LogWarning($"[StageController] laser prefab is null. objectIndex={objectIndex}");
                        continue;
                    }

                    // _materialIndex 是时间轴颜色分组索引：LaserA/B/C -> 0/1/2。
                    // 它不能因为 _laserMaterials 模板数组长度不足而被 Clamp，
                    // 否则 B/C 会合并到同一颜色组。
                    int timelineMaterialIndex = Mathf.Max(
                        0,
                        GetLaserMaterialIndex(laserData, result.Count));

                    Material material = ResolveLaserSourceMaterial(timelineMaterialIndex);

                    var controller = new LaserController();
                    try
                    {
                        controller.Initialize(prefab, transform, cameraTransform, material);
                    }
                    catch (Exception ex)
                    {
                        controller.Release();
                        Debug.LogException(ex);
                        continue;
                    }

                    if (!controller.IsInitialized)
                    {
                        controller.Release();
                        Debug.LogWarning($"[StageController] laser initialize failed. prefab={prefab.name}, objectIndex={objectIndex}");
                        continue;
                    }

                    int controllerIndex = result.Count;
                    if (laserData != null && !_laserDataIndexMap.ContainsKey(laserData))
                        _laserDataIndexMap.Add(laserData, controllerIndex);

                    if (!_laserRenderersByMaterialIndex.TryGetValue(timelineMaterialIndex, out var laserRenderers))
                    {
                        laserRenderers = new List<Renderer>();
                        _laserRenderersByMaterialIndex.Add(timelineMaterialIndex, laserRenderers);
                    }
                    controller.AppendRuntimeRenderers(laserRenderers);

                    result.Add(controller);

                    Debug.Log(
                        $"[StageController] instantiated laser index={controllerIndex}, " +
                        $"prefab={prefab.name}, objectIndex={objectIndex}, " +
                        $"timelineMaterialIndex={timelineMaterialIndex}, " +
                        $"sourceMaterial={(material != null ? material.name : "<prefab>")}");
                }
            }

            _laserControllerArray = result.ToArray();

            // 核心性能治理：仅当真正实例化出 Laser 对象时，才执行动态材质扫描与 RebuildBgColorCache，
            // 杜绝无激光场景（如 1157）全场景材质无谓扫描与 GC/IO 开销；仅在成功初始化时输出单次日志
            if (_laserControllerArray != null && _laserControllerArray.Length > 0)
            {
                PopulateLaserMaterialsFromRuntimeIfNeeded();
                RebuildBgColorCache();
                Debug.Log("[StageController] SetupLaserObject complete. count=" + _laserControllerArray.Length);
            }
        }

        private Material ResolveLaserSourceMaterial(int timelineMaterialIndex)
        {
            if (_laserMaterials == null || _laserMaterials.Length == 0)
                return null;

            // 有对应模板时直接使用。
            if (timelineMaterialIndex >= 0 &&
                timelineMaterialIndex < _laserMaterials.Length &&
                _laserMaterials[timelineMaterialIndex] != null)
            {
                return _laserMaterials[timelineMaterialIndex];
            }

            // 模板数组不完整时只回退 shader/material 模板，
            // 颜色分组仍保留原始 timelineMaterialIndex。
            for (int i = 0; i < _laserMaterials.Length; i++)
            {
                if (_laserMaterials[i] != null)
                    return _laserMaterials[i];
            }

            return null;
        }

        private void PopulateLaserMaterialsFromRuntimeIfNeeded()
        {
            if (_laserMaterials != null && _laserMaterials.Any(m => m != null))
                return;

            if (_laserControllerArray == null || _laserControllerArray.Length == 0)
                return;

            var runtimeMaterials = new List<Material>();
            for (int i = 0; i < _laserControllerArray.Length; i++)
            {
                LaserController controller = _laserControllerArray[i];
                controller?.AppendRuntimeMaterials(runtimeMaterials);
            }

            var unique = new List<Material>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < runtimeMaterials.Count; i++)
            {
                Material material = runtimeMaterials[i];
                if (material == null)
                    continue;

                string key = NormalizeKey(CleanMaterialName(material.name));
                if (string.IsNullOrEmpty(key))
                    key = material.GetInstanceID().ToString();

                if (seen.Add(key))
                    unique.Add(material);
            }

            _laserMaterials = unique.ToArray();

            if (_bgColorVerboseLog)
                Debug.Log($"[StageController] collected runtime laser materials={_laserMaterials.Length}");
        }

        /// <summary>
        /// 从运行时激光渲染器映射中生成 Laser 专用的 BgColor2 运行时分组
        /// </summary>
        private void AddLaserRuntimeGroups()
        {
            if (_laserRenderersByMaterialIndex.Count == 0)
            {
                // 没有运行时映射时才使用旧的材质数组匹配。
                AddRuntimeGroupsFromMaterialArray(_laserMaterials, BgColor2RuntimeKind.Laser);
                return;
            }

            foreach (var pair in _laserRenderersByMaterialIndex.OrderBy(p => p.Key))
            {
                int sourceIndex = pair.Key;
                List<Renderer> sourceRenderers = pair.Value;
                if (sourceRenderers == null || sourceRenderers.Count == 0) continue;

                Material sourceMaterial = FindFirstRendererMaterial(sourceRenderers);
                if (sourceMaterial == null && _laserMaterials != null && sourceIndex >= 0 && sourceIndex < _laserMaterials.Length)
                {
                    sourceMaterial = _laserMaterials[sourceIndex];
                }

                var group = new BgColor2RuntimeGroup
                {
                    kind = BgColor2RuntimeKind.Laser,
                    sourceIndex = sourceIndex,
                    sourceMaterial = sourceMaterial,
                    sourceName = sourceMaterial != null ? CleanMaterialName(sourceMaterial.name) : $"Laser{sourceIndex}",
                    sourceKey = sourceMaterial != null ? NormalizeKey(CleanMaterialName(sourceMaterial.name)) : $"laser{sourceIndex}"
                };

                var seenBindings = new HashSet<string>(StringComparer.Ordinal);
                var seenRenderers = new HashSet<int>();

                for (int r = 0; r < sourceRenderers.Count; r++)
                {
                    Renderer renderer = sourceRenderers[r];
                    if (renderer == null) continue;

                    if (seenRenderers.Add(renderer.GetInstanceID()))
                        group.renderers.Add(renderer);

                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null) continue;

                    for (int m = 0; m < materials.Length; m++)
                    {
                        if (materials[m] == null) continue;

                        string bindingKey = renderer.GetInstanceID().ToString() + ":" + m.ToString();
                        if (!seenBindings.Add(bindingKey)) continue;

                        group.bindings.Add(new BgColor2RuntimeBinding { renderer = renderer, materialIndex = m });
                    }
                }

                _bgColor2Groups.Add(group);

                if (_bgColorVerboseLog)
                {
                    Debug.Log($"[StageController] Laser BgColor2 group built. materialIndex={sourceIndex}, src='{group.sourceName}', bindings={group.bindings.Count}, renderers={group.renderers.Count}");
                }
            }
        }

        private static Material FindFirstRendererMaterial(List<Renderer> renderers)
        {
            if (renderers == null) return null;

            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null) continue;

                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null)
                        return materials[m];
                }
            }

            return null;
        }

        private GameObject[] FindExistingStageLaserObjects()
        {
            Transform[] all = transform.GetComponentsInChildren<Transform>(true);
            var result = new List<GameObject>();

            for (int i = 0; i < all.Length; i++)
            {
                Transform candidate = all[i];
                if (candidate == null || candidate == transform)
                    continue;

                string n = candidate.name;
                if (string.IsNullOrEmpty(n) || n.IndexOf("_laser", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (FindChildRecursive(candidate, "lasercontroller") == null)
                    continue;

                bool nestedInAnotherLaser = false;
                Transform parent = candidate.parent;
                while (parent != null && parent != transform)
                {
                    if (parent.name.IndexOf("_laser", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        FindChildRecursive(parent, "lasercontroller") != null)
                    {
                        nestedInAnotherLaser = true;
                        break;
                    }
                    parent = parent.parent;
                }

                if (!nestedInAnotherLaser)
                    result.Add(candidate.gameObject);
            }

            result.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return result.ToArray();
        }

        private static Transform FindChildRecursive(Transform root, string childName)
        {
            if (root == null)
                return null;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase))
                    return child;

                Transform found = FindChildRecursive(child, childName);
                if (found != null)
                    return found;
            }

            return null;
        }

        private void UpdateLaser(ref LaserUpdateInfo updateInfo)
        {
            if (_laserControllerArray == null)
                return;

            int index = updateInfo.timelineIndex;

            if (index < 0 || index >= _laserControllerArray.Length)
                return;

            LaserController controller = _laserControllerArray[index];
            controller?.UpdateInfo(ref updateInfo);
        }

        private void ReleaseLaserControllers()
        {
            if (_laserControllerArray == null)
                return;

            for (int i = 0; i < _laserControllerArray.Length; i++)
            {
                if (_laserControllerArray[i] != null)
                    _laserControllerArray[i].Release();
            }

            _laserControllerArray = null;
            _laserRenderersByMaterialIndex.Clear();
        }

        private Transform ResolveLaserCameraTransform()
        {
            if (Director.instance != null && Director.instance.MainCameraTransform != null)
                return Director.instance.MainCameraTransform;

            if (Camera.main != null)
                return Camera.main.transform;

            if (Director.instance != null)
            {
                Camera cam = Director.instance.GetComponentInChildren<Camera>(true);
                if (cam != null)
                    return cam.transform;
            }

            Camera anyCam = FindObjectOfType<Camera>();
            return anyCam != null ? anyCam.transform : null;
        }

        private static IList GetWorksheetList(LiveTimelineData data)
        {
            if (data == null)
                return null;

            object v = GetMemberValue(data, "worksheetList");
            return v as IList;
        }

        private int GetLaserUpdateIndex(LaserUpdateInfo info)
        {
            return info.timelineIndex;
        }

        private static int GetLaserObjectIndex(object laserData)
        {
            int v;

            // 官方 LiveTimelineLaserData 实际字段名。
            v = GetIntMember(laserData, "_objectIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "objectIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "laserObjectIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "prefabIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "laserPrefabIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "assetIndex", int.MinValue);
            if (v != int.MinValue) return v;

            // fallback：官方普通 live 默认从 pfb_eff_live_laser_01 开始
            return 0;
        }

        private int GetLaserMaterialIndex(object laserData, int fallbackOrder)
        {
            int v;

            // 官方 LiveTimelineLaserData 实际字段名。
            v = GetIntMember(laserData, "_materialIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "materialIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "laserMaterialIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "matIndex", int.MinValue);
            if (v != int.MinValue) return v;

            v = GetIntMember(laserData, "materialNo", int.MinValue);
            if (v != int.MinValue) return v;

            if (_laserMaterials != null && _laserMaterials.Length > 0)
                return Mathf.Clamp(fallbackOrder, 0, _laserMaterials.Length - 1);

            return 0;
        }

        private static int GetIntMember(object obj, string name, int fallback)
        {
            object v = GetMemberValue(obj, name);

            if (v is int i)
                return i;

            return fallback;
        }

        private static object GetMemberValue(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name))
                return null;

            Type t = obj.GetType();

            while (t != null)
            {
                var f = t.GetField(
                    name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (f != null)
                    return f.GetValue(obj);

                var p = t.GetProperty(
                    name,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);

                if (p != null)
                    return p.GetValue(obj, null);

                t = t.BaseType;
            }

            return null;
        }

        private static void DumpLaserDataIntFields(object data)
        {
            if (data == null)
                return;

            Debug.Log("[LaserDataDump] type=" + data.GetType().FullName);

            var fields = data.GetType().GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            foreach (var f in fields)
            {
                if (f.FieldType == typeof(int))
                    Debug.Log("[LaserDataDump] int " + f.Name + "=" + f.GetValue(data));
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return obj != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj) : 0;
            }
        }
    }
}
