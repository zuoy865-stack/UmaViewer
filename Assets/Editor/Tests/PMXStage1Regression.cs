using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>第一阶段独立报告，不覆盖此前验收证据。</summary>
public static class PMXStage1Regression
{
    public static void RunBatch()
    {
        string output = Path.GetFullPath("Logs/teio-export-stage1-20261005");
        Directory.CreateDirectory(output);
        var results = new JArray();
        Run(results, "统一网格、材质、表情与源保护", PMXMeshExportRegression.Run);
        Run(results, "显示框覆盖及无副作用往返", PMXDisplayFrameRegression.Run);
        Run(results, "来源、哈希和双方掩码", PMXExportDiagnosticsRegression.Run);
        string oldReport = Path.GetFullPath("Logs/pmx-export-validation/assertions.json");
        byte[] previous = File.Exists(oldReport) ? File.ReadAllBytes(oldReport) : null;
        try
        {
            PMXExportRegressionTests.RunFromMenu();
            JObject existing = JObject.Parse(File.ReadAllText(oldReport));
            File.WriteAllText(Path.Combine(output, "existing-regressions.json"), existing.ToString());
            foreach (JObject item in (JArray)existing["cases"]) results.Add(item);
        }
        catch (Exception exception) { results.Add(Result("已有回归", "failed", exception.ToString())); }
        finally
        {
            if (previous != null) File.WriteAllBytes(oldReport, previous);
            else if (File.Exists(oldReport)) File.Delete(oldReport);
        }
        bool failed = false;
        foreach (JObject item in results) if ((string)item["status"] != "passed") failed = true;
        var report = new JObject
        {
            ["completedUtc"] = DateTime.UtcNow.ToString("o"),
            ["status"] = failed ? "failed-or-incomplete" : "passed",
            ["graphicsDevice"] = SystemInfo.graphicsDeviceType.ToString(),
            ["cases"] = results
        };
        File.WriteAllText(Path.Combine(output, "assertions.json"), report.ToString());
        Debug.Log("PMX 第一阶段：" + report);
        EditorApplication.Exit(failed ? 1 : 0);
    }

    private static void Run(JArray results, string name, Action action)
    {
        try { action(); results.Add(Result(name, "passed", "断言完成")); }
        catch (Exception exception) { results.Add(Result(name, "failed", exception.ToString())); }
    }
    private static JObject Result(string name, string status, string details) => new JObject
    { ["name"] = name, ["status"] = status, ["details"] = details };
}
