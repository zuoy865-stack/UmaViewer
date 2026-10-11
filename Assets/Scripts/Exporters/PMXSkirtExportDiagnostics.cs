using System;
using System.Linq;
using System.Runtime.CompilerServices;
using LibMMD.Model;
using Newtonsoft.Json.Linq;

/// <summary>随新文件保存实际裙摆布局；真实裙面与延长代理分开统计。</summary>
internal static class PMXSkirtExportDiagnostics
{
    private static readonly ConditionalWeakTable<RawMMDModel, JObject> Records = new ConditionalWeakTable<RawMMDModel, JObject>();

    internal static void Record(RawMMDModel model, PMXPhysicsExporter.Context context, PMXSkirtLayoutExporter.Result layout)
    {
        var columns = new JArray();
        if (layout != null)
            foreach (var column in layout.Columns)
            {
                columns.Add(new JObject
                {
                    ["sourceRoot"] = column.Source?.Chain?.Root?.name ?? "missing",
                    ["columnId"] = column.ColumnId,
                    ["isCircumferentialSubdivision"] = column.IsCircumferential,
                    ["neighborColumnIds"] = new JArray(column.NeighborColumnIds),
                    ["componentId"] = column.ComponentId,
                    ["realLengthModelUnits"] = column.RealLength,
                    ["achievedLengthModelUnits"] = column.AchievedLength,
                    ["targetSegmentLengthModelUnits"] = column.TargetSegmentLength,
                    ["maximumFitErrorModelUnits"] = column.MaximumFitError,
                    ["circumferentiallyClosed"] = column.RegionClosed,
                    ["segments"] = new JArray(column.Segments.Select(segment => new JObject
                    {
                        ["row"] = segment.Row, ["boneIndex"] = segment.BoneIndex,
                        ["sourceBoneName"] = segment.SourceBoneName,
                        ["originalStart"] = new JArray(segment.OriginalStart.x, segment.OriginalStart.y, segment.OriginalStart.z),
                        ["originalEnd"] = new JArray(segment.OriginalEnd.x, segment.OriginalEnd.y, segment.OriginalEnd.z),
                        ["name"] = segment.Name, ["extendsBeyondRealHem"] = segment.IsVirtual,
                        ["start"] = new JArray(segment.Start.x, segment.Start.y, segment.Start.z),
                        ["end"] = new JArray(segment.End.x, segment.End.y, segment.End.z)
                    }))
                });
            }
        var dynamic = (model.Rigidbodies ?? Array.Empty<MMDRigidBody>()).Where(body =>
            body.CollisionGroup == PMXPhysicsExporter.SkirtCollisionGroup && body.Type != MMDRigidBody.RigidBodyType.RigidTypeKinematic).ToArray();
        var record = new JObject
        {
            ["status"] = layout != null ? "enhanced" : context.SkirtColumns.Count > 0 ? "legacy-fallback" : "no-skirt",
            ["sourceColumns"] = context.SkirtColumns.Count,
            ["exportColumns"] = layout?.Columns.Count ?? 0,
            ["layoutMode"] = layout != null ? "original-segment-quartering" : "legacy",
            ["replacedBoneNames"] = new JArray(layout?.ReplacedBoneNames ?? new System.Collections.Generic.List<string>()),
            // 四影响上限只折叠裙份额，保存实际折叠量供外部检查。
            ["compressedVertices"] = layout?.CompressedVertexCount ?? 0,
            ["maximumMergedSkirtWeight"] = layout?.MaximumMergedSkirtWeight ?? 0f,
            ["totalMergedSkirtWeight"] = layout?.TotalMergedSkirtWeight ?? 0f,
            ["columns"] = columns,
            ["dynamicBodies"] = dynamic.Length,
            ["totalDynamicMass"] = dynamic.Sum(body => body.Mass),
            ["rawGroup0Based"] = PMXPhysicsExporter.SkirtCollisionGroup,
            ["uiGroup1Based"] = PMXPhysicsExporter.SkirtCollisionGroup + 1,
            ["horizontalJoints"] = model.Joints.Count(joint => joint.Name.Contains("skirt_horizontal")),
            ["fitErrorMeaning"] = "original bone endpoints and direction preserved; each original segment divided into four",
            ["adjacencyEvidence"] = "dominant skirt-weight boundaries on original mesh edges; sparse waist co-occurrence rejected",
            ["targetBulletVisualValidation"] = "not-run"
        };
        Records.Remove(model); Records.Add(model, record);
    }

    internal static JToken Get(RawMMDModel model) => model != null && Records.TryGetValue(model, out JObject record)
        ? record.DeepClone() : new JObject { ["status"] = "not-recorded" };
}
