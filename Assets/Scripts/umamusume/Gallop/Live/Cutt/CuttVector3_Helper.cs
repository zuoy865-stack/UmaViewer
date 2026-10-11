using UnityEngine;

namespace Gallop.Live.Cutt
{
    /// <summary>
    /// Live演出Cutt系统Vector3辅助工具类，用于对浮点数坐标进行精度舍入对齐
    /// </summary>
    public static class CuttVector3_Helper
    {
        /// <summary>
        /// 将向量各分量保留3位小数（乘以1000取整后再除以1000），消除浮点数微小震颤
        /// </summary>
        public static Vector3 Round(this Vector3 This)
        {
            This.x = Mathf.Round(This.x * 1000f) / 1000f;
            This.y = Mathf.Round(This.y * 1000f) / 1000f;
            This.z = Mathf.Round(This.z * 1000f) / 1000f;
            return This;
        }
    }
}
