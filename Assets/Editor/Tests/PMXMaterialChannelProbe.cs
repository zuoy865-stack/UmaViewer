using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class PMXMaterialChannelProbe
{
    private const string OutputRelativePath = "Logs/pmx-pseudo-highlight-plan-20261006/shader-evidence/runtime-properties.json";
    private static readonly string[] Targets =
    {
        "Gallop/3D/Chara/Toon/TSER",
        "Gallop/3D/Chara/ToonHair/TSER",
        "Gallop/3D/Chara/ToonFace/TSER",
        "Gallop/3D/Chara/NolineToon/TSER",
        "Gallop/3D/Chara/ToonMayu",
        "Gallop/3D/Chara/ToonEye/T"
    };

    [Serializable]
    private sealed class Report
    {
        public string capturedUtc;
        public string status;
        public string bundleAssetPath;
        public string[] missingTargets;
        public ShaderRecord[] shaders;
        public string error;
    }

    [Serializable]
    private sealed class ShaderRecord
    {
        public string shaderName;
        public string[] missingTargets;
        public PropertyRecord[] properties;
        public string[] passes;
        public string passProbeNote;
    }

    [Serializable]
    private sealed class PropertyRecord
    {
        public int index;
        public string name;
        public string type;
        public string description;
        public string[] attributes;
        public float floatDefault;
        public int intDefault;
        public Vector2 rangeLimits;
        public bool hasRangeLimits;
        public Vector4 vectorDefault;
        public string textureDefaultName;
        public string textureDimension;
        public string flags;
    }

    [Serializable]
    private sealed class LocalConfig
    {
        public string MainPath;
        public string DBKeyText;
        public string DBBaseKeyText;
        public string ABKeyText;
    }

    [MenuItem("UmaViewer/Probe PMX Material Shader Properties")]
    public static void RunFromMenu() => RunBatch(false);

    public static void RunBatch() => RunBatch(true);

    private static void RunBatch(bool exitEditor)
    {
        var report = new Report { capturedUtc = DateTime.UtcNow.ToString("O"), status = "failed" };
        try
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            LocalConfig config = ReadConfig(project);
            byte[] dbKey = Hex(config.DBKeyText);
            byte[] dbBase = Hex(config.DBBaseKeyText);
            byte[] abBase = Hex(config.ABKeyText);
            if (dbBase.Length < 13 || abBase.Length == 0)
                throw new InvalidOperationException("本地 DB/AB key 配置无效。");
            // 密钥只用于解密，不写入报告或日志。
            for (int i = 0; i < dbKey.Length; i++) dbKey[i] ^= dbBase[i % 13];

            string mainPath = Path.GetFullPath(config.MainPath);
            string metaPath = Path.Combine(mainPath, "meta");
            var entries = UmaDatabaseController.ReadMetaFromEncryptedDb(metaPath, dbKey, 3);
            var entry = entries.Values.FirstOrDefault(item =>
                string.Equals(item.Name, "shader", StringComparison.OrdinalIgnoreCase));
            if (entry == null || string.IsNullOrEmpty(entry.Url))
                throw new InvalidDataException("meta 中没有 shader AssetBundle 条目。");

            string bundlePath = Path.Combine(mainPath, "dat", entry.Url.Substring(0, 2), entry.Url);
            report.bundleAssetPath = "dat/" + entry.Url.Substring(0, 2) + "/" + entry.Url;
            byte[] bundleKey = AssetBundleKey(abBase, entry.Key);
            using (var stream = new UmaAssetBundleStream(bundlePath, bundleKey))
            {
                AssetBundle bundle = AssetBundle.LoadFromStream(stream);
                if (bundle == null) throw new InvalidDataException("shader AssetBundle 加载失败。");
                try
                {
                    Shader[] all = bundle.LoadAllAssets<Shader>();
                    report.shaders = BuildRecords(all, out report.missingTargets);
                    if (report.shaders.Length == 0)
                        throw new InvalidDataException("shader.a 未返回目标 Shader。");
                    report.status = report.shaders.Any(item => item.missingTargets.Length > 0)
                        ? "partial" : "complete";
                }
                finally
                {
                    bundle.Unload(false);
                }
            }
        }
        catch (Exception exception)
        {
            report.error = exception.ToString();
        }
        finally
        {
            WriteReport(report);
            Debug.Log("PMX 材质 Shader 属性探针：" + report.status + "；报告：" + OutputRelativePath);
            if (exitEditor) EditorApplication.Exit(report.status == "complete" ? 0 : 1);
        }
    }

    private static ShaderRecord[] BuildRecords(Shader[] all, out string[] missing)
    {
        var records = new List<ShaderRecord>();
        foreach (string target in Targets)
        {
            Shader shader = all.FirstOrDefault(item => item != null &&
                string.Equals(item.name, target, StringComparison.OrdinalIgnoreCase));
            if (shader == null) continue;
            records.Add(ReadShader(shader));
        }
        string[] found = records.Select(item => item.shaderName).ToArray();
        missing = Targets.Where(target => !found.Contains(target, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (ShaderRecord record in records) record.missingTargets = missing;
        return records.ToArray();
    }

    private static ShaderRecord ReadShader(Shader shader)
    {
        int count = shader.GetPropertyCount();
        var properties = new PropertyRecord[count];
        for (int i = 0; i < count; i++)
        {
            ShaderPropertyType type = shader.GetPropertyType(i);
            var item = new PropertyRecord
            {
                index = i,
                name = shader.GetPropertyName(i),
                type = type.ToString(),
                description = shader.GetPropertyDescription(i),
                attributes = shader.GetPropertyAttributes(i),
                flags = shader.GetPropertyFlags(i).ToString()
            };
            if (type == ShaderPropertyType.Float || type == ShaderPropertyType.Range)
            {
                item.floatDefault = shader.GetPropertyDefaultFloatValue(i);
                if (type == ShaderPropertyType.Range)
                {
                    item.rangeLimits = shader.GetPropertyRangeLimits(i);
                    item.hasRangeLimits = true;
                }
            }
            else if (type == ShaderPropertyType.Int)
                item.intDefault = shader.GetPropertyDefaultIntValue(i);
            else if (type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector)
                item.vectorDefault = shader.GetPropertyDefaultVectorValue(i);
            else if (type == ShaderPropertyType.Texture)
            {
                item.textureDefaultName = shader.GetPropertyTextureDefaultName(i);
                item.textureDimension = shader.GetPropertyTextureDimension(i).ToString();
            }
            properties[i] = item;
        }

        string note;
        string[] passes = ReadPassNames(shader, out note);
        return new ShaderRecord
        {
            shaderName = shader.name,
            properties = properties,
            passes = passes,
            passProbeNote = note
        };
    }

    private static string[] ReadPassNames(Shader shader, out string note)
    {
        Material material = null;
        try
        {
            // 临时材质只读取 pass 名，并在 finally 中销毁。
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var result = new string[material.passCount];
            for (int i = 0; i < result.Length; i++) result[i] = material.GetPassName(i);
            note = null;
            return result;
        }
        catch (Exception exception)
        {
            note = "pass 查询失败：" + exception.GetBaseException().Message;
            return Array.Empty<string>();
        }
        finally
        {
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
        }
    }

    private static LocalConfig ReadConfig(string project)
    {
        string path = Path.Combine(project, "Config.json");
        if (!File.Exists(path)) throw new FileNotFoundException("Config.json 不存在。", path);
        LocalConfig config = JsonUtility.FromJson<LocalConfig>(File.ReadAllText(path));
        if (config == null || string.IsNullOrWhiteSpace(config.MainPath))
            throw new InvalidOperationException("Config.json 未配置 MainPath。");
        return config;
    }

    private static void WriteReport(Report report)
    {
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string path = Path.Combine(project, OutputRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(report, true));
    }

    private static byte[] AssetBundleKey(byte[] baseKey, long key)
    {
        if (key == 0) return null;
        byte[] keyBytes = BitConverter.GetBytes(key);
        if (!BitConverter.IsLittleEndian) Array.Reverse(keyBytes);
        byte[] result = new byte[baseKey.Length * 8];
        for (int i = 0; i < baseKey.Length; i++)
            for (int j = 0; j < 8; j++) result[i * 8 + j] = (byte)(baseKey[i] ^ keyBytes[j]);
        return result;
    }

    private static byte[] Hex(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length % 2 != 0) return Array.Empty<byte>();
        byte[] bytes = new byte[value.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return bytes;
    }
}




