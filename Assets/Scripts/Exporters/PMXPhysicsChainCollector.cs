using System;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using UnityEngine;

/// <summary>按 CySpring 声明、骨骼层级和正蒙皮权重筛选附件链。</summary>
internal static class PMXPhysicsChainCollector
{
    internal sealed class Candidate
    {
        internal Transform Root;
        internal PMXPhysicsExporter.AttachmentCategory Category;
        internal readonly Dictionary<Transform, float> Radii = new Dictionary<Transform, float>();
        internal readonly List<string> Sources = new List<string>();
    }

    internal static List<Candidate> CollectCandidates(UmaContainerCharacter character, Transform[] hierarchy,
        HashSet<Transform> claimed)
    {
        var result = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var byName = hierarchy.GroupBy(item => item.name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (CySpringDataContainer container in character.cySpringDataContainers)
        {
            if (container == null || container.springParam == null) continue;
            foreach (CySpringParamDataElement element in container.springParam)
            {
                if (element == null || string.IsNullOrWhiteSpace(element._boneName)) continue;
                if (!TryResolve(byName, element._boneName, out Transform root)) continue;
                var names = new List<string> { element._boneName };
                if (element._childElements != null)
                    names.AddRange(element._childElements.Where(child => child != null).Select(child => child._boneName));
                PMXPhysicsExporter.AttachmentCategory category = Classify(names, root);
                if (category == PMXPhysicsExporter.AttachmentCategory.None)
                {
                    if (LooksLikeUnclassifiedAttachment(names, root))
                        Debug.LogWarning("[PMXPhysics] CySpring 附件类别不明确，未自动收链: " + GetPath(root));
                    continue;
                }
                if (claimed.Contains(root))
                {
                    Debug.LogWarning("[PMXPhysics] 附件链根已由裙摆或既有链接管，跳过: " + root.name);
                    continue;
                }
                string key = category + "|" + GetPath(root);
                if (!result.TryGetValue(key, out Candidate candidate))
                {
                    candidate = new Candidate { Root = root, Category = category };
                    result.Add(key, candidate);
                }
                candidate.Sources.Add(FormatSource(element._boneName, element._stiffnessForce,
                    element._dragForce, element._gravity, element._collisionRadius));
                Add(candidate, byName, element._boneName, element._collisionRadius);
                if (element._childElements == null) continue;
                foreach (CySpringParamDataChildElement child in element._childElements)
                {
                    if (child == null) continue;
                    candidate.Sources.Add(FormatSource(child._boneName, child._stiffnessForce,
                        child._dragForce, child._gravity, child._collisionRadius));
                    Add(candidate, byName, child._boneName, child._collisionRadius);
                }
            }
        }
        return result.Values.ToList();
    }

    internal static void ResolveAttachments(PMXPhysicsExporter.Context context, PMXMeshExportContext mesh)
    {
        if (context == null || mesh == null) return;
        var weighted = CollectWeightedBones(mesh);
        var claimed = new HashSet<Transform>(context.DynamicBones);
        foreach (PMXPhysicsExporter.SkirtColumn column in context.SkirtColumns)
            if (column.Chain != null) claimed.UnionWith(column.Chain.Bones);

        foreach (Candidate candidate in context.AttachmentCandidates)
        {
            var chain = new PMXPhysicsExporter.Chain
            {
                Root = candidate.Root,
                Attachment = candidate.Category,
                IsEar = false,
                IsTail = false
            };
            CollectContinuous(candidate.Root, candidate.Radii, chain, claimed);
            if (!HasCompleteChain(candidate, chain))
            {
                Debug.LogWarning("[PMXPhysics] 附件 CySpring 参数链断开或与既有链重叠，跳过: " + GetPath(candidate.Root));
                continue;
            }
            if (chain.Bones.Count == 0 || !HasPositiveWeight(chain, weighted))
            {
                Debug.LogWarning("[PMXPhysics] 附件链无连续参数骨或无正蒙皮权重，跳过: " + GetPath(candidate.Root));
                continue;
            }
            if (claimed.Contains(candidate.Root)) continue;
            context.Chains.Add(chain);
            context.DynamicBones.UnionWith(chain.Bones);
            claimed.UnionWith(chain.Bones);
            Debug.Log("[PMXPhysics] 附件分类=" + candidate.Category + " root=" + GetPath(candidate.Root) +
                      " bones=" + chain.Bones.Count + " weighted=" + chain.Bones.Count(weighted.Contains) +
                      " dynamicGroup=" + PMXPhysicsExporter.DynamicCollisionGroup +
                      " allowedGroups=" + AllowedGroups(candidate.Category) + " mask=" + GetMask(candidate.Category).ToString("X4") +
                      " source=" + string.Join(";", candidate.Sources));
        }

        if (context.Chains.Any(chain => chain.IsEar))
            Debug.LogWarning("[PMXPhysics] 耳部保留现有根链；没有 CySpring 源映射证明的逐段兄弟骨驱动未改动。");
    }

    internal static ushort GetMask(PMXPhysicsExporter.AttachmentCategory category)
    {
        switch (category)
        {
            case PMXPhysicsExporter.AttachmentCategory.Bust:
                return PMXPhysicsExporter.CreateCollisionMaskWithAllowedGroups(
                    PMXPhysicsExporter.SkirtCollisionGroup, PMXPhysicsExporter.SkirtLegCollisionGroup);
            case PMXPhysicsExporter.AttachmentCategory.Ribbon:
                return PMXPhysicsExporter.CreateCollisionMaskWithAllowedGroups(
                    PMXPhysicsExporter.SkirtCollisionGroup, PMXPhysicsExporter.SkirtLegCollisionGroup);
            case PMXPhysicsExporter.AttachmentCategory.Cape:
                return PMXPhysicsExporter.CreateCollisionMaskWithAllowedGroups(
                    PMXPhysicsExporter.SkirtCollisionGroup, PMXPhysicsExporter.SkirtLegCollisionGroup);
            default: return 0;
        }
    }

    private static string AllowedGroups(PMXPhysicsExporter.AttachmentCategory category)
    {
        switch (category)
        {
            case PMXPhysicsExporter.AttachmentCategory.Bust: return "skirt,body-leg";
            case PMXPhysicsExporter.AttachmentCategory.Ribbon: return "skirt,body-leg";
            case PMXPhysicsExporter.AttachmentCategory.Cape: return "skirt,body-leg";
            default: return "none";
        }
    }

    private static string FormatSource(string name, float stiffness, float drag, float gravity, float radius)
    {
        return name + "(stiff=" + stiffness.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
               ",drag=" + drag.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
               ",gravity=" + gravity.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
               ",radius=" + radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")";
    }

    private static bool LooksLikeUnclassifiedAttachment(List<string> names, Transform root)
    {
        string text = string.Join(" ", names.Where(name => !string.IsNullOrEmpty(name))) + " " + GetPath(root);
        return new[] { "cloth", "dress", "sleeve", "ornament", "accessory", "mskirt", "leaf", "装饰", "衣装" }
            .Any(token => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static HashSet<Transform> CollectWeightedBones(PMXMeshExportContext mesh)
    {
        var result = new HashSet<Transform>();
        foreach (PMXMeshExportContext.Item item in mesh.Items)
        {
            if (item.Weights == null || item.Bones == null) continue;
            foreach (BoneWeight weight in item.Weights)
            {
                AddWeighted(result, item.Bones, weight.boneIndex0, weight.weight0);
                AddWeighted(result, item.Bones, weight.boneIndex1, weight.weight1);
                AddWeighted(result, item.Bones, weight.boneIndex2, weight.weight2);
                AddWeighted(result, item.Bones, weight.boneIndex3, weight.weight3);
            }
        }
        return result;
    }

    private static void AddWeighted(HashSet<Transform> bones, Transform[] source, int index, float weight)
    {
        if (weight > 0f && index >= 0 && index < source.Length && source[index] != null)
            bones.Add(source[index]);
    }

    private static void CollectContinuous(Transform bone, Dictionary<Transform, float> configured,
        PMXPhysicsExporter.Chain chain, HashSet<Transform> claimed)
    {
        if (!configured.TryGetValue(bone, out float radius) || claimed.Contains(bone)) return;
        chain.Bones.Add(bone);
        chain.Radii[bone] = PMXPhysicsExporter.SanitizeRadius(radius);
        for (int i = 0; i < bone.childCount; i++)
            CollectContinuous(bone.GetChild(i), configured, chain, claimed);
    }

    private static void Add(Candidate candidate, Dictionary<string, Transform[]> byName, string name, float radius)
    {
        if (string.IsNullOrWhiteSpace(name) || !TryResolve(byName, name, out Transform bone)) return;
        if (!IsDescendantOrSelf(bone, candidate.Root))
        {
            Debug.LogWarning("[PMXPhysics] CySpring 参数骨不在根的实际后代链，忽略: " + name);
            return;
        }
        candidate.Radii[bone] = radius;
    }

    internal static PMXPhysicsExporter.AttachmentCategory Classify(List<string> names, Transform root)
    {
        // 只看 CySpring 骨名和根名，避免 Chest 等身体祖先污染附件类别。
        string text = string.Join(" ", names.Where(name => !string.IsNullOrEmpty(name))) + " " + root.name;
        var found = new HashSet<PMXPhysicsExporter.AttachmentCategory>();
        AddCategory(found, text, PMXPhysicsExporter.AttachmentCategory.Bust,
            "sp_ch_bust0_", "bust", "breast", "mune", "胸");
        AddCategory(found, text, PMXPhysicsExporter.AttachmentCategory.Ribbon,
            "sp_kn_ribbon", "sp_ch_ribbon", "ribbon", "ribon", "リボン", "飘带");
        AddCategory(found, text, PMXPhysicsExporter.AttachmentCategory.Cape,
            "sp_ch_mantle", "sp_so_mantle", "cape", "cloak", "poncho", "マント", "披风");
        return found.Count == 1 ? found.First() : PMXPhysicsExporter.AttachmentCategory.None;
    }

    internal static bool HasPositiveWeight(PMXPhysicsExporter.Chain chain, HashSet<Transform> weighted)
    {
        return chain != null && weighted != null && chain.Bones.Any(weighted.Contains);
    }

    internal static bool HasCompleteChain(Candidate candidate, PMXPhysicsExporter.Chain chain)
    {
        return candidate != null && chain != null && chain.Bones.Count == candidate.Radii.Count;
    }

    private static void AddCategory(HashSet<PMXPhysicsExporter.AttachmentCategory> found, string text,
        PMXPhysicsExporter.AttachmentCategory category, params string[] tokens)
    {
        if (tokens.Any(token => text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)) found.Add(category);
    }

    private static bool TryResolve(Dictionary<string, Transform[]> byName, string name, out Transform bone)
    {
        bone = null;
        if (!byName.TryGetValue(name, out Transform[] matches)) return false;
        if (matches.Length != 1)
        {
            Debug.LogWarning("[PMXPhysics] 同名 CySpring 骨无法消歧，跳过: " + name + " count=" + matches.Length);
            return false;
        }
        bone = matches[0];
        return true;
    }

    private static bool IsDescendantOrSelf(Transform candidate, Transform root)
    {
        for (Transform current = candidate; current != null; current = current.parent)
            if (current == root) return true;
        return false;
    }

    private static string GetPath(Transform transform)
    {
        var names = new Stack<string>();
        for (Transform current = transform; current != null; current = current.parent) names.Push(current.name);
        return string.Join("/", names);
    }
}
