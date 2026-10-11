using System;
using System.IO;
using System.Linq;
using System.Text;
using LibMMD.Model;
using LibMMD.Reader;
using LibMMD.Writer;

/// <summary>PMX 显示框构建与读写回归断言。</summary>
public static class PMXDisplayFrameRegression
{
    public static void Run()
    {
        AssertAllElementsCovered();
        AssertEmptyModel();
        AssertRepeatedWriteIsStable();
        AssertReaderKeepsFrames();
        AssertInvalidIndexesAreClear();
    }

    private static void AssertAllElementsCovered()
    {
        var model = CreateModel();
        PMXDisplayFrameExporter.Build(model);
        Assert(model.Entrys.Count == 10, "应生成 Root、表情及八个骨骼分组。");
        Assert(model.Entrys.Count(frame => frame.EntryItemName == "Root") == 1, "Root 必须唯一。");
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "Root").IsSpecial, "Root 应标记为特殊框。");
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "表情").IsSpecial, "表情框应标记为特殊框。");

        var morphIndexes = model.Entrys.Where(frame => frame.EntryItemName == "表情")
            .SelectMany(frame => frame.Elements).Where(element => element.IsMorph)
            .Select(element => element.MorphIndex).OrderBy(index => index).ToArray();
        Assert(morphIndexes.SequenceEqual(Enumerable.Range(0, model.Morphs.Length)), "表情框必须覆盖全部表情。");

        var boneIndexes = model.Entrys.Where(frame => frame.EntryItemName != "Root")
            .SelectMany(frame => frame.Elements).Where(element => !element.IsMorph)
            .Select(element => element.BoneIndex).Distinct().OrderBy(index => index).ToArray();
        Assert(boneIndexes.SequenceEqual(Enumerable.Range(0, model.Bones.Length)), "骨骼分组必须覆盖全部骨骼。");
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "IK").Elements.Any(element => element.BoneIndex == 1),
            "IK 控制骨应进入 IK 组。");
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "fingers").Elements.Any(element => element.BoneIndex == 2),
            "手指骨应进入手指组。");
    }

    private static void AssertEmptyModel()
    {
        var model = CreateModel();
        model.Bones = Array.Empty<Bone>();
        model.Morphs = Array.Empty<Morph>();
        PMXDisplayFrameExporter.Build(model);
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "Root").Elements.Count == 0,
            "空模型 Root 不得引用不存在的骨骼。");
        Assert(model.Entrys.Single(frame => frame.EntryItemName == "表情").Elements.Count == 0,
            "空模型表情框应为空。");
        var bytes = Write(model);
        var read = Read(bytes);
        Assert(read.Bones.Length == 0 && read.Morphs.Length == 0, "空模型应能安全往返。");
    }

    private static void AssertRepeatedWriteIsStable()
    {
        var model = CreateModel();
        PMXDisplayFrameExporter.Build(model);
        var before = Signature(model.Entrys);
        var first = Write(model);
        var afterFirst = Signature(model.Entrys);
        var second = Write(model);
        var afterSecond = Signature(model.Entrys);
        Assert(before == afterFirst && before == afterSecond, "Writer 不得修改输入显示框。");
        Assert(first.SequenceEqual(second), "同一模型连续写出字节应一致。");

        var fallbackModel = CreateModel();
        fallbackModel.Entrys.Clear();
        var fallbackRead = Read(Write(fallbackModel));
        Assert(fallbackRead.Entrys.Count(frame => frame.EntryItemName == "Root") == 1,
            "Writer 应在局部输出中补一个 Root。");
        Assert(fallbackModel.Entrys.Count == 0, "Root fallback 不得写回输入对象。");
    }

    private static void AssertReaderKeepsFrames()
    {
        var model = CreateModel();
        PMXDisplayFrameExporter.Build(model);
        var expected = Signature(model.Entrys);
        var read = Read(Write(model));
        Assert(Signature(read.Entrys) == expected, "Reader 应保留显示框名称、特殊标志及元素类型/索引。");
        var reread = Read(Write(read));
        Assert(Signature(reread.Entrys) == expected, "Reader 再 Writer 后显示框应保留。");
    }

    private static void AssertInvalidIndexesAreClear()
    {
        var model = CreateModel();
        model.Entrys.Clear();
        model.Entrys.Add(new PMXEntryItem
        {
            EntryItemName = "bad morph",
            Elements = new System.Collections.Generic.List<PMXEntryItem.Element>
            {
                new PMXEntryItem.Element { IsMorph = true, MorphIndex = model.Morphs.Length }
            }
        });
        AssertErrorContains(model, "bad morph", "表情索引");

        model.Entrys[0].EntryItemName = "bad bone";
        model.Entrys[0].Elements[0] = new PMXEntryItem.Element
        {
            IsMorph = false,
            BoneIndex = model.Bones.Length
        };
        AssertErrorContains(model, "bad bone", "骨骼索引");
    }

    private static void AssertErrorContains(RawMMDModel model, string frameName, string indexKind)
    {
        try
        {
            PMXDisplayFrameExporter.ValidateEntries(model);
        }
        catch (InvalidDataException exception)
        {
            Assert(exception.Message.Contains(frameName) && exception.Message.Contains(indexKind) &&
                exception.Message.Contains("越界") && exception.Message.Contains("有效范围"),
                "越界异常需指出显示框、索引类型和有效范围：" + exception.Message);
            return;
        }
        throw new InvalidOperationException("非法显示框索引未被拒绝。");
    }

    private static RawMMDModel CreateModel()
    {
        return new RawMMDModel
        {
            Name = "display frame regression",
            NameEn = "display frame regression",
            Description = string.Empty,
            DescriptionEn = string.Empty,
            Vertices = Array.Empty<Vertex>(),
            TriangleIndexes = Array.Empty<int>(),
            Parts = Array.Empty<Part>(),
            Bones = new[]
            {
                new Bone { Name = "center", NameEn = "center" },
                new Bone { Name = "左足ＩＫ", NameEn = "left leg ik", HasIk = true,
                    IkInfoVal = new Bone.IkInfo { IkTargetIndex = 0, IkLinks = Array.Empty<Bone.IkLink>() } },
                new Bone { Name = "左手指１", NameEn = "left finger 1" }
            },
            Morphs = new[]
            {
                NewMorph("smile"), NewMorph("blink")
            },
            Rigidbodies = Array.Empty<MMDRigidBody>(),
            Joints = Array.Empty<MMDJoint>()
        };
    }

    private static Morph NewMorph(string name) => new Morph
    {
        Name = name,
        NameEn = name,
        Category = Morph.MorphCategory.MorphCatOther,
        Type = Morph.MorphType.MorphTypeVertex,
        MorphDatas = Array.Empty<Morph.MorphData>()
    };

    private static byte[] Write(RawMMDModel model)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, true))
        {
            PMXWriter.Write(writer, model, new ModelConfig());
            writer.Flush();
            return stream.ToArray();
        }
    }

    private static RawMMDModel Read(byte[] bytes)
    {
        using (var stream = new MemoryStream(bytes))
        using (var reader = new BinaryReader(stream, Encoding.Unicode, true))
            return new PMXReader().Read(reader, new ModelConfig());
    }

    private static string Signature(System.Collections.Generic.IEnumerable<PMXEntryItem> entries)
    {
        var text = new StringBuilder();
        foreach (var frame in entries)
        {
            text.Append(frame.EntryItemName).Append('|').Append(frame.EntryItemNameEn).Append('|')
                .Append(frame.IsSpecial).Append(';');
            foreach (var element in frame.Elements)
                text.Append(element.IsMorph ? 'M' : 'B')
                    .Append(element.IsMorph ? element.MorphIndex : element.BoneIndex).Append(',');
            text.AppendLine();
        }
        return text.ToString();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
