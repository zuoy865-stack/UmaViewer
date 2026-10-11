using System.Collections.Generic;
using UnityEngine;
using Gallop;
using System;
using System.Globalization;

public class UmaCharaData
{
    public int id;
    public string tail_model_id;
}

public class UmaHeadData
{
    public int id;
    public string costumeId;
    public int tailId;
    public CharaEntry chara;
}

public class UmaLyricsData
{
    public float time;
    public string text;
}

[System.Serializable]
public class EmotionKey
{
    public FacialMorph morph;
    public float weight;
}

[System.Serializable]
public class FaceTypeData
{
    public string label, eyebrow_l, eyebrow_r, eye_l, eye_r, mouth, inverce_face_type;
    public int mouth_shape_type, set_face_group;
    public FaceEmotionKeyTarget target;
    public List<EmotionKey> emotionKeys;
    public float weight;
}

[System.Serializable]
public class CharaEntry
{
    /// master.mdb 中读取到的原始日文名。
    public string Name;

    /// <summary>
    /// 从在线更新、持久缓存或 LocalizeEn.cs 内置表读取的英文名。
    /// </summary>
    public string EnName;

    public Sprite Icon;
    public int Id;
    public string ThemeColor;
    public bool IsMob;

    public string GetName()
    {
        switch (Config.Instance.Language)
        {
            case Language.En:
                if (!string.IsNullOrEmpty(EnName))
                    return EnName;

                return IsMob
                    ? LocalizeEn.GetMobName(Id, Name)
                    : LocalizeEn.GetCharaName(Id, Name);

            case Language.Cn:
                Localize.CurrentRegion = Localize.Region.CN;

                return IsMob
                    ? Localize.GetMobName(Id, Name)
                    : Localize.GetCharaName(Id, Name);

            case Language.Jp:
            default:
                Localize.CurrentRegion = Localize.Region.JP;
                return Name ?? string.Empty;
        }
    }
}

[System.Serializable]
public class LiveEntry
{
    public int MusicId;
    public string SongName;
    public int MemberCount;
    public int DefaultDress;
    public string BackGroundId;
    public Sprite Icon;
    public List<string[]> LiveSettings = new List<string[]>();

    public LiveEntry(string data)
    {
        string[] lines = data.Split("\n"[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            LiveSettings.Add(lines[i].Split(','));
        }
        BackGroundId = LiveSettings[1][2];
    }
}

public class PartEntry
{
    public Dictionary<string, List<float>> PartSettings = new Dictionary<string, List<float>>();
    public int SingerCount = 0;

    /// <summary>
    /// 把站位列名归一化成 PartForm 的枚举名（left2/right2/left3/right3）。
    /// 游戏数据里同一个站位有 lleft/rright（二重前缀）与 llleft/rrright（三重前缀）两种写法，
    /// 而归一化目标（vocal.tag）是 PartForm 名，两者必须对齐，否则 key 与 tag 对不上，人声会全程静音。
    /// 注意：C# 的 string.Replace 返回新字符串，必须把结果赋回变量，不赋回则替换完全失效。
    /// </summary>
    private static string NormalizeColumnName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        // Trim 同时防止 CSV 行尾 \r 残留导致 key 不匹配
        string key = name.Trim();

        // 三重前缀必须先替换，否则会被二重前缀抢先匹配成 left2/right2
        key = key.Replace("llleft", "left3");
        key = key.Replace("rrright", "right3");
        key = key.Replace("lleft", "left2");
        key = key.Replace("rright", "right2");

        return key;
    }

    /// <summary>
    /// 消解重复列名。游戏数据里存在形如 ...center_vol,right_vol,right_vol,... 的重复列
    /// （第二个 right_vol 实际应为 right2_vol）。若不去重，两列的值会被追加进同一个列表，
    /// 之后按 targetIndex 取值时行号就会整体错位，读到别的时间段的音量。
    /// 规则：_vol / _pan 块内第 n 列，对应基础站位块的第 n 列。
    /// </summary>
    private static void ResolveDuplicatedSuffixColumns(string[] keys)
    {
        int baseCount = 0;
        for (int j = 0; j < keys.Length; j++)
        {
            if (keys[j] != "time" && keys[j].IndexOf('_') < 0)
            {
                baseCount++;
            }
        }

        if (baseCount == 0)
        {
            return;
        }

        var baseKeys = new string[baseCount];
        int slot = 0;
        for (int j = 0; j < keys.Length; j++)
        {
            if (keys[j] != "time" && keys[j].IndexOf('_') < 0)
            {
                baseKeys[slot++] = keys[j];
            }
        }

        string[] suffixes = { "_vol", "_pan" };

        for (int s = 0; s < suffixes.Length; s++)
        {
            string suffix = suffixes[s];

            int blockStart = -1;
            for (int j = 0; j < keys.Length; j++)
            {
                if (keys[j].EndsWith(suffix, StringComparison.Ordinal))
                {
                    blockStart = j;
                    break;
                }
            }

            if (blockStart < 0)
            {
                continue;
            }

            for (int j = blockStart; j < keys.Length; j++)
            {
                if (!keys[j].EndsWith(suffix, StringComparison.Ordinal))
                {
                    continue;
                }

                int ordinal = j - blockStart;
                if (ordinal >= baseKeys.Length)
                {
                    continue;
                }

                string expected = baseKeys[ordinal] + suffix;

                // 只在"预期列名与实际不符、且预期列名未被占用"时才纠正，避免破坏本来正确的表头
                if (expected == keys[j] || IsKeyUsed(keys, expected, j))
                {
                    continue;
                }

                keys[j] = expected;
            }
        }
    }

    private static bool IsKeyUsed(string[] keys, string candidate, int skipIndex)
    {
        for (int j = 0; j < keys.Length; j++)
        {
            if (j != skipIndex && keys[j] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseValue(string text, out float value)
    {
        value = 0f;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // 固定用 InvariantCulture：CSV 里的分隔符固定是 '.'，
        // 若跟随系统区域设置（小数点被当作千位分隔符）会解析出完全错误的音量。
        return float.TryParse(
            text.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    public PartEntry(string data)
    {
        string[] lines = data.Split('\n');

        // 表头也要 Trim：否则行尾 \r 会粘在最后一列列名上，同样导致 key 不匹配
        string[] names = lines[0].Trim().Split(',');

        var keys = new string[names.Length];
        for (int j = 0; j < names.Length; j++)
        {
            keys[j] = NormalizeColumnName(names[j]);
        }

        ResolveDuplicatedSuffixColumns(keys);

        // 必须用归一化后的 keys 建表。
        // 此前这里用归一化后的名字建表、却用原始列名（lleft 等）填值，
        // 结果填值时会访问到不存在的 key 直接抛 KeyNotFoundException，绝大多数歌曲一进 Live 就崩。
        for (int j = 0; j < keys.Length; j++)
        {
            PartSettings[keys[j]] = new List<float>();
        }

        var rowValues = new float[keys.Length];

        for (int i = 1; i < lines.Length; i++)
        {
            string row = lines[i].Trim();
            if (row.Length == 0)
            {
                continue;
            }

            string[] values = row.Split(',');

            // 整行要么全部解析成功，要么整行丢弃：保证所有列表长度一致，targetIndex 不会错位
            bool rowOk = values.Length >= keys.Length;

            for (int j = 0; j < keys.Length && rowOk; j++)
            {
                rowOk = TryParseValue(values[j], out rowValues[j]);
            }

            if (!rowOk)
            {
                continue;
            }

            for (int j = 0; j < keys.Length; j++)
            {
                PartSettings[keys[j]].Add(rowValues[j]);
            }
        }

        foreach(var part in PartSettings)
        {
            if (part.Key != "time" && !part.Key.Contains("_"))
            {
                if (part.Value.FindAll(v => v > 0).Count > 0) 
                {
                    SingerCount += 1;
                }
            }
        } 
    }
}


[System.Serializable]
public class CostumeEntry
{
    public string Id;
    public int CharaId;
    public string DressName;
    public int BodyType;
    public int BodyTypeSub;
    public Sprite Icon;
}

public enum UIMessageType
{
    Default,
    Warning,
    Error,
    Success,
    Close
}
public enum UmaFileType
{
    _3d_cutt,
    announce,
    atlas,
    bg,
    chara,
    font,
    fontresources,
    gacha,
    gachaselect,
    guide,
    heroes,
    home,
    imageeffect,
    item,
    lipsync,
    live,
    loginbonus,
    manifest,
    manifest2,
    manifest3,
    mapevent,
    master,
    minigame,
    mob,
    movie,
    outgame,
    paddock,
    race,
    shader,
    single,
    sound,
    story,
    storyevent,
    supportcard,
    uianimation,
    transferevent,
    teambuilding,
    challengematch,
    collectevent,
    ratingrace,
    jobs,
    manualdownloadatlas,
}
