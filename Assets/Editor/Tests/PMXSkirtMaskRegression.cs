using System;
using System.Collections.Generic;
using System.Reflection;
using Gallop;
using LibMMD.Model;
using UnityEngine;

/// <summary>裙摆与身体碰撞掩码回归断言。</summary>
public static class PMXSkirtMaskRegression
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags AnyInstanceField = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void AssertExportedMasks()
    {
        GameObject rootObject = new GameObject("PMXSkirtMaskRegressionRoot");
        try
        {
            Transform root = rootObject.transform;
            Transform center = Child(root, "center", Vector3.zero);
            Transform thigh = Child(center, "thigh_L", new Vector3(0.1f, -0.1f, 0f));
            Transform knee = Child(thigh, "knee_L", new Vector3(0f, -0.4f, 0f));
            Transform ankle = Child(knee, "ankle_L", new Vector3(0f, -0.4f, 0f));
            Transform skirtRoot = Child(root, "skirt_root", Vector3.right);
            SkirtController controller = rootObject.AddComponent<SkirtController>();
            SetField(typeof(SkirtController), controller, "_center", center);
            SetField(typeof(SkirtController), controller, "_kneeL", knee);
            SetField(typeof(SkirtController), controller, "_ankleL", ankle);

            Assembly runtimeAssembly = typeof(SkirtController).Assembly;
            Type skirtExporter = runtimeAssembly.GetType("PMXSkirtPhysicsExporter", true);
            Type physicsExporter = runtimeAssembly.GetType("PMXPhysicsExporter", true);
            Type chainType = physicsExporter.GetNestedType("Chain", BindingFlags.NonPublic);
            Type columnType = physicsExporter.GetNestedType("SkirtColumn", BindingFlags.NonPublic);

            MMDRigidBody anchor = BuildAnchor(skirtExporter, chainType, columnType, skirtRoot, root);
            AssertMask(anchor, 0xFFDF, "裙摆静态锚点");
            AssertNear(anchor.Dimemsions.x, 0.01f, "裙摆锚点半径");

            MMDRigidBody panel = BuildPanel(skirtExporter, skirtRoot);
            AssertMask(panel, 0xFFFF, "裙摆动态片");
            AssertNear(panel.Mass, 0.7f, "裙摆根段质量");
            AssertNear(panel.Dimemsions.y, 0.23f, "裙摆盒体高度");

            var boneIndexes = new Dictionary<Transform, int>
            {
                [center] = 0,
                [thigh] = 1,
                [knee] = 2,
                [ankle] = 3
            };
            Array columns = Array.CreateInstance(columnType, 1);
            object column = Activator.CreateInstance(columnType, true);
            SetField(columnType, column, "IsCheckLeftLeg", true);
            columns.SetValue(column, 0);
            var bodies = new List<MMDRigidBody>();
            MethodInfo addColliders = GetMethod(skirtExporter, "AddSkirtLegColliders");
            Invoke(addColliders, null, controller, columns, root, boneIndexes, bodies);

            AssertMask(FindBody(bodies, "pelvis_skirt_collider"), 0xFFFF, "骨盆碰撞体");
            AssertMask(FindBody(bodies, "left_thigh_skirt_collider"), 0xFFFF, "大腿碰撞体");
            AssertMask(FindBody(bodies, "left_shin_skirt_collider"), 0xFFFF, "小腿碰撞体");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(rootObject);
        }
    }

    private static MMDRigidBody BuildAnchor(Type exporter, Type chainType, Type columnType,
        Transform skirtRoot, Transform coordinateRoot)
    {
        object chain = Activator.CreateInstance(chainType, true);
        SetField(chainType, chain, "Root", skirtRoot);
        var radii = (Dictionary<Transform, float>)GetField(chainType, chain, "Radii");
        radii.Add(skirtRoot, 0.02f);

        object column = Activator.CreateInstance(columnType, true);
        SetField(columnType, column, "Chain", chain);
        return (MMDRigidBody)Invoke(GetMethod(exporter, "CreateSkirtAnchorBody"), null,
            column, coordinateRoot, 1, Vector3.zero);
    }

    private static MMDRigidBody BuildPanel(Type exporter, Transform bone)
    {
        Type segmentType = exporter.GetNestedType("SkirtSegment", BindingFlags.NonPublic);
        object segment = Activator.CreateInstance(segmentType, true);
        SetField(segmentType, segment, "Bone", bone);
        SetField(segmentType, segment, "Length", 0.5f);
        SetField(segmentType, segment, "HalfWidth", 0.02f);
        SetField(segmentType, segment, "HalfThickness", 0.005f);
        SetField(segmentType, segment, "Center", Vector3.zero);
        SetField(segmentType, segment, "Rotation", Vector3.zero);
        return (MMDRigidBody)Invoke(GetMethod(exporter, "CreateSkirtPanelBody"), null,
            segment, 1, 0f);
    }

    private static Transform Child(Transform parent, string name, Vector3 localPosition)
    {
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        child.localPosition = localPosition;
        return child;
    }

    private static MethodInfo GetMethod(Type type, string name)
    {
        MethodInfo method = type.GetMethod(name, PrivateStatic);
        if (method == null) throw new InvalidOperationException("找不到导出器方法: " + name);
        return method;
    }

    private static object Invoke(MethodInfo method, object target, params object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
    }

    private static object GetField(Type type, object instance, string name)
    {
        FieldInfo field = type.GetField(name, AnyInstanceField);
        if (field == null) throw new InvalidOperationException("找不到测试字段: " + name);
        return field.GetValue(instance);
    }

    private static void SetField(Type type, object instance, string name, object value)
    {
        FieldInfo field = type.GetField(name, AnyInstanceField);
        if (field == null) throw new InvalidOperationException("找不到测试字段: " + name);
        field.SetValue(instance, value);
    }

    private static MMDRigidBody FindBody(List<MMDRigidBody> bodies, string name)
    {
        foreach (MMDRigidBody body in bodies)
            if (body.Name == name) return body;
        throw new InvalidOperationException("导出器没有生成刚体: " + name);
    }

    private static void AssertMask(MMDRigidBody body, ushort expected, string label)
    {
        if (body == null || body.CollisionMask != expected)
            throw new InvalidOperationException(label + "掩码错误，预期 0x" + expected.ToString("X4") +
                "，实际 0x" + (body != null ? body.CollisionMask.ToString("X4") : "null"));
    }

    private static void AssertNear(float actual, float expected, string label)
    {
        if (Mathf.Abs(actual - expected) > 0.0001f)
            throw new InvalidOperationException(label + "错误，预期 " + expected + "，实际 " + actual);
    }
}
