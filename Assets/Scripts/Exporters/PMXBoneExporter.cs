using LibMMD.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 将 Uma 的运行时骨架整理为 PMX 可用的标准骨架。
/// </summary>
internal static class PMXBoneExporter
{
    internal sealed class Result
    {
        public Bone[] Bones { get; set; }
        public Dictionary<Transform, int> BoneIndexes { get; set; }
    }

    private static readonly Dictionary<string, string> BoneNameMapping = new Dictionary<string, string>()
    {
        { "Spine", "上半身" }, { "Chest", "上半身2" }, { "Neck", "首" }, { "Head", "頭" },
        { "Shoulder_L", "左肩" }, { "Arm_L", "左腕" }, { "Elbow_L", "左ひじ" }, { "Wrist_L", "左手首" },
        { "Shoulder_R", "右肩" }, { "Arm_R", "右腕" }, { "Elbow_R", "右ひじ" }, { "Wrist_R", "右手首" },
        { "Thumb_01_L", "左親指０" }, { "Thumb_02_L", "左親指１" }, { "Thumb_03_L", "左親指２" },
        { "Index_01_L", "左人指１" }, { "Index_02_L", "左人指２" }, { "Index_03_L", "左人指３" },
        { "Middle_01_L", "左中指１" }, { "Middle_02_L", "左中指２" }, { "Middle_03_L", "左中指３" },
        { "Ring_01_L", "左薬指１" }, { "Ring_02_L", "左薬指２" }, { "Ring_03_L", "左薬指３" },
        { "Pinky_01_L", "左小指１" }, { "Pinky_02_L", "左小指２" }, { "Pinky_03_L", "左小指３" },
        { "Thumb_01_R", "右親指０" }, { "Thumb_02_R", "右親指１" }, { "Thumb_03_R", "右親指２" },
        { "Index_01_R", "右人指１" }, { "Index_02_R", "右人指２" }, { "Index_03_R", "右人指３" },
        { "Middle_01_R", "右中指１" }, { "Middle_02_R", "右中指２" }, { "Middle_03_R", "右中指３" },
        { "Ring_01_R", "右薬指１" }, { "Ring_02_R", "右薬指２" }, { "Ring_03_R", "右薬指３" },
        { "Pinky_01_R", "右小指１" }, { "Pinky_02_R", "右小指２" }, { "Pinky_03_R", "右小指３" },
        { "Thigh_L", "左足" }, { "Knee_L", "左ひざ" }, { "Ankle_L", "左足首" }, { "Toe_L", "左足先EX" },
        { "Thigh_R", "右足" }, { "Knee_R", "右ひざ" }, { "Ankle_R", "右足首" }, { "Toe_R", "右足先EX" },
        { "Eye_L", "左目" }, { "Eye_R", "右目" },
        { "Ear_01_L", "左耳" }, { "Ear_02_L", "左耳1" }, { "Ear_03_L", "左耳2" },
        { "Ear_01_R", "右耳" }, { "Ear_02_R", "右耳1" }, { "Ear_03_R", "右耳2" },
        { "Mouth", "口" }, { "Jaw", "顎" }
    };

    private static readonly string[] RequiredBoneNames =
    {
        "Hip", "Spine", "Chest", "Neck", "Head",
        "Shoulder_L", "Arm_L", "Elbow_L", "Wrist_L",
        "Shoulder_R", "Arm_R", "Elbow_R", "Wrist_R",
        "Thigh_L", "Knee_L", "Ankle_L", "Toe_L",
        "Thigh_R", "Knee_R", "Ankle_R", "Toe_R", "Eye_L", "Eye_R"
    };

    internal static Result Build(Transform skeletonRoot, Transform coordinateRoot, IEnumerable<Renderer> renderers)
    {
        Transform[] hierarchy = skeletonRoot.GetComponentsInChildren<Transform>(true);
        HashSet<Transform> selected = CollectReferencedBones(renderers);

        // 某些标准骨可能没有直接权重，但仍是动画和父链所必需的。
        foreach (string boneName in RequiredBoneNames)
        {
            Transform bone = hierarchy.FirstOrDefault(t => t.name.Equals(boneName, StringComparison.OrdinalIgnoreCase));
            if (bone != null)
            {
                selected.Add(bone);
            }
        }

        Transform hip = Find(selected, "Hip");
        List<Bone> bones = new List<Bone>();
        Dictionary<Transform, int> indexes = new Dictionary<Transform, int>();
        Vector3 origin = coordinateRoot.InverseTransformPoint(skeletonRoot.position);
        Vector3 hipPosition = hip != null ? coordinateRoot.InverseTransformPoint(hip.position) : origin;

        int parentOfAll = AddVirtualBone(bones, "全ての親", "ParentOfAll", origin, -1, true);
        int center = AddVirtualBone(bones, "センター", "Center", origin, parentOfAll, true);
        int groove = AddVirtualBone(bones, "グルーブ", "Groove", hipPosition, center, true);
        int waist = AddVirtualBone(bones, "腰", "Waist", hipPosition, groove, false);
        int lowerBody = AddVirtualBone(bones, "下半身", "LowerBody", hipPosition, waist, false);

        if (skeletonRoot != null) indexes[skeletonRoot] = center;
        if (hip != null) indexes[hip] = waist;

        foreach (Transform transform in hierarchy)
        {
            if (!selected.Contains(transform) || transform == hip || IsRuntimeHelper(transform.name))
            {
                continue;
            }

            int parentIndex = ResolveParentIndex(transform, hip, selected, indexes, waist, lowerBody);
            Bone bone = CreateTransformBone(transform, coordinateRoot, parentIndex);
            indexes[transform] = bones.Count;
            bones.Add(bone);
        }

        ReparentTongueChain(bones, indexes);
        RebuildChildLinks(bones);
        AddFootIk(bones, indexes, coordinateRoot, parentOfAll, "L");
        AddFootIk(bones, indexes, coordinateRoot, parentOfAll, "R");
        RebuildChildLinks(bones);
        Validate(bones);

        return new Result { Bones = bones.ToArray(), BoneIndexes = indexes };
    }

    private static HashSet<Transform> CollectReferencedBones(IEnumerable<Renderer> renderers)
    {
        HashSet<Transform> result = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer renderer in renderers.OfType<SkinnedMeshRenderer>())
        {
            foreach (Transform bone in renderer.bones)
            {
                if (bone != null && !IsRuntimeHelper(bone.name)) result.Add(bone);
            }
        }
        return result;
    }

    private static bool IsRuntimeHelper(string name)
    {
        return name.StartsWith("Col_", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Handle", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Pole", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Target", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Ctrl", StringComparison.OrdinalIgnoreCase) ||
               name.IndexOf("locator", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Transform Find(IEnumerable<Transform> bones, string name)
    {
        return bones.FirstOrDefault(t => t.name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveParentIndex(Transform transform, Transform hip, HashSet<Transform> selected,
        Dictionary<Transform, int> indexes, int waist, int lowerBody)
    {
        Transform parent = transform.parent;
        while (parent != null && parent != hip)
        {
            if (selected.Contains(parent) && indexes.TryGetValue(parent, out int parentIndex)) return parentIndex;
            parent = parent.parent;
        }

        if (parent == hip)
        {
            // 上半身控制链中的实际骨挂腰，其余髋部支链（腿、裙摆、尾巴）挂下半身。
            return IsUpperBodyBranch(transform, hip) ? waist : lowerBody;
        }
        return waist;
    }

    private static bool IsUpperBodyBranch(Transform transform, Transform hip)
    {
        Transform current = transform;
        while (current != null && current != hip)
        {
            if (current.name.Equals("Spine", StringComparison.OrdinalIgnoreCase) ||
                current.name.Equals("Waist", StringComparison.OrdinalIgnoreCase) ||
                current.name.Equals("UpBody_Ctrl", StringComparison.OrdinalIgnoreCase)) return true;
            current = current.parent;
        }
        return false;
    }

    private static Bone CreateTransformBone(Transform transform, Transform coordinateRoot, int parentIndex)
    {
        string mappedName;
        BoneNameMapping.TryGetValue(transform.name, out mappedName);
        return new Bone
        {
            Name = mappedName ?? transform.name,
            NameEn = transform.name,
            Position = coordinateRoot.InverseTransformPoint(transform.position),
            ParentIndex = parentIndex,
            TransformLevel = 0,
            Rotatable = true,
            Movable = false,
            Visible = true,
            Controllable = true,
            ChildBoneVal = new Bone.ChildBone { ChildUseId = false, Offset = Vector3.up * 0.1f }
        };
    }

    private static int AddVirtualBone(List<Bone> bones, string name, string nameEn, Vector3 position, int parent, bool movable)
    {
        bones.Add(new Bone
        {
            Name = name,
            NameEn = nameEn,
            Position = position,
            ParentIndex = parent,
            TransformLevel = 0,
            Rotatable = true,
            Movable = movable,
            Visible = true,
            Controllable = true,
            ChildBoneVal = new Bone.ChildBone { ChildUseId = false, Offset = Vector3.up * 0.1f }
        });
        return bones.Count - 1;
    }

    private static void RebuildChildLinks(List<Bone> bones)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            int child = bones.FindIndex(b => b.ParentIndex == i);
            if (child >= 0)
            {
                bones[i].ChildBoneVal.ChildUseId = true;
                bones[i].ChildBoneVal.Index = child;
            }
            else
            {
                bones[i].ChildBoneVal.ChildUseId = false;
                bones[i].ChildBoneVal.Offset = Vector3.up * 0.1f;
            }
        }
    }

    private static void ReparentTongueChain(List<Bone> bones, Dictionary<Transform, int> indexes)
    {
        // 保留原导出器的舌骨兼容关系，避免拆分骨架模块后出现既有功能回退。
        Transform chin = Find(indexes.Keys, "Chin") ?? indexes.Keys.FirstOrDefault(t => t.name.StartsWith("Jaw"));
        Transform tongue = Find(indexes.Keys, "Tongue");
        Transform tongueOut01 = Find(indexes.Keys, "Tongue_Out_01");
        Transform tongueOut02 = Find(indexes.Keys, "Tongue_Out_02");
        if (chin == null) return;

        if (tongue != null)
        {
            bones[indexes[tongue]].ParentIndex = indexes[chin];
            if (tongueOut01 != null) bones[indexes[tongueOut01]].ParentIndex = indexes[tongue];
            if (tongueOut01 != null && tongueOut02 != null) bones[indexes[tongueOut02]].ParentIndex = indexes[tongueOut01];
            return;
        }

        foreach (Transform candidate in indexes.Keys.Where(t => t.name.StartsWith("Tongue") && !t.name.Contains("Out")))
        {
            bones[indexes[candidate]].ParentIndex = indexes[chin];
        }
    }

    private static void AddFootIk(List<Bone> bones, Dictionary<Transform, int> indexes,
        Transform coordinateRoot, int parentOfAll, string side)
    {
        Transform thigh = Find(indexes.Keys, "Thigh_" + side);
        Transform knee = Find(indexes.Keys, "Knee_" + side);
        Transform ankle = Find(indexes.Keys, "Ankle_" + side);
        Transform toe = Find(indexes.Keys, "Toe_" + side);
        if (thigh == null || knee == null || ankle == null) return;

        string prefix = side == "L" ? "左" : "右";
        Vector3 anklePosition = coordinateRoot.InverseTransformPoint(ankle.position);
        Vector3 ikParentPosition = new Vector3(anklePosition.x, 0, anklePosition.z);
        int ikParent = AddVirtualBone(bones, prefix + "足IK親", "FootIKParent_" + side, ikParentPosition, parentOfAll, true);
        int footIk = AddVirtualBone(bones, prefix + "足ＩＫ", "FootIK_" + side, anklePosition, ikParent, true);
        bones[footIk].HasIk = true;
        bones[footIk].TransformLevel = 1;
        bones[footIk].IkInfoVal = new Bone.IkInfo
        {
            IkTargetIndex = indexes[ankle],
            CcdIterateLimit = 40,
            CcdAngleLimit = 2.0f,
            IkLinks = new[]
            {
                // 按 PMX 文件原始语义写负 X 区间；MMD Skin/Saba 会在求解器侧取反并交换上下限。
                new Bone.IkLink { LinkIndex = indexes[knee], HasLimit = true, LoLimit = new Vector3(-Mathf.PI, 0, 0), HiLimit = new Vector3(-0.0087f, 0, 0) },
                new Bone.IkLink { LinkIndex = indexes[thigh], HasLimit = false }
            }
        };

        if (toe == null) return;
        int toeIk = AddVirtualBone(bones, prefix + "つま先ＩＫ", "ToeIK_" + side,
            coordinateRoot.InverseTransformPoint(toe.position), footIk, true);
        bones[toeIk].HasIk = true;
        bones[toeIk].TransformLevel = 1;
        bones[toeIk].IkInfoVal = new Bone.IkInfo
        {
            IkTargetIndex = indexes[toe],
            CcdIterateLimit = 3,
            CcdAngleLimit = 1.0f,
            IkLinks = new[] { new Bone.IkLink { LinkIndex = indexes[ankle], HasLimit = false } }
        };
    }

    private static void Validate(List<Bone> bones)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            Bone bone = bones[i];
            if (bone.ParentIndex < -1 || bone.ParentIndex >= bones.Count || bone.ParentIndex == i)
                throw new InvalidOperationException("PMX 骨骼父索引无效: " + bone.Name);
            if (bone.ChildBoneVal.ChildUseId && (bone.ChildBoneVal.Index < 0 || bone.ChildBoneVal.Index >= bones.Count))
                throw new InvalidOperationException("PMX 骨骼尾端索引无效: " + bone.Name);
            if (bone.HasIk && (bone.IkInfoVal == null || bone.IkInfoVal.IkTargetIndex < 0 || bone.IkInfoVal.IkTargetIndex >= bones.Count))
                throw new InvalidOperationException("PMX IK 目标索引无效: " + bone.Name);
            if (!bone.HasIk) continue;

            foreach (Bone.IkLink link in bone.IkInfoVal.IkLinks)
            {
                if (link.LinkIndex < 0 || link.LinkIndex >= bones.Count)
                    throw new InvalidOperationException("PMX IK 链接索引无效: " + bone.Name);
                if (!link.HasLimit) continue;
                ValidateIkLimit(link.LoLimit, link.HiLimit, bone.Name);
            }
        }
    }

    private static void ValidateIkLimit(Vector3 lower, Vector3 upper, string boneName)
    {
        if (!IsFinite(lower) || !IsFinite(upper))
            throw new InvalidOperationException("PMX IK 角度限制包含 NaN 或 Infinity: " + boneName);
        if (lower.x > upper.x || lower.y > upper.y || lower.z > upper.z)
            throw new InvalidOperationException("PMX IK 角度限制上下限倒置: " + boneName);
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
