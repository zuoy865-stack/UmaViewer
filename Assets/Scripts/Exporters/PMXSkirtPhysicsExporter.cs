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
    private const float SkirtMaximumPanelHalfWidth = 0.05f;
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
        Dictionary<Transform, int> dynamicRigidIndexes)
    {
        if (context.SkirtController == null || context.SkirtColumns.Count < 3) return;

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

        // 少于三列或任意列没有有效首段时无法建立稳定的裙摆网格
        if (segmentsByColumn.Count < 3 || segmentsByColumn.Any(segments => segments.Count == 0)) return;

        bool closeRing = IsClosedSkirtRing(columns, context.SkirtController, coordinateRoot);
        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            List<SkirtSegment> segments = segmentsByColumn[columnIndex];
            for (int rowIndex = 0; rowIndex < segments.Count; rowIndex++)
            {
                SkirtSegment segment = segments[rowIndex];
                Vector3 tangent = CalculateSkirtTangent(segmentsByColumn, columnIndex, rowIndex, closeRing);
                segment.HalfWidth = EstimateSkirtPanelHalfWidth(
                    segmentsByColumn, columnIndex, rowIndex, closeRing);
                float configuredRadius = columns[columnIndex].Chain.Radii[segment.Bone];
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
                int boneIndex = boneResult.BoneIndexes[segment.Bone];
                segment.RigidIndex = rigidBodies.Count;
                rigidBodies.Add(CreateSkirtPanelBody(segment, boneIndex, depthRatio));
                dynamicRigidIndexes[segment.Bone] = segment.RigidIndex;
                joints.Add(CreateSkirtVerticalJoint(segment, parentRigidIndex, depthRatio));
                parentRigidIndex = segment.RigidIndex;
            }
        }

        AddSkirtLegColliders(
            context.SkirtController, columns, coordinateRoot, boneResult.BoneIndexes, rigidBodies);
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
        int columnIndex, int rowIndex, bool closeRing)
    {
        SkirtSegment current = columns[columnIndex][rowIndex];
        SkirtSegment previous = GetNeighborSkirtSegment(columns, columnIndex - 1, rowIndex, closeRing);
        SkirtSegment next = GetNeighborSkirtSegment(columns, columnIndex + 1, rowIndex, closeRing);
        float spacing = 0f;
        int sampleCount = 0;
        if (previous != null)
        {
            spacing += Vector3.Distance(current.Center, previous.Center);
            sampleCount++;
        }
        if (next != null)
        {
            spacing += Vector3.Distance(current.Center, next.Center);
            sampleCount++;
        }
        if (sampleCount == 0) return SkirtMinimumPanelHalfWidth;
        // 采用 0.38f 系数留出微小周向间隙，防止相邻裙片刚体互相挤压与抖动
        return Mathf.Clamp(spacing / sampleCount * 0.38f,
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
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskOnlyCollideWith(
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

    private static MMDRigidBody CreateSkirtPanelBody(SkirtSegment segment, int boneIndex,
        float depthRatio)
    {
        return new MMDRigidBody
        {
            Name = segment.Bone.name + "_skirt_physics",
            NameEn = segment.Bone.name + "_skirt_physics",
            AssociatedBoneIndex = boneIndex,
            CollisionGroup = PMXPhysicsExporter.SkirtCollisionGroup,
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskOnlyCollideWith(
                PMXPhysicsExporter.SkirtLegCollisionGroup),
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
            Name = segment.Bone.name + "_skirt_vertical_joint",
            NameEn = segment.Bone.name + "_skirt_vertical_joint",
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
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskOnlyCollideWith(
                PMXPhysicsExporter.SkirtCollisionGroup),
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
            CollisionMask = PMXPhysicsExporter.CreateCollisionMaskOnlyCollideWith(
                PMXPhysicsExporter.SkirtCollisionGroup),
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
