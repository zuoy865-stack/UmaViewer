using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gallop.Live.Cutt
{
    public class LiveTimelineCharaLocator : ILiveTimelineCharactorLocator
    {
        public UmaContainerCharacter UmaContainer;
        public Dictionary<string, Transform> Bones;

        private LiveCharaPosition _liveCharaStandingPosition;
        private Vector3 _liveCharaInitialPosition = Vector3.zero;

        private bool _liveCharaVisible = true;
        private Transform _liveParentDefaultTransform;
        private Transform _liveParentTransform;

        private int _liveCharaHeightLevel = 0;
        private float _liveCharaHeightRatioBase = 1.0f;
        private float _liveCharaHeightRatio = 1.0f;
        private Vector3 _liveCharaFormationHeightRateOffset = Vector3.zero;

        private bool _isPositionNodePositionAddParent = false;
        private bool _isCastShadow = true;
        private float _cySpringRate = 1.0f;

        private float HeadHeght;
        private float WaistHeght;
        private float ChestHeght;

        public LiveTimelineCharaLocator(UmaContainerCharacter umaContainer)
        {
            UmaContainer = umaContainer;
            Bones = new Dictionary<string, Transform>();

            foreach (Transform bone in umaContainer.GetComponentsInChildren<Transform>(true))
            {
                if (!Bones.ContainsKey(bone.name))
                {
                    Bones.Add(bone.name, bone);
                }
            }

            Transform position = GetBone("Position");
            Transform head = GetBone("Head");
            Transform waist = GetBone("Waist");
            Transform chest = GetBone("Chest");

            if (position == null)
            {
                position = umaContainer.transform;
            }

            Transform root = umaContainer.transform;
            _liveParentDefaultTransform = root.parent;
            _liveParentTransform = root.parent;
            _liveCharaInitialPosition = root.parent != null
                ? root.localPosition
                : root.position;

            float bodyScale = UmaContainer != null ? UmaContainer.BodyScale : 1.0f;
            if (head != null)
            {
                HeadHeght = (position.InverseTransformPoint(head.position).y + 0.1f) * bodyScale;
            }

            if (waist != null)
            {
                WaistHeght = position.InverseTransformPoint(waist.position).y * bodyScale;
            }

            if (chest != null)
            {
                ChestHeght = position.InverseTransformPoint(chest.position).y * bodyScale;
            }

            Debug.Log($"InfoChara {UmaContainer.CharaEntry.Name}: {HeadHeght}, {WaistHeght}, {ChestHeght}");
        }

        private Transform GetBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (Bones != null)
            {
                if (Bones.TryGetValue(name, out Transform bone) && bone != null)
                {
                    return bone;
                }

                // 针对高频关键骨骼节点的别名与层级容错回退机制
                string[] fallbacks = null;
                if (name.Equals("Chest", StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks = new string[] { "Chest_Attach", "Spine2", "Spine1", "Spine", "UpperBody" };
                }
                else if (name.Equals("Waist", StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks = new string[] { "Waist_Attach", "Pelvis", "Hip", "Spine" };
                }
                else if (name.Equals("Head", StringComparison.OrdinalIgnoreCase))
                {
                    if (UmaContainer != null && UmaContainer.HeadBone != null)
                    {
                        return UmaContainer.HeadBone.transform;
                    }
                    fallbacks = new string[] { "Head_Attach", "Neck" };
                }
                else if (name.Equals("Wrist_L", StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks = new string[] { "Hand_Attach_L", "Hand_L", "Forearm_L" };
                }
                else if (name.Equals("Wrist_R", StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks = new string[] { "Hand_Attach_R", "Hand_R", "Forearm_R" };
                }

                if (fallbacks != null)
                {
                    for (int f = 0; f < fallbacks.Length; f++)
                    {
                        if (Bones.TryGetValue(fallbacks[f], out Transform fbBone) && fbBone != null)
                        {
                            return fbBone;
                        }
                    }
                }
            }

            return null;
        }

        private Transform PositionTransform
        {
            get
            {
                Transform t = GetBone("Position");
                if (t != null)
                {
                    return t;
                }

                return UmaContainer != null ? UmaContainer.transform : null;
            }
        }

        private Vector3 GetBonePosition(string name)
        {
            Transform t = GetBone(name);
            if (t != null)
            {
                return t.position;
            }

            return liveCharaPosition;
        }

        public bool liveCharaVisible
        {
            get => _liveCharaVisible;
            set
            {
                _liveCharaVisible = value;

                // 先只记录状态，不强制 SetActive。
                // 如果这里直接 UmaContainer.gameObject.SetActive(value)，可能会影响官方 timeline/physics 初始化。
            }
        }

        public LiveCharaPosition liveCharaStandingPosition
        {
            get => _liveCharaStandingPosition;
            set => _liveCharaStandingPosition = value;
        }

        public Vector3 liveCharaInitialPosition
        {
            get => _liveCharaInitialPosition;
            set => _liveCharaInitialPosition = value;
        }

        public Vector3 liveCharaPosition
        {
            get
            {
                Transform t = PositionTransform;
                return t != null ? t.position : Vector3.zero;
            }
        }

        public Quaternion liveCharaPositionLocalRotation
        {
            get
            {
                Transform t = PositionTransform;
                return t != null ? t.localRotation : Quaternion.identity;
            }
            set
            {
                Transform t = PositionTransform;
                if (t != null)
                {
                    t.localRotation = value;
                }
            }
        }

        public Vector3 liveCharaHeadPosition
        {
            get
            {
                if (UmaContainer != null && UmaContainer.HeadBone != null)
                {
                    return UmaContainer.HeadBone.transform.position + new Vector3(0, 0.1f, 0);
                }

                return GetBonePosition("Head") + new Vector3(0, 0.1f, 0);
            }
        }

        public Vector3 liveCharaWaistPosition => GetBonePosition("Waist");

        public Vector3 liveCharaLeftHandWristPosition => GetBonePosition("Wrist_L");

        public Vector3 liveCharaLeftHandAttachPosition => GetBonePosition("Hand_Attach_L");

        public Vector3 liveCharaRightHandAttachPosition => GetBonePosition("Hand_Attach_R");

        public Vector3 liveCharaRightHandWristPosition => GetBonePosition("Wrist_R");

        public Vector3 liveCharaChestPosition => GetBonePosition("Chest");

        public Vector3 liveCharaFootPosition
        {
            get
            {
                Vector3 chest = liveCharaChestPosition;
                return new Vector3(chest.x, 0f, chest.z);
            }
        }

        // 官方Cutt规范：固定高度部位提供相对于原点的纯高度向量，站位世界坐标由时间轴charaPos提供
        public Vector3 liveCharaConstHeightHeadPosition =>
            new Vector3(liveCharaInitialPosition.x, HeadHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaConstHeightWaistPosition =>
            new Vector3(liveCharaInitialPosition.x, WaistHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaConstHeightChestPosition =>
            new Vector3(liveCharaInitialPosition.x, ChestHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaInitialHeightHeadPosition =>
            new Vector3(liveCharaInitialPosition.x, HeadHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaInitialHeightWaistPosition =>
            new Vector3(liveCharaInitialPosition.x, WaistHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaInitialHeightChestPosition =>
            new Vector3(liveCharaInitialPosition.x, ChestHeght, liveCharaInitialPosition.z);

        public Vector3 liveCharaScale
        {
            get
            {
                Transform t = PositionTransform;
                return t != null ? t.localScale : Vector3.one;
            }
        }

        public Transform liveParentDefaultTransform
        {
            get => _liveParentDefaultTransform;
            set => _liveParentDefaultTransform = value;
        }

        public Transform liveParentTransform
        {
            get => _liveParentTransform;
            set => _liveParentTransform = value;
        }

        public Transform liveRootTransform
        {
            get
            {
                if (UmaContainer != null)
                {
                    return UmaContainer.transform;
                }

                return PositionTransform;
            }
        }

        public int liveCharaHeightLevel
        {
            get => _liveCharaHeightLevel;
            set => _liveCharaHeightLevel = value;
        }

        public float liveCharaHeightValue
        {
            get
            {
                Transform t = PositionTransform;
                float scaleY = t != null ? t.localScale.y : 1.0f;
                return 160.0f * scaleY;
            }
        }

        public float liveCharaHeightRatioBase
        {
            get => _liveCharaHeightRatioBase;
            set => _liveCharaHeightRatioBase = value;
        }

        public float liveCharaHeightRatio
        {
            get => _liveCharaHeightRatio;
            set => _liveCharaHeightRatio = value;
        }

        public Vector3 liveCharaFormationHeightRateOffset
        {
            get => _liveCharaFormationHeightRateOffset;
            set => _liveCharaFormationHeightRateOffset = value;
        }

        public bool IsPositionNodePositionAddParent
        {
            get => _isPositionNodePositionAddParent;
            set => _isPositionNodePositionAddParent = value;
        }

        public bool IsCastShadow
        {
            get => _isCastShadow;
            set => _isCastShadow = value;
        }

        public float CySpringRate
        {
            get => _cySpringRate;
            set => _cySpringRate = value;
        }

        public int LayerIndex
        {
            set
            {
                if (UmaContainer == null)
                {
                    return;
                }

                foreach (Transform t in UmaContainer.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = value;
                }
            }
        }

        public Color EmissiveColor
        {
            set
            {
                // 先留空。
                // 官方这里大概率是设置角色材质的发光颜色。
                // 你现在先别乱写 shader property，避免把角色材质搞黑。
            }
        }

        public void SetEmissiveScale(float speed, float energyScale)
        {
            // 官方接口要求的方法。
            // 先空实现，保证 timeline 调用不会报错。
        }

        public void SetEmissiveIntensityParam(float emissiveIntensity)
        {
            // 官方接口要求的方法。
            // 先空实现，保证 timeline 调用不会报错。
        }

        public void SetEmissiveSoftEdgeParam(float emissiveRimPower, float emissiveRimIntensity, bool isemissiveCenter)
        {
            // 官方接口要求的方法。
            // 先空实现，保证 timeline 调用不会报错。
        }

        public void SetScaleFactor(float scale)
        {
            // 官方接口要求的方法。
            // 暂时不要直接改 transform.localScale，避免影响角色骨骼/物理。
            // 如果后面确认 timeline scale key 需要生效，再按官方逻辑接这里。
        }
    }
}
