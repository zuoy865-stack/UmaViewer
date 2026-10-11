using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Director VMD 动作导出分部类，负责在退出 Live 时将角色骨骼动作与多机位镜头导出为 VMD 格式
    /// </summary>
    public partial class Director
    {
        DateTime ExitTime;

        /// <summary>
        /// 退出 Live 场景并在需要时触发 VMD 保存
        /// </summary>
        private void ExitLive()
        {
            isExit = true;
            ClearLiveProps();
            if (_liveTimelineControl != null && _liveTimelineControl.IsRecordVMD)
            {
                ExitTime = DateTime.Now;
                SaveCameraVMD();
                SaveMultiCameraVMD();
                SaveCharacterVMD();
            }
            UmaSceneController.LoadScene(
                "Version2",
                null,
                delegate
                {
                    // 等旧 LiveScene 完全销毁后再清理，避免过场期间角色/舞台对象失去资源。
                    UmaAssetManager.UnloadAllBundle(true);
                });
        }

        /// <summary>
        /// 保存全部角色的骨骼动作轨迹为 VMD 文件
        /// </summary>
        private void SaveCharacterVMD()
        {
            foreach (var container in CharaContainerScript)
            {
                if (container == null) continue;
                var rootbone = container.transform.Find("Position");
                if (rootbone != null && rootbone.gameObject.TryGetComponent(out UnityHumanoidVMDRecorder recorder))
                {
                    if (recorder.IsRecording)
                    {
                        recorder.StopRecording();
                        recorder.SaveLiveVMD(live, ExitTime, $"Live{live.MusicId}_Pos{CharaContainerScript.IndexOf(container)}", Config.Instance.VmdKeyReductionLevel);
                    }
                }
            }
        }

        /// <summary>
        /// 保存多机位相机轨迹为 VMD 文件
        /// </summary>
        private void SaveMultiCameraVMD()
        {
            if (_liveTimelineControl?.data?.worksheetList == null || _liveTimelineControl.data.worksheetList.Count == 0)
                return;

            for (int i = 0; i < _liveTimelineControl.data.worksheetList[0].multiCameraPosKeys.Count; i++)
            {
                var frames = _liveTimelineControl.MultiRecordFrames[i];
                if (frames == null || frames.Count == 0) continue;
                frames[0].FovVaild = true;
                var fov = _liveTimelineControl.data.worksheetList[0].multiCameraPosKeys[i].keys.thisList;
                fov.ForEach(k =>
                {
                    var keyframe = frames.Find(f => f.frameIndex == k.frame);
                    if (keyframe != null)
                    {
                        var index = frames.IndexOf(keyframe);
                        keyframe.FovVaild = true;
                        if (index + 1 < frames.Count) frames[index + 1].FovVaild = true;
                        if (index - 1 > 0) frames[index - 1].FovVaild = true;
                        if (index - 2 > 0) frames[index - 2].FovVaild = true;
                        if (index - 3 > 0) frames[index - 3].FovVaild = true;
                    }
                });

                UnityCameraVMDRecorder.SaveLiveCameraVMD(live, ExitTime, frames, i);
            }
        }

        /// <summary>
        /// 保存主相机轨迹为 VMD 文件
        /// </summary>
        private void SaveCameraVMD()
        {
            if (_liveTimelineControl?.data?.worksheetList == null || _liveTimelineControl.data.worksheetList.Count == 0)
                return;

            var frames = _liveTimelineControl.RecordFrames;
            if (frames == null || frames.Count == 0) return;
            frames[0].FovVaild = true;
            var fov = _liveTimelineControl.data.worksheetList[0].cameraFovKeys.thisList;
            fov.ForEach(k =>
            {
                var keyframe = frames.Find(f => f.frameIndex == k.frame);
                if (keyframe != null)
                {
                    var index = frames.IndexOf(keyframe);
                    keyframe.FovVaild = true;
                    if (index + 1 < frames.Count) frames[index + 1].FovVaild = true;
                    if (index - 1 > 0) frames[index - 1].FovVaild = true;
                    if (index - 2 > 0) frames[index - 2].FovVaild = true;
                    if (index - 3 > 0) frames[index - 3].FovVaild = true;
                }
            });

            UnityCameraVMDRecorder.SaveLiveCameraVMD(live, ExitTime, frames);
        }
    }
}
