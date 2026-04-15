using System;

namespace Gallop.Live.Cutt
{
    [Serializable]
    public class LiveTimelineKeyMonitorCameraPositionData : LiveTimelineKeyCameraPositionData
    {
    }

    [Serializable]
    public class LiveTimelineKeyMonitorCameraPositionDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyMonitorCameraPositionData>
    {
    }

    [Serializable]
    public class LiveTimelineMonitorCameraPositionData : ILiveTimelineGroupDataWithName
    {
        private const string default_name = "MonitorCameraPos";
        public LiveTimelineKeyMonitorCameraPositionDataList keys;
    }

    [Serializable]
    public class LiveTimelineKeyMonitorCameraLookAtData : LiveTimelineKeyCameraLookAtData
    {
    }

    [Serializable]
    public class LiveTimelineKeyMonitorCameraLookAtDataList : LiveTimelineKeyDataListTemplate<LiveTimelineKeyMonitorCameraLookAtData>
    {
    }

    [Serializable]
    public class LiveTimelineMonitorCameraLookAtData : ILiveTimelineGroupDataWithName
    {
        private const string default_name = "MonitorCameraLookAt";
        public LiveTimelineKeyMonitorCameraLookAtDataList keys;
    }
}
