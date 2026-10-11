using System;
using System.Collections.Generic;
using System.IO;
using LibMMD.Model;

/// <summary>按最终 PMX 数组生成并校验显示框。</summary>
public static class PMXDisplayFrameExporter
{
    private static readonly string[] BoneFrameNames =
    {
        "center", "IK", "上身", "arms", "fingers", "下身", "legs", "other"
    };

    public static void Build(RawMMDModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));

        var bones = model.Bones ?? Array.Empty<Bone>();
        var morphs = model.Morphs ?? Array.Empty<Morph>();
        var entries = new List<PMXEntryItem>();
        entries.Add(CreateEntry("Root", "Root", true,
            bones.Length == 0 ? new List<PMXEntryItem.Element>() :
            new List<PMXEntryItem.Element> { BoneElement(0) }));

        var morphElements = new List<PMXEntryItem.Element>(morphs.Length);
        for (var i = 0; i < morphs.Length; i++)
            morphElements.Add(MorphElement(i));
        entries.Add(CreateEntry("表情", "Expressions", true, morphElements));

        var boneGroups = new Dictionary<string, List<PMXEntryItem.Element>>();
        foreach (var name in BoneFrameNames)
            boneGroups[name] = new List<PMXEntryItem.Element>();

        for (var i = 0; i < bones.Length; i++)
            boneGroups[ClassifyBone(bones[i])].Add(BoneElement(i));

        foreach (var name in BoneFrameNames)
            entries.Add(CreateEntry(name, name, false, boneGroups[name]));

        model.Entrys = entries;
        ValidateEntries(model, entries);
    }

    public static void ValidateEntries(RawMMDModel model)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        ValidateEntries(model, model.Entrys);
    }

    internal static void ValidateEntries(RawMMDModel model, IList<PMXEntryItem> entries)
    {
        var boneCount = model.Bones == null ? 0 : model.Bones.Length;
        var morphCount = model.Morphs == null ? 0 : model.Morphs.Length;
        if (entries == null)
            return;

        for (var frameIndex = 0; frameIndex < entries.Count; frameIndex++)
        {
            var frame = entries[frameIndex];
            if (frame == null)
                throw new InvalidDataException($"显示框[{frameIndex}]为空。");
            if (frame.Elements == null)
                throw new InvalidDataException($"显示框“{frame.EntryItemName}”的元素列表为空。");

            for (var elementIndex = 0; elementIndex < frame.Elements.Count; elementIndex++)
            {
                var element = frame.Elements[elementIndex];
                if (element == null)
                    throw new InvalidDataException($"显示框“{frame.EntryItemName}”第 {elementIndex} 项为空。");

                var index = element.IsMorph ? element.MorphIndex : element.BoneIndex;
                var limit = element.IsMorph ? morphCount : boneCount;
                var kind = element.IsMorph ? "表情" : "骨骼";
                if (index < 0 || index >= limit)
                    throw new InvalidDataException(
                        $"显示框“{frame.EntryItemName}”第 {elementIndex} 项{kind}索引 {index} 越界；有效范围为 0..{limit - 1}。");
            }
        }
    }

    private static string ClassifyBone(Bone bone)
    {
        if (bone == null)
            return "other";
        var name = ((bone.Name ?? string.Empty) + " " + (bone.NameEn ?? string.Empty)).ToLowerInvariant();
        if (bone.HasIk || HasAny(name, "ik", "ｉｋ", "ＩＫ")) return "IK";
        if (HasAny(name, "center", "centre", "センター", "中心")) return "center";
        if (HasAny(name, "finger", "fingers", "指")) return "fingers";
        if (HasAny(name, "arm", "shoulder", "elbow", "wrist", "腕", "肩", "肘", "手首")) return "arms";
        if (HasAny(name, "lower body", "lowerbody", "pelvis", "waist", "groin", "下半身", "腰")) return "下身";
        if (HasAny(name, "upper body", "upperbody", "spine", "chest", "上半身", "上体", "胸")) return "上身";
        if (HasAny(name, "leg", "thigh", "knee", "ankle", "foot", "toe", "ひざ", "膝", "足", "腿", "踵")) return "legs";
        return "other";
    }

    private static bool HasAny(string value, params string[] terms)
    {
        foreach (var term in terms)
            if (value.Contains(term.ToLowerInvariant())) return true;
        return false;
    }

    private static PMXEntryItem CreateEntry(string name, string nameEn, bool special,
        List<PMXEntryItem.Element> elements)
    {
        return new PMXEntryItem
        {
            EntryItemName = name,
            EntryItemNameEn = nameEn,
            IsSpecial = special,
            Elements = elements
        };
    }

    private static PMXEntryItem.Element BoneElement(int index) =>
        new PMXEntryItem.Element { IsMorph = false, BoneIndex = index };

    private static PMXEntryItem.Element MorphElement(int index) =>
        new PMXEntryItem.Element { IsMorph = true, MorphIndex = index };
}
