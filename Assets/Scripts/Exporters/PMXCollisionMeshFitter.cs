using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>从冻结网格与蒙皮影响拟合腿部、骨盆代理半径。</summary>
internal static class PMXCollisionMeshFitter
{
    private const float RequiredWeight = 0.45f;
    private const int MinimumSamples = 8;

    internal static bool TryFitCapsuleRadius(PMXMeshExportContext mesh, Transform coordinateRoot,
        Transform start, Transform end, out float radius)
    {
        radius = 0;
        if (mesh == null || coordinateRoot == null || start == null || end == null) return false;
        Vector3 axisStart = coordinateRoot.InverseTransformPoint(start.position);
        Vector3 axisEnd = coordinateRoot.InverseTransformPoint(end.position);
        Vector3 axis = axisEnd - axisStart;
        float lengthSquared = axis.sqrMagnitude;
        if (lengthSquared < 1e-8f) return false;

        var chain = new HashSet<Transform>();
        bool foundStart = false;
        for (Transform current = end; current != null; current = current.parent)
        {
            if (IsSkirtBone(current)) return false;
            chain.Add(current);
            if (current == start) { foundStart = true; break; }
        }
        if (!foundStart) return false;

        var samples = new List<float>();
        foreach (PMXMeshExportContext.Item item in mesh.Items)
        {
            if (item.Weights == null || item.Weights.Length != item.Positions.Length || item.Bones == null) continue;
            Matrix4x4 toRoot = coordinateRoot.worldToLocalMatrix * item.Renderer.transform.localToWorldMatrix;
            for (int i = 0; i < item.Positions.Length; i++)
            {
                if (WeightedBy(item, item.Weights[i], chain) < RequiredWeight) continue;
                Vector3 point = toRoot.MultiplyPoint3x4(item.Positions[i]);
                float t = Vector3.Dot(point - axisStart, axis) / lengthSquared;
                if (t < -0.08f || t > 1.08f) continue;
                Vector3 nearest = axisStart + axis * Mathf.Clamp01(t);
                float radial = Vector3.Distance(point, nearest);
                if (IsFinite(radial)) samples.Add(radial);
            }
        }
        return TryRobustRadius(samples, out radius);
    }

    internal static bool TryFitPelvisRadius(PMXMeshExportContext mesh, Transform coordinateRoot,
        Transform pelvisCenter, Transform leftThigh, Transform rightThigh, out float radius)
    {
        radius = 0;
        if (coordinateRoot == null || pelvisCenter == null) return false;
        return TryFitPelvisRadiusAt(mesh, coordinateRoot, coordinateRoot.InverseTransformPoint(pelvisCenter.position),
            pelvisCenter, leftThigh, rightThigh, out radius);
    }

    internal static bool TryFitPelvisRadiusAt(PMXMeshExportContext mesh, Transform coordinateRoot,
        Vector3 center, Transform pelvisCenter, Transform leftThigh, Transform rightThigh, out float radius)
    {
        radius = 0;
        if (mesh == null || coordinateRoot == null || pelvisCenter == null ||
            leftThigh == null || rightThigh == null) return false;
        Vector3 left = coordinateRoot.InverseTransformPoint(leftThigh.position);
        Vector3 right = coordinateRoot.InverseTransformPoint(rightThigh.position);
        float hipSpan = Vector3.Distance(left, right);
        if (!IsFinite(hipSpan) || hipSpan < 1e-5f) return false;

        var hipBones = new HashSet<Transform> { pelvisCenter, leftThigh, rightThigh };
        var samples = new List<float>();
        foreach (PMXMeshExportContext.Item item in mesh.Items)
        {
            if (item.Weights == null || item.Weights.Length != item.Positions.Length || item.Bones == null) continue;
            Matrix4x4 toRoot = coordinateRoot.worldToLocalMatrix * item.Renderer.transform.localToWorldMatrix;
            for (int i = 0; i < item.Positions.Length; i++)
            {
                if (WeightedBy(item, item.Weights[i], hipBones) < RequiredWeight) continue;
                Vector3 point = toRoot.MultiplyPoint3x4(item.Positions[i]);
                // 以髋宽限制骨盆高度窗口，避免把整条腿和躯干带入拟合。
                if (Mathf.Abs(point.y - center.y) > hipSpan * 0.65f) continue;
                float radial = Vector3.Distance(point, center);
                if (IsFinite(radial)) samples.Add(radial);
            }
        }
        return TryRobustRadius(samples, out radius);
    }

    private static float WeightedBy(PMXMeshExportContext.Item item, BoneWeight weight,
        HashSet<Transform> accepted)
    {
        float sum = 0;
        Add(item, weight.boneIndex0, weight.weight0, accepted, ref sum);
        Add(item, weight.boneIndex1, weight.weight1, accepted, ref sum);
        Add(item, weight.boneIndex2, weight.weight2, accepted, ref sum);
        Add(item, weight.boneIndex3, weight.weight3, accepted, ref sum);
        return sum;
    }

    private static void Add(PMXMeshExportContext.Item item, int index, float weight,
        HashSet<Transform> accepted, ref float sum)
    {
        if (weight <= 0 || index < 0 || index >= item.Bones.Length) return;
        Transform bone = item.Bones[index];
        if (bone != null && accepted.Contains(bone) && !IsSkirtBone(bone)) sum += weight;
    }

    private static bool TryRobustRadius(List<float> samples, out float radius)
    {
        radius = 0;
        if (samples.Count < MinimumSamples) return false;
        samples.Sort();
        // 高分位抑制少量错误权重和孤立顶点，少于八点则回退旧尺寸。
        int index = Mathf.Clamp(Mathf.CeilToInt((samples.Count - 1) * 0.95f), 0, samples.Count - 1);
        float fitted = samples[index] * 1.05f;
        if (!IsFinite(fitted) || fitted <= 1e-5f) return false;
        radius = fitted;
        return true;
    }

    private static bool IsSkirtBone(Transform bone)
    {
        string name = bone.name.ToLowerInvariant();
        return name.Contains("skirt") || name.Contains("dress") || name.Contains("cloth") ||
               name.Contains("sukato") || name.Contains("スカート") || name.Contains("裙");
    }

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
