using System;
using System.IO;
using System.Security.Cryptography;
using LibMMD.Model;
using Newtonsoft.Json.Linq;

/// <summary>独立验证导出诊断的哈希、程序集标识与来源关联边界。</summary>
public static class PMXExportDiagnosticsRegression
{
    /// <summary>失败时抛异常，可由 Editor 或批处理入口单独调用。</summary>
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "UmaViewer-PMXDiagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pmxPath = Path.Combine(root, "probe.pmx");
        try
        {
            File.WriteAllBytes(pmxPath, new byte[] { 0x50, 0x4D, 0x58, 0x00, 0x01 });
            MMDRigidBody hair = NewBody("hair", 3, 0x0030);
            MMDRigidBody tail = NewBody("tail", 3, 0x0034);
            var model = new RawMMDModel
            {
                Name = "probe",
                Vertices = Array.Empty<Vertex>(),
                Bones = Array.Empty<Bone>(),
                Parts = Array.Empty<Part>(),
                Rigidbodies = new[] { hair, tail },
                Joints = Array.Empty<MMDJoint>()
            };

            Require(!PMXExportDiagnostics.IsCollisionPairAllowed(hair, tail),
                "raw group 3 hair 0x0030 / tail 0x0034 must fail the two-way same-group bit check");
            PMXExportDiagnostics.Write(pmxPath, model, null, new[] { new { renderer = "probe", submeshes = 1 } });
            string recordPath = pmxPath + ".export.json";
            Require(File.Exists(recordPath), "diagnostic JSON was not written");
            JObject record = JObject.Parse(File.ReadAllText(recordPath));
            Require((string)record["pmxSha256"] == HashFile(pmxPath), "PMX hash does not match output bytes");
            Require((string)record["assembly"]["name"] == typeof(PMXExportDiagnostics).Assembly.GetName().Name,
                "runtime assembly name was not recorded");
            Require((string)record["assembly"]["mvid"] == typeof(PMXExportDiagnostics).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                "runtime assembly MVID was not recorded");
            Require((string)record["physics"]["bodies"][0]["maskHex"] == "0x0030", "raw collision mask was not recorded");
            Require((string)record["sourceAssemblyAssociation"]["status"] != "verified" ||
                    !string.IsNullOrEmpty((string)record["sourceAssemblyAssociation"]["manifestPath"]),
                "verified association has no manifest evidence path");

            Require(PMXExportDiagnostics.EvaluateSourceAssociation("{}", "Assembly-CSharp", "mvid", "hash") == "unknown-manifest-missing",
                "a missing manifest was incorrectly accepted");
            const string validMvid = "11111111-1111-1111-1111-111111111111";
            const string otherMvid = "22222222-2222-2222-2222-222222222222";
            string staleSource = "{\"assemblies\":[{\"assemblyName\":\"Assembly-CSharp\",\"sourceSnapshotStatus\":\"stale\",\"mvid\":\"" + validMvid + "\",\"sha256\":\"hash\"}]}";
            Require(PMXExportDiagnostics.EvaluateSourceAssociation(staleSource, "Assembly-CSharp", validMvid, "hash") == "stale-source-snapshot",
                "a stale source snapshot was incorrectly accepted");
            string staleAssembly = "{\"assemblies\":[{\"assemblyName\":\"Assembly-CSharp\",\"sourceSnapshotStatus\":\"matched\",\"mvid\":\"" + validMvid + "\",\"sha256\":\"hash\"}]}";
            Require(PMXExportDiagnostics.EvaluateSourceAssociation(staleAssembly, "Assembly-CSharp", otherMvid, "hash") == "stale-runtime-assembly",
                "a stale runtime assembly was incorrectly accepted");
            string sourceA = "[{\"path\":\"Assets/A.cs\",\"sha256\":\"a\"}]";
            Require(PMXExportDiagnostics.EvaluateSourceInventory(sourceA, sourceA) == "matched",
                "unchanged source inventory was rejected");
            Require(PMXExportDiagnostics.EvaluateSourceInventory(sourceA, "[{\"path\":\"Assets/A.cs\",\"sha256\":\"b\"}]") == "stale",
                "changed source contents were accepted");
            Require(PMXExportDiagnostics.EvaluateSourceInventory(sourceA,
                    "[{\"path\":\"Assets/A.cs\",\"sha256\":\"a\"},{\"path\":\"Assets/B.cs\",\"sha256\":\"b\"}]") == "stale",
                "an added source file was accepted");
            Require(PMXExportDiagnostics.EvaluateSourceInventory(
                    "[{\"path\":\"Assets/A.cs\",\"sha256\":\"a\"},{\"path\":\"Assets/B.cs\",\"sha256\":\"b\"}]", sourceA) == "stale",
                "a deleted source file was accepted");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static MMDRigidBody NewBody(string name, int group, ushort mask) => new MMDRigidBody
    {
        Name = name,
        CollisionGroup = group,
        CollisionMask = mask,
        Type = MMDRigidBody.RigidBodyType.RigidTypePhysics
    };

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
