using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

public class LiveUvMovieSlotDumper : MonoBehaviour
{
    [Header("Dump Control")]
    public bool autoDump = true;
    public bool dumpOnlyOnce = true;
    public float initialDelay = 5f;
    public float retryInterval = 1f;
    public KeyCode hotkey = KeyCode.F8;

    private bool _dumped;
    private Coroutine _routine;

    private void Start()
    {
        if (autoDump)
            _routine = StartCoroutine(AutoDumpCoroutine());
    }

    private void Update()
    {
        if (Input.GetKeyDown(hotkey))
            TryDump("hotkey");
    }

    private IEnumerator AutoDumpCoroutine()
    {
        yield return new WaitForSeconds(initialDelay);

        while (!dumpOnlyOnce || !_dumped)
        {
            bool ok = TryDump("auto");
            if (ok && dumpOnlyOnce)
            {
                _dumped = true;
                yield break;
            }

            yield return new WaitForSeconds(retryInterval);
        }
    }

    private bool TryDump(string reason)
    {
        object director = FindDirectorInstance();
        if (director == null)
        {
            Debug.LogWarning("[LiveUvMovieSlotDumper] Director instance not found yet.");
            return false;
        }

        object live3DSettings = FindLive3DSettings(director);
        object stageController = FindStageController(director);
        object uvMovieManager = FindUvMovieManager(stageController);

        Array uvMovieDataArray = FindUvMovieDataArray(live3DSettings);
        Array contextArray = FindContextArray(stageController);
        Array controllerArray = FindControllerArray(uvMovieManager);

        if (uvMovieDataArray == null && contextArray == null && controllerArray == null)
        {
            Debug.LogWarning("[LiveUvMovieSlotDumper] Nothing to dump yet. Arrays are still null.");
            return false;
        }

        string outDir = GetOutputDirectory();
        Directory.CreateDirectory(outDir);

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string outPath = Path.Combine(outDir, $"uvmovie_slots_{stamp}_{reason}.txt");

        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("=== Live UVMovie Runtime Dump ===");
        sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Reason: {reason}");
        sb.AppendLine($"Director: {GetTypeNameSafe(director)}");
        sb.AppendLine($"Live3DSettings: {GetTypeNameSafe(live3DSettings)}");
        sb.AppendLine($"StageController: {GetTypeNameSafe(stageController)}");
        sb.AppendLine($"UVMovieManager: {GetTypeNameSafe(uvMovieManager)}");
        sb.AppendLine();

        DumpUvMovieDataArray(sb, uvMovieDataArray);
        sb.AppendLine();
        DumpContextArray(sb, contextArray);
        sb.AppendLine();
        DumpControllerArray(sb, controllerArray);

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Debug.Log($"[LiveUvMovieSlotDumper] Dumped to: {outPath}");

        return true;
    }

    private static string GetOutputDirectory()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                return Path.Combine(desktop, "uvmovie_dumps");
        }
        catch
        {
        }

        return Path.Combine(Application.persistentDataPath, "uvmovie_dumps");
    }

    private static object FindDirectorInstance()
    {
        Type directorType = FindType("Gallop.Live.Director", "Director");
        if (directorType == null)
            return null;

        object instance = GetStaticMemberValue(directorType, "instance");
        if (instance != null)
            return instance;

        instance = GetStaticMemberValue(directorType, "Instance");
        if (instance != null)
            return instance;

        try
        {
            UnityEngine.Object obj = UnityEngine.Object.FindObjectOfType(directorType);
            if (obj != null)
                return obj;
        }
        catch
        {
        }

        return null;
    }

    private static object FindLive3DSettings(object director)
    {
        if (director == null)
            return null;

        object value = GetNamedMemberValue(director, "live3DSettings");
        if (value != null) return value;

        value = GetNamedMemberValue(director, "_live3DSettings");
        if (value != null) return value;

        value = GetNamedMemberValue(director, "Live3DSettings");
        if (value != null) return value;

        return FindFirstMemberValueByTypeNameContains(director, "Live3DSettings");
    }

    private static object FindStageController(object director)
    {
        if (director == null)
            return null;

        object value = GetNamedMemberValue(director, "_stageController");
        if (value != null) return value;

        value = GetNamedMemberValue(director, "stageController");
        if (value != null) return value;

        value = GetNamedMemberValue(director, "StageController");
        if (value != null) return value;

        value = FindFirstMemberValueByTypeNameContains(director, "StageController");
        if (value != null) return value;

        Type stageType = FindType("Gallop.Live.StageController", "StageController");
        if (stageType != null)
        {
            try
            {
                UnityEngine.Object obj = UnityEngine.Object.FindObjectOfType(stageType);
                if (obj != null)
                    return obj;
            }
            catch
            {
            }
        }

        return null;
    }

    private static object FindUvMovieManager(object stageController)
    {
        if (stageController == null)
            return null;

        object value = GetNamedMemberValue(stageController, "_uvMovieManager");
        if (value != null) return value;

        value = GetNamedMemberValue(stageController, "uvMovieManager");
        if (value != null) return value;

        value = GetNamedMemberValue(stageController, "UVMovieManager");
        if (value != null) return value;

        return FindFirstMemberValueByTypeNameContains(stageController, "UVMovieManager");
    }

    private static Array FindUvMovieDataArray(object live3DSettings)
    {
        if (live3DSettings == null)
            return null;

        object value = GetNamedMemberValue(live3DSettings, "UvMovieDataArray");
        if (value is Array arr1) return arr1;

        value = GetNamedMemberValue(live3DSettings, "_uvMovieDataArray");
        if (value is Array arr2) return arr2;

        return FindFirstArrayByElementTypeNameContains(live3DSettings, "UvMovieData");
    }

    private static Array FindContextArray(object stageController)
    {
        if (stageController == null)
            return null;

        object value = GetNamedMemberValue(stageController, "_uvMovieContextArray");
        if (value is Array arr1) return arr1;

        value = GetNamedMemberValue(stageController, "UVMovieContextArray");
        if (value is Array arr2) return arr2;

        return FindFirstArrayByContextSignature(stageController);
    }

    private static Array FindControllerArray(object uvMovieManager)
    {
        if (uvMovieManager == null)
            return null;

        object value = GetNamedMemberValue(uvMovieManager, "_uvMovieControllerArray");
        if (value is Array arr1) return arr1;

        value = GetNamedMemberValue(uvMovieManager, "UVMovieControllerArray");
        if (value is Array arr2) return arr2;

        return FindFirstArrayByElementTypeNameContains(uvMovieManager, "UVMovieController");
    }

    private static void DumpUvMovieDataArray(StringBuilder sb, Array array)
    {
        sb.AppendLine("=== Director.Live3DSettings.UvMovieDataArray ===");
        if (array == null)
        {
            sb.AppendLine("<null>");
            return;
        }

        sb.AppendLine($"Count = {array.Length}");
        for (int i = 0; i < array.Length; i++)
        {
            object elem = array.GetValue(i);
            if (elem == null)
            {
                sb.AppendLine($"[{i}] slotId={i + 1} <null>");
                continue;
            }

            string resourceName = GetStringMember(elem, "ResourceName");
            int loadCondition = GetIntMember(elem, "LoadConditionValue");
            int playCondition = GetIntMember(elem, "PlayConditionValue");
            bool isEnabledLoad = GetBoolMember(elem, "IsEnabledLoad");

            sb.AppendLine(
                $"[{i}] slotId={i + 1}, " +
                $"ResourceName=\"{resourceName}\", " +
                $"LoadConditionValue={loadCondition}, " +
                $"PlayConditionValue={playCondition}, " +
                $"IsEnabledLoad={isEnabledLoad}");
        }
    }

    private static void DumpContextArray(StringBuilder sb, Array array)
    {
        sb.AppendLine("=== StageController._uvMovieContextArray ===");
        if (array == null)
        {
            sb.AppendLine("<null>");
            return;
        }

        sb.AppendLine($"Count = {array.Length}");
        for (int i = 0; i < array.Length; i++)
        {
            object elem = array.GetValue(i);
            if (elem == null)
            {
                sb.AppendLine($"[{i}] slotId={i + 1} <null>");
                continue;
            }

            string resourceName = GetStringMember(elem, "ResourceName");
            bool isLoadAlways = GetBoolMember(elem, "IsLoadAlways");
            int playCondition = GetIntMember(elem, "PlayConditionValue");

            sb.AppendLine(
                $"[{i}] slotId={i + 1}, " +
                $"ResourceName=\"{resourceName}\", " +
                $"IsLoadAlways={isLoadAlways}, " +
                $"PlayConditionValue={playCondition}");
        }
    }

    private static void DumpControllerArray(StringBuilder sb, Array array)
    {
        sb.AppendLine("=== UVMovieManager._uvMovieControllerArray ===");
        if (array == null)
        {
            sb.AppendLine("<null>");
            return;
        }

        sb.AppendLine($"Count = {array.Length}");
        for (int i = 0; i < array.Length; i++)
        {
            object elem = array.GetValue(i);
            if (elem == null)
            {
                sb.AppendLine($"[{i}] slotId={i + 1} <null controller>");
                continue;
            }

            string resourceName = GetStringMember(elem, "resourceName");
            if (string.IsNullOrEmpty(resourceName))
                resourceName = GetStringMember(elem, "_resourceName");
            if (string.IsNullOrEmpty(resourceName))
                resourceName = GetStringMember(elem, "ResourceName");

            bool isLoadAlways = GetBoolMember(elem, "isLoadAlways");
            if (!isLoadAlways)
                isLoadAlways = GetBoolMember(elem, "_isLoadAlways");

            int playCondition = GetIntMember(elem, "playCondition");
            if (playCondition == 0)
                playCondition = GetIntMember(elem, "_playCondition");
            if (playCondition == 0)
                playCondition = GetIntMember(elem, "PlayConditionValue");

            sb.AppendLine(
                $"[{i}] slotId={i + 1}, " +
                $"ResourceName=\"{resourceName}\", " +
                $"IsLoadAlways={isLoadAlways}, " +
                $"PlayCondition={playCondition}, " +
                $"Type={GetTypeNameSafe(elem)}");
        }
    }

    private static Type FindType(params string[] names)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int n = 0; n < names.Length; n++)
        {
            string wanted = names[n];
            if (string.IsNullOrEmpty(wanted))
                continue;

            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType(wanted, false);
                if (t != null)
                    return t;
            }
        }

        return null;
    }

    private static object GetStaticMemberValue(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
            return null;

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo fi = type.GetField(name, flags);
        if (fi != null)
        {
            try { return fi.GetValue(null); } catch { }
        }

        PropertyInfo pi = type.GetProperty(name, flags);
        if (pi != null && pi.CanRead)
        {
            try { return pi.GetValue(null, null); } catch { }
        }

        return null;
    }

    private static object GetNamedMemberValue(object owner, string name)
    {
        if (owner == null || string.IsNullOrEmpty(name))
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = owner.GetType();

        while (type != null)
        {
            FieldInfo fi = type.GetField(name, flags);
            if (fi != null)
            {
                try { return fi.GetValue(owner); } catch { }
            }

            PropertyInfo pi = type.GetProperty(name, flags);
            if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0)
            {
                try { return pi.GetValue(owner, null); } catch { }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static object FindFirstMemberValueByTypeNameContains(object owner, string token)
    {
        if (owner == null || string.IsNullOrEmpty(token))
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = owner.GetType();

        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                Type ft = fields[i].FieldType;
                string name = ft != null ? (ft.FullName ?? ft.Name ?? string.Empty) : string.Empty;
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    object value = fields[i].GetValue(owner);
                    if (value != null)
                        return value;
                }
                catch { }
            }

            PropertyInfo[] props = type.GetProperties(flags);
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo pi = props[i];
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0)
                    continue;

                Type pt = pi.PropertyType;
                string name = pt != null ? (pt.FullName ?? pt.Name ?? string.Empty) : string.Empty;
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    object value = pi.GetValue(owner, null);
                    if (value != null)
                        return value;
                }
                catch { }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static Array FindFirstArrayByElementTypeNameContains(object owner, string token)
    {
        if (owner == null || string.IsNullOrEmpty(token))
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = owner.GetType();

        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].FieldType.IsArray)
                    continue;

                Type et = fields[i].FieldType.GetElementType();
                string name = et != null ? (et.FullName ?? et.Name ?? string.Empty) : string.Empty;
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    Array arr = fields[i].GetValue(owner) as Array;
                    if (arr != null)
                        return arr;
                }
                catch { }
            }

            PropertyInfo[] props = type.GetProperties(flags);
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo pi = props[i];
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0 || !pi.PropertyType.IsArray)
                    continue;

                Type et = pi.PropertyType.GetElementType();
                string name = et != null ? (et.FullName ?? et.Name ?? string.Empty) : string.Empty;
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    Array arr = pi.GetValue(owner, null) as Array;
                    if (arr != null)
                        return arr;
                }
                catch { }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static Array FindFirstArrayByContextSignature(object owner)
    {
        if (owner == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type type = owner.GetType();

        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].FieldType.IsArray)
                    continue;

                Type et = fields[i].FieldType.GetElementType();
                string fullName = et != null ? (et.FullName ?? string.Empty) : string.Empty;
                string name = et != null ? (et.Name ?? string.Empty) : string.Empty;
                if (!string.Equals(name, "Context", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (fullName.IndexOf("UVMovie", StringComparison.OrdinalIgnoreCase) < 0 &&
                    fullName.IndexOf("MovieManager", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    Array arr = fields[i].GetValue(owner) as Array;
                    if (arr != null)
                        return arr;
                }
                catch { }
            }

            type = type.BaseType;
        }

        return null;
    }

    private static string GetStringMember(object owner, string name)
    {
        object value = GetNamedMemberValue(owner, name);
        return value as string ?? string.Empty;
    }

    private static int GetIntMember(object owner, string name)
    {
        object value = GetNamedMemberValue(owner, name);
        if (value is int i) return i;
        if (value is short s) return s;
        if (value is byte b) return b;
        if (value is long l) return (int)l;

        try
        {
            if (value != null)
                return Convert.ToInt32(value);
        }
        catch { }

        return 0;
    }

    private static bool GetBoolMember(object owner, string name)
    {
        object value = GetNamedMemberValue(owner, name);
        if (value is bool b) return b;

        try
        {
            if (value != null)
                return Convert.ToBoolean(value);
        }
        catch { }

        return false;
    }

    private static string GetTypeNameSafe(object obj)
    {
        if (obj == null)
            return "<null>";

        Type t = obj.GetType();
        return t.FullName ?? t.Name ?? "<unknown>";
    }
}