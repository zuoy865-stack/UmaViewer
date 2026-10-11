using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Director 镜面反射逻辑分部类，负责舞台镜面反射组件的收集、初始化与每帧相机参数同步渲染
    /// </summary>
    public partial class Director
    {
        [SerializeField] private bool _enableMirrorReflection = true;
        [SerializeField] private List<MirrorReflection> _mirrorReflections = new List<MirrorReflection>();
        [SerializeField] private bool _mirrorRenderInLateUpdate = true;

        /// <summary>
        /// 初始化全舞台与场景中的镜面反射组件
        /// </summary>
        private void InitializeMirrorReflections()
        {
            if (!_enableMirrorReflection)
                return;

            _mirrorReflections.Clear();

            AddMirrorReflections(_mirrorReflections, GetComponentsInChildren<MirrorReflection>(true));

            if (_stageController != null)
                AddMirrorReflections(_mirrorReflections, _stageController.GetComponentsInChildren<MirrorReflection>(true));

            if (_mirrorReflections.Count == 0)
                AddMirrorReflections(_mirrorReflections, FindObjectsOfType<MirrorReflection>(true));

            if (_mirrorReflections.Count == 0)
            {
                Debug.Log("[Mirror] No MirrorReflection found.");
                return;
            }

            Camera mainCam = null;
            if (_cameraObjects != null && _activeCameraIndex >= 0 && _activeCameraIndex < _cameraObjects.Length)
                mainCam = _cameraObjects[_activeCameraIndex];

            if (mainCam == null)
                mainCam = Camera.main;

            for (int i = 0; i < _mirrorReflections.Count; i++)
            {
                var mirror = _mirrorReflections[i];
                if (mirror == null) continue;

                mirror.Initialize(mainCam, i, false);
                mirror.SetupBaseCamera(mainCam, GetMainCameraFovFactor);
            }

            Debug.Log($"[Mirror] Initialized {_mirrorReflections.Count} mirrors.");
        }

        /// <summary>
        /// 批量添加镜面反射组件到目标列表中并自动去重
        /// </summary>
        private static void AddMirrorReflections(List<MirrorReflection> target, MirrorReflection[] mirrors)
        {
            if (target == null || mirrors == null)
                return;

            for (int i = 0; i < mirrors.Length; i++)
            {
                var mirror = mirrors[i];
                if (mirror == null || target.Contains(mirror))
                    continue;

                target.Add(mirror);
            }
        }

        /// <summary>
        /// 获取主相机 FOV 缩放系数
        /// </summary>
        private float GetMainCameraFovFactor()
        {
            return 1f;
        }

        /// <summary>
        /// 每帧更新镜面反射渲染参数与基准相机
        /// </summary>
        private void UpdateMirrorReflections()
        {
            if (_mirrorReflections == null || _mirrorReflections.Count == 0)
                return;

            Camera mainCam = null;
            if (_cameraObjects != null && _activeCameraIndex >= 0 && _activeCameraIndex < _cameraObjects.Length)
                mainCam = _cameraObjects[_activeCameraIndex];

            if (mainCam == null)
                mainCam = Camera.main;

            for (int i = 0; i < _mirrorReflections.Count; i++)
            {
                var mirror = _mirrorReflections[i];
                if (mirror == null || !mirror.isActiveAndEnabled) continue;

                mirror.SetBaseCamera(mainCam);
                mirror.SetFovFactorGetter(GetMainCameraFovFactor);
                mirror.UpdateMirrorParams();
                mirror.ForceRenderOnce();
            }
        }
    }
}
