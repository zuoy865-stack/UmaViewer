using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LibMMD.Material;
using LibMMD.Model;
using LibMMD.Reader;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>对帝王两套服装及另一角色运行真实双次 PMX 材质导出。</summary>
[InitializeOnLoad]
public static class PMXMaterialRealModelValidation
{
    private const string Key = "PMXMaterialRealModelValidation";
    private const string DefaultOutput = "Logs/pmx-pseudo-highlight-plan-20261006/multi-model";
    private static string Root => Path.GetFullPath(GetArgument("-pmxMaterialValidationOutput") ?? DefaultOutput);
    private static string ReportPath => Path.Combine(Root, "report.json");

    static PMXMaterialRealModelValidation()
    {
        EditorApplication.playModeStateChanged += OnPlayMode;
        EditorApplication.update += Poll;
    }

    public static void RunBatch()
    {
        if (SessionState.GetBool(Key + ".active", false)) throw new InvalidOperationException("验证 runner 已在运行。");
        string[] runDirs = { "teio-1003-00", "teio-1003-alt", "other-character" };
        if (Directory.Exists(Root) || File.Exists(ReportPath) || runDirs.SelectMany(name => new[] { name + "/run1", name + "/run2" })
                .Any(name => Directory.Exists(Path.Combine(Root, name))))
            throw new IOException("指定验证输出已存在；为保护旧结果，本次停止：" + Root);

        string configPath = Path.GetFullPath(Config.configPath);
        SessionState.SetBool(Key + ".configExisted", File.Exists(configPath));
        SessionState.SetString(Key + ".configPath", configPath);
        if (File.Exists(configPath)) SessionState.SetString(Key + ".config", Convert.ToBase64String(File.ReadAllBytes(configPath)));
        Config config = Config.Instance;
        SessionState.SetBool(Key + ".download", config.DownloadMissingResources);
        try
        {
            config.DownloadMissingResources = false;
            config.UpdateConfig(false);
        }
        catch
        {
            RestoreConfig();
            throw;
        }
        SessionState.SetString(Key + ".started", DateTime.UtcNow.ToString("o"));
        SessionState.SetBool(Key + ".active", true);
        SessionState.SetString(Key + ".phase", "entering-play");
        try
        {
            Directory.CreateDirectory(Root);
            Save(new JObject { ["status"] = "running", ["outputRoot"] = Root, ["startedUtc"] = DateTime.UtcNow.ToString("o") });
            EditorSceneManager.OpenScene("Assets/Scenes/Version2.unity");
            EditorApplication.isPlaying = true;
        }
        catch (Exception e) { CompleteFailure(e); }
    }

    private static void OnPlayMode(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || SessionState.GetString(Key + ".phase", "") != "entering-play") return;
        UmaViewerMain main = UmaViewerMain.Instance;
        if (main == null) { CompleteFailure(new InvalidOperationException("Version2 场景没有 UmaViewerMain。")); return; }
        SessionState.SetString(Key + ".phase", "running");
        main.StartCoroutine(Run(main));
    }

    private static IEnumerator Run(UmaViewerMain main)
    {
        JObject report = new JObject
        {
            ["status"] = "running", ["unityVersion"] = Application.unityVersion,
            ["applicationVersion"] = Application.version, ["outputRoot"] = Root,
            ["models"] = new JArray(), ["candidates"] = new JArray()
        };
        IEnumerator workflow = RunWorkflow(main, report);
        Exception failure = null;
        while (true)
        {
            object current = null;
            bool moved = false;
            try
            {
                moved = workflow.MoveNext();
                if (moved) current = workflow.Current;
            }
            catch (Exception e) { failure = e; }
            if (failure != null || !moved) break;
            yield return current;
        }
        if (failure != null)
        {
            report["status"] = "failed";
            report["error"] = failure.ToString();
        }
        else if ((string)report["status"] == "running")
        {
            bool failed = ((JArray)report["models"]).OfType<JObject>().Any(m => (string)m["status"] == "failed");
            bool incomplete = ((JArray)report["models"]).OfType<JObject>().Any(m => (string)m["status"] != "passed");
            report["status"] = failed ? "failed" : incomplete ? "partial" : "passed";
        }
        report["finishedUtc"] = DateTime.UtcNow.ToString("o");
        Save(report);
        Config.Instance.DownloadMissingResources = SessionState.GetBool(Key + ".download", true);
    }

    private static IEnumerator RunWorkflow(UmaViewerMain main, JObject report)
    {
        Config config = Config.Instance;
        config.DownloadMissingResources = false;
        float start = Time.realtimeSinceStartup;
        while (!Ready(main))
        {
            if (Time.realtimeSinceStartup - start > 180) throw new TimeoutException("等待 Main.AbList、Characters、UI Shader 列表就绪超时。");
            yield return null;
        }

        List<Candidate> candidates = FindCandidates(main, (JArray)report["candidates"]);
        Candidate teio00 = candidates.FirstOrDefault(c => c.Id == 1003 && c.Costume == "00");
        string requestedCostume = GetArgument("-pmxMaterialValidationCostume");
        Candidate teioAlt = string.IsNullOrEmpty(requestedCostume)
            ? candidates.Where(c => c.Id == 1003 && c.Costume != "00").OrderBy(c => c.Costume, StringComparer.Ordinal).FirstOrDefault()
            : candidates.FirstOrDefault(c => c.Id == 1003 && c.Costume == requestedCostume);
        report["requestedTeioCostume"] = requestedCostume ?? "auto-first-local";
        Candidate other = candidates.Where(c => c.Id != 1003 && c.Character != null)
            .OrderBy(c => c.Costume == "00" ? 0 : 1).ThenBy(c => c.Id).ThenBy(c => c.Costume, StringComparer.Ordinal).FirstOrDefault();
        var selected = new[] { Tuple.Create("teio-1003-00", teio00), Tuple.Create("teio-1003-alt", teioAlt), Tuple.Create("other-character", other) };
        foreach (var item in selected)
        {
            JObject model = new JObject { ["role"] = item.Item1 };
            ((JArray)report["models"]).Add(model);
            if (item.Item2 == null)
            {
                model["status"] = "skipped";
                model["reason"] = "未找到角色表匹配且本地文件存在的身体资源候选。";
                Save(report);
                continue;
            }
            model["candidate"] = item.Item2.ToJson();
            IEnumerator candidateRun = ExportCandidate(main, item.Item2, item.Item1, model);
            Exception candidateFailure = null;
            while (true)
            {
                object current = null;
                bool moved = false;
                try
                {
                    moved = candidateRun.MoveNext();
                    if (moved) current = candidateRun.Current;
                }
                catch (Exception e) { candidateFailure = e; }
                if (candidateFailure != null || !moved) break;
                yield return current;
            }
            if (candidateFailure != null)
            {
                model["status"] = "failed";
                model["error"] = candidateFailure.ToString();
            }
            if (main != null && UmaViewerBuilder.Instance != null) UmaViewerBuilder.Instance.UnloadUma();
            Save(report);
            yield return null;
        }
    }

    private static IEnumerator ExportCandidate(UmaViewerMain main, Candidate candidate, string role, JObject record)
    {
        UmaViewerBuilder builder = UmaViewerBuilder.Instance;
        if (builder == null) throw new InvalidOperationException("找不到 UmaViewerBuilder。");
        builder.UnloadUma();
        yield return null;
        Coroutine load = main.StartCoroutine(builder.LoadUma(candidate.Character, candidate.Costume, false));
        float started = Time.realtimeSinceStartup;
        while (builder.CurrentUMAContainer == null || builder.CurrentUMAContainer.CharaEntry == null ||
               builder.CurrentUMAContainer.CharaEntry.Id != candidate.Id ||
               builder.CurrentUMAContainer.GetComponentsInChildren<Renderer>(true).Length == 0)
        {
            if (Time.realtimeSinceStartup - started > 120) throw new TimeoutException("LoadUma 等待超时：" + candidate.Key);
            yield return null;
        }
        if (load != null) yield return load;
        yield return null;
        UmaContainerCharacter container = builder.CurrentUMAContainer;
        if (container == null || container.CharaEntry.Id != candidate.Id || container.GetComponentsInChildren<Renderer>(true).Length == 0)
            throw new InvalidOperationException("LoadUma 未生成目标角色和 Renderer：" + candidate.Key);

        string baseDir = Path.Combine(Root, role);
        string dir1 = Path.Combine(baseDir, "run1"), dir2 = Path.Combine(baseDir, "run2");
        if (Directory.Exists(dir1) || Directory.Exists(dir2)) throw new IOException("导出目录已存在，停止保护旧文件：" + baseDir);
        JObject materialsBefore = CaptureMaterials(container);
        string sourceHashBefore = HashFile(candidate.Path);
        ModelExporter.ExportModel(container, Path.Combine(dir1, "model.pmx"));
        JObject afterFirst = CaptureMaterials(container);
        if (!JToken.DeepEquals(materialsBefore, afterFirst)) throw new InvalidDataException("第一次导出后源材质状态变化。");
        ModelExporter.ExportModel(container, Path.Combine(dir2, "model.pmx"));
        JObject materialsAfter = CaptureMaterials(container);
        if (!JToken.DeepEquals(materialsBefore, materialsAfter)) throw new InvalidDataException("第二次导出后源材质状态变化。");
        string sourceHashAfter = HashFile(candidate.Path);
        if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.Ordinal))
            throw new InvalidDataException("重复导出期间本地源 bundle 内容发生变化。");

        JObject first = ReadExport(Path.Combine(dir1, "model.pmx"));
        JObject second = ReadExport(Path.Combine(dir2, "model.pmx"));
        record["diagnosticMaterialStatusesRun1"] = ReadMaterialStatusEvidence(first);
        record["diagnosticMaterialStatusesRun2"] = ReadMaterialStatusEvidence(second);
        record["exportPaths"] = new JArray(Path.Combine(dir1, "model.pmx"), Path.Combine(dir2, "model.pmx"));
        JObject summary1 = (JObject)first["model"], summary2 = (JObject)second["model"];
        string pmxHash1 = (string)first["pmxSha256"], pmxHash2 = (string)second["pmxSha256"];
        if (string.IsNullOrEmpty(pmxHash1) || !string.Equals(pmxHash1, pmxHash2, StringComparison.Ordinal))
            throw new InvalidDataException("两次导出的 PMX 文件哈希不一致。");
        string[] fields = { "vertices", "triangles", "bones", "morphs", "materials", "rigidBodies", "joints" };
        foreach (string field in fields)
            if (!JToken.DeepEquals(summary1[field], summary2[field])) throw new InvalidDataException("两次导出的结构字段不一致：" + field);
        JArray bindings1 = ReadFinalTextureBindings(Path.Combine(dir1, "model.pmx"));
        JArray bindings2 = ReadFinalTextureBindings(Path.Combine(dir2, "model.pmx"));
        JArray baked1 = ReadMaterialBakes(first, bindings1), baked2 = ReadMaterialBakes(second, bindings2);
        record["status"] = "validating";
        record["sourceMaterialsUnchanged"] = true;
        record["sourceBundleSha256"] = sourceHashBefore;
        record["structure"] = summary1.DeepClone();
        record["materialPngs"] = baked1;
        record["finalTextureBindings"] = bindings1;
        bool repeatMaterials = JToken.DeepEquals(baked1, baked2);
        bool repeatBindings = JToken.DeepEquals(BindingSignature(bindings1), BindingSignature(bindings2));
        record["materialPngHashesRepeat"] = repeatMaterials;
        record["finalTextureBindingsRepeat"] = repeatBindings;
        record["pmxSha256"] = pmxHash1;
        if (!repeatMaterials || !repeatBindings)
            throw new InvalidDataException("两次导出的材质状态、PMX 最终贴图绑定或文件哈希不一致。");
        string[] unsupported = baked1.OfType<JObject>()
            .Where(x => (string)x["status"] == "failed" || (string)x["status"] == "unsupported")
            .Select(x => (string)x["material"] + " [" + (string)x["status"] + "]: " + (string)x["reason"]).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidDataException("存在未完成材质，已保留逐材质证据：" + string.Join(" | ", unsupported));
        if (!baked1.OfType<JObject>().Any(x => (string)x["status"] == "baked"))
            throw new InvalidDataException("该样本没有任何非眼材质完成烘焙。");
        JObject[] invalidBindings = bindings1.OfType<JObject>().Where(x => (bool)x["hasTexture"] && !(bool)x["fileExists"]).ToArray();
        if (invalidBindings.Length > 0)
            throw new InvalidDataException("PMX Reader 回读后存在缺失的最终贴图文件：" +
                string.Join(", ", invalidBindings.Select(x => (string)x["material"] + "=" + (string)x["texturePath"])));
        JObject[] invalidRows = baked1.OfType<JObject>().Where(x => !(bool)x["finalBindingValid"] &&
            ((string)x["status"] == "baked" || (string)x["status"] == "baked-eye" ||
             (string)x["status"] == "no-applicable-layer")).ToArray();
        if (invalidRows.Length > 0)
            throw new InvalidDataException("材质烘焙或 no-applicable 回退没有绑定到存在的最终贴图。");
        record["status"] = "passed";
    }

    private static JObject ReadExport(string pmxPath)
    {
        if (!File.Exists(pmxPath)) throw new FileNotFoundException("PMX 未生成。", pmxPath);
        string diagnostics = pmxPath + ".export.json";
        if (!File.Exists(diagnostics)) throw new FileNotFoundException("PMX 导出诊断未生成。", diagnostics);
        return JObject.Parse(File.ReadAllText(diagnostics));
    }

    private static JArray BindingSignature(JArray bindings)
    {
        // run1/run2 的绝对目录不同，比较 PMX 相对引用与真实文件内容。
        var result = (JArray)bindings.DeepClone();
        foreach (JObject row in result.OfType<JObject>()) row.Remove("resolvedPath");
        return result;
    }

    private static JArray ReadFinalTextureBindings(string pmxPath)
    {
        RawMMDModel model = new PMXReader().Read(pmxPath, new ModelConfig { GlobalToonPath = "Toon" });
        string directory = Path.GetDirectoryName(Path.GetFullPath(pmxPath));
        var rows = new JArray();
        for (int i = 0; i < (model.Parts ?? Array.Empty<Part>()).Length; i++)
        {
            MMDMaterial material = model.Parts[i]?.Material;
            string path = material?.Texture?.TexturePath;
            string fullPath = string.IsNullOrEmpty(path) ? null : ResolveTexturePath(directory, path);
            bool exists = fullPath != null && File.Exists(fullPath);
            rows.Add(new JObject
            {
                ["part"] = i, ["material"] = material?.Name ?? "missing",
                ["texturePath"] = path, ["hasTexture"] = !string.IsNullOrEmpty(path),
                ["resolvedPath"] = fullPath, ["fileExists"] = exists,
                ["pngBytes"] = exists ? new FileInfo(fullPath).Length : 0,
                ["pngSha256"] = exists ? HashFile(fullPath) : null
            });
        }
        return rows;
    }

    private static JArray ReadMaterialBakes(JObject export, JArray finalBindings)
    {
        var output = new JArray();
        foreach (JObject renderer in (export["meshDiagnostics"] as JArray ?? new JArray()).OfType<JObject>())
        foreach (JObject submesh in (renderer["submeshes"] as JArray ?? new JArray()).OfType<JObject>())
        {
            JObject bake = submesh["textureBake"] as JObject ?? new JObject();
            string path = (string)bake["TexturePath"] ?? (string)bake["texturePath"];
            string status = (string)bake["Status"] ?? (string)bake["status"] ?? "missing";
            JObject[] materialBindings = finalBindings.OfType<JObject>().Where(x =>
                string.Equals((string)x["material"], (string)submesh["materialName"], StringComparison.Ordinal)).ToArray();
            JObject binding = materialBindings.FirstOrDefault(x => (bool)x["hasTexture"] && (bool)x["fileExists"] &&
                ((status != "baked" && status != "baked-eye") || string.Equals(path, (string)x["texturePath"], StringComparison.Ordinal)))
                ?? materialBindings.FirstOrDefault();
            bool validBinding = binding != null && (bool)binding["hasTexture"] && (bool)binding["fileExists"];
            if ((status == "baked" || status == "baked-eye") &&
                !string.Equals(path, (string)binding?["texturePath"], StringComparison.Ordinal)) validBinding = false;
            if (status == "no-applicable-layer") validBinding = validBinding && !string.IsNullOrEmpty((string)binding["pngSha256"]);
            output.Add(new JObject
            {
                ["renderer"] = (string)renderer["rendererPath"], ["material"] = (string)submesh["materialName"],
                ["sourceShader"] = (string)submesh["shader"], ["status"] = status,
                ["reason"] = (string)bake["Reason"] ?? (string)bake["reason"], ["texturePath"] = path,
                ["textureBakePngSha256"] = FindBindingHash(finalBindings, path),
                ["finalBindingValid"] = validBinding,
                ["finalBoundTexturePath"] = (string)binding?["texturePath"],
                ["finalBoundPngSha256"] = (string)binding?["pngSha256"]
            });
        }
        return new JArray(output.OrderBy(x => (string)x["renderer"], StringComparer.Ordinal)
            .ThenBy(x => (string)x["material"], StringComparer.Ordinal));
    }

    private static JArray ReadMaterialStatusEvidence(JObject export)
    {
        var rows = new JArray();
        foreach (JObject renderer in (export["meshDiagnostics"] as JArray ?? new JArray()).OfType<JObject>())
        foreach (JObject submesh in (renderer["submeshes"] as JArray ?? new JArray()).OfType<JObject>())
        {
            JObject bake = submesh["textureBake"] as JObject ?? new JObject();
            rows.Add(new JObject
            {
                ["renderer"] = (string)renderer["rendererPath"], ["material"] = (string)submesh["materialName"],
                ["sourceShader"] = (string)submesh["shader"],
                ["status"] = (string)bake["Status"] ?? (string)bake["status"] ?? "missing",
                ["reason"] = (string)bake["Reason"] ?? (string)bake["reason"],
                ["texturePath"] = (string)bake["TexturePath"] ?? (string)bake["texturePath"]
            });
        }
        return rows;
    }

    private static string FindBindingHash(JArray bindings, string path)
    {
        JObject binding = bindings.OfType<JObject>().FirstOrDefault(x =>
            string.Equals((string)x["texturePath"], path, StringComparison.Ordinal));
        return (string)binding?["pngSha256"];
    }

    private static string ResolveTexturePath(string directory, string texturePath)
    {
        string normalized = texturePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized));
    }

    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static JObject CaptureMaterials(UmaContainerCharacter container)
    {
        var rows = new JArray();
        foreach (Renderer renderer in container.GetComponentsInChildren<Renderer>(true).OrderBy(r => r.name, StringComparer.Ordinal))
        {
            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                rows.Add(new JObject
                {
                    ["renderer"] = renderer.name, ["slot"] = i,
                    ["material"] = material == null ? "missing" : material.name,
                    ["shader"] = material == null || material.shader == null ? "missing" : material.shader.name,
                    ["mainTexture"] = material == null || material.mainTexture == null ? "missing" : material.mainTexture.name,
                    ["keywords"] = material == null ? new JArray() : new JArray(material.shaderKeywords.OrderBy(x => x, StringComparer.Ordinal)),
                    ["serializedStateSha256"] = material == null ? "missing" : HashText(EditorJsonUtility.ToJson(material, true))
                });
            }
        }
        return new JObject { ["materials"] = rows };
    }

    private static List<Candidate> FindCandidates(UmaViewerMain main, JArray report)
    {
        var regex = new System.Text.RegularExpressions.Regex("^3d/chara/body/bdy(\\d+)_(\\d+)/pfb_bdy\\1_\\2$");
        var output = new List<Candidate>();
        foreach (var pair in main.AbList.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var match = regex.Match(pair.Key);
            if (!match.Success) continue;
            int id = int.Parse(match.Groups[1].Value);
            string costume = match.Groups[2].Value;
            CharaEntry character = main.Characters.FirstOrDefault(c => c.Id == id);
            bool local = pair.Value != null && File.Exists(pair.Value.Path);
            string reason = character == null ? "角色不在 Main.Characters。" : !local ? "身体 bundle 不在本地。" : "eligible";
            report.Add(new JObject { ["id"] = id, ["costume"] = costume, ["key"] = pair.Key,
                ["path"] = pair.Value == null ? null : pair.Value.Path, ["localFileExists"] = local,
                ["inCharacters"] = character != null, ["reason"] = reason });
            if (character != null && local) output.Add(new Candidate { Id = id, Costume = costume, Key = pair.Key, Path = pair.Value.Path, Character = character });
        }
        return output;
    }

    private static bool Ready(UmaViewerMain main) => main != null && main.AbList != null && main.AbList.Count > 0 &&
        main.Characters != null && main.Characters.Count > 0 && UmaViewerUI.Instance != null &&
        UmaViewerUI.Instance.ModelSettings != null && UmaViewerBuilder.Instance != null &&
        UmaViewerBuilder.Instance.ShaderList != null && UmaViewerBuilder.Instance.ShaderList.Count > 0;

    private sealed class Candidate
    {
        internal int Id; internal string Costume, Key, Path; internal CharaEntry Character;
        internal JObject ToJson() => new JObject { ["id"] = Id, ["costume"] = Costume, ["key"] = Key,
            ["path"] = Path, ["characterName"] = Character.Name };
    }

    private static void Poll()
    {
        if (!SessionState.GetBool(Key + ".active", false)) return;
        if (DateTime.TryParse(SessionState.GetString(Key + ".started", ""), out DateTime start) &&
            DateTime.UtcNow - start.ToUniversalTime() > TimeSpan.FromMinutes(9)) CompleteFailure(new TimeoutException("多模型验证总超时。"));
        if (!File.Exists(ReportPath)) return;
        string status;
        try { status = (string)JObject.Parse(File.ReadAllText(ReportPath))["status"]; }
        catch { return; }
        if (status != "passed" && status != "partial" && status != "failed") return;
        RestoreConfig();
        SessionState.SetBool(Key + ".active", false);
        SessionState.EraseString(Key + ".phase");
        EditorApplication.Exit(status == "passed" ? 0 : 1);
    }

    private static void CompleteFailure(Exception exception)
    {
        try
        {
            JObject report = File.Exists(ReportPath) ? JObject.Parse(File.ReadAllText(ReportPath)) : new JObject { ["outputRoot"] = Root };
            report["status"] = "failed";
            report["error"] = exception.ToString();
            report["finishedUtc"] = DateTime.UtcNow.ToString("o");
            Save(report);
        }
        catch (Exception writeError) { Debug.LogException(writeError); }
        RestoreConfig();
        SessionState.SetBool(Key + ".active", false);
        Debug.LogException(exception);
        EditorApplication.Exit(1);
    }

    private static void RestoreConfig()
    {
        string path = SessionState.GetString(Key + ".configPath", "");
        if (string.IsNullOrEmpty(path)) return;
        if (SessionState.GetBool(Key + ".configExisted", false))
            File.WriteAllBytes(path, Convert.FromBase64String(SessionState.GetString(Key + ".config", "")));
        else if (File.Exists(path)) File.Delete(path);
        SessionState.EraseString(Key + ".config");
        SessionState.EraseString(Key + ".download");
    }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string HashText(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", "").ToLowerInvariant();
    }

    private static void Save(JObject report)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ReportPath, report.ToString(Formatting.Indented));
    }
}
