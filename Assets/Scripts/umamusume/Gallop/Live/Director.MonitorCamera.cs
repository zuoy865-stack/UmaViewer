using Gallop.Live.Cutt;
using System;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Director 的监视器摄像机（MonitorCamera）扩展分部类
    /// 负责为包含舞台屏幕/点唱机监视器轨道的 Live 场景动态创建专用的离屏 MonitorCamera，
    /// 每帧根据时间轴上的 monitorCameraPosKeys 与 monitorCameraLookAtKeys 关键帧计算空间坐标与注视点，
    /// 将渲染画面输出至独立的离屏 RenderTexture，为 StageMonitorDriver 提供舞台大屏幕实时摄像机输入源。
    /// </summary>
    public partial class Director
    {
        [Header("舞台监视器摄像机运行时管理")]
        [SerializeField] private GameObject _monitorCameraObj;
        [SerializeField] private Camera _monitorCamera;
        private RenderTexture _monitorCameraRT;
        private LiveTimelineControl _boundMonitorCameraTimeline;
        private bool _hasMonitorCameraData;

        /// <summary>
        /// 监视器摄像机离屏渲染纹理（供 StageMonitorDriver 作为实时画面主纹理）
        /// </summary>
        public RenderTexture MonitorCameraTexture => _monitorCameraRT;

        /// <summary>
        /// 根据当前 Live 时间轴数据初始化监视器摄像机与专用离屏 RenderTexture
        /// </summary>
        /// <param name="control">当前 Live 时间轴控制器</param>
        public void InitializeMonitorCamera(LiveTimelineControl control)
        {
            CleanupMonitorCamera();

            if (control == null || control.data == null || control.data.worksheetList == null || control.data.worksheetList.Count == 0)
            {
                return;
            }

            LiveTimelineWorkSheet camSheet = control.data.worksheetList[0];
            bool hasPosKeys = camSheet.monitorCameraPosKeys != null && camSheet.monitorCameraPosKeys.Count > 0 &&
                              camSheet.monitorCameraPosKeys[0].keys != null && camSheet.monitorCameraPosKeys[0].keys.Count > 0;

            if (!hasPosKeys)
            {
                return;
            }

            _boundMonitorCameraTimeline = control;
            _hasMonitorCameraData = true;

            // 根据屏幕分辨率按比例创建离屏渲染目标纹理（通常为 0.5 倍宽屏分辨率，兜底 960x540）
            int width = Mathf.Max(512, Mathf.RoundToInt((Screen.width > 0 ? Screen.width : 1920) * 0.5f));
            int height = Mathf.Max(288, Mathf.RoundToInt((Screen.height > 0 ? Screen.height : 1080) * 0.5f));

            _monitorCameraRT = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
            {
                name = "Live_MonitorCamera_RT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            _monitorCameraRT.Create();

            // 创建专用的 MonitorCamera 节点
            _monitorCameraObj = new GameObject("MonitorCamera");
            _monitorCameraObj.transform.SetParent(control.transform, false);

            _monitorCamera = _monitorCameraObj.AddComponent<Camera>();
            _monitorCamera.targetTexture = _monitorCameraRT;
            _monitorCamera.clearFlags = CameraClearFlags.Color;
            _monitorCamera.backgroundColor = Color.black;
            // 深度设为 -5，确保在主相机渲染之前完成画面绘制，纹理就绪
            _monitorCamera.depth = -5;
            _monitorCamera.enabled = true;

            Debug.Log($"[Director.MonitorCamera] 舞台监视器摄像机初始化就绪，离屏分辨率：{width}x{height}。");
        }

        /// <summary>
        /// 每帧在时间轴更新时计算监视器摄像机的位置、视野 FOV、倾角 Roll 与注视点 LookAt
        /// </summary>
        /// <param name="sheet">当前时间轴工作表</param>
        /// <param name="currentFrame">当前时间轴帧号</param>
        public void UpdateMonitorCamera(LiveTimelineWorkSheet sheet, float currentFrame)
        {
            if (!_hasMonitorCameraData || _monitorCamera == null || sheet == null)
            {
                return;
            }

            if (sheet.monitorCameraPosKeys == null || sheet.monitorCameraPosKeys.Count == 0)
            {
                return;
            }

            var posKeys = sheet.monitorCameraPosKeys[0].keys;
            if (posKeys == null || posKeys.Count == 0)
            {
                return;
            }

            LiveTimelineControl.FindTimelineKey(out LiveTimelineKey curPosKeyBase, out LiveTimelineKey nextPosKeyBase, posKeys, currentFrame);
            if (!(curPosKeyBase is LiveTimelineKeyMonitorCameraPositionData curPosKey))
            {
                return;
            }

            LiveTimelineKeyMonitorCameraPositionData nextPosKey = nextPosKeyBase as LiveTimelineKeyMonitorCameraPositionData;

            // 1. 计算相机空间坐标
            Vector3 camPos = curPosKey.GetValue(_boundMonitorCameraTimeline);
            float fov = curPosKey.fov > 0f ? curPosKey.fov : 45f;
            float roll = curPosKey.roll;

            if (nextPosKey != null && nextPosKey.interpolateType != 0)
            {
                float t = LiveTimelineControl.CalculateInterpolationValue(curPosKey, nextPosKey, currentFrame);
                Vector3 nextCamPos = nextPosKey.GetValue(_boundMonitorCameraTimeline);
                camPos = Vector3.LerpUnclamped(camPos, nextCamPos, t);
                if (nextPosKey.fov > 0f)
                {
                    fov = Mathf.LerpUnclamped(fov, nextPosKey.fov, t);
                }
                roll = Mathf.LerpUnclamped(roll, nextPosKey.roll, t);
            }

            _monitorCamera.transform.position = camPos;
            _monitorCamera.fieldOfView = fov;
            _monitorCamera.nearClipPlane = curPosKey.nearClip > 0.01f ? curPosKey.nearClip : 0.1f;
            _monitorCamera.farClipPlane = curPosKey.farClip > 1f ? curPosKey.farClip : 1000f;

            // 2. 计算相机注视点 LookAt
            if (sheet.monitorCameraLookAtKeys != null && sheet.monitorCameraLookAtKeys.Count > 0 &&
                sheet.monitorCameraLookAtKeys[0].keys != null && sheet.monitorCameraLookAtKeys[0].keys.Count > 0)
            {
                var lookKeys = sheet.monitorCameraLookAtKeys[0].keys;
                LiveTimelineControl.FindTimelineKey(out LiveTimelineKey curLookKeyBase, out LiveTimelineKey nextLookKeyBase, lookKeys, currentFrame);
                if (curLookKeyBase is LiveTimelineKeyMonitorCameraLookAtData curLookKey)
                {
                    Vector3 lookAtPos = curLookKey.GetValue(_boundMonitorCameraTimeline);
                    if (nextLookKeyBase is LiveTimelineKeyMonitorCameraLookAtData nextLookKey && nextLookKey.interpolateType != 0)
                    {
                        float tLook = LiveTimelineControl.CalculateInterpolationValue(curLookKey, nextLookKey, currentFrame);
                        Vector3 nextLookAtPos = nextLookKey.GetValue(_boundMonitorCameraTimeline);
                        lookAtPos = Vector3.LerpUnclamped(lookAtPos, nextLookAtPos, tLook);
                    }

                    _monitorCamera.transform.LookAt(lookAtPos, Vector3.up);
                }
            }

            // 3. 叠加 Z 轴倾斜 Roll 旋转
            if (Mathf.Abs(roll) > 0.001f)
            {
                Vector3 euler = _monitorCamera.transform.localEulerAngles;
                _monitorCamera.transform.localEulerAngles = new Vector3(euler.x, euler.y, roll);
            }
        }

        /// <summary>
        /// 安全释放监视器摄像机节点与离屏纹理
        /// </summary>
        public void CleanupMonitorCamera()
        {
            if (_monitorCameraRT != null)
            {
                _monitorCameraRT.Release();
                if (Application.isPlaying)
                {
                    Destroy(_monitorCameraRT);
                }
                else
                {
                    DestroyImmediate(_monitorCameraRT);
                }
                _monitorCameraRT = null;
            }

            if (_monitorCameraObj != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_monitorCameraObj);
                }
                else
                {
                    DestroyImmediate(_monitorCameraObj);
                }
                _monitorCameraObj = null;
            }

            _monitorCamera = null;
            _boundMonitorCameraTimeline = null;
            _hasMonitorCameraData = false;
        }
    }
}
