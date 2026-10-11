using System;
using System.Collections.Generic;
using LibMMD.Model;

/// <summary>移除骨骼后统一重映射 PMX 中的骨骼索引。</summary>
internal static class PMXBoneIndexRemapper
{
    internal static void Remap(RawMMDModel model, PMXBoneExporter.Result boneResult,
        Bone[] original, List<Bone> output, int[] first, int[] last)
    {
        if (model == null || boneResult == null || original == null || output == null || first == null || last == null)
            throw new ArgumentNullException("PMX 骨骼重映射参数不能为空。");
        if (first.Length != last.Length || first.Length < original.Length)
            throw new ArgumentException("骨骼映射必须覆盖原骨和临时骨索引。");

        var originalIndexes = new Dictionary<Bone, int>();
        for (int i = 0; i < original.Length; i++)
            if (original[i] != null) originalIndexes[original[i]] = i;

        // 只处理原骨对象；布局中新建骨的链接已经按临时索引构造。
        foreach (Bone bone in output)
        {
            if (bone == null || !originalIndexes.ContainsKey(bone)) continue;
            bone.ParentIndex = Map(bone.ParentIndex, last);
            if (bone.ChildBoneVal != null && bone.ChildBoneVal.ChildUseId)
                bone.ChildBoneVal.Index = Map(bone.ChildBoneVal.Index, first);
            if ((bone.AppendRotate || bone.AppendTranslate) && bone.AppendBoneVal != null)
                bone.AppendBoneVal.Index = Map(bone.AppendBoneVal.Index, first);
            if (bone.HasIk && bone.IkInfoVal != null)
            {
                bone.IkInfoVal.IkTargetIndex = Map(bone.IkInfoVal.IkTargetIndex, first);
                if (bone.IkInfoVal.IkLinks != null)
                    foreach (Bone.IkLink link in bone.IkInfoVal.IkLinks)
                        if (link != null) link.LinkIndex = Map(link.LinkIndex, first);
            }
        }

        if (model.Vertices != null)
            foreach (Vertex vertex in model.Vertices)
                RemapSkinning(vertex == null ? null : vertex.SkinningOperator, first);

        if (model.Morphs != null)
            foreach (Morph morph in model.Morphs)
                if (morph != null && morph.MorphDatas != null && morph.Type == Morph.MorphType.MorphTypeBone)
                    foreach (Morph.MorphData data in morph.MorphDatas)
                        if (data is Morph.BoneMorphData boneData)
                            boneData.BoneIndex = Map(boneData.BoneIndex, first);

        if (model.Rigidbodies != null)
            foreach (MMDRigidBody body in model.Rigidbodies)
                if (body != null) body.AssociatedBoneIndex = Map(body.AssociatedBoneIndex, first);

        if (model.Entrys != null)
            foreach (PMXEntryItem entry in model.Entrys)
                if (entry != null && entry.Elements != null)
                    foreach (PMXEntryItem.Element element in entry.Elements)
                        if (element != null && !element.IsMorph)
                            element.BoneIndex = Map(element.BoneIndex, first);

        if (boneResult.BoneIndexes != null)
        {
            var transforms = new List<UnityEngine.Transform>(boneResult.BoneIndexes.Keys);
            foreach (UnityEngine.Transform transform in transforms)
                boneResult.BoneIndexes[transform] = Map(boneResult.BoneIndexes[transform], first);
        }

        boneResult.Bones = output.ToArray();
        model.Bones = boneResult.Bones;
    }

    private static void RemapSkinning(SkinningOperator skinning, int[] first)
    {
        if (skinning == null || skinning.Param == null) return;
        if (skinning.Param is SkinningOperator.Bdef1 bdef1)
            bdef1.BoneId = Map(bdef1.BoneId, first);
        else if (skinning.Param is SkinningOperator.Bdef2 bdef2)
            Remap(bdef2.BoneId, first);
        else if (skinning.Param is SkinningOperator.Bdef4 bdef4)
            Remap(bdef4.BoneId, first);
        else if (skinning.Param is SkinningOperator.Sdef sdef)
            Remap(sdef.BoneId, first);
    }

    private static void Remap(int[] indexes, int[] map)
    {
        if (indexes == null) return;
        for (int i = 0; i < indexes.Length; i++)
            indexes[i] = Map(indexes[i], map);
    }

    private static int Map(int index, int[] map)
    {
        if (index < 0) return index;
        if (index >= map.Length) throw new ArgumentOutOfRangeException(nameof(index));
        int mapped = map[index];
        if (mapped < 0) throw new InvalidOperationException("骨骼映射缺少有效目标：" + index);
        return mapped;
    }
}
