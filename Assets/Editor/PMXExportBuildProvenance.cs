using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;
using CompiledAssembly = UnityEditor.Compilation.Assembly;
using ReflectionAssembly = System.Reflection.Assembly;

/// <summary>用编译前后清单关联源码快照与实际 PMX 导出程序集。</summary>
[InitializeOnLoad]
public static class PMXExportBuildProvenance
{
    private const string ManifestRelativePath = "Logs/teio-export-stage1-20261005/assembly-provenance.json";
    private const string PendingBuildFile = "Logs/teio-export-stage1-20261005/build-source-pending.json";
    private const string RuntimeManifestName = "teio-export-assembly-provenance.json";

    static PMXExportBuildProvenance()
    {
        CompilationPipeline.compilationStarted += OnCompilationStarted;
        CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        CompilationPipeline.compilationFinished += OnCompilationFinished;
    }

    /// <summary>由验证 runner 在脚本加载后调用，确保本轮编译事件已订阅。</summary>
    public static void RequestVerificationCompilation()
    {
        if (EditorApplication.isCompiling) throw new InvalidOperationException("Unity 正在编译，暂不能重复请求。");
        CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
    }

    [MenuItem("UmaViewer/PMX Export/Request Provenance Compilation")]
    private static void RequestVerificationCompilationFromMenu() => RequestVerificationCompilation();

    private static void OnCompilationStarted(object context)
    {
        try
        {
            CompiledAssembly[] editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
            CompiledAssembly[] playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            JObject manifest = CreateSnapshot(editorAssemblies, playerAssemblies);
            JArray items = manifest["assemblies"] as JArray;
            DeduplicateSnapshots(items);
            manifest["compilationContext"] = IsPlayerBuildActive() ? "Player" : "Editor";
            manifest["phase"] = "compiling";
            manifest["gitSnapshot"] = CaptureGitSnapshot();
            WriteManifest(manifest);
        }
        catch (Exception exception) { DebugWarning("编译前源码快照失败", exception); }
    }

    private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
    {
        try
        {
            JObject manifest = ReadManifest();
            JArray assemblies = manifest["assemblies"] as JArray;
            if (assemblies == null) assemblies = new JArray();
            string assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
            string preferredKind = (string)manifest["compilationContext"] ?? "Editor";
            JObject[] candidates = assemblies.OfType<JObject>().Where(value =>
                    string.Equals((string)value["assemblyName"], assemblyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((string)value["assemblyKind"], preferredKind, StringComparison.Ordinal))
                .GroupBy(value => HashSources(value["sources"] as JArray), StringComparer.Ordinal)
                .Select(group => group.First()).ToArray();
            JObject item = candidates.Length == 1 ? candidates[0] : null;

            bool hasErrors = messages != null && messages.Any(message => message.type == CompilerMessageType.Error);
            JObject eventRecord = new JObject
            {
                ["assemblyName"] = assemblyName,
                ["eventPath"] = NormalizePath(assemblyPath),
                ["eventPathExists"] = File.Exists(assemblyPath),
                ["eventMvid"] = File.Exists(assemblyPath) ? ReadMvid(assemblyPath) : "missing",
                ["eventSha256"] = File.Exists(assemblyPath) ? HashFile(assemblyPath) : "missing",
                ["preferredSourceKind"] = preferredKind,
                ["sourceMatchCount"] = candidates.Length,
                ["compileStatus"] = hasErrors ? "failed" : "succeeded",
                ["compiledAtUtc"] = DateTime.UtcNow.ToString("o")
            };
            if (item == null)
            {
                eventRecord["sourceSnapshotStatus"] = candidates.Length == 0
                    ? "unknown-event-assembly-unmatched" : "unknown-event-source-ambiguous";
                AddCompilationEvent(manifest, eventRecord);
                WriteManifest(manifest);
                DebugWarning("程序集编译事件没有唯一源码快照，路径=" + NormalizePath(assemblyPath));
                return;
            }

            eventRecord["sourceSnapshotOutputPath"] = (string)item["outputPath"] ?? "missing";
            item["eventPath"] = NormalizePath(assemblyPath);
            item["compileStatus"] = hasErrors ? "failed" : "succeeded";
            item["compiledAtUtc"] = (string)eventRecord["compiledAtUtc"];
            if (!hasErrors && File.Exists(assemblyPath))
            {
                JArray currentSources = CaptureCurrentSources(item);
                if (currentSources == null)
                {
                    item["sourceHashAfter"] = "missing";
                    item["sourceSnapshotStatus"] = "unknown-current-source-list";
                    item["sourcesAfter"] = new JArray();
                }
                else
                {
                    string sourceHashAfter = HashSources(currentSources);
                    item["sourceHashAfter"] = sourceHashAfter;
                    item["sourceSnapshotStatus"] = string.Equals((string)item["sourceHashBefore"], sourceHashAfter, StringComparison.Ordinal)
                        ? "matched" : "stale";
                    item["sourcesAfter"] = currentSources;
                }
                string artifactPath = assemblyPath;
                item["mvid"] = ReadMvid(artifactPath);
                item["sha256"] = HashFile(artifactPath);
                item["assemblyPath"] = Path.GetFullPath(artifactPath);
                eventRecord["artifactPath"] = NormalizePath(artifactPath);
                eventRecord["artifactMvid"] = item["mvid"];
                eventRecord["artifactSha256"] = item["sha256"];
                eventRecord["sourceSnapshotStatus"] = item["sourceSnapshotStatus"];
            }
            else
            {
                item["sourceHashAfter"] = "missing";
                item["sourceSnapshotStatus"] = "unknown-compile-failed";
                item["mvid"] = "missing";
                item["sha256"] = "missing";
                eventRecord["sourceSnapshotStatus"] = "unknown-compile-failed";
            }
            AddCompilationEvent(manifest, eventRecord);
            WriteManifest(manifest);
        }
        catch (Exception exception) { DebugWarning("编译后程序集关联失败", exception); }
    }

    private static void OnCompilationFinished(object context)
    {
        try
        {
            JObject manifest = ReadManifest();
            manifest["phase"] = "compilation-finished";
            manifest["finishedAtUtc"] = DateTime.UtcNow.ToString("o");
            foreach (JObject item in ((JArray)manifest["assemblies"] ?? new JArray()).OfType<JObject>())
            {
                if (!string.Equals((string)item["compileStatus"], "succeeded", StringComparison.Ordinal) ||
                    !string.Equals((string)item["sourceSnapshotStatus"], "matched", StringComparison.Ordinal)) continue;
                string eventPath = (string)item["eventPath"];
                string outputPath = (string)item["outputPath"];
                string eventMvid = File.Exists(eventPath) ? ReadMvid(eventPath) : "missing";
                string eventHash = File.Exists(eventPath) ? HashFile(eventPath) : "missing";
                if (File.Exists(outputPath) && string.Equals(ReadMvid(outputPath), eventMvid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(HashFile(outputPath), eventHash, StringComparison.OrdinalIgnoreCase))
                    item["assemblyPath"] = Path.GetFullPath(outputPath);
                else if (File.Exists(eventPath)) item["assemblyPath"] = Path.GetFullPath(eventPath);
                item["mvid"] = eventMvid;
                item["sha256"] = eventHash;
            }
            JArray events = manifest["assemblyCompilationEvents"] as JArray ?? new JArray();
            manifest["unmatchedEventCount"] = events.OfType<JObject>().Count(item =>
                ((string)item["sourceSnapshotStatus"] ?? "").StartsWith("unknown-event-", StringComparison.Ordinal));
            WriteManifest(manifest);
        }
        catch (Exception exception) { DebugWarning("编译清单收尾失败", exception); }
    }

    private static JObject CreateSnapshot(CompiledAssembly[] editorAssemblies, CompiledAssembly[] playerAssemblies)
    {
        JArray items = new JArray();
        AppendSnapshotItems(items, editorAssemblies, "Editor");
        AppendSnapshotItems(items, playerAssemblies, "Player");
        return new JObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "editor-compilation",
            ["startedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["phase"] = "snapshot-created",
            ["assemblies"] = items
        };
    }

    private static void AppendSnapshotItems(JArray items, CompiledAssembly[] assemblies, string assemblyKind)
    {
        foreach (CompiledAssembly assembly in assemblies ?? Array.Empty<CompiledAssembly>())
        {
            JArray sources = new JArray();
            foreach (string source in assembly.sourceFiles ?? Array.Empty<string>())
            {
                string fullPath = Path.GetFullPath(source);
                sources.Add(new JObject
                {
                    ["path"] = fullPath,
                    ["sha256"] = File.Exists(fullPath) ? HashFile(fullPath) : "missing"
                });
            }
            items.Add(new JObject
            {
                ["assemblyName"] = assembly.name,
                ["assemblyKind"] = assemblyKind,
                ["outputPath"] = NormalizePath(assembly.outputPath),
                ["sourceHashBefore"] = HashSources(sources),
                ["sourceHashAfter"] = "pending",
                ["sourceSnapshotStatus"] = "pending",
                ["compileStatus"] = "pending",
                ["mvid"] = "missing",
                ["sha256"] = "missing",
                ["sources"] = sources
            });
        }
    }

    private static void DeduplicateSnapshots(JArray items)
    {
        if (items == null) return;
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            JObject item = items[i] as JObject;
            if (item == null) continue;
            string key = (string)item["assemblyKind"] + "|" + (string)item["assemblyName"] + "|" +
                         NormalizePath((string)item["outputPath"]) + "|" + HashSources(item["sources"] as JArray);
            if (!seen.Add(key)) items.RemoveAt(i);
        }
    }

    private static JObject ReadManifest()
    {
        string path = ProjectPath(ManifestRelativePath);
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject { ["assemblies"] = new JArray() };
    }

    private static void WriteManifest(JObject manifest)
    {
        string path = ProjectPath(ManifestRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, manifest.ToString(Formatting.Indented));
    }

    private static string ProjectPath(string relativePath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath));

    private static string HashSources(JArray sources)
    {
        string joined = string.Join("\n", (sources ?? new JArray()).OfType<JObject>()
            .OrderBy(source => (string)source["path"], StringComparer.OrdinalIgnoreCase)
            .Select(source => ((string)source["path"] ?? "missing") + "=" + ((string)source["sha256"] ?? "missing")));
        return HashText(joined);
    }

    private static JArray CaptureCurrentSources(JObject item)
    {
        AssembliesType type = string.Equals((string)item["assemblyKind"], "Player", StringComparison.Ordinal)
            ? AssembliesType.Player : AssembliesType.Editor;
        CompiledAssembly[] named = CompilationPipeline.GetAssemblies(type).Where(candidate =>
            string.Equals(candidate.name, (string)item["assemblyName"], StringComparison.OrdinalIgnoreCase)).ToArray();
        CompiledAssembly exact = named.FirstOrDefault(candidate =>
            string.Equals(NormalizePath(candidate.outputPath), NormalizePath((string)item["outputPath"]), StringComparison.OrdinalIgnoreCase));
        JArray[] sourceSets = named.Select(candidate => CaptureSources(candidate.sourceFiles)).ToArray();
        JArray[] uniqueSourceSets = sourceSets.GroupBy(HashSources, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        if (exact != null) return CaptureSources(exact.sourceFiles);
        if (uniqueSourceSets.Length != 1) return null;
        return uniqueSourceSets[0];
    }

    private static JArray CaptureSources(string[] sourceFiles)
    {
        JArray sources = new JArray();
        foreach (string source in sourceFiles ?? Array.Empty<string>())
        {
            string fullPath = Path.GetFullPath(source);
            sources.Add(new JObject
            {
                ["path"] = fullPath,
                ["sha256"] = File.Exists(fullPath) ? HashFile(fullPath) : "missing"
            });
        }
        return sources;
    }

    private static bool IsPlayerBuildActive()
    {
        string path = ProjectPath(PendingBuildFile);
        if (!File.Exists(path)) return false;
        try
        {
            JObject pending = JObject.Parse(File.ReadAllText(path));
            DateTime start = ParseUtc((string)pending["buildStartedAtUtc"]);
            return start != DateTime.MinValue && DateTime.UtcNow - start < TimeSpan.FromMinutes(30) &&
                   string.Equals((string)pending["phase"], "build-started", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void AddCompilationEvent(JObject manifest, JObject eventRecord)
    {
        JArray events = manifest["assemblyCompilationEvents"] as JArray;
        if (events == null) manifest["assemblyCompilationEvents"] = events = new JArray();
        events.Add(eventRecord);
    }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string HashText(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)))
                .Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string ReadMvid(string path)
    {
        try { return ReflectionAssembly.Load(File.ReadAllBytes(path)).ManifestModule.ModuleVersionId.ToString("D"); }
        catch { return "unknown"; }
    }

    private static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path).Replace('\\', '/');

    private static DateTime ParseUtc(string value) => DateTime.TryParse(value, out DateTime parsed)
        ? parsed.ToUniversalTime() : DateTime.MinValue;

    private static void DebugWarning(string message, Exception exception) =>
        UnityEngine.Debug.LogWarning("PMX 导出来源清单：" + message + "：" + exception.Message);

    private static void DebugWarning(string message) =>
        UnityEngine.Debug.LogWarning("PMX 导出来源清单：" + message);

    /// <summary>Player 构建前记录源码，构建后以成品 Managed 程序集建立关联。</summary>
    public sealed class BuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            try
            {
                JObject manifest = CreateSnapshot(Array.Empty<CompiledAssembly>(),
                    CompilationPipeline.GetAssemblies(AssembliesType.Player));
                manifest["kind"] = "player-build";
                manifest["phase"] = "build-started";
                manifest["buildStartedAtUtc"] = DateTime.UtcNow.ToString("o");
                manifest["gitSnapshot"] = CaptureGitSnapshot();
                foreach (JObject item in ((JArray)manifest["assemblies"]).OfType<JObject>())
                    item["assemblyKind"] = "Player";
                string path = ProjectPath(PendingBuildFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, manifest.ToString(Formatting.Indented));
            }
            catch (Exception exception) { DebugWarning("构建前源码快照失败", exception); }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                string pendingPath = ProjectPath(PendingBuildFile);
                if (!File.Exists(pendingPath)) return;
                JObject manifest = JObject.Parse(File.ReadAllText(pendingPath));
                JArray assemblies = manifest["assemblies"] as JArray;
                JObject compileManifest = ReadManifest();
                DateTime buildStartedAt = ParseUtc((string)manifest["buildStartedAtUtc"]);
                DateTime compileStartedAt = ParseUtc((string)compileManifest["startedAtUtc"]);
                bool hasFreshCompileEvents = compileStartedAt >= buildStartedAt && compileStartedAt != DateTime.MinValue;
                string outputRoot = Directory.Exists(report.summary.outputPath)
                    ? report.summary.outputPath : Path.GetDirectoryName(report.summary.outputPath);
                string[] files = Directory.Exists(outputRoot)
                    ? Directory.GetFiles(outputRoot, "*.dll", SearchOption.AllDirectories) : Array.Empty<string>();

                foreach (JObject item in (assemblies ?? new JArray()).OfType<JObject>())
                {
                    string name = (string)item["assemblyName"] ?? "";
                    string assemblyPath = files.FirstOrDefault(file =>
                        string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase) &&
                        file.IndexOf("Managed", StringComparison.OrdinalIgnoreCase) >= 0);
                    JArray currentSources = CaptureCurrentSources(item);
                    string sourceHashAfter = currentSources == null ? "missing" : HashSources(currentSources);
                    item["sourceHashAfter"] = sourceHashAfter;
                    item["sourcesAfter"] = currentSources ?? new JArray();
                    item["sourceSnapshotStatus"] = currentSources == null ? "unknown-current-source-list" :
                        string.Equals((string)item["sourceHashBefore"], sourceHashAfter, StringComparison.Ordinal)
                            ? "pending-assembly-match" : "stale";
                    item["compileStatus"] = "build-finished";
                    item["compiledAtUtc"] = DateTime.UtcNow.ToString("o");
                    if (string.IsNullOrEmpty(assemblyPath))
                    {
                        item["mvid"] = "missing";
                        item["sha256"] = "missing";
                        item["assemblyPath"] = "missing";
                        item["sourceSnapshotStatus"] = "unknown-built-assembly-missing";
                        continue;
                    }
                    string mvid = ReadMvid(assemblyPath);
                    string sha256 = HashFile(assemblyPath);
                    JObject compileEvidence = hasFreshCompileEvents
                        ? ((JArray)compileManifest["assemblies"] ?? new JArray()).OfType<JObject>().FirstOrDefault(candidate =>
                            string.Equals((string)candidate["assemblyKind"], "Player", StringComparison.Ordinal) &&
                            string.Equals((string)candidate["assemblyName"], name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals((string)candidate["sourceSnapshotStatus"], "matched", StringComparison.Ordinal) &&
                            string.Equals(HashSources(candidate["sourcesAfter"] as JArray), sourceHashAfter, StringComparison.Ordinal) &&
                            string.Equals((string)candidate["mvid"], mvid, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals((string)candidate["sha256"], sha256, StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (compileEvidence == null && item["sourceSnapshotStatus"].ToString() == "pending-assembly-match")
                        item["sourceSnapshotStatus"] = "unknown-player-compile-association";
                    else if (compileEvidence != null)
                        item["sourceSnapshotStatus"] = "matched";
                    item["mvid"] = mvid;
                    item["sha256"] = sha256;
                    item["assemblyPath"] = Path.GetFullPath(assemblyPath);
                    item["buildRelativePath"] = assemblyPath.Substring(outputRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    item["compilerManifestPath"] = compileEvidence == null ? "missing" : ProjectPath(ManifestRelativePath);
                    item["associatedGitSnapshot"] = compileEvidence == null
                        ? new JObject { ["status"] = "unknown-unassociated" }
                        : compileManifest["gitSnapshot"]?.DeepClone() ?? new JObject { ["status"] = "unknown" };
                }
                manifest["buildGitSnapshot"] = manifest["gitSnapshot"]?.DeepClone() ?? new JObject { ["status"] = "unknown" };
                manifest["gitSnapshot"] = hasFreshCompileEvents
                    ? compileManifest["gitSnapshot"]?.DeepClone() ?? new JObject { ["status"] = "unknown" }
                    : new JObject { ["status"] = "unknown-unassociated" };
                manifest["phase"] = "build-finished";
                manifest["finishedAtUtc"] = DateTime.UtcNow.ToString("o");
                WriteManifest(manifest);
                CopyManifestToPlayer(report, manifest);
                File.Delete(pendingPath);
            }
            catch (Exception exception) { DebugWarning("构建后程序集关联失败", exception); }
        }

        private static void CopyManifestToPlayer(BuildReport report, JObject manifest)
        {
            string root = Directory.Exists(report.summary.outputPath)
                ? report.summary.outputPath : Path.GetDirectoryName(report.summary.outputPath);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string dataDirectory = Directory.GetDirectories(root, "*_Data", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrEmpty(dataDirectory)) return;
            string streamingAssets = Path.Combine(dataDirectory, "StreamingAssets");
            Directory.CreateDirectory(streamingAssets);
            File.WriteAllText(Path.Combine(streamingAssets, RuntimeManifestName), manifest.ToString(Formatting.Indented));
        }
    }

    private static JObject CaptureGitSnapshot()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        try
        {
            string head = RunGit(root, "rev-parse HEAD");
            string status = RunGit(root, "status --short");
            return new JObject { ["status"] = "captured", ["head"] = head.Trim(), ["workingTreeShort"] = status.TrimEnd() };
        }
        catch (Exception exception)
        {
            return new JObject { ["status"] = "unknown", ["detail"] = exception.Message };
        }
    }

    private static string RunGit(string root, string fixedArguments)
    {
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "-C \"" + root.Replace("\"", "") + "\" " + fixedArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!process.Start()) throw new InvalidOperationException("git 无法启动");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1800))
            {
                process.Kill();
                throw new TimeoutException("git 命令超时");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException("git 退出码 " + process.ExitCode + ": " + error.GetAwaiter().GetResult().Trim());
            return output.GetAwaiter().GetResult();
        }
    }
}
