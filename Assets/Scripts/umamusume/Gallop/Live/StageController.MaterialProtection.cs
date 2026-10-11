using System;
using UnityEngine;

namespace Gallop.Live
{
    /// <summary>
    /// StageController 材质防护分部类：
    /// 负责舞台部件实例化后的材质损坏检测、空材质回退及备用材质生成，杜绝白色无光照死模。
    /// </summary>
    public partial class StageController
    {
        /// <summary>
        /// 针对舞台部件实例下的所有渲染器进行空材质防护检测与修复，防止因缺少材质而呈现纯白死模遮蔽舞台。
        /// </summary>
        /// <param name="instance">已实例化的舞台部件对象</param>
        /// <param name="partName">部件资源预制体名称</param>
        private void ProtectRendererMaterials(GameObject instance, string partName)
        {
            if (instance == null)
                return;

            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return;

            Material[] loadedMaterialsCache = null;

            for (int ri = 0; ri < renderers.Length; ri++)
            {
                var r = renderers[ri];
                if (r == null)
                    continue;

                // 只修 sharedMaterials 里为 null 的槽。URP 下 Gallop 着色器经常 isSupported=false，不能当损坏。
                var sharedMats = r.sharedMaterials;
                if (sharedMats == null || sharedMats.Length == 0)
                    continue;

                bool hasNullSlot = false;
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    if (sharedMats[i] == null)
                    {
                        hasNullSlot = true;
                        break;
                    }
                }
                if (!hasNullSlot)
                    continue;

                bool isSky = StageColorLane.NameLooksLikeSky(r.name) || StageColorLane.NameLooksLikeSky(partName);
                var fixedMats = (Material[])sharedMats.Clone();
                bool changed = false;

                for (int i = 0; i < fixedMats.Length; i++)
                {
                    if (fixedMats[i] != null)
                        continue;

                    if (loadedMaterialsCache == null)
                        loadedMaterialsCache = Resources.FindObjectsOfTypeAll<Material>();

                    Material found = FindMatchingLoadedMaterial(loadedMaterialsCache, r.name, partName);
                    if (found == null && !isSky)
                        found = FindSiblingFallbackMaterial(renderers);

                    // 天空找不到就保持 null，不造浅白 Unlit，也不关网格。
                    if (found == null)
                        continue;
                    if (isSky && !StageColorLane.NameLooksLikeSky(found.name))
                        continue;

                    fixedMats[i] = found;
                    changed = true;
                }

                if (changed)
                    r.sharedMaterials = fixedMats;
            }
        }

        /// <summary>
        /// 从已载入的材质列表中按渲染器名、部件名检索最匹配的备用材质
        /// </summary>
        private Material FindMatchingLoadedMaterial(Material[] loadedMaterials, string rendererName, string partName)
        {
            if (loadedMaterials == null || loadedMaterials.Length == 0)
                return null;

            string rName = (rendererName ?? "").ToLowerInvariant();
            string pName = (partName ?? "").ToLowerInvariant();

            // 1. 精确/包含渲染器名称匹配（例如 sky000、sky001 等）
            if (!string.IsNullOrEmpty(rName))
            {
                for (int i = 0; i < loadedMaterials.Length; i++)
                {
                    var mat = loadedMaterials[i];
                    if (mat == null || string.IsNullOrEmpty(mat.name))
                        continue;

                    string mName = mat.name.ToLowerInvariant();
                    if (mName.Contains(rName))
                        return mat;
                }
            }

            // 2. 天空网格专属匹配：若当前为天空网格，优先匹配包含 sky 与 env 的材质
            bool isSky = rName.Contains("sky") || pName.Contains("sky");
            if (isSky)
            {
                for (int i = 0; i < loadedMaterials.Length; i++)
                {
                    var mat = loadedMaterials[i];
                    if (mat == null || string.IsNullOrEmpty(mat.name))
                        continue;

                    string mName = mat.name.ToLowerInvariant();
                    if (mName.Contains("sky") && mName.Contains("env"))
                        return mat;
                }
            }

            return null;
        }

        /// <summary>
        /// 从同一部件中的其他有效渲染器中获取可用的备用材质
        /// </summary>
        private Material FindSiblingFallbackMaterial(Renderer[] siblings)
        {
            if (siblings == null)
                return null;

            for (int i = 0; i < siblings.Length; i++)
            {
                var sib = siblings[i];
                if (sib == null)
                    continue;

                var mats = sib.sharedMaterials;
                if (mats == null)
                    continue;

                for (int j = 0; j < mats.Length; j++)
                {
                    if (mats[j] != null)
                        return mats[j];
                }
            }

            return null;
        }
    }
}
