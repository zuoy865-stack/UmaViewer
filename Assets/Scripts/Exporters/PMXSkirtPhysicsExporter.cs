using Gallop;
using LibMMD.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 负责将 SkirtController 与裙摆骨骼网格转换为 PMX 2.0 盒体刚体与约束 Joint。
/// </summary>
internal static class PMXSkirtPhysicsExporter
{
    private const float SkirtMinimumPanelHalfWidth = 0.008f;
    private const float SkirtMaximumPanelHalfWidth = 0.15f;
    private const float SkirtMinimumPanelHalfThickness = 0.003f;
    private const float SkirtMaximumPanelHalfThickness = 0.008f;
    private const float SkirtVerticalBendDegrees = 16f;
    private const float SkirtVerticalTwistDegrees = 6f;

    // 腿部与骨盆碰撞体真实物理尺寸（Unity米制单位，导出PMX时除以0.08换算为PMX单位）
    // 大腿视觉厚度（直径）约 0.65~0.75 PMX单位，对应半径约 0.35 PMX单位 = 0.028m
    private const float DefaultPelvisColliderRadius = 0.040f; // PMX 半径 0.50，直径 1.00，贴合骨盆腰臀内部
    private const float DefaultThighColliderRadius = 0.028f;  // PMX 半径 0.35，直径 0.70，精准贴合大腿视觉网格
    private const float DefaultShinColliderRadius = 0.018f;   // PMX 半径 0.225，直径 0.45，精准贴合小腿

    // 裙摆段临时信息
    internal sealed class SkirtSegment
    {
        internal Transform Bone;
        internal Transform Child;
        internal int BoneIndex;
        internal bool IsVirtual;
        internal string BoneName;
        internal int ColumnIndex;
        internal int RowIndex;
        internal int RigidIndex;
        internal Vector3 Start;
        internal Vector3 End;
        internal Vector3 Center;
        internal Vector3 Rotation;
        internal float Length;
        internal float HalfWidth;
        internal float HalfThickness;
    }

    /// <summary>
    /// 收集角色裙摆列链。
    /// </summary>
    internal static void CollectSkirtColumns(UmaContainerCharacter character, Transform skeletonRoot,
        PMXPhysicsExporter.Context context, HashSet<Transform> claimedBones)
    {
        SkirtController controller = context.SkirtController;
        if (controller == null || controller.SkirtDataArray == null) return;

        foreach (SkirtController.SkirtData skirtData in controller.SkirtDataArray)
        {
            if (skirtData == null || skirtData.SkirtRoot == null || skirtData.SkirtChild == null) continue;
            if (skirtData.SkirtRoot != skeletonRoot && !skirtData.SkirtRoot.IsChildOf(skeletonRoot)) continue;
            if (claimedBones.Contains(skirtData.SkirtRoot)) continue;

            PMXPhysicsExporter.Chain bestChain = null;
            foreach (CySpringDataContainer container in character.cySpringDataContainers)
            {
                if (container == null || container.springParam == null) continue;
                foreach (CySpringParamDataElement element in container.springParam)
                {
                    if (element == null || !string.Equals(
                            element._boneName, skirtData.SkirtRoot.name, StringComparison.Ordinal)) continue;

                    PMXPhysicsExporter.Chain candidate = BuildChainFromRoot(element, skirtData.SkirtRoot);
                    if (candidate == null || !candidate.Bones.Contains(skirtData.SkirtChild) ||
                        !IsStrictLinearChain(candidate, skirtData.SkirtChild)) continue;
                    if (bestChain == null || candidate.Bones.Count > bestChain.Bones.Count) bestChain = candidate;
                }
            }

            if (bestChain == null) continue;
            claimedBones.UnionWith(bestChain.Bones);
            context.SkirtColumns.Add(new PMXPhysicsExporter.SkirtColumn
            {
                Chain = bestChain,
                IsCheckRightLeg = skirtData.IsCheckRightLeg,
                IsCheckLeftLeg = skirtData.IsCheckLeftLeg
            });

            foreach (Transform bone in bestChain.Bones)
            {
                // 裙摆骨骼即使无蒙皮权重，也决定物理链完整性
                context.DynamicBones.Add(bone);
            }
        }
    }

    /// <summary>
    /// 构建裙摆刚体、Joint 与腿部碰撞体。
    /// </summary>
    internal static void BuildSkirtPhysics(PMXPhysicsExporter.Context context, Transform coordinateRoot,
        PMXBoneExporter.Result boneResult, List<MMDRigidBody> rigidBodies, List<MMDJoint> joints,
        Dictionary<Transform, int> dynamicRigidIndexes, PMXSkirtLayoutExporter.Result layout = null)
    {
        if (context.SkirtController == null || context.SkirtColumns.Count < 3) return;
        if (layout != null && layout.Columns.Count >= 3)
        {
            BuildFromLayout(context, coordinateRoot, boneResult, rigidBodies, joints, layout);
            return;
        }

        List<PMXPhysicsExporter.SkirtColumn> columns = OrderSkirtColumns(context.SkirtColumns, context.SkirtController, coordinateRoot);
        List<List<SkirtSegment>> segmentsByColumn = new List<List<SkirtSegment>>();
        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            List<Transform> bones = GetOrderedLinearBones(columns[columnIndex].Chain);
            List<SkirtSegment> segments = new List<SkirtSegment>();
            for (int rowIndex = 0; rowIndex + 1 < bones.Count; rowIndex++)
            {
                Transform bone = bones[rowIndex];
                Transform child = bones[rowIndex + 1];
                if (!boneResult.BoneIndexes.ContainsKey(bone)) break;

                Vector3 start = coordinateRoot.InverseTransformPoint(bone.position);
                Vector3 end = coordinateRoot.InverseTransformPoint(child.position);
                float length = Vector3.Distance(start, end);
                if (!PMXPhysicsExporter.IsFinite(length) || length <= PMXPhysicsExporter.MinimumSegmentLength) break;

                segments.Add(new SkirtSegment
                {
                    Bone = bone,
                    Child = child,
                    BoneIndex = boneResult.BoneIndexes[bone],
                    BoneName = bone.name,
                    ColumnIndex = columnIndex,
                    RowIndex = rowIndex,
                    Start = start,
                    End = end,
                    Center = (start + end) * 0.5f,
                    Length = length,
                    RigidIndex = -1
                });
            }
            segmentsByColumn.Add(segments);
        }

        // 旧路径只沿用已有的真实链段。
        if (segmentsByColumn.Count < 3 || segmentsByColumn.Any(segments => segments.Count == 0)) return;
        const bool closeRing = false;
        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            List<SkirtSegment> segments = segmentsByColumn[columnIndex];
            for (int rowIndex = 0; rowIndex < segments.Count; rowIndex++)
            {
                SkirtSegment segment = segments[rowIndex];
                Vector3 tangent = CalculateSkirtTangent(segmentsByColumn, columnIndex, rowIndex, closeRing);
                segment.HalfWidth = EstimateSkirtPanelHalfWidth(
                    segmentsByColumn, columnIndex, rowIndex, closeRing, false);
                float configuredRadius = segment.Bone != null && columns[columnIndex].Chain.Radii.ContainsKey(segment.Bone)
                    ? columns[columnIndex].Chain.Radii[segment.Bone]
                    : columns[columnIndex].Chain.Radii[columns[columnIndex].Chain.Root];
                segment.HalfThickness = Mathf.Clamp(
                    configuredRadius * 0.25f,
                    SkirtMinimumPanelHalfThickness,
                    SkirtMaximumPanelHalfThickness);
                segment.Rotation = CalculatePanelRotation(segment.End - segment.Start, tangent);
            }
        }

        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            PMXPhysicsExporter.SkirtColumn column = columns[columnIndex];
            List<SkirtSegment> segments = segmentsByColumn[columnIndex];
            Transform anchorBone = PMXPhysicsExporter.FindNearestExportedParentTransform(column.Chain.Root.parent, boneResult.BoneIndexes);
            int anchorBoneIndex = anchorBone != null ? boneResult.BoneIndexes[anchorBone] : 0;
            int parentRigidIndex = rigidBodies.Count;
            Vector3 anchorRotation = segments.Count > 0 ? segments[0].Rotation : Vector3.zero;
            rigidBodies.Add(CreateSkirtAnchorBody(column, coordinateRoot, anchorBoneIndex, anchorRotation));

            for (int rowIndex = 0; rowIndex < segments.Count; rowIndex++)
            {
                SkirtSegment segment = segments[rowIndex];
                float depthRatio = segments.Count > 1 ? rowIndex / (float)(segments.Count - 1) : 0f;
                int boneIndex = segment.BoneIndex;
                segment.RigidIndex = rigidBodies.Count;
                rigidBodies.Add(CreateSkirtPanelBody(segment, boneIndex, depthRatio));
                if (segment.Bone != null)
                    dynamicRigidIndexes[segment.Bone] = segment.RigidIndex;
                joints.Add(CreateSkirtVerticalJoint(segment, parentRigidIndex, depthRatio));
                parentRigidIndex = segment.RigidIndex;
            }
        }

        AddSkirtLegColliders(
            context.SkirtController, columns, coordinateRoot, boneResult.BoneIndexes, rigidBodies);
    }

    private static void BuildFromLayout(PMXPhysicsExporter.Context context, Transform coordinateRoot,
        PMXBoneExporter.Result boneResult, List<MMDRigidBody> rigidBodies, List<MMDJoint> joints,
        PMXSkirtLayoutExporter.Result layout)
    {
        var columns = layout.Columns.ToList();
        var originalPanels = BuildOriginalPanelFrames(context, coordinateRoot);
        foreach (var group in columns.GroupBy(column => column.ComponentId))
        {
            List<PMXSkirtLayoutExporter.Column> groupColumns = group.ToList();
            int sourceColumnCount = groupColumns.Count(column => column.SourceBoneIndexes.Count > 0);
            if (sourceColumnCount == 0)
            {
                Debug.LogWarning("PMX 裙摆布局组件没有真实来源列，跳过增强刚体。");
                continue;
            }
            int groupBodyStart = rigidBodies.Count;
            bool closed = groupColumns.Count >= 3 && groupColumns.All(column => column.RegionClosed);
            var table = new List<List<SkirtSegment>>();
            foreach (PMXSkirtLayoutExporter.Column column in groupColumns)
            {
                var segments = new List<SkirtSegment>();
                foreach (PMXSkirtLayoutExporter.Segment piece in column.Segments)
                {
                    float length = Vector3.Distance(piece.Start, piece.End);
                    segments.Add(new SkirtSegment
                    {
                        Bone = column.Source.Chain.Root,
                        BoneIndex = piece.BoneIndex,
                        BoneName = piece.Name,
                        IsVirtual = piece.IsVirtual,
                        ColumnIndex = table.Count,
                        RowIndex = piece.Row,
                        Start = piece.Start,
                        End = piece.End,
                        Center = (piece.Start + piece.End) * 0.5f,
                        Length = Mathf.Max(length, PMXPhysicsExporter.MinimumSegmentLength),
                        RigidIndex = -1
                    });
                }
                table.Add(segments);
            }
            for (int columnIndex = 0; columnIndex < table.Count; columnIndex++)
            {
                float radius = groupColumns[columnIndex].Source.Chain.Radii[groupColumns[columnIndex].Source.Chain.Root];
                for (int rowIndex = 0; rowIndex < table[columnIndex].Count; rowIndex++)
                {
                    SkirtSegment segment = table[columnIndex][rowIndex];
                    var piece = groupColumns[columnIndex].Segments[rowIndex];
                    var originalPanel = originalPanels[piece.SourceBoneName];
                    // 四分只改变纵向长度，横向宽度仍取原面板。
                    segment.HalfWidth = originalPanel.HalfWidth;
                    segment.HalfThickness = Mathf.Clamp(radius * 0.25f, SkirtMinimumPanelHalfThickness, SkirtMaximumPanelHalfThickness);
                    // 同一原骨的四块沿用原面板朝向与横向尺寸。
                    segment.Rotation = originalPanel.Rotation;
                }
            }
            for (int columnIndex = 0; columnIndex < groupColumns.Count; columnIndex++)
            {
                PMXSkirtLayoutExporter.Column column = groupColumns[columnIndex];
                List<SkirtSegment> segments = table[columnIndex];
                int parentRigid = rigidBodies.Count;
                rigidBodies.Add(CreateSkirtAnchorBodyFromLayout(column, segments.Count > 0 ? segments[0].Rotation : Vector3.zero));
                for (int rowIndex = 0; rowIndex < segments.Count; rowIndex++)
                {
                    SkirtSegment segment = segments[rowIndex];
                    float depth = segments.Count > 1 ? rowIndex / (float)(segments.Count - 1) : 0f;
                    segment.RigidIndex = rigidBodies.Count;
                    MMDRigidBody body = CreateEnhancedSkirtPanelBody(segment, segment.BoneIndex, depth);
                    body.CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(
                        PMXPhysicsExporter.SkirtCollisionGroup);
                    body.Dimemsions = new Vector3(segment.HalfWidth, segment.Length * 0.5f, segment.HalfThickness);
                    body.Mass *= Mathf.Max(0.0001f, segment.HalfWidth * segment.Length);
                    rigidBodies.Add(body);
                    joints.Add(CreateSkirtVerticalJoint(segment, parentRigid, depth));
                    parentRigid = segment.RigidIndex;
                }
            }
            float massTotal = rigidBodies.Skip(groupBodyStart).Where(body => body.Type != MMDRigidBody.RigidBodyType.RigidTypeKinematic).Sum(body => body.Mass);
            float budget = PMXPhysicsExporter.SkirtPreset.RootMass * sourceColumnCount;
            if (massTotal > 0.0001f)
                for (int i = groupBodyStart; i < rigidBodies.Count; i++)
                    if (rigidBodies[i].Type != MMDRigidBody.RigidBodyType.RigidTypeKinematic) rigidBodies[i].Mass *= budget / massTotal;
            var indexById = groupColumns.Select((column, index) => new { column.ColumnId, index })
                .ToDictionary(pair => pair.ColumnId, pair => pair.index, StringComparer.Ordinal);
            var linkedPairs = new HashSet<string>(StringComparer.Ordinal);
            for (int columnIndex = 0; columnIndex < groupColumns.Count; columnIndex++)
            {
                foreach (string neighborId in groupColumns[columnIndex].NeighborColumnIds)
                {
                    if (!indexById.TryGetValue(neighborId, out int next)) continue;
                    string pairKey = string.CompareOrdinal(groupColumns[columnIndex].ColumnId, neighborId) < 0
                        ? groupColumns[columnIndex].ColumnId + "|" + neighborId : neighborId + "|" + groupColumns[columnIndex].ColumnId;
                    if (!linkedPairs.Add(pairKey)) continue;
                    int rows = Mathf.Min(table[columnIndex].Count, table[next].Count);
                    for (int rowIndex = 0; rowIndex < rows; rowIndex++)
                    {
                        SkirtSegment left = table[columnIndex][rowIndex];
                        SkirtSegment right = table[next][rowIndex];
                        if (left.RigidIndex >= 0 && right.RigidIndex >= 0 && left.RigidIndex != right.RigidIndex)
                            joints.Add(CreateSkirtHorizontalJoint(left, right));
                    }
                }
            }
        }
        AddSkirtLegCollidersEnhanced(context.SkirtController, columns.Select(column => column.Source),
            coordinateRoot, boneResult.BoneIndexes, rigidBodies, context.Mesh);
    }

    private static Dictionary<string, SkirtSegment> BuildOriginalPanelFrames(
        PMXPhysicsExporter.Context context, Transform coordinateRoot)
    {
        var sources = OrderSkirtColumns(context.SkirtColumns, context.SkirtController, coordinateRoot);
        // 仅复原旧面板局部框架；新横关节仍只用实际网格邻接。
        bool closedFrame = IsClosedSkirtRing(sources, context.SkirtController, coordinateRoot);
        var table = new List<List<SkirtSegment>>();
        foreach (var source in sources)
        {
            var chain = GetOrderedLinearBones(source.Chain);
            var panels = new List<SkirtSegment>();
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                Vector3 start = coordinateRoot.InverseTransformPoint(chain[i].position);
                Vector3 end = coordinateRoot.InverseTransformPoint(chain[i + 1].position);
                panels.Add(new SkirtSegment { BoneName = chain[i].name, Start = start, End = end,
                    Center = (start + end) * 0.5f });
            }
            table.Add(panels);
        }
        var result = new Dictionary<string, SkirtSegment>(StringComparer.Ordinal);
        for (int col = 0; col < table.Count; col++)
            for (int row = 0; row < table[col].Count; row++)
            {
                var panel = table[col][row];
                panel.Rotation = CalculatePanelRotation(panel.End - panel.Start,
                    CalculateSkirtTangent(table, col, row, closedFrame));
                panel.HalfWidth = EstimateSkirtPanelHalfWidth(table, col, row, closedFrame, false);
                result.Add(panel.BoneName, panel);
            }
        return result;
    }

    private static PMXPhysicsExporter.Chain BuildChainFromRoot(CySpringParamDataElement element, Transform root)
    {
        if (element == null || root == null ||
            !string.Equals(element._boneName, root.name, StringComparison.Ordinal)) return null;

        Dictionary<string, float> configuredBones = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            [element._boneName] = PMXPhysicsExporter.SanitizeRadius(element._collisionRadius)
        };
        if (element._childElements != null)
        {
            foreach (CySpringParamDataChildElement child in element._childElements)
            {
                if (child != null && !string.IsNullOrEmpty(child._boneName))
                    configuredBones[child._boneName] = PMXPhysicsExporter.SanitizeRadius(child._collisionRadius);
            }
        }

        PMXPhysicsExporter.Chain chain = new PMXPhysicsExporter.Chain { Root = root };
        PMXPhysicsExporter.CollectContinuousBones(root, configuredBones, chain);
        return chain.Bones.Count > 0 ? chain : null;
    }

    private static bool IsStrictLinearChain(PMXPhysicsExporter.Chain chain, Transform expectedFirstChild)
    {
        if (chain == null || chain.Root == null || expectedFirstChild == null) return false;

        Transform current = chain.Root;
        int visited = 0;
        bool foundExpectedChild = false;
        while (current != null && chain.Bones.Contains(current))
        {
            visited++;
            Transform next = PMXPhysicsExporter.GetSingleChainChild(current, chain.Bones);
            if (current == chain.Root) foundExpectedChild = next == expectedFirstChild;
            if (next == null) break;
            current = next;
        }
        return foundExpectedChild && visited == chain.Bones.Count && visited >= 2;
    }

    private static List<Transform> GetOrderedLinearBones(PMXPhysicsExporter.Chain chain)
    {
        List<Transform> result = new List<Transform>();
        Transform current = chain.Root;
        while (current != null && chain.Bones.Contains(current))
        {
            result.Add(current);
            current = PMXPhysicsExporter.GetSingleChainChild(current, chain.Bones);
        }
        return result;
    }

    private static List<PMXPhysicsExporter.SkirtColumn> OrderSkirtColumns(IEnumerable<PMXPhysicsExporter.SkirtColumn> columns,
        SkirtController controller, Transform coordinateRoot)
    {
        List<PMXPhysicsExporter.SkirtColumn> result = columns.ToList();
        Vector3 center = controller.CenterBone != null
            ? coordinateRoot.InverseTransformPoint(controller.CenterBone.position)
            : result.Aggregate(Vector3.zero, (sum, column) =>
                sum + coordinateRoot.InverseTransformPoint(column.Chain.Root.position)) / result.Count;

        return result.OrderBy(column =>
        {
            Vector3 position = coordinateRoot.InverseTransformPoint(column.Chain.Root.position) - center;
            return Mathf.Atan2(position.z, position.x);
        }).ToList();
    }

    private static bool IsClosedSkirtRing(IList<PMXPhysicsExporter.SkirtColumn> columns, SkirtController controller,
        Transform coordinateRoot)
    {
        if (columns.Count < 3) return false;
        Vector3 center = controller.CenterBone != null
            ? coordinateRoot.InverseTransformPoint(controller.CenterBone.position)
            : columns.Aggregate(Vector3.zero, (sum, column) =>
                sum + coordinateRoot.InverseTransformPoint(column.Chain.Root.position)) / columns.Count;
        List<float> angles = columns.Select(column =>
        {
            Vector3 position = coordinateRoot.InverseTransformPoint(column.Chain.Root.position) - center;
            return Mathf.Atan2(position.z, position.x);
        }).OrderBy(angle => angle).ToList();

        float maximumGap = 0f;
        for (int i = 0; i < angles.Count; i++)
        {
            float next = i + 1 < angles.Count ? angles[i + 1] : angles[0] + Mathf.PI * 2f;
            maximumGap = Mathf.Max(maximumGap, next - angles[i]);
        }
        float averageGap = Mathf.PI * 2f / angles.Count;
        return maximumGap <= Mathf.Min(averageGap * 2.25f, 150f * Mathf.Deg2Rad);
    }

    private static Vector3 CalculateSkirtTangent(IList<List<SkirtSegment>> columns,
        int columnIndex, int rowIndex, bool closeRing)
    {
        SkirtSegment current = columns[columnIndex][rowIndex];
        SkirtSegment previous = GetNeighborSkirtSegment(columns, columnIndex - 1, rowIndex, closeRing);
        SkirtSegment next = GetNeighborSkirtSegment(columns, columnIndex + 1, rowIndex, closeRing);
        if (previous != null && next != null) return next.Center - previous.Center;
        if (next != null) return next.Center - current.Center;
        if (previous != null) return current.Center - previous.Center;
        return Vector3.right;
    }

    private static float EstimateSkirtPanelHalfWidth(IList<List<SkirtSegment>> columns,
        int columnIndex, int rowIndex, bool closeRing, bool enhanced)
    {
        SkirtSegment current = columns[columnIndex][rowIndex];
        SkirtSegment previous = GetNeighborSkirtSegment(columns, columnIndex - 1, rowIndex, closeRing);
        SkirtSegment next = GetNeighborSkirtSegment(columns, columnIndex + 1, rowIndex, closeRing);
        float spacing = 0f, sum = 0f;
        int count = 0;
        if (previous != null) { float d = Vector3.Distance(current.Center, previous.Center); spacing = Mathf.Max(spacing, d); sum += d; count++; }
        if (next != null) { float d = Vector3.Distance(current.Center, next.Center); spacing = Mathf.Max(spacing, d); sum += d; count++; }
        if (spacing <= 0f) return SkirtMinimumPanelHalfWidth;
        if (!enhanced) return Mathf.Clamp(sum / count * 0.38f, SkirtMinimumPanelHalfWidth, 0.05f);
        // 增强网络关闭自碰后，面板半宽覆盖最大相邻间距的一半。
        return Mathf.Clamp(spacing * 0.5f,
            SkirtMinimumPanelHalfWidth, SkirtMaximumPanelHalfWidth);
    }

    private static SkirtSegment GetNeighborSkirtSegment(IList<List<SkirtSegment>> columns,
        int columnIndex, int rowIndex, bool closeRing)
    {
        if (closeRing)
        {
            columnIndex %= columns.Count;
            if (columnIndex < 0) columnIndex += columns.Count;
        }
        else if (columnIndex < 0 || columnIndex >= columns.Count)
        {
            return null;
        }

        List<SkirtSegment> segments = columns[columnIndex];
        return rowIndex >= 0 && rowIndex < segments.Count ? segments[rowIndex] : null;
    }

    private static Vector3 CalculatePanelRotation(Vector3 segmentDirection, Vector3 tangentDirection)
    {
        Vector3 up = segmentDirection.normalized;
        Vector3 right = Vector3.ProjectOnPlane(tangentDirection, up);
        if (right.sqrMagnitude < PMXPhysicsExporter.MinimumSegmentLength * PMXPhysicsExporter.MinimumSegmentLength)
            right = Vector3.ProjectOnPlane(Vector3.right, up);
        if (right.sqrMagnitude < PMXPhysicsExporter.MinimumSegmentLength * PMXPhysicsExporter.MinimumSegmentLength)
            right = Vector3.ProjectOnPlane(Vector3.forward, up);
        right.Normalize();
        Vector3 forward = Vector3.Cross(right, up).normalized;
        right = Vector3.Cross(up, forward).normalized;
        return PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(Quaternion.LookRotation(forward, up));
    }

    private static MMDRigidBody CreateSkirtAnchorBody(PMXPhysicsExporter.SkirtColumn column,
        Transform coordinateRoot, int boneIndex, Vector3 rotation)
    {
        float radius = Mathf.Max(PMXPhysicsExporter.MinimumRadius, column.Chain.Radii[column.Chain.Root] * 0.5f);
        return new MMDRigidBody
        {
            Name = column.Chain.Root.name + "_skirt_anchor",
            NameEn = column.Chain.Root.name + "_skirt_anchor",
            AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(
                PMXPhysicsExporter.SkirtLegCollisionGroup),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeSphere,
            Dimemsions = new Vector3(radius, 0, 0),
            Position = coordinateRoot.InverseTransformPoint(column.Chain.Root.position),
            Rotation = rotation,
            Mass = 0,
            TranslateDamp = 1,
            RotateDamp = 1,
            Restitution = 0,
            Friction = 0,
            Type = MMDRigidBody.RigidBodyType.RigidTypeKinematic
        };
    }

    private static MMDRigidBody CreateSkirtPanelBody(SkirtSegment segment, int boneIndex, float depthRatio)
    { return CreateSkirtPanelBodyCore(segment, boneIndex, depthRatio, false); }

    private static MMDRigidBody CreateEnhancedSkirtPanelBody(SkirtSegment segment, int boneIndex, float depthRatio)
    { return CreateSkirtPanelBodyCore(segment, boneIndex, depthRatio, true); }

    private static MMDRigidBody CreateSkirtPanelBodyCore(SkirtSegment segment, int boneIndex,
        float depthRatio, bool enhanced)
    {
        return new MMDRigidBody
        {
            Name = segment.BoneName + "_skirt_physics",
            NameEn = segment.BoneName + "_skirt_physics",
            AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtCollisionGroup,
            CollisionMask = enhanced
                ? PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(PMXPhysicsExporter.SkirtCollisionGroup)
                : PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeBox,
            Dimemsions = new Vector3(
                segment.HalfWidth, segment.Length * 0.46f, segment.HalfThickness),
            Position = segment.Center,
            Rotation = segment.Rotation,
            Mass = Mathf.Lerp(PMXPhysicsExporter.SkirtPreset.RootMass, PMXPhysicsExporter.SkirtPreset.TipMass, depthRatio),
            TranslateDamp = Mathf.Lerp(
                PMXPhysicsExporter.SkirtPreset.RootTranslateDamp, PMXPhysicsExporter.SkirtPreset.TipTranslateDamp, depthRatio),
            RotateDamp = Mathf.Lerp(
                PMXPhysicsExporter.SkirtPreset.RootRotateDamp, PMXPhysicsExporter.SkirtPreset.TipRotateDamp, depthRatio),
            Restitution = 0,
            Friction = 0,
            Type = MMDRigidBody.RigidBodyType.RigidTypePhysics
        };
    }

    private static MMDJoint CreateSkirtVerticalJoint(SkirtSegment segment,
        int parentRigidIndex, float depthRatio)
    {
        float bend = SkirtVerticalBendDegrees * Mathf.Deg2Rad;
        float twist = SkirtVerticalTwistDegrees * Mathf.Deg2Rad;
        float bendSpring = Mathf.Lerp(
            PMXPhysicsExporter.SkirtPreset.BendSpring, PMXPhysicsExporter.SkirtPreset.BendSpring * 0.5f, depthRatio);
        float twistSpring = Mathf.Lerp(
            PMXPhysicsExporter.SkirtPreset.TwistSpring, PMXPhysicsExporter.SkirtPreset.TwistSpring * 0.5f, depthRatio);
        return new MMDJoint
        {
            Name = segment.BoneName + "_skirt_vertical_joint",
            NameEn = segment.BoneName + "_skirt_vertical_joint",
            AssociatedRigidBodyIndex = new[] { parentRigidIndex, segment.RigidIndex },
            Position = segment.Start,
            Rotation = segment.Rotation,
            PositionLowLimit = Vector3.zero,
            PositionHiLimit = Vector3.zero,
            RotationLowLimit = new Vector3(-bend, -twist, -bend * 0.75f),
            RotationHiLimit = new Vector3(bend, twist, bend * 0.75f),
            SpringTranslate = Vector3.zero,
            SpringRotate = new Vector3(bendSpring, twistSpring, bendSpring)
        };
    }

    private static MMDJoint CreateSkirtHorizontalJoint(SkirtSegment left, SkirtSegment right)
    {
        Vector3 direction = right.Center - left.Center;
        if (direction.sqrMagnitude < 0.000001f) direction = Vector3.right;
        Vector3 rotation = CalculatePanelRotation(direction, right.End - right.Start);
        float bend = 10f * Mathf.Deg2Rad;
        return new MMDJoint
        {
            Name = left.BoneName + "_to_" + right.BoneName + "_skirt_horizontal_joint",
            NameEn = left.BoneName + "_to_" + right.BoneName + "_skirt_horizontal_joint",
            AssociatedRigidBodyIndex = new[] { left.RigidIndex, right.RigidIndex },
            Position = (left.Center + right.Center) * 0.5f,
            Rotation = rotation,
            PositionLowLimit = Vector3.zero,
            PositionHiLimit = Vector3.zero,
            RotationLowLimit = new Vector3(-bend, -bend * 0.5f, -bend),
            RotationHiLimit = new Vector3(bend, bend * 0.5f, bend),
            SpringTranslate = Vector3.zero,
            SpringRotate = new Vector3(8f, 3f, 8f)
        };
    }

    /// <summary>
    /// 构建骨盆与下半身刚体以及左右腿部碰撞体，为裙摆提供完整的内部物理支撑。
    /// </summary>
    private static void AddSkirtLegColliders(SkirtController controller,
        IEnumerable<PMXPhysicsExporter.SkirtColumn> columns, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies)
    {
        // 1. 添加骨盆/腰臀部支撑刚体（Pelvis Collider），解决裙摆在根部向内塌陷的问题
        AddPelvisCollider(controller, coordinateRoot, boneIndexes, rigidBodies);

        // 2. 添加左右腿部碰撞体，恢复真实大腿与小腿半径（匹配 0.65~0.8 PMX 视觉厚度）
        bool checkLeft = columns.Any(column => column.IsCheckLeftLeg);
        bool checkRight = columns.Any(column => column.IsCheckRightLeg);
        float thighRadius = SanitizeSkirtColliderRadius(
            controller.KneeColliderRadius, DefaultThighColliderRadius);
        float shinRadius = SanitizeSkirtColliderRadius(
            controller.AnkleColliderRadius, DefaultShinColliderRadius);

        if (checkLeft) AddLegColliderChain(
            "left", controller.KneeLBone, controller.AnkleLBone, thighRadius, shinRadius,
            coordinateRoot, boneIndexes, rigidBodies);
        if (checkRight) AddLegColliderChain(
            "right", controller.KneeRBone, controller.AnkleRBone, thighRadius, shinRadius,
            coordinateRoot, boneIndexes, rigidBodies);
    }

    private static MMDRigidBody CreateSkirtAnchorBodyFromLayout(PMXSkirtLayoutExporter.Column column, Vector3 rotation)
    {
        string name = column.ColumnId + "_skirt_anchor";
        return new MMDRigidBody
        {
            Name = name, NameEn = name, AssociatedBoneIndex = column.AnchorBoneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(PMXPhysicsExporter.SkirtLegCollisionGroup),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeSphere,
            Dimemsions = new Vector3(Mathf.Max(PMXPhysicsExporter.MinimumRadius, column.AnchorRadius * 0.5f), 0, 0),
            Position = column.AnchorPosition, Rotation = rotation, Mass = 0,
            TranslateDamp = 1, RotateDamp = 1, Restitution = 0, Friction = 0,
            Type = MMDRigidBody.RigidBodyType.RigidTypeKinematic
        };
    }

    private static void AddSkirtLegCollidersEnhanced(SkirtController controller,
        IEnumerable<PMXPhysicsExporter.SkirtColumn> columns, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies,
        PMXMeshExportContext mesh)
    {
        AddPelvisColliderEnhanced(controller, coordinateRoot, boneIndexes, rigidBodies, mesh);
        bool checkLeft = columns.Any(column => column.IsCheckLeftLeg);
        bool checkRight = columns.Any(column => column.IsCheckRightLeg);
        float thighRadius = SanitizeSkirtColliderRadius(controller.KneeColliderRadius, DefaultThighColliderRadius);
        float shinRadius = SanitizeSkirtColliderRadius(controller.AnkleColliderRadius, DefaultShinColliderRadius);
        if (checkLeft) AddLegColliderChainEnhanced("left", controller.KneeLBone, controller.AnkleLBone,
            thighRadius, shinRadius, coordinateRoot, boneIndexes, rigidBodies, mesh);
        if (checkRight) AddLegColliderChainEnhanced("right", controller.KneeRBone, controller.AnkleRBone,
            thighRadius, shinRadius, coordinateRoot, boneIndexes, rigidBodies, mesh);
    }

    private static void AddLegColliderChainEnhanced(string side, Transform knee, Transform ankle,
        float thighRadius, float shinRadius, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies,
        PMXMeshExportContext mesh)
    {
        if (knee == null || !boneIndexes.ContainsKey(knee)) return;
        Transform thigh = PMXPhysicsExporter.FindNearestExportedParentTransform(knee.parent, boneIndexes);
        if (thigh != null) AddKinematicCapsuleEnhanced(side + "_thigh_skirt_collider", thigh, knee,
            thighRadius, coordinateRoot, boneIndexes, rigidBodies, mesh);
        if (ankle != null && boneIndexes.ContainsKey(ankle)) AddKinematicCapsuleEnhanced(
            side + "_shin_skirt_collider", knee, ankle, shinRadius, coordinateRoot, boneIndexes, rigidBodies, mesh);
    }

    private static void AddKinematicCapsuleEnhanced(string name, Transform startBone, Transform endBone,
        float fallbackRadius, Transform coordinateRoot, Dictionary<Transform, int> boneIndexes,
        List<MMDRigidBody> rigidBodies, PMXMeshExportContext mesh)
    {
        float radius = fallbackRadius;
        float axisLength = Vector3.Distance(coordinateRoot.InverseTransformPoint(startBone.position),
            coordinateRoot.InverseTransformPoint(endBone.position));
        if (PMXCollisionMeshFitter.TryFitCapsuleRadius(mesh, coordinateRoot, startBone, endBone, out float fitted))
            radius = SanitizeFittedRadius(fitted, axisLength, fallbackRadius);
        else Debug.LogWarning("PMX 裙摆碰撞体拟合回退，沿用原半径：" + name);
        AddKinematicCapsule(name, startBone, endBone, radius, coordinateRoot, boneIndexes, rigidBodies);
    }

    /// <summary>
    /// 为腰臀/骨盆区域创建物理碰撞体，托住裙摆上段与臀部，消除重力下拉造成的内陷。
    /// </summary>
    private static void AddPelvisCollider(SkirtController controller, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies)
    {
        // 查找骨盆/下半身关联骨骼
        Transform pelvisBone = controller.CenterBone;
        if (pelvisBone == null)
        {
            if (controller.KneeLBone != null && controller.KneeLBone.parent != null)
                pelvisBone = controller.KneeLBone.parent.parent;
            else if (controller.KneeRBone != null && controller.KneeRBone.parent != null)
                pelvisBone = controller.KneeRBone.parent.parent;
        }

        Transform exportedPelvisBone = PMXPhysicsExporter.FindNearestExportedParentTransform(pelvisBone, boneIndexes);
        if (exportedPelvisBone == null || !boneIndexes.TryGetValue(exportedPelvisBone, out int boneIndex))
            return;

        // 计算骨盆中心位置与尺寸：若有左右大腿骨则根据跨度动态确定，否则使用 Center 骨骼
        Vector3 pelvisPositionWorld;
        float pelvisRadius = DefaultPelvisColliderRadius;

        if (controller.KneeLBone != null && controller.KneeRBone != null)
        {
            Transform thighL = PMXPhysicsExporter.FindNearestExportedParentTransform(controller.KneeLBone.parent, boneIndexes);
            Transform thighR = PMXPhysicsExporter.FindNearestExportedParentTransform(controller.KneeRBone.parent, boneIndexes);
            if (thighL != null && thighR != null)
            {
                Vector3 thighCenter = (thighL.position + thighR.position) * 0.5f;
                float legSpan = Vector3.Distance(thighL.position, thighR.position);
                pelvisPositionWorld = thighCenter + Vector3.up * (legSpan * 0.15f);
                pelvisRadius = Mathf.Clamp(legSpan * 0.32f, 0.035f, 0.048f);
            }
            else
            {
                pelvisPositionWorld = exportedPelvisBone.position;
            }
        }
        else
        {
            pelvisPositionWorld = exportedPelvisBone.position;
        }

        Vector3 position = coordinateRoot.InverseTransformPoint(pelvisPositionWorld);
        Quaternion boneRot = Quaternion.Inverse(coordinateRoot.rotation) * exportedPelvisBone.rotation;
        Vector3 rotationEuler = PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(boneRot);

        rigidBodies.Add(new MMDRigidBody
        {
            Name = "pelvis_skirt_collider",
            NameEn = "pelvis_skirt_collider",
            AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtLegCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeSphere,
            Dimemsions = new Vector3(pelvisRadius, 0, 0),
            Position = position,
            Rotation = rotationEuler,
            Mass = 0,
            TranslateDamp = 1,
            RotateDamp = 1,
            Restitution = 0,
            Friction = 0.5f,
            Type = MMDRigidBody.RigidBodyType.RigidTypeKinematic
        });
    }

    private static void AddPelvisColliderEnhanced(SkirtController controller, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies, PMXMeshExportContext mesh)
    {
        Transform pelvis = controller.CenterBone;
        if (pelvis == null) pelvis = controller.KneeLBone != null ? controller.KneeLBone.parent?.parent : controller.KneeRBone?.parent?.parent;
        Transform exported = PMXPhysicsExporter.FindNearestExportedParentTransform(pelvis, boneIndexes);
        if (exported == null || !boneIndexes.TryGetValue(exported, out int boneIndex)) return;
        Transform left = controller.KneeLBone == null ? null : PMXPhysicsExporter.FindNearestExportedParentTransform(controller.KneeLBone.parent, boneIndexes);
        Transform right = controller.KneeRBone == null ? null : PMXPhysicsExporter.FindNearestExportedParentTransform(controller.KneeRBone.parent, boneIndexes);
        Vector3 center = exported.position;
        float radius = DefaultPelvisColliderRadius;
        if (left != null && right != null)
        {
            float span = Vector3.Distance(left.position, right.position);
            center = (left.position + right.position) * 0.5f + Vector3.up * (span * 0.15f);
            radius = Mathf.Clamp(span * 0.32f, 0.035f, 0.048f);
            Vector3 centerInRoot = coordinateRoot.InverseTransformPoint(center);
            if (PMXCollisionMeshFitter.TryFitPelvisRadiusAt(mesh, coordinateRoot, centerInRoot, exported, left, right, out float fitted))
                radius = SanitizePelvisFittedRadius(fitted, Vector3.Distance(
                    coordinateRoot.InverseTransformPoint(left.position), coordinateRoot.InverseTransformPoint(right.position)), radius);
            else Debug.LogWarning("PMX 裙摆骨盆碰撞体拟合回退，沿用原半径。");
        }
        rigidBodies.Add(new MMDRigidBody
        {
            Name = "pelvis_skirt_collider", NameEn = "pelvis_skirt_collider", AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtLegCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeSphere, Dimemsions = new Vector3(radius, 0, 0),
            Position = coordinateRoot.InverseTransformPoint(center),
            Rotation = PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(Quaternion.Inverse(coordinateRoot.rotation) * exported.rotation),
            Mass = 0, TranslateDamp = 1, RotateDamp = 1, Restitution = 0, Friction = 0.5f,
            Type = MMDRigidBody.RigidBodyType.RigidTypeKinematic
        });
    }

    private static float SanitizeFittedRadius(float fitted, float axisLength, float fallback)
    {
        if (float.IsNaN(fitted) || float.IsInfinity(fitted) || fitted <= 0) return fallback;
        return Mathf.Clamp(fitted, 0.005f, Mathf.Max(0.005f, axisLength * 0.48f));
    }

    private static float SanitizePelvisFittedRadius(float fitted, float hipSpan, float fallback)
    {
        if (float.IsNaN(fitted) || float.IsInfinity(fitted) || fitted <= 0) return fallback;
        return Mathf.Clamp(fitted, Mathf.Max(0.005f, hipSpan * 0.12f), Mathf.Max(0.01f, hipSpan * 0.8f));
    }

    private static void AddLegColliderChain(string side, Transform knee, Transform ankle,
        float thighRadius, float shinRadius, Transform coordinateRoot,
        Dictionary<Transform, int> boneIndexes, List<MMDRigidBody> rigidBodies)
    {
        if (knee == null || !boneIndexes.ContainsKey(knee)) return;
        Transform thigh = PMXPhysicsExporter.FindNearestExportedParentTransform(knee.parent, boneIndexes);
        if (thigh != null)
            AddKinematicCapsule(side + "_thigh_skirt_collider", thigh, knee, thighRadius,
                coordinateRoot, boneIndexes, rigidBodies);
        if (ankle != null && boneIndexes.ContainsKey(ankle))
            AddKinematicCapsule(side + "_shin_skirt_collider", knee, ankle,
                shinRadius, coordinateRoot, boneIndexes, rigidBodies);
    }

    private static void AddKinematicCapsule(string name, Transform startBone, Transform endBone,
        float radius, Transform coordinateRoot, Dictionary<Transform, int> boneIndexes,
        List<MMDRigidBody> rigidBodies)
    {
        if (startBone == null || endBone == null || !boneIndexes.TryGetValue(startBone, out int boneIndex)) return;
        Vector3 start = coordinateRoot.InverseTransformPoint(startBone.position);
        Vector3 end = coordinateRoot.InverseTransformPoint(endBone.position);
        float length = Vector3.Distance(start, end);
        if (length <= PMXPhysicsExporter.MinimumSegmentLength) return;

        // 计算腿部胶囊体旋转与中心
        Quaternion boneRot = Quaternion.Inverse(coordinateRoot.rotation) * startBone.rotation;
        Vector3 localDelta = startBone.InverseTransformPoint(endBone.position);
        Vector3 localDir = localDelta.sqrMagnitude > 0.000001f ? localDelta.normalized : Vector3.down;
        Quaternion capsuleRot = boneRot * Quaternion.FromToRotation(Vector3.up, localDir);

        rigidBodies.Add(new MMDRigidBody
        {
            Name = name,
            NameEn = name,
            AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtLegCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskAllowingAllExceptGroups(),
            Shape = MMDRigidBody.RigidBodyShape.RigidShapeCapsule,
            Dimemsions = new Vector3(radius, PMXPhysicsExporter.GetCapsuleCylinderLength(length, radius), 0),
            Position = (start + end) * 0.5f,
            Rotation = PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(capsuleRot),
            Mass = 0,
            TranslateDamp = 1,
            RotateDamp = 1,
            Restitution = 0,
            Friction = 0,
            Type = MMDRigidBody.RigidBodyType.RigidTypeKinematic
        });
    }

    private static float SanitizeSkirtColliderRadius(float radius, float fallback)
    {
        float value = PMXPhysicsExporter.IsFinite(radius) && radius > 0.01f ? radius : fallback;
        return Mathf.Clamp(value, 0.015f, 0.045f);
    }
}
