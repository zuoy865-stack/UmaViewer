using Gallop.Live.Cutt;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live
{
    internal enum StageColorLaneKind
    {
        Unknown = 0,
        Character = 1,
        Ambient = 2,
        Sky = 3,
        Stage = 4
    }

    internal enum StageSkyPart
    {
        None = 0,
        Base = 1,
        Grad = 2,
        Common = 3,
        Named = 4
    }

    /// <summary>
    /// 把一条 BgColor1 轨道归到互斥车道，避免角色色或环境色落到天空/草地。
    /// </summary>
    internal readonly struct StageColorLane
    {
        public readonly StageColorLaneKind Kind;
        public readonly StageSkyPart SkyPart;
        public readonly string RawName;
        public readonly string StrippedName;
        public readonly int FnvHash;

        private static readonly int HashCharaColor = FNVHash.Generate("CharaColor");
        private static readonly int HashCharaCenter = FNVHash.Generate("CharaCenter");
        private static readonly int HashCharaLeft = FNVHash.Generate("CharaLeft");
        private static readonly int HashCharaRight = FNVHash.Generate("CharaRight");
        private static readonly int HashCharaBack = FNVHash.Generate("CharaBack");
        private static readonly int HashCharaOther = FNVHash.Generate("CharaOther");
        private static readonly int HashBgBl = FNVHash.Generate("BG_BL");
        private static readonly int HashBgBlCompact = FNVHash.Generate("BgBL");

        private StageColorLane(StageColorLaneKind kind, StageSkyPart skyPart, string rawName, string strippedName, int fnvHash)
        {
            Kind = kind;
            SkyPart = skyPart;
            RawName = rawName ?? string.Empty;
            StrippedName = strippedName ?? string.Empty;
            FnvHash = fnvHash;
        }

        public static StageColorLane FromTimeline(string timelineName, int incomingHash)
        {
            string raw = timelineName ?? string.Empty;
            string stripped = StageColorBinder.StripCloneToken(raw);
            int fnv = 0;
            if (!string.IsNullOrEmpty(raw))
                fnv = FNVHash.Generate(raw);
            else if (incomingHash != 0)
                fnv = incomingHash;

            if (string.IsNullOrEmpty(raw) && fnv == 0)
                return new StageColorLane(StageColorLaneKind.Unknown, StageSkyPart.None, raw, stripped, 0);

            if (IsCharacterLane(raw, fnv, incomingHash))
                return new StageColorLane(StageColorLaneKind.Character, StageSkyPart.None, raw, stripped, fnv);

            if (IsAmbientLane(raw, stripped, fnv, incomingHash))
                return new StageColorLane(StageColorLaneKind.Ambient, StageSkyPart.None, raw, stripped, fnv);

            if (TryReadSkyPart(raw, out StageSkyPart skyPart))
                return new StageColorLane(StageColorLaneKind.Sky, skyPart, raw, stripped, fnv);

            if (string.IsNullOrEmpty(raw))
                return new StageColorLane(StageColorLaneKind.Unknown, StageSkyPart.None, raw, stripped, fnv);

            return new StageColorLane(StageColorLaneKind.Stage, StageSkyPart.None, raw, stripped, fnv);
        }

        public static bool IsCharacterLane(string timelineName, int fnvHash, int incomingHash)
        {
            if (MatchesKnownHash(fnvHash, incomingHash,
                HashCharaColor, HashCharaCenter, HashCharaLeft, HashCharaRight, HashCharaBack, HashCharaOther))
            {
                return true;
            }

            if (string.IsNullOrEmpty(timelineName))
                return false;

            if (timelineName.IndexOf("chara", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return EqualsIgnoreCase(timelineName, "CharaColor")
                || EqualsIgnoreCase(timelineName, "CharaCenter")
                || EqualsIgnoreCase(timelineName, "CharaLeft")
                || EqualsIgnoreCase(timelineName, "CharaRight")
                || EqualsIgnoreCase(timelineName, "CharaBack")
                || EqualsIgnoreCase(timelineName, "CharaOther");
        }

        public static bool NameLooksLikeSky(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf("sky", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool NameLooksLikeGrass(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAmbientLane(string raw, string stripped, int fnvHash, int incomingHash)
        {
            if (MatchesKnownHash(fnvHash, incomingHash, HashBgBl, HashBgBlCompact))
                return true;

            if (EqualsIgnoreCase(raw, "BG_BL") || EqualsIgnoreCase(raw, "BgBL") || EqualsIgnoreCase(stripped, "BG_BL"))
                return true;

            string compact = StageController.NormalizeKey(stripped);
            return compact == "bgbl";
        }

        private static bool TryReadSkyPart(string timelineName, out StageSkyPart skyPart)
        {
            skyPart = StageSkyPart.None;
            if (string.IsNullOrEmpty(timelineName))
                return false;

            if (timelineName.IndexOf("sky_base", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skyPart = StageSkyPart.Base;
                return true;
            }

            if (timelineName.IndexOf("sky_grad", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skyPart = StageSkyPart.Grad;
                return true;
            }

            if (timelineName.IndexOf("cmn_sky", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skyPart = StageSkyPart.Common;
                return true;
            }

            if (timelineName.IndexOf("sky", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                skyPart = StageSkyPart.Named;
                return true;
            }

            return false;
        }

        private static bool MatchesKnownHash(int fnvHash, int incomingHash, params int[] known)
        {
            for (int i = 0; i < known.Length; i++)
            {
                int value = known[i];
                if (value == 0)
                    continue;
                if (fnvHash == value || incomingHash == value)
                    return true;
            }

            return false;
        }

        private static bool EqualsIgnoreCase(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 用 FNV 名字哈希建立舞台染色索引。找不到就空着，绝不把所有带 _MulColor0 的网格刷一遍。
    /// </summary>
    internal sealed class StageColorBinder
    {
        private static readonly int MulColor0Id = Shader.PropertyToID("_MulColor0");
        private static readonly int ColorPowerId = Shader.PropertyToID("_ColorPower");
        private static readonly int AmbientColorId = Shader.PropertyToID("_AmbientColor");

        private readonly Dictionary<int, List<Renderer>> _renderersByFnv = new Dictionary<int, List<Renderer>>(256);
        private readonly List<Renderer> _ambientOwners = new List<Renderer>(32);
        private readonly HashSet<int> _seenRendererIds = new HashSet<int>();
        private readonly MaterialPropertyBlock _sharedBlock = new MaterialPropertyBlock();

        public IReadOnlyList<Renderer> AmbientOwners => _ambientOwners;

        public void Rebuild(StageController stage)
        {
            _renderersByFnv.Clear();
            _ambientOwners.Clear();

            if (stage == null)
                return;

            if (stage.StageObjectMap != null)
            {
                foreach (var pair in stage.StageObjectMap)
                {
                    if (pair.Value == null)
                        continue;
                    RememberNamedObject(pair.Key, pair.Value);
                    RememberNamedObject(pair.Value.name, pair.Value);
                }
            }

            Renderer[] renderers = stage.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                RememberRenderer(renderer.name, renderer);
                RememberRenderer(StripCloneToken(renderer.name), renderer);
                if (renderer.gameObject != null)
                {
                    RememberRenderer(renderer.gameObject.name, renderer);
                    RememberRenderer(StripCloneToken(renderer.gameObject.name), renderer);
                }

                if (RendererOwnsAmbient(renderer))
                    _ambientOwners.Add(renderer);
            }
        }

        public bool TryFillExactHits(StageController stage, in StageColorLane lane, List<Renderer> dest)
        {
            dest.Clear();
            _seenRendererIds.Clear();

            if (stage == null || dest == null)
                return false;
            if (lane.Kind != StageColorLaneKind.Stage)
                return false;

            bool timelineMentionsGrass = StageColorLane.NameLooksLikeGrass(lane.RawName);
            GameObject namedObject = FindExactObject(stage, lane);
            if (namedObject != null)
                CollectSubtree(namedObject, dest, timelineMentionsGrass);

            AppendHashedRenderers(lane.FnvHash, dest, timelineMentionsGrass);

            if (!string.IsNullOrEmpty(lane.StrippedName) && lane.StrippedName != lane.RawName)
                AppendHashedRenderers(FNVHash.Generate(lane.StrippedName), dest, timelineMentionsGrass);

            return dest.Count > 0;
        }

        public void WriteMulColor(Renderer renderer, Color color, float power)
        {
            if (renderer == null)
                return;

            renderer.GetPropertyBlock(_sharedBlock);
            _sharedBlock.SetColor(MulColor0Id, color);
            if (power > 0f)
                _sharedBlock.SetFloat(ColorPowerId, power);
            renderer.SetPropertyBlock(_sharedBlock);
        }

        public void WriteAmbient(Color color)
        {
            for (int i = 0; i < _ambientOwners.Count; i++)
            {
                Renderer renderer = _ambientOwners[i];
                if (renderer == null)
                    continue;

                renderer.GetPropertyBlock(_sharedBlock);
                _sharedBlock.SetColor(AmbientColorId, color);
                renderer.SetPropertyBlock(_sharedBlock);
            }
        }

        public static string StripCloneToken(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            return name.Replace("(Clone)", string.Empty).Trim();
        }

        private void RememberNamedObject(string name, GameObject go)
        {
            if (string.IsNullOrEmpty(name) || go == null)
                return;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
                RememberRenderer(name, renderer);
        }

        private void RememberRenderer(string name, Renderer renderer)
        {
            if (string.IsNullOrEmpty(name) || renderer == null)
                return;

            int hash = FNVHash.Generate(name);
            if (!_renderersByFnv.TryGetValue(hash, out List<Renderer> list))
            {
                list = new List<Renderer>(2);
                _renderersByFnv.Add(hash, list);
            }

            if (!list.Contains(renderer))
                list.Add(renderer);
        }

        private static GameObject FindExactObject(StageController stage, in StageColorLane lane)
        {
            if (stage.StageObjectMap == null)
                return null;

            GameObject go;
            if (!string.IsNullOrEmpty(lane.RawName) && stage.StageObjectMap.TryGetValue(lane.RawName, out go) && go != null)
                return go;

            if (!string.IsNullOrEmpty(lane.StrippedName)
                && stage.StageObjectMap.TryGetValue(lane.StrippedName, out go)
                && go != null)
            {
                return go;
            }

            return null;
        }

        private void CollectSubtree(GameObject root, List<Renderer> dest, bool timelineMentionsGrass)
        {
            if (root == null)
                return;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                TryAddHit(renderers[i], dest, timelineMentionsGrass);
        }

        private void AppendHashedRenderers(int hash, List<Renderer> dest, bool timelineMentionsGrass)
        {
            if (hash == 0)
                return;
            if (!_renderersByFnv.TryGetValue(hash, out List<Renderer> list) || list == null)
                return;

            for (int i = 0; i < list.Count; i++)
                TryAddHit(list[i], dest, timelineMentionsGrass);
        }

        private void TryAddHit(Renderer renderer, List<Renderer> dest, bool timelineMentionsGrass)
        {
            if (renderer == null)
                return;

            string objectName = renderer.gameObject != null ? renderer.gameObject.name : renderer.name;
            if (StageColorLane.NameLooksLikeSky(renderer.name) || StageColorLane.NameLooksLikeSky(objectName))
                return;
            if (!timelineMentionsGrass && (StageColorLane.NameLooksLikeGrass(renderer.name) || StageColorLane.NameLooksLikeGrass(objectName)))
                return;

            if (!_seenRendererIds.Add(renderer.GetInstanceID()))
                return;

            dest.Add(renderer);
        }

        private static bool RendererOwnsAmbient(Renderer renderer)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return false;

            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.HasProperty(AmbientColorId))
                    return true;
            }

            return false;
        }
    }
}
