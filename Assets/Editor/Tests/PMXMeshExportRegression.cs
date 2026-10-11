using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>PMX 网格快照、索引、材质段与源数据回归断言。</summary>
public static class PMXMeshExportRegression
{
    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Root;
        public readonly UmaContainer Container;
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();

        public Fixture(string name)
        {
            Root = new GameObject(name);
            Container = Root.AddComponent<UmaContainer>();
        }

        public Mesh Own(Mesh mesh) { _owned.Add(mesh); return mesh; }
        public Material Own(Material material) { _owned.Add(material); return material; }

        public MeshRenderer AddStatic(string name, Mesh mesh, params Material[] materials)
        {
            var child = new GameObject(name);
            child.transform.SetParent(Root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials ?? Array.Empty<Material>();
            return renderer;
        }

        public void Dispose()
        {
            UnityEngine.Object.DestroyImmediate(Root);
            foreach (UnityEngine.Object value in _owned)
                if (value != null) UnityEngine.Object.DestroyImmediate(value);
        }
    }

    public static void Run()
    {
        TestSameMaterialDuplicateFaces();
        TestDifferentMaterialsAndReverseWinding();
        TestTransparentPropertyBlockAndRendererBoundaries();
        TestUnknownOpaqueShaderPreserved();
        TestBaseVertexAndPartRanges();
        TestMissingMaterialAndMorphOffsets();
        TestRootCoordinatesAndSourcePreservation();
        TestSkinnedSourceWeightsPreserved();
        TestSkinnedMorphNormalFallback();
        TestNonReadableSkinnedRendererSkipped();
        TestNonReadableStaticMultiStreamCopy();
    }

    private static void TestSameMaterialDuplicateFaces()
    {
        using (var fixture = new Fixture("Mesh duplicate regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(
                new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }));
            fixture.AddStatic("Body", mesh, material, material, material);

            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 3, "同材质同向重复面（含循环换序）应只保留一个三角形。");
            Assert(model.Parts.Length == 1 && model.Parts[0].TriangleIndexNum == 3,
                "去重后应移除空材质段并重建有效材质段。");
            AssertPartRanges(model);
        }
    }

    private static void TestDifferentMaterialsAndReverseWinding()
    {
        using (var fixture = new Fixture("Mesh material semantics regression"))
        {
            Material first = fixture.Own(OpaqueMaterial(Color.white));
            Material second = fixture.Own(OpaqueMaterial(Color.red));
            first.name = second.name = "Same visible name";
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }, new[] { 0, 1, 2 }));
            fixture.AddStatic("Different materials", mesh, first, second);

            RawMMDModel differentMaterialModel = Export(fixture);
            Assert(differentMaterialModel.TriangleIndexes.Length == 6,
                "不同 Material 实例即使同名也必须保留重叠面。");
            Assert(differentMaterialModel.Parts.Length == 2, "不同材质语义应保留独立材质段。");
            AssertPartRanges(differentMaterialModel);
        }

        using (var fixture = new Fixture("Mesh reverse winding regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }, new[] { 0, 2, 1 }));
            fixture.AddStatic("Reverse winding", mesh, material, material);

            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 6, "反绕序三角形不得按同一个面去重。");
            AssertPartRanges(model);
        }
    }

    private static void TestTransparentPropertyBlockAndRendererBoundaries()
    {
        using (var fixture = new Fixture("Mesh transparent regression"))
        {
            Material material = fixture.Own(TransparentMaterial());
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }, new[] { 0, 1, 2 }));
            fixture.AddStatic("Transparent", mesh, material, material);
            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 6, "透明层重叠面必须保留。");
            AssertPartRanges(model);
        }

        using (var fixture = new Fixture("Mesh property block regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }, new[] { 0, 1, 2 }));
            MeshRenderer renderer = fixture.AddStatic("Property block", mesh, material, material);
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", Color.green);
            renderer.SetPropertyBlock(block);
            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 6, "存在 Renderer 属性块时不得自动合并重叠面。");
        }

        using (var fixture = new Fixture("Mesh renderer boundary regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            fixture.AddStatic("Renderer A", mesh, material);
            fixture.AddStatic("Renderer B", mesh, material);
            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 6 && model.Vertices.Length == 6,
                "跨 Renderer 的重叠面及顶点必须分别保留。");
            AssertPartRanges(model);
        }
    }

    private static void TestBaseVertexAndPartRanges()
    {
        using (var fixture = new Fixture("Mesh base vertex regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            mesh.vertices = new[]
            {
                Vector3.left * 3, Vector3.left * 2, Vector3.left,
                Vector3.zero, Vector3.right, Vector3.up
            };
            mesh.normals = Enumerable.Repeat(Vector3.back, 6).ToArray();
            mesh.uv = new Vector2[6];
            mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0, false, 3);
            fixture.AddStatic("Base vertex", mesh, material);

            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.SequenceEqual(new[] { 3, 4, 5 }),
                "非零 baseVertex 必须计入最终全局顶点索引。");
            AssertPartRanges(model);
        }
    }

    private static void TestUnknownOpaqueShaderPreserved()
    {
        using (var fixture = new Fixture("Unknown opaque shader regression"))
        {
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
                throw new InvalidOperationException("未覆盖：Unity Unlit/Color shader 不可用。");
            var material = fixture.Own(new Material(shader) { color = Color.white, renderQueue = -1 });
            material.SetOverrideTag("RenderType", "Opaque");
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }, new[] { 0, 1, 2 }));
            fixture.AddStatic("Unknown shader", mesh, material, material);

            RawMMDModel model = Export(fixture);
            Assert(model.TriangleIndexes.Length == 6,
                "不在确认名单内的 opaque shader 即使同材质同向重叠也必须保留。");
            AssertPartRanges(model);
        }
    }

    private static void TestMissingMaterialAndMorphOffsets()
    {
        using (var fixture = new Fixture("Mesh skipped renderer morph regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh skipped = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            AddShape(skipped, "SkippedShape", 2, Vector3.forward * 0.2f);
            fixture.AddStatic("Missing material", skipped);

            Mesh first = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            AddShape(first, "FirstShape", 1, Vector3.up * 0.1f);
            fixture.AddStatic("First valid", first, material);

            Mesh second = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            AddShape(second, "SecondShape", 2, Vector3.right * 0.15f);
            fixture.AddStatic("Second valid", second, material);

            RawMMDModel model = Export(fixture);
            Assert(model.Vertices.Length == 6, "缺少材质的 Renderer 应连同顶点一起跳过。");
            Assert(model.TriangleIndexes.Length == 6, "缺少材质的 Renderer 不得留下索引。");
            AssertPartRanges(model);
            AssertMorphVertex(model, "FirstShape", 1);
            AssertMorphVertex(model, "SecondShape", 5);
            Assert(model.Morphs.All(morph => morph.MorphDatas.OfType<Morph.VertexMorphData>()
                    .All(data => data.VertexIndex >= 0 && data.VertexIndex < model.Vertices.Length)),
                "所有表情顶点索引都必须落在最终顶点数组内。");
        }
    }

    private static void TestRootCoordinatesAndSourcePreservation()
    {
        using (var fixture = new Fixture("Mesh source preservation regression"))
        {
            fixture.Root.transform.position = new Vector3(10, 20, 30);
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = Enumerable.Repeat(Vector3.back, 3).ToArray();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            MeshRenderer renderer = fixture.AddStatic("Offset renderer", mesh, material);
            renderer.transform.localPosition = new Vector3(2, 3, 4);
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Vector3[] verticesBefore = mesh.vertices;
            Vector2[] uvBefore = mesh.uv;
            int[] trianglesBefore = mesh.triangles;
            Mesh meshReferenceBefore = filter.sharedMesh;

            RawMMDModel model = Export(fixture);
            AssertNear(model.Vertices[0].Coordinate, new Vector3(2, 3, 4),
                "静态网格坐标应相对导出容器根节点，不应混入根节点世界平移。");
            Assert(filter.sharedMesh == meshReferenceBefore, "导出不得替换 MeshFilter 的共享网格引用。");
            Assert(mesh.vertices.SequenceEqual(verticesBefore), "导出不得改变源顶点。");
            Assert(mesh.uv.SequenceEqual(uvBefore), "导出不得改变源 UV。");
            Assert(mesh.triangles.SequenceEqual(trianglesBefore), "导出不得改变源索引。");
            AssertPartRanges(model);
        }
    }

    private static void TestSkinnedSourceWeightsPreserved()
    {
        using (var fixture = new Fixture("Mesh weights preservation regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            var boneObject = new GameObject("WeightedBone");
            boneObject.transform.SetParent(fixture.Root.transform, false);
            var meshObject = new GameObject("Skinned source");
            meshObject.transform.SetParent(fixture.Root.transform, false);
            var renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            var sourceWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 }
            };
            mesh.boneWeights = sourceWeights;
            mesh.bindposes = new[] { boneObject.transform.worldToLocalMatrix * meshObject.transform.localToWorldMatrix };
            renderer.sharedMesh = mesh;
            renderer.sharedMaterials = new[] { material };
            renderer.bones = new[] { boneObject.transform };
            renderer.rootBone = boneObject.transform;
            Vector3[] verticesBefore = mesh.vertices;
            Vector2[] uvBefore = mesh.uv;
            BoneWeight[] weightsBefore = mesh.boneWeights;

            RawMMDModel model = Export(fixture);
            Assert(mesh.vertices.SequenceEqual(verticesBefore), "导出不得改变蒙皮源顶点。");
            Assert(mesh.uv.SequenceEqual(uvBefore), "导出不得改变蒙皮源 UV。");
            BoneWeight[] weightsAfter = mesh.boneWeights;
            Assert(weightsAfter.Length == weightsBefore.Length, "导出不得改变源骨权重数量。");
            for (int i = 0; i < weightsBefore.Length; i++)
            {
                Assert(weightsAfter[i].boneIndex0 == weightsBefore[i].boneIndex0 &&
                    Mathf.Abs(weightsAfter[i].weight0 - weightsBefore[i].weight0) < 0.000001f,
                    "导出不得改变源骨权重。");
            }
            Assert(model.Vertices.Length == 3 && model.Vertices.All(vertex =>
                    vertex.SkinningOperator.Type == SkinningOperator.SkinningType.SkinningBdef1),
                "简单单骨蒙皮应保持 BDEF1 权重结构。");
        }
    }

    private static void TestSkinnedMorphNormalFallback()
    {
        using (var fixture = new Fixture("Skinned morph normal fallback regression"))
        {
            Material material = fixture.Own(OpaqueMaterial(Color.white));
            var boneObject = new GameObject("NormalFallbackBone");
            boneObject.transform.SetParent(fixture.Root.transform, false);
            var meshObject = new GameObject("Normal fallback skin");
            meshObject.transform.SetParent(fixture.Root.transform, false);
            var renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            Mesh mesh = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            Vector3[] sourceNormals = Enumerable.Repeat(Vector3.up, 3).ToArray();
            mesh.normals = sourceNormals;
            var deltaNormals = new[] { -Vector3.up, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame("CancelNormal", 100, new Vector3[3], deltaNormals, new Vector3[3]);
            mesh.boneWeights = Enumerable.Range(0, 3)
                .Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            mesh.bindposes = new[] { boneObject.transform.worldToLocalMatrix * meshObject.transform.localToWorldMatrix };
            renderer.sharedMesh = mesh;
            renderer.sharedMaterials = new[] { material };
            renderer.bones = new[] { boneObject.transform };
            renderer.rootBone = boneObject.transform;
            renderer.SetBlendShapeWeight(0, 100);

            RawMMDModel model = Export(fixture);
            Assert(model.Vertices.Length == 3, "法线抵消夹具应完整导出蒙皮顶点。");
            foreach (Vertex vertex in model.Vertices)
                Assert(IsFinite(vertex.Normal) && Mathf.Abs(vertex.Normal.magnitude - 1) < 0.0001f,
                    "烘焙法线无效时应回退为有限单位源法线。");
            Assert(mesh.normals.SequenceEqual(sourceNormals), "法线回退不得改写源网格法线。");
        }
    }

    private static void TestNonReadableSkinnedRendererSkipped()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("未覆盖：不可读 Skinned 网格整段跳过断言需要图形设备。");

        using (var fixture = new Fixture("Non-readable skinned regression"))
        {
            Material skinMaterial = fixture.Own(OpaqueMaterial(Color.white));
            Material staticMaterial = fixture.Own(OpaqueMaterial(Color.gray));
            var boneObject = new GameObject("UnreadableBone");
            boneObject.transform.SetParent(fixture.Root.transform, false);
            var skinnedObject = new GameObject("Unreadable skinned");
            skinnedObject.transform.SetParent(fixture.Root.transform, false);
            var skinned = skinnedObject.AddComponent<SkinnedMeshRenderer>();
            Mesh unreadable = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            unreadable.boneWeights = Enumerable.Range(0, 3)
                .Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            unreadable.bindposes = new[] { boneObject.transform.worldToLocalMatrix * skinnedObject.transform.localToWorldMatrix };
            skinned.sharedMesh = unreadable;
            skinned.sharedMaterials = new[] { skinMaterial };
            skinned.bones = new[] { boneObject.transform };
            skinned.rootBone = boneObject.transform;
            unreadable.UploadMeshData(true);

            Mesh valid = fixture.Own(TriangleMesh(new[] { 0, 1, 2 }));
            fixture.AddStatic("Valid static", valid, staticMaterial);

            RawMMDModel model = Export(fixture);
            Assert(model.Vertices.Length == 3 && model.TriangleIndexes.SequenceEqual(new[] { 0, 1, 2 }),
                "不可读 Skinned Renderer 应整段跳过，后续网格索引仍从零开始。");
            Assert(model.Parts.Length == 1 && model.Parts[0].BaseShift == 0 && model.Parts[0].TriangleIndexNum == 3,
                "不可读 Skinned Renderer 不得留下材质段或索引偏移。");
            var diagnostics = ModelExporter.GetMeshDiagnostics(model) as List<PMXMeshExportContext.RendererAudit>;
            PMXMeshExportContext.RendererAudit skipped = diagnostics?.FirstOrDefault(item =>
                item.rendererPath.EndsWith("/Unreadable skinned", StringComparison.Ordinal));
            Assert(skipped != null && skipped.status == "skipped" &&
                skipped.reason.IndexOf("不可读", StringComparison.Ordinal) >= 0,
                "不可读 Skinned Renderer 应记录明确跳过原因。");
        }
    }

    private static void TestNonReadableStaticMultiStreamCopy()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new InvalidOperationException("未覆盖：非可读静态网格 GPU 回读断言需要图形设备。");

        using (var fixture = new Fixture("Non-readable static GPU regression"))
        {
            Material first = fixture.Own(OpaqueMaterial(Color.white));
            Material second = fixture.Own(OpaqueMaterial(Color.gray));
            Mesh mesh = fixture.Own(NonReadableMultiStreamMesh());
            fixture.AddStatic("GPU copied", mesh, first, second);
            mesh.UploadMeshData(true);
            Assert(!mesh.isReadable, "夹具必须先转为不可读网格。");

            RawMMDModel model = Export(fixture);
            Assert(model.Vertices.Length == 6, "GPU 网格复制必须保留所有顶点。");
            Assert(model.TriangleIndexes.SequenceEqual(new[] { 3, 4, 5, 4, 5, 3 }),
                "GPU 复制必须保留非零 baseVertex 与非连续 indexStart 的子网格索引。");
            Assert(model.Parts.Length == 2 && model.Parts[0].BaseShift == 0 &&
                model.Parts[0].TriangleIndexNum == 3 && model.Parts[1].BaseShift == 3 &&
                model.Parts[1].TriangleIndexNum == 3,
                "GPU 复制后的两个材质段必须连续映射到最终索引范围。");
            AssertNear(model.Vertices[3].Normal, Vector3.forward, "GPU 复制应恢复第二顶点流法线。");
            AssertNear(new Vector3(model.Vertices[3].UvCoordinate.x, 1 - model.Vertices[3].UvCoordinate.y, 0),
                new Vector3(0.25f, 0.75f, 0), "GPU 复制应恢复第三顶点流 UV。");
            AssertPartRanges(model);
            var diagnostics = ModelExporter.GetMeshDiagnostics(model) as List<PMXMeshExportContext.RendererAudit>;
            PMXMeshExportContext.RendererAudit copied = diagnostics?.FirstOrDefault(item =>
                item.rendererPath.EndsWith("/GPU copied", StringComparison.Ordinal));
            Assert(copied != null && copied.status == "captured" && copied.gpuCopy &&
                copied.submeshes.Count == 2 && copied.submeshes[0].indexStart == 2 &&
                copied.submeshes[1].indexStart == 8 && copied.submeshes.All(item => item.baseVertex == 3),
                "GPU 回读诊断应记录实际复制、子网格间隙和 baseVertex。");
        }
    }

    private static RawMMDModel Export(Fixture fixture)
        => ModelExporter.ReadPMXModel(fixture.Container, Array.Empty<string>());

    private static Mesh TriangleMesh(params int[][] submeshTriangles)
    {
        var mesh = new Mesh { name = "PMX mesh regression fixture" };
        mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
        mesh.normals = Enumerable.Repeat(Vector3.back, 3).ToArray();
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
        mesh.subMeshCount = submeshTriangles.Length;
        for (int i = 0; i < submeshTriangles.Length; i++)
            mesh.SetTriangles(submeshTriangles[i], i, false);
        return mesh;
    }

    private static Mesh NonReadableMultiStreamMesh()
    {
        var mesh = new Mesh { name = "PMX multi-stream unreadable fixture" };
        mesh.SetVertexBufferParams(6,
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 1),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2, 2));
        mesh.SetVertexBufferData(new[]
        {
            Vector3.left * 3, Vector3.left * 2, Vector3.left,
            Vector3.zero, Vector3.right, Vector3.up
        }, 0, 0, 6, 0, MeshUpdateFlags.DontRecalculateBounds);
        mesh.SetVertexBufferData(Enumerable.Repeat(Vector3.forward, 6).ToArray(),
            0, 0, 6, 1, MeshUpdateFlags.DontRecalculateBounds);
        mesh.SetVertexBufferData(new[]
        {
            Vector2.zero, Vector2.zero, Vector2.zero,
            new Vector2(0.25f, 0.75f), Vector2.right, Vector2.up
        }, 0, 0, 6, 2, MeshUpdateFlags.DontRecalculateBounds);

        mesh.SetIndexBufferParams(12, IndexFormat.UInt16);
        mesh.SetIndexBufferData(new ushort[] { 5, 5, 0, 1, 2, 5, 5, 5, 1, 2, 0, 5 },
            0, 0, 12, MeshUpdateFlags.DontRecalculateBounds);
        mesh.subMeshCount = 2;
        var bounds = new Bounds(Vector3.zero, Vector3.one * 10);
        mesh.SetSubMesh(0, new SubMeshDescriptor(2, 3, MeshTopology.Triangles)
        { baseVertex = 3, firstVertex = 3, vertexCount = 3, bounds = bounds },
            MeshUpdateFlags.DontRecalculateBounds);
        mesh.SetSubMesh(1, new SubMeshDescriptor(8, 3, MeshTopology.Triangles)
        { baseVertex = 3, firstVertex = 3, vertexCount = 3, bounds = bounds },
            MeshUpdateFlags.DontRecalculateBounds);
        mesh.bounds = bounds;
        return mesh;
    }

    private static void AddShape(Mesh mesh, string name, int movedVertex, Vector3 offset)
    {
        var delta = new Vector3[mesh.vertexCount];
        delta[movedVertex] = offset;
        mesh.AddBlendShapeFrame(name, 100, delta, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
    }

    private static Material OpaqueMaterial(Color color)
    {
        Shader shader = Shader.Find("Standard");
        if (shader == null) throw new InvalidOperationException("测试需要 Unity Standard shader。");
        var material = new Material(shader) { color = color, renderQueue = -1 };
        material.SetOverrideTag("RenderType", "Opaque");
        return material;
    }

    private static Material TransparentMaterial()
    {
        Material material = OpaqueMaterial(new Color(1, 1, 1, 0.5f));
        material.SetFloat("_Mode", 3);
        material.SetOverrideTag("RenderType", "Transparent");
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        return material;
    }

    private static void AssertPartRanges(RawMMDModel model)
    {
        int total = 0;
        foreach (Part part in model.Parts)
        {
            Assert(part.BaseShift >= 0 && part.TriangleIndexNum >= 0 &&
                part.BaseShift + part.TriangleIndexNum <= model.TriangleIndexes.Length,
                "材质段索引范围超出最终索引数组。");
            total += part.TriangleIndexNum;
        }
        Assert(total == model.TriangleIndexes.Length, "材质段三角索引数量之和应等于最终索引总数。");
        foreach (int index in model.TriangleIndexes)
            Assert(index >= 0 && index < model.Vertices.Length, "最终三角索引必须指向有效顶点。");
    }

    private static void AssertMorphVertex(RawMMDModel model, string name, int expectedIndex)
    {
        Morph morph = model.Morphs.FirstOrDefault(item => item.Name == name);
        Assert(morph != null, "缺少表情: " + name);
        Morph.VertexMorphData[] data = morph.MorphDatas.OfType<Morph.VertexMorphData>().ToArray();
        Assert(data.Length == 1 && data[0].VertexIndex == expectedIndex,
            name + " 的顶点偏移错误，预期索引 " + expectedIndex + "。");
    }

    private static void AssertNear(Vector3 actual, Vector3 expected, string message)
    {
        Assert(Vector3.Distance(actual, expected) < 0.0001f,
            message + " 预期 " + expected + "，实际 " + actual + "。");
    }

    private static bool IsFinite(Vector3 value)
        => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
           !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
           !float.IsNaN(value.z) && !float.IsInfinity(value.z);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
