using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// Live 每帧耗时分段统计器（极轻量、无托管分配）。
    ///
    /// 存在意义：把「掉帧到底发生在哪个子系统」变成可读的数字，而不是靠猜。
    /// Begin/End 成对包裹一段代码，slot 用下面的常量；每 ReportEveryFrames 帧自动输出一次
    /// 按耗时排序的分段表（平均 ms/帧 + 占整帧比例）。
    ///
    /// 不需要时把 Enabled 置 false，开销降为每帧几条 bool 判断。
    /// 注意：Begin/End 必须严格执行栈式配对（同一 slot 不要嵌套自己）。
    /// </summary>
    public static class LiveFrameProfiler
    {
        public static bool Enabled = true;
        public static int ReportEveryFrames = 180;

        public const int AlterUpdate = 0;
        public const int MotionSequence = 1;
        public const int Facial = 2;
        public const int AlterLateUpdate = 3;
        public const int CameraGroup = 4;
        public const int LightingGroup = 5;
        public const int WorksheetLoop = 6;
        public const int WsTransformObject = 7;
        public const int WsMobCyalume = 8;
        public const int WsBlinkWash = 9;
        public const int WsLaserUv = 10;
        public const int WsAnimProps = 11;
        public const int BgColor2 = 12;
        public const int AudioVocal = 13;
        public const int StageController = 14;
        public const int SlotCount = 15;

        private static readonly string[] Names =
        {
            "AlterUpdate(总)",
            "  角色动作序列",
            "  表情/眼动",
            "AlterLateUpdate(总)",
            "  相机组",
            "  光照/后处理",
            "  逐 worksheet 循环(总)",
            "    Transform/Object",
            "    Mob/Cyalume",
            "    Blink/WashLight",
            "    Laser/UVScroll",
            "    Anim/Props",
            "  BgColor2",
            "人声更新",
            "舞台控制器"
        };

        private static readonly long[] _accumTicks = new long[SlotCount];
        private static readonly long[] _startTicks = new long[SlotCount];
        private static readonly int[] _order = new int[SlotCount];
        private static readonly StringBuilder _sb = new StringBuilder(1536);

        private static long _frameStartTicks;
        private static long _totalFrameTicks;
        private static float _realFrameTimeAccum;
        private static int _frames;

        public static void BeginFrame()
        {
            if (!Enabled) return;

            // Time.deltaTime 在 Update 里读到的就是上一帧的真实时长（含渲染与 LateUpdate）。
            // 用它才能判断「掉帧到底有多少是渲染/GPU，而不是 Live 的 C# 逻辑」。
            float realDt = Time.deltaTime;
            if (realDt > 0f && realDt < 0.5f)
                _realFrameTimeAccum += realDt;

            _frameStartTicks = Stopwatch.GetTimestamp();
        }

        public static void EndFrame()
        {
            if (!Enabled) return;

            long d = Stopwatch.GetTimestamp() - _frameStartTicks;

            // 丢弃明显异常的跨度（早退导致的未配对帧），避免污染统计
            if (d > 0 && d < Stopwatch.Frequency / 2)
                _totalFrameTicks += d;

            _frames++;
            if (_frames < ReportEveryFrames)
                return;

            Report();
            _frames = 0;
            _totalFrameTicks = 0;
            _realFrameTimeAccum = 0f;
            Array.Clear(_accumTicks, 0, SlotCount);
        }

        public static void Begin(int slot)
        {
            if (!Enabled) return;
            if (slot < 0 || slot >= SlotCount) return;
            _startTicks[slot] = Stopwatch.GetTimestamp();
        }

        public static void End(int slot)
        {
            if (!Enabled) return;
            if (slot < 0 || slot >= SlotCount) return;

            long d = Stopwatch.GetTimestamp() - _startTicks[slot];
            if (d > 0)
                _accumTicks[slot] += d;
        }

        private static void Report()
        {
            double ticksToMs = 1000.0 / Stopwatch.Frequency;
            double frameMs = _totalFrameTicks * ticksToMs / _frames;

            for (int i = 0; i < SlotCount; i++)
                _order[i] = i;

            // 插入排序（15 个元素，避免 Linq 分配）
            for (int i = 1; i < SlotCount; i++)
            {
                int key = _order[i];
                int j = i - 1;
                while (j >= 0 && _accumTicks[_order[j]] < _accumTicks[key])
                {
                    _order[j + 1] = _order[j];
                    j--;
                }
                _order[j + 1] = key;
            }

            _sb.Length = 0;

            double realMs = _realFrameTimeAccum * 1000.0 / _frames;
            double liveMs = frameMs;

            _sb.Append("[LiveProfiler] 真实帧 ").Append(realMs.ToString("F2")).Append(" ms (约 ")
               .Append((1000.0 / System.Math.Max(realMs, 0.0001)).ToString("F1")).Append(" fps)   ")
               .Append("Live C# ").Append(liveMs.ToString("F2")).Append(" ms (占 ")
               .Append((liveMs / System.Math.Max(realMs, 0.0001) * 100.0).ToString("F1")).Append("%)  ")
               .Append(_frames).Append(" 帧\n");

            if (realMs > liveMs)
            {
                _sb.Append("  (其余 ").Append((realMs - liveMs).ToString("F2"))
                   .Append(" ms 在渲染/LateUpdate/物理侧，本表未覆盖 —— 这部分要去 Unity Profiler 看)\n");
            }

            for (int i = 0; i < SlotCount; i++)
            {
                int slot = _order[i];
                double ms = _accumTicks[slot] * ticksToMs / _frames;
                if (ms < 0.01)
                    continue;

                _sb.Append("  ").Append(Names[slot].PadRight(24))
                   .Append(ms.ToString("F2").PadLeft(8)).Append(" ms  ")
                   .Append((ms / System.Math.Max(frameMs, 0.0001) * 100.0).ToString("F1")).Append("%\n");
            }

            UnityEngine.Debug.Log(_sb.ToString());
        }
    }
}
