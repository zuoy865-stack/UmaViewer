using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Gallop;
using UnityEngine;

/// <summary>附件链分类、连续性与正权重门槛。</summary>
public static class PMXAttachmentChainRegression
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void RunFixtures()
    {
        Assembly runtime = typeof(UmaContainerCharacter).Assembly;
        Type collector = runtime.GetType("PMXPhysicsChainCollector", true);
        Type exporter = runtime.GetType("PMXPhysicsExporter", true);
        Type candidateType = collector.GetNestedType("Candidate", BindingFlags.NonPublic);
        Type contextType = exporter.GetNestedType("Context", BindingFlags.NonPublic);
        Type chainType = exporter.GetNestedType("Chain", BindingFlags.NonPublic);

        CheckCategory(collector, "胸部父骨不会污染飘带", "Chest", "Sp_Kn_Ribbon0_L_00", "Ribbon");
        CheckCategory(collector, "胸部参数骨分类", "Position", "Sp_Ch_Bust0_L_00", "Bust");
        CheckCategory(collector, "披风参数骨分类", "Chest", "Sp_Ch_Mantle1_BL_00", "Cape");
        CheckUnknown(collector, "Sp_ClothSleeve_MSkirt_leaf_00");
        CheckAmbiguousName(collector, candidateType);
        CheckParameterWeightGate(collector, candidateType, contextType, chainType);
        CheckBrokenChain(collector, candidateType, chainType);
    }

    private static void CheckCategory(Type collector, string label, string parentName, string boneName,
        string expected)
    {
        GameObject parentObject = new GameObject(parentName);
        try
        {
            Transform bone = Child(parentObject.transform, boneName);
            object result = Invoke(collector.GetMethod("Classify", StaticAny), null,
                new List<string> { boneName }, bone);
            Assert(result.ToString() == expected, label + " 分类错误: " + result);
        }
        finally { UnityEngine.Object.DestroyImmediate(parentObject); }
    }

    private static void CheckUnknown(Type collector, string boneName)
    {
        var root = new GameObject("PMXUnknownAttachment");
        try
        {
            Transform bone = Child(root.transform, boneName);
            object result = Invoke(collector.GetMethod("Classify", StaticAny), null,
                new List<string> { boneName }, bone);
            Assert(result.ToString() == "None", "未证明的服饰链被自动归类。");
            bool loggedAsUnknown = (bool)Invoke(collector.GetMethod("LooksLikeUnclassifiedAttachment", StaticPrivate),
                null, new List<string> { boneName }, bone);
            Assert(loggedAsUnknown, "未分类服饰链没有进入限制诊断。");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void CheckAmbiguousName(Type collector, Type candidateType)
    {
        var root = new GameObject("PMXAmbiguousRoot");
        try
        {
            Transform first = Child(root.transform, "Sp_Kn_Ribbon0_L_00");
            Child(root.transform, first.name);
            UmaContainerCharacter character = root.AddComponent<UmaContainerCharacter>();
            CySpringDataContainer data = root.AddComponent<CySpringDataContainer>();
            character.cySpringDataContainers = new List<CySpringDataContainer> { data };
            data.springParam.Add(Source(first.name, "Sp_Kn_Ribbon0_L_01"));
            IList candidates = (IList)Invoke(collector.GetMethod("CollectCandidates", StaticAny), null,
                character, root.GetComponentsInChildren<Transform>(true), new HashSet<Transform>());
            Assert(candidates.Count == 0, "同名根骨未被拒绝消歧。");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void CheckParameterWeightGate(Type collector, Type candidateType, Type contextType,
        Type chainType)
    {
        var root = new GameObject("PMXSourceParameterRoot");
        try
        {
            Transform chainRoot = Child(root.transform, "Sp_Kn_Ribbon0_L_00");
            Transform child = Child(chainRoot, "Sp_Kn_Ribbon0_L_01");
            UmaContainerCharacter character = root.AddComponent<UmaContainerCharacter>();
            CySpringDataContainer data = root.AddComponent<CySpringDataContainer>();
            character.cySpringDataContainers = new List<CySpringDataContainer> { data };
            data.springParam.Add(Source(chainRoot.name, child.name));
            IList candidates = (IList)Invoke(collector.GetMethod("CollectCandidates", StaticAny), null,
                character, root.GetComponentsInChildren<Transform>(true), new HashSet<Transform>());
            Assert(candidates.Count == 1, "源 CySpring 参数链未收集。");
            object candidate = candidates[0];
            Assert(((List<string>)GetField(candidateType, candidate, "Sources")).Count == 2,
                "源 CySpring 根/子参数未完整保留。");

            object weightedMesh = CreateMeshSnapshot(child, 1f);
            object weightedContext = CreateContext(contextType, candidateType, candidate);
            Invoke(collector.GetMethod("ResolveAttachments", StaticAny), null, weightedContext, weightedMesh);
            IList chains = (IList)GetField(contextType, weightedContext, "Chains");
            Assert(chains.Count == 1, "有源参数和正权重的附件链未生成。");
            Assert(((HashSet<Transform>)GetField(chainType, chains[0], "Bones")).Count == 2,
                "附件参数链骨段没有连续保留。");

            object unweightedMesh = CreateMeshSnapshot(child, 0f);
            object unweightedContext = CreateContext(contextType, candidateType, candidate);
            Invoke(collector.GetMethod("ResolveAttachments", StaticAny), null, unweightedContext, unweightedMesh);
            Assert(((IList)GetField(contextType, unweightedContext, "Chains")).Count == 0,
                "无正蒙皮权重的附件链不应生成。");
            ((IDisposable)weightedMesh).Dispose();
            ((IDisposable)unweightedMesh).Dispose();
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static void CheckBrokenChain(Type collector, Type candidateType, Type chainType)
    {
        var root = new GameObject("Sp_Ch_Bust0_L_00");
        try
        {
            Transform gap = Child(root.transform, "unconfigured_bridge");
            Transform end = Child(gap, "Sp_Ch_Bust0_L_01");
            object candidate = Activator.CreateInstance(candidateType, true);
            SetField(candidateType, candidate, "Root", root.transform);
            var radii = (Dictionary<Transform, float>)GetField(candidateType, candidate, "Radii");
            radii.Add(root.transform, 0.02f);
            radii.Add(end, 0.01f);
            object chain = Activator.CreateInstance(chainType, true);
            Invoke(collector.GetMethod("CollectContinuous", StaticPrivate), null,
                root.transform, radii, chain, new HashSet<Transform>());
            bool complete = (bool)Invoke(collector.GetMethod("HasCompleteChain", StaticAny), null, candidate, chain);
            Assert(!complete, "跳过未配置父节点的断链被误判为完整链。");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static CySpringParamDataElement Source(string rootName, string childName)
    {
        var element = new CySpringParamDataElement
        {
            _boneName = rootName,
            _stiffnessForce = 0.5f,
            _dragForce = 0.2f,
            _collisionRadius = 0.02f
        };
        element._childElements.Add(new CySpringParamDataChildElement
        {
            _boneName = childName,
            _stiffnessForce = 0.4f,
            _dragForce = 0.3f,
            _collisionRadius = 0.01f
        });
        return element;
    }

    private static object CreateContext(Type contextType, Type candidateType, object candidate)
    {
        object context = Activator.CreateInstance(contextType, true);
        ((IList)GetField(contextType, context, "AttachmentCandidates")).Add(candidate);
        return context;
    }

    private static PMXMeshExportContext CreateMeshSnapshot(Transform weightedBone, float weight)
    {
        var snapshot = (PMXMeshExportContext)Activator.CreateInstance(typeof(PMXMeshExportContext), InstanceAny,
            null, new object[] { weightedBone.root }, null);
        var item = new PMXMeshExportContext.Item();
        SetField(typeof(PMXMeshExportContext.Item), item, "Bones", new[] { weightedBone });
        SetField(typeof(PMXMeshExportContext.Item), item, "Weights",
            new[] { new BoneWeight { boneIndex0 = 0, weight0 = weight } });
        snapshot.Items.Add(item);
        return snapshot;
    }

    private static Transform Child(Transform parent, string name)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }

    private static object GetField(Type type, object target, string name) =>
        type.GetField(name, InstanceAny).GetValue(target);

    private static void SetField(Type type, object target, string name, object value) =>
        type.GetField(name, InstanceAny).SetValue(target, value);

    private static object Invoke(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
