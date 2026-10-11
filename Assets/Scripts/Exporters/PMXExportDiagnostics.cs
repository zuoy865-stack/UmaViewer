using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LibMMD.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor.Compilation;
using EditorCompiledAssembly = UnityEditor.Compilation.Assembly;
#endif
using ReflectionAssembly = System.Reflection.Assembly;

/// <summary>记录 PMX 文件、实际运行程序集与物理分类摘要。</summary>
public static class PMXExportDiagnostics
{
    private const string ManifestRelativePath = "Logs/teio-export-stage1-20261005/assembly-provenance.json";
    private const string RuntimeManifestName = "teio-export-assembly-provenance.json";

    /// <summary>诊断写入失败只发警告，不影响已完成的 PMX 导出。</summary>
    public static void Write(string path, RawMMDModel model, UmaContainer container, object meshDiagnostics)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("PMX 路径为空", nameof(path));
            JObject record = new JObject
            {
                ["recordedUtc"] = DateTime.UtcNow.ToString("o"),
                ["unityVersion"] = Application.unityVersion,
                ["applicationVersion"] = Application.version,
                ["pmxPath"] = Path.GetFullPath(path),
                ["pmxSha256"] = File.Exists(path) ? HashFile(path) : "missing",
                ["assembly"] = BuildAssemblyRecord(typeof(PMXExportDiagnostics).Assembly),
                ["character"] = BuildCharacterRecord(container),
                ["model"] = BuildModelRecord(model),
                ["meshDiagnostics"] = ToToken(meshDiagnostics)
            };
            record["sourceAssemblyAssociation"] = VerifySourceAssociation((JObject)record["assembly"]);
            record["physics"] = BuildPhysicsRecord(model);
            record["skirtLayout"] = PMXSkirtExportDiagnostics.Get(model);

            string outputPath = path + ".export.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            File.WriteAllText(outputPath, record.ToString(Formatting.Indented));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("PMX 已导出，但诊断记录写入失败：" + exception.Message);
        }
    }

    /// <summary>双向检查 PMX 的组掩码；每个刚体掩码位代表允许与对应 raw group 接触。</summary>
    public static bool IsCollisionPairAllowed(MMDRigidBody first, MMDRigidBody second)
    {
        if (first == null || second == null || first.CollisionGroup < 0 || first.CollisionGroup > 15 ||
            second.CollisionGroup < 0 || second.CollisionGroup > 15) return false;
        return (first.CollisionMask & (1 << second.CollisionGroup)) != 0 &&
               (second.CollisionMask & (1 << first.CollisionGroup)) != 0;
    }

    /// <summary>仅在源码快照匹配且运行程序集 MVID 与文件哈希均匹配时返回 verified。</summary>
    public static string EvaluateSourceAssociation(string manifestJson, string assemblyName, string mvid, string sha256)
    {
        try
        {
            JObject manifest = JObject.Parse(manifestJson ?? string.Empty);
            JArray assemblies = manifest["assemblies"] as JArray;
            if (assemblies == null) return "unknown-manifest-missing";
            if (!Guid.TryParse(mvid, out _)) return "unknown-runtime-mvid";
            JObject[] matches = assemblies.OfType<JObject>().Where(item =>
                string.Equals((string)item["assemblyName"], assemblyName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) return "unknown-assembly-missing";
            if (matches.Any(item => string.Equals((string)item["sourceSnapshotStatus"], "matched", StringComparison.Ordinal) &&
                                    string.Equals((string)item["mvid"], mvid, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals((string)item["sha256"], sha256, StringComparison.OrdinalIgnoreCase)))
                return "verified";
            if (!matches.Any(item => string.Equals((string)item["sourceSnapshotStatus"], "matched", StringComparison.Ordinal)))
                return "stale-source-snapshot";
            return "stale-runtime-assembly";
        }
        catch
        {
            return "unknown-manifest-invalid";
        }
    }

    /// <summary>源码路径集合或任一文件内容变化均视为旧快照。</summary>
    public static string EvaluateSourceInventory(string compiledSourcesJson, string currentSourcesJson)
    {
        try
        {
            JArray compiled = JArray.Parse(compiledSourcesJson ?? "[]");
            JArray current = JArray.Parse(currentSourcesJson ?? "[]");
            return string.Equals(HashSources(compiled), HashSources(current), StringComparison.Ordinal) ? "matched" : "stale";
        }
        catch { return "unknown-source-inventory"; }
    }

    private static JObject BuildAssemblyRecord(ReflectionAssembly assembly)
    {
        string path = SafeAssemblyPath(assembly);
        string hash = !string.IsNullOrEmpty(path) && File.Exists(path) ? HashFile(path) : "missing";
        string mvid;
        try { mvid = assembly.ManifestModule.ModuleVersionId.ToString("D"); }
        catch { mvid = "missing"; }
        return new JObject
        {
            ["name"] = assembly.GetName().Name ?? "missing",
            ["path"] = string.IsNullOrEmpty(path) ? "missing" : path,
            ["sha256"] = hash,
            ["mvid"] = mvid
        };
    }

    private static JToken VerifySourceAssociation(JObject runtimeAssembly)
    {
        try
        {
            string manifestPath = FindManifestPath();
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return new JObject { ["status"] = "unknown-manifest-missing", ["manifestPath"] = "missing", ["sourceHash"] = "missing" };
            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
            string assemblyName = (string)runtimeAssembly["name"];
            JArray assemblies = manifest["assemblies"] as JArray;
            JObject[] matches = (assemblies ?? new JArray()).OfType<JObject>().Where(item =>
                string.Equals((string)item["assemblyName"], assemblyName, StringComparison.OrdinalIgnoreCase)).ToArray();
            JObject match = matches.FirstOrDefault(item =>
                string.Equals((string)item["mvid"], (string)runtimeAssembly["mvid"], StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)item["sha256"], (string)runtimeAssembly["sha256"], StringComparison.OrdinalIgnoreCase)) ??
                matches.FirstOrDefault();
            string status = EvaluateSourceAssociation(manifest.ToString(Formatting.None), assemblyName,
                (string)runtimeAssembly["mvid"], (string)runtimeAssembly["sha256"]);
            string currentSourcesStatus = "not-available-in-player";
#if UNITY_EDITOR
            if (status == "verified")
            {
                currentSourcesStatus = ValidateCurrentEditorSources(match, (string)runtimeAssembly["path"]);
                if (currentSourcesStatus != "matched")
                    status = currentSourcesStatus == "stale" ? "stale-current-source-snapshot" : "unknown-current-source-list";
            }
#endif
            return new JObject
            {
                ["status"] = status,
                ["manifestPath"] = manifestPath,
                ["sourceHashBefore"] = match == null ? "missing" : (string)match["sourceHashBefore"] ?? "missing",
                ["sourceHashAfter"] = match == null ? "missing" : (string)match["sourceHashAfter"] ?? "missing",
                ["sourceSnapshotStatus"] = match == null ? "missing" : (string)match["sourceSnapshotStatus"] ?? "missing",
                ["compileStatus"] = match == null ? "missing" : (string)match["compileStatus"] ?? "missing",
                ["currentSourcesStatus"] = currentSourcesStatus,
                ["gitSnapshot"] = manifest["gitSnapshot"]?.DeepClone() ?? new JObject { ["status"] = "unknown" }
            };
        }
        catch (Exception exception)
        {
            return new JObject { ["status"] = "unknown-manifest-unreadable", ["sourceHash"] = "missing", ["detail"] = exception.Message };
        }
    }

    private static string FindManifestPath()
    {
        string playerManifest = Path.Combine(Application.streamingAssetsPath, RuntimeManifestName);
        if (File.Exists(playerManifest)) return playerManifest;
        if (!Application.isEditor) return null;
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", ManifestRelativePath));
    }

#if UNITY_EDITOR
    private static string ValidateCurrentEditorSources(JObject manifestAssembly, string runningAssemblyPath)
    {
        if (manifestAssembly == null || string.IsNullOrEmpty(runningAssemblyPath)) return "unknown";
        string assemblyName = (string)manifestAssembly["assemblyName"];
        string normalizedPath;
        try { normalizedPath = Path.GetFullPath(runningAssemblyPath).Replace('\\', '/'); }
        catch { return "unknown"; }
        EditorCompiledAssembly current = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
            .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player)).FirstOrDefault(item =>
                string.Equals(item.name, assemblyName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFullPath(item.outputPath).Replace('\\', '/'), normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (current == null) return "unknown";
        JArray currentSources = new JArray();
        foreach (string source in current.sourceFiles ?? Array.Empty<string>())
        {
            string path = Path.GetFullPath(source);
            currentSources.Add(new JObject
            {
                ["path"] = path,
                ["sha256"] = File.Exists(path) ? HashFile(path) : "missing"
            });
        }
        JArray compiledSources = manifestAssembly["sourcesAfter"] as JArray ?? manifestAssembly["sources"] as JArray;
        return EvaluateSourceInventory(compiledSources.ToString(Formatting.None), currentSources.ToString(Formatting.None));
    }
#endif

    private static string HashSources(JArray sources)
    {
        string joined = string.Join("\n", (sources ?? new JArray()).OfType<JObject>()
            .OrderBy(source => (string)source["path"], StringComparer.OrdinalIgnoreCase)
            .Select(source => Path.GetFullPath((string)source["path"] ?? "missing").Replace('\\', '/') + "=" +
                              ((string)source["sha256"] ?? "missing")));
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined)))
                .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static JObject BuildCharacterRecord(UmaContainer container)
    {
        JObject result = new JObject
        {
            ["containerName"] = container != null ? container.gameObject.name : "missing",
            ["characterName"] = "missing",
            ["characterId"] = "missing",
            ["costume"] = new JObject
            {
                ["shortId"] = "missing",
                ["longId"] = "missing",
                ["skin"] = "missing",
                ["height"] = "missing",
                ["socks"] = "missing",
                ["bust"] = "missing"
            }
        };
        UmaContainerCharacter character = container as UmaContainerCharacter;
        if (character == null) return result;
        if (character.CharaEntry != null)
        {
            result["characterName"] = string.IsNullOrEmpty(character.CharaEntry.Name) ? "missing" : character.CharaEntry.Name;
            result["characterId"] = character.CharaEntry.Id;
        }
        JObject costume = (JObject)result["costume"];
        AddIdentifier(costume, "shortId", character.VarCostumeIdShort);
        AddIdentifier(costume, "longId", character.VarCostumeIdLong);
        AddIdentifier(costume, "skin", character.VarSkin);
        AddIdentifier(costume, "height", character.VarHeight);
        AddIdentifier(costume, "socks", character.VarSocks);
        AddIdentifier(costume, "bust", character.VarBust);
        return result;
    }

    private static void AddIdentifier(JObject target, string key, string value)
    {
        target[key] = string.IsNullOrWhiteSpace(value) ? "missing" : value;
    }

    private static JObject BuildModelRecord(RawMMDModel model)
    {
        return new JObject
        {
            ["name"] = model == null || string.IsNullOrEmpty(model.Name) ? "missing" : model.Name,
            ["vertices"] = model?.Vertices?.Length ?? 0,
            ["triangles"] = (model?.TriangleIndexes?.Length ?? 0) / 3,
            ["bones"] = model?.Bones?.Length ?? 0,
            ["materials"] = model?.Parts?.Length ?? 0,
            ["morphs"] = model?.Morphs?.Length ?? 0,
            ["displayFrames"] = model?.Entrys?.Count ?? 0,
            ["rigidBodies"] = model?.Rigidbodies?.Length ?? 0,
            ["joints"] = model?.Joints?.Length ?? 0
        };
    }

    private static JObject BuildPhysicsRecord(RawMMDModel model)
    {
        var bodies = model?.Rigidbodies ?? Array.Empty<MMDRigidBody>();
        JArray details = new JArray();
        foreach (MMDRigidBody body in bodies)
        {
            if (body == null) continue;
            details.Add(new JObject
            {
                ["name"] = body.Name ?? "missing",
                ["sourceCategory"] = "unknown",
                ["boneIndex"] = body.AssociatedBoneIndex,
                ["rawGroup0Based"] = body.CollisionGroup,
                ["uiGroup1Based"] = body.CollisionGroup + 1,
                ["mode"] = (byte)body.Type,
                ["modeName"] = body.Type.ToString(),
                ["maskRaw"] = body.CollisionMask,
                ["maskHex"] = "0x" + body.CollisionMask.ToString("X4")
            });
        }
        int allowed = 0;
        int checkedPairs = 0;
        JArray allowedPairs = new JArray();
        for (int i = 0; i < bodies.Length; i++)
        for (int j = i + 1; j < bodies.Length; j++)
        {
            if (bodies[i] == null || bodies[j] == null) continue;
            checkedPairs++;
            if (!IsCollisionPairAllowed(bodies[i], bodies[j])) continue;
            allowed++;
            allowedPairs.Add(new JObject { ["first"] = bodies[i].Name ?? "missing", ["second"] = bodies[j].Name ?? "missing" });
        }
        return new JObject
        {
            ["groupNumbering"] = "raw=0-based, UI=1-based",
            ["maskRule"] = "pair requires both masks to contain the other raw group bit",
            ["checkedPairs"] = checkedPairs,
            ["qualifiedPairCount"] = allowed,
            ["qualifiedPairs"] = allowedPairs,
            ["bodies"] = details
        };
    }

    private static JToken ToToken(object value)
    {
        if (value == null) return JValue.CreateNull();
        try { return value as JToken ?? JToken.FromObject(value); }
        catch (Exception exception) { return new JObject { ["serializationWarning"] = exception.Message }; }
    }

    private static string SafeAssemblyPath(ReflectionAssembly assembly)
    {
        try { return assembly.Location; }
        catch { return null; }
    }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }
}
