using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LibMMD.Material;
using LibMMD.Model;
using UnityEngine;
using UnityEngine.Rendering;
using static LibMMD.Model.SkinningOperator;

/// <summary>同一次导出的顶点、子网格、材质和表情共用快照。</summary>
public sealed class PMXMeshExportContext : IDisposable
{
    public sealed class Item
    {
        public Renderer Renderer;
        public Mesh Mesh;
        public int VertexOffset;
        internal Vector3[] Positions, Normals;
        internal Vector2[] Uv, Uv1, Uv2;
        internal Color[] Colors;
        internal BoneWeight[] Weights;
        internal Transform[] Bones;
        internal readonly List<Submesh> Submeshes = new List<Submesh>();
        internal RendererAudit Audit;
    }

    internal sealed class Submesh
    {
        internal int Number;
        internal int[] Indices;
        internal Material Material;
        internal bool CanDeduplicate;
        internal SubmeshAudit Audit;
    }

    public sealed class RendererAudit
    {
        public string rendererPath, meshName, status, reason;
        public int sourceMeshId, readableMeshId, vertexOffset, vertexCount, bakedNormalFallbackCount;
        public bool gpuCopy;
        public List<SubmeshAudit> submeshes = new List<SubmeshAudit>();
    }

    public sealed class SubmeshAudit
    {
        public int submesh, indexStart, baseVertex, sourceIndexCount, exportedIndexCount, removedTriangles;
        public int materialInstanceId;
        public string materialName, shader, indicesSha256, materialSignature, deduplicationReason;
        public bool propertyBlock, reusedLastMaterialSlot;
        public Dictionary<string, object> sourceProperties;
        public PMXMaterialTextureExporter.Result textureBake;
    }

    public readonly List<Item> Items = new List<Item>();
    public readonly List<RendererAudit> Diagnostics = new List<RendererAudit>();
    private readonly List<Mesh> _owned = new List<Mesh>();
    private readonly Transform _root;

    private PMXMeshExportContext(Transform root) { _root = root; }

    public static PMXMeshExportContext Capture(IEnumerable<Renderer> renderers, Transform root)
    {
        var context = new PMXMeshExportContext(root);
        int offset = 0;
        foreach (Renderer renderer in renderers)
        {
            var audit = new RendererAudit { rendererPath = PathOf(renderer.transform), status = "skipped" };
            context.Diagnostics.Add(audit);
            try
            {
                Mesh source;
                if (renderer is SkinnedMeshRenderer skin) source = skin.sharedMesh;
                else if (renderer is MeshRenderer)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    source = filter != null ? filter.sharedMesh : null;
                }
                else { audit.reason = "unsupported_renderer"; continue; }
                if (source == null) throw new InvalidOperationException("缺少源 Mesh。");
                audit.meshName = source.name;
                audit.sourceMeshId = source.GetInstanceID();
                Material[] materials = renderer.sharedMaterials;
                if (materials.Length == 0 || materials.Any(material => material == null))
                    throw new InvalidOperationException("缺少有效材质槽，整段网格跳过。");
                // GPU 顶点复制无法还原非可读蒙皮的权重和形状，不能假装恢复成功。
                if (!source.isReadable && (renderer is SkinnedMeshRenderer || source.blendShapeCount > 0))
                    throw new InvalidOperationException("不可读蒙皮/形状网格无法可靠恢复权重和表情。");
                Mesh mesh = source;
                if (!source.isReadable)
                {
                    mesh = CopyReadableMesh(source);
                    context._owned.Add(mesh);
                    audit.gpuCopy = true;
                }
                var item = new Item { Renderer = renderer, Mesh = mesh, VertexOffset = offset, Audit = audit };
                item.Positions = mesh.vertices;
                item.Normals = mesh.normals;
                item.Uv = mesh.uv; item.Uv1 = mesh.uv2; item.Uv2 = mesh.uv3;
                item.Colors = mesh.colors;
                if (renderer is SkinnedMeshRenderer smr)
                {
                    item.Weights = mesh.boneWeights;
                    item.Bones = smr.bones;
                    using (var counts = mesh.GetBonesPerVertex())
                        if (counts.Any(count => count > 4))
                            throw new InvalidOperationException("源顶点超过四个蒙皮影响，不能无损写入当前 PMX 权重格式。");
                    if (item.Weights.Length != 0 && item.Weights.Length != mesh.vertexCount)
                        throw new InvalidOperationException("蒙皮权重数量与顶点数量不一致。");
                    foreach (BoneWeight weight in item.Weights)
                    {
                        ValidateWeight(item.Bones, weight.boneIndex0, weight.weight0);
                        ValidateWeight(item.Bones, weight.boneIndex1, weight.weight1);
                        ValidateWeight(item.Bones, weight.boneIndex2, weight.weight2);
                        ValidateWeight(item.Bones, weight.boneIndex3, weight.weight3);
                    }
                    var baked = new Mesh();
                    context._owned.Add(baked);
                    smr.BakeMesh(baked, false);
                    if (baked.vertexCount != mesh.vertexCount) throw new InvalidOperationException("BakeMesh 顶点布局发生变化。");
                    item.Positions = baked.vertices;
                    Vector3[] bakedNormals = baked.normals;
                    if (bakedNormals.Length != mesh.vertexCount)
                        throw new InvalidOperationException("BakeMesh 缺少完整法线。");
                    // 面部形状可能抵消烘焙法线；使用有效源法线并明确记录。
                    for (int i = 0; i < bakedNormals.Length; i++)
                        if (!ValidNormal(bakedNormals[i]))
                        {
                            if (i >= item.Normals.Length || !ValidNormal(item.Normals[i]))
                                throw new InvalidOperationException("源与烘焙法线均无效。");
                            bakedNormals[i] = item.Normals[i];
                            audit.bakedNormalFallbackCount++;
                        }
                    item.Normals = bakedNormals;
                }
                if (item.Normals.Length != mesh.vertexCount || item.Normals.Any(normal => !ValidNormal(normal)))
                    throw new InvalidOperationException("缺少完整有效法线，不能生成可靠导出法线。");
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        throw new InvalidOperationException("含非三角子网格，整段网格跳过。");
                    int[] indices = mesh.GetTriangles(submesh, true);
                    if (indices.Length % 3 != 0 || indices.Any(index => index < 0 || index >= mesh.vertexCount))
                        throw new InvalidOperationException("子网格三角索引无效。");
                    Material material = materials[Math.Min(submesh, materials.Length - 1)];
                    var descriptor = mesh.GetSubMesh(submesh);
                    var subAudit = new SubmeshAudit
                    {
                        submesh = submesh, indexStart = descriptor.indexStart, baseVertex = descriptor.baseVertex,
                        sourceIndexCount = indices.Length, materialInstanceId = material.GetInstanceID(),
                        materialName = material.name, shader = material.shader?.name,
                        indicesSha256 = HashIndices(indices), propertyBlock = renderer.HasPropertyBlock(),
                        reusedLastMaterialSlot = submesh >= materials.Length,
                        sourceProperties = MaterialProperties(material)
                    };
                    bool opaque = IsOpaque(material);
                    bool safe = opaque && !subAudit.propertyBlock;
                    subAudit.deduplicationReason = safe ? "same_material_instance_and_winding_only" :
                        !opaque ? "preserve_nonopaque_or_unknown_shader" : "preserve_property_block_semantics";
                    audit.submeshes.Add(subAudit);
                    item.Submeshes.Add(new Submesh
                    { Number = submesh, Indices = indices, Material = material, CanDeduplicate = safe, Audit = subAudit });
                }
                if (item.Submeshes.Count == 0) throw new InvalidOperationException("没有三角子网格。");
                audit.readableMeshId = mesh.GetInstanceID();
                audit.vertexCount = mesh.vertexCount; audit.vertexOffset = offset;
                audit.status = "captured";
                context.Items.Add(item);
                offset += mesh.vertexCount;
            }
            catch (Exception exception)
            {
                audit.reason = exception.Message;
                Debug.LogWarning("PMX 跳过网格 " + audit.rendererPath + "：" + exception.Message);
            }
        }
        return context;
    }

    public Vertex[] ReadVertices(Dictionary<Transform, int> boneIndexes)
    {
        var vertices = new List<Vertex>();
        foreach (Item item in Items)
        {
            Matrix4x4 matrix = _root.worldToLocalMatrix * item.Renderer.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            for (int i = 0; i < item.Positions.Length; i++)
            {
                Vector2 uv = At(item.Uv, i);
                Vector2 uv1 = At(item.Uv1, i), uv2 = At(item.Uv2, i);
                vertices.Add(new Vertex
                {
                    Coordinate = matrix.MultiplyPoint3x4(item.Positions[i]),
                    Normal = normalMatrix.MultiplyVector(item.Normals[i]).normalized,
                    UvCoordinate = new Vector2(uv.x, 1 - uv.y),
                    ExtraUvCoordinate = new Vector4[]
                    { item.Uv1.Length > 0 ? new Vector4(uv1.x, 1 - uv1.y) : Vector4.zero,
                      item.Uv2.Length > 0 ? new Vector4(uv2.x, 1 - uv2.y) : Vector4.zero,
                      i < item.Colors.Length ? (Vector4)item.Colors[i] : Vector4.zero },
                    SkinningOperator = Skinning(item, i, boneIndexes), EdgeScale = 1
                });
            }
        }
        return vertices.ToArray();
    }

    public Part[] ReadParts(RawMMDModel model, string textureDirectory)
    {
        var parts = new List<Part>();
        var indices = new List<int>();
        // 缓存仅属于一次导出，不能复用上次导出的纹理或属性块状态。
        var textureCache = new Dictionary<string, PMXMaterialTextureExporter.Result>();
        var incompleteTextures = new List<string>();
        foreach (Item item in Items)
        {
            var seen = new Dictionary<string, HashSet<Triangle>>();
            foreach (Submesh submesh in item.Submeshes)
            {
                MMDMaterial material = PMXMeshMaterialExporter.Create(item.Renderer, item.Mesh,
                    submesh.Number, submesh.Material, model, textureDirectory, textureCache, out var textureBake);
                submesh.Audit.textureBake = textureBake;
                if (textureBake.Status == "unsupported" || textureBake.Status == "failed")
                    incompleteTextures.Add(submesh.Material.name + "：" + textureBake.Reason);
                submesh.Audit.materialSignature = PMXMeshMaterialExporter.Signature(material);
                // 材质实例不同、属性块或透明层均保留；同一 Renderer 才共享集合。
                string materialId = submesh.Material.GetInstanceID() + "|" + submesh.Audit.materialSignature;
                if (!seen.TryGetValue(materialId, out HashSet<Triangle> triangles))
                    seen[materialId] = triangles = new HashSet<Triangle>();
                int start = indices.Count;
                for (int t = 0; t < submesh.Indices.Length; t += 3)
                {
                    var triangle = new Triangle(submesh.Indices[t], submesh.Indices[t + 1], submesh.Indices[t + 2]);
                    if (submesh.CanDeduplicate && !triangles.Add(triangle))
                    { submesh.Audit.removedTriangles++; continue; }
                    indices.Add(submesh.Indices[t] + item.VertexOffset);
                    indices.Add(submesh.Indices[t + 1] + item.VertexOffset);
                    indices.Add(submesh.Indices[t + 2] + item.VertexOffset);
                }
                submesh.Audit.exportedIndexCount = indices.Count - start;
                if (indices.Count > start)
                    parts.Add(new Part { Material = material, BaseShift = start, TriangleIndexNum = indices.Count - start });
            }
        }
        model.TriangleIndexes = indices.ToArray();
        if (incompleteTextures.Count > 0)
            Debug.LogWarning("PMX 材质叠加未全部完成，以下材质保留原贴图：\n" +
                string.Join("\n", incompleteTextures.Distinct()));
        return parts.ToArray();
    }

    private struct Triangle : IEquatable<Triangle>
    {
        private readonly int _a, _b, _c;
        internal Triangle(int a, int b, int c)
        {
            // 只归一循环轮换，反绕序保留。
            if (b < a && b <= c) { _a = b; _b = c; _c = a; }
            else if (c < a && c < b) { _a = c; _b = a; _c = b; }
            else { _a = a; _b = b; _c = c; }
        }
        public bool Equals(Triangle other) => _a == other._a && _b == other._b && _c == other._c;
        public override bool Equals(object other) => other is Triangle triangle && Equals(triangle);
        public override int GetHashCode() { unchecked { return ((_a * 397) ^ _b) * 397 ^ _c; } }
    }

    private static SkinningOperator Skinning(Item item, int i, Dictionary<Transform, int> indexes)
    {
        int fallback = BoneIndex(indexes, item.Renderer.transform);
        if (item.Weights == null || item.Weights.Length == 0)
            return new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = fallback } };
        BoneWeight weight = item.Weights[i];
        int[] ids = { weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3 };
        float[] values = { weight.weight0, weight.weight1, weight.weight2, weight.weight3 };
        var activeIds = new List<int>(); var activeWeights = new List<float>();
        for (int n = 0; n < 4; n++)
            if (values[n] > 0)
            { activeIds.Add(BoneIndex(indexes, item.Bones[ids[n]])); activeWeights.Add(values[n]); }
        if (activeIds.Count == 0)
            return new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = fallback } };
        if (activeIds.Count == 1)
            return new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = activeIds[0] } };
        if (activeIds.Count == 2)
            return new SkinningOperator { Type = SkinningType.SkinningBdef2,
                Param = new Bdef2 { BoneId = activeIds.ToArray(), BoneWeight = activeWeights[0] } };
        while (activeIds.Count < 4) { activeIds.Add(fallback); activeWeights.Add(0); }
        return new SkinningOperator { Type = SkinningType.SkinningBdef4,
            Param = new Bdef4 { BoneId = activeIds.ToArray(), BoneWeight = activeWeights.ToArray() } };
    }

    private static void ValidateWeight(Transform[] bones, int index, float weight)
    {
        if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0)
            throw new InvalidOperationException("蒙皮权重无效。");
        if (weight > 0 && (index < 0 || index >= bones.Length || bones[index] == null))
            throw new InvalidOperationException("正权重引用无效骨骼。");
    }

    private static int BoneIndex(Dictionary<Transform, int> indexes, Transform bone)
    {
        for (Transform current = bone; current != null; current = current.parent)
            if (indexes.TryGetValue(current, out int index)) return index;
        return 0;
    }

    private static bool ValidNormal(Vector3 normal) =>
        !float.IsNaN(normal.x) && !float.IsNaN(normal.y) && !float.IsNaN(normal.z) &&
        !float.IsInfinity(normal.x) && !float.IsInfinity(normal.y) && !float.IsInfinity(normal.z) &&
        normal.sqrMagnitude > 0.000000000001f;

    private static Vector2 At(Vector2[] array, int index) => index < array.Length ? array[index] : Vector2.zero;
    private static string PathOf(Transform transform)
    {
        var names = new List<string>();
        for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
        names.Reverse(); return string.Join("/", names);
    }

    private static bool IsOpaque(Material material)
    {
        // 未取证的自定义 Shader 可能有额外混合/裁剪语义，默认保留。
        if (material.shader == null || (material.shader.name != "Standard" &&
            material.shader.name != "Gallop/3D/Chara/Toon/TSER")) return false;
        return material.GetTag("RenderType", false, "") == "Opaque" &&
            material.renderQueue < 2450 && !material.IsKeywordEnabled("_ALPHATEST_ON") &&
            !material.IsKeywordEnabled("_ALPHABLEND_ON") && !material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") &&
            (!material.HasProperty("_Color") || material.GetColor("_Color").a >= 1) &&
            (!material.HasProperty("_Mode") || material.GetFloat("_Mode") == 0) &&
            (!material.HasProperty("_Surface") || material.GetFloat("_Surface") == 0) &&
            (!material.HasProperty("_SrcBlend") || material.GetFloat("_SrcBlend") == (float)BlendMode.One) &&
            (!material.HasProperty("_DstBlend") || material.GetFloat("_DstBlend") == (float)BlendMode.Zero) &&
            (!material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") != 0);
    }

    private static Dictionary<string, object> MaterialProperties(Material material)
    {
        var result = new Dictionary<string, object>
        { ["renderQueue"] = material.renderQueue, ["keywords"] = material.shaderKeywords.OrderBy(x => x).ToArray() };
        if (material.shader == null) return result;
        for (int i = 0; i < material.shader.GetPropertyCount(); i++)
        {
            string name = material.shader.GetPropertyName(i);
            switch (material.shader.GetPropertyType(i))
            {
                case ShaderPropertyType.Texture:
                    Texture texture = material.GetTexture(name);
                    result[name] = new { name = texture?.name, instanceId = texture?.GetInstanceID(),
                        scale = new[] { material.GetTextureScale(name).x, material.GetTextureScale(name).y },
                        offset = new[] { material.GetTextureOffset(name).x, material.GetTextureOffset(name).y } };
                    break;
                case ShaderPropertyType.Color:
                    Color color = material.GetColor(name); result[name] = new[] { color.r, color.g, color.b, color.a }; break;
                case ShaderPropertyType.Vector:
                    Vector4 vector = material.GetVector(name); result[name] = new[] { vector.x, vector.y, vector.z, vector.w }; break;
                case ShaderPropertyType.Int: result[name] = material.GetInt(name); break;
                default: result[name] = material.GetFloat(name); break;
            }
        }
        return result;
    }

    private static string HashIndices(int[] indices)
    {
        byte[] bytes = new byte[indices.Length * sizeof(int)]; Buffer.BlockCopy(indices, 0, bytes, 0, bytes.Length);
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }

    public static Mesh CopyReadableMesh(Mesh source)
    {
        var copy = new Mesh { name = source.name + "_PMX_readable", indexFormat = source.indexFormat };
        try
        {
            copy.SetVertexBufferParams(source.vertexCount, source.GetVertexAttributes());
            for (int stream = 0; stream < source.vertexBufferCount; stream++)
                using (GraphicsBuffer buffer = source.GetVertexBuffer(stream))
                {
                    var bytes = new byte[buffer.stride * buffer.count]; buffer.GetData(bytes);
                    copy.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream, MeshUpdateFlags.DontRecalculateBounds);
                }
            using (GraphicsBuffer buffer = source.GetIndexBuffer())
            {
                var bytes = new byte[buffer.stride * buffer.count]; buffer.GetData(bytes);
                copy.SetIndexBufferParams(buffer.count, source.indexFormat);
                copy.SetIndexBufferData(bytes, 0, 0, bytes.Length, MeshUpdateFlags.DontRecalculateBounds);
            }
            copy.subMeshCount = source.subMeshCount;
            for (int i = 0; i < source.subMeshCount; i++)
                copy.SetSubMesh(i, source.GetSubMesh(i), MeshUpdateFlags.DontRecalculateBounds);
            copy.bounds = source.bounds;
            return copy;
        }
        catch { Destroy(copy); throw; }
    }

    public void Dispose() { foreach (Mesh mesh in _owned) Destroy(mesh); _owned.Clear(); }
    private static void Destroy(UnityEngine.Object value)
    { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
}
