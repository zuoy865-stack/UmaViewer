using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>隔离夹具与只读 PMX 输入验证；不修改正式导出或场景。</summary>
public static class PMXStage2aValidation
{
    public static void RunBatch()
    {
        string output = Argument("-pmxStage2aRoot", "Logs/teio-export-stage2a-20261005");
        Directory.CreateDirectory(output);
        var report = new JObject { ["startedUtc"] = DateTime.UtcNow.ToString("o") };
        bool passed = false;
        try
        {
            PMXSkirtTopologyRegression.RunFixtures();
            report["fixtures"] = "passed";
            var provenance = JObject.Parse(File.ReadAllText(Path.Combine(output, "input-provenance.json")));
            string source = (string)provenance["source"];
            string inputFile = Path.Combine(output, "teio-input.json");
            string before = Hash(source);
            Require(before == (string)provenance["sourceSha256"], "PMX 输入来源已变化。");
            Require(Hash(inputFile) == (string)provenance["inputSha256"], "提取输入哈希不匹配。");
            var input = JsonConvert.DeserializeObject<PMXSkirtTopologyInput>(File.ReadAllText(inputFile));
            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new VectorConverter());
            string inputBefore = JsonConvert.SerializeObject(input, settings);
            PMXSkirtTopologyResult result = PMXSkirtTopologyExporter.Analyze(input);
            File.WriteAllText(Path.Combine(output, "teio-topology.json"), JsonConvert.SerializeObject(result, Formatting.Indented, settings));
            Require(inputBefore == JsonConvert.SerializeObject(input, settings), "算法修改了输入位置、三角、骨链或权重。");
            Require(Hash(source) == before, "PMX 原文件被修改。");
            Require(result.Supported && result.Columns.Any(c => c.Supported), "真实 Teio 没有可分析的裙列。");
            foreach (var column in result.Columns.Where(c => c.Supported))
            {
                Require(column.MeshGeodesicLength > 0 && Finite(column.TargetLength) && Finite(column.AchievedLength), "真实列长度无效。");
                foreach (var segment in column.Segments)
                    Require(Finite(segment.Start) && Finite(segment.End) && Vector3.Distance(segment.Start, segment.End) > 0,
                        "实际布局含非有限或零长段。");
                if (!column.IsIncomplete)
                    Require(Math.Abs(column.AchievedLength - column.TargetLength) <= input.LengthTolerance, "完整列未达到最长侧。");
            }
            File.WriteAllText(Path.Combine(output, "teio-topology.json"), JsonConvert.SerializeObject(result, Formatting.Indented, settings));
            report["source"] = provenance;
            report["inputUnchanged"] = true;
            report["sourcePmxUnchanged"] = true;
            report["supportedColumns"] = result.Columns.Count(c => c.Supported);
            report["unsupportedColumns"] = result.Columns.Count(c => !c.Supported);
            report["incompleteColumns"] = result.Columns.Count(c => c.IsIncomplete);
            report["regions"] = result.Regions.Count;
            report["layoutCoverage"] = !result.IsIncomplete && result.Columns.All(c => c.Supported && !c.IsIncomplete) ? "complete" : "partial";
            report["productionConnected"] = false;
            report["targetBulletVisualValidation"] = "not-run";
            var assembly = typeof(PMXSkirtTopologyExporter).Assembly;
            report["runtimeAssembly"] = new JObject { ["mvid"] = assembly.ManifestModule.ModuleVersionId.ToString(),
                ["path"] = assembly.Location, ["sha256"] = Hash(assembly.Location) };
            Require(!result.IsIncomplete && result.Columns.Count == input.Columns.Count &&
                result.Columns.All(c => c.Supported && !c.IsIncomplete), "真实 Teio 裙列尚未全部完整覆盖。");
            passed = true;
        }
        catch (Exception exception) { report["error"] = exception.ToString(); Debug.LogException(exception); }
        report["status"] = passed ? "passed" : "failed";
        report["completedUtc"] = DateTime.UtcNow.ToString("o");
        File.WriteAllText(Path.Combine(output, "validation.json"), report.ToString());
        Debug.Log("PMX 第二阶段 2A：" + report);
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private static string Argument(string name, string fallback)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return Path.GetFullPath(args[i + 1]);
        return Path.GetFullPath(fallback);
    }
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    // Vector3 的派生属性会递归，仅保存坐标分量。
    private sealed class VectorConverter : JsonConverter
    {
        public override bool CanConvert(Type type) => type == typeof(Vector3);
        public override bool CanRead => false;
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            Vector3 vector = (Vector3)value;
            writer.WriteStartObject();
            writer.WritePropertyName("x"); writer.WriteValue(vector.x);
            writer.WritePropertyName("y"); writer.WriteValue(vector.y);
            writer.WritePropertyName("z"); writer.WriteValue(vector.z);
            writer.WriteEndObject();
        }
        public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) => throw new NotSupportedException();
    }
}
