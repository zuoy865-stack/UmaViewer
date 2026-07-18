using Gallop.ImageEffect;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop
{
    [DisallowMultipleComponent]
    public class GallopImageEffect : MonoBehaviour
    {
        [SerializeField]
        private Volume _volume;

        [SerializeField]
        private VolumeProfile _runtimeProfile;

        [SerializeField]
        private DofDiffusionBloomOverlayParam
            _dofDiffusionBloomOverlayParam =
                new DofDiffusionBloomOverlayParam();

        private Bloom _bloom;

        // 对应官方 GallopImageEffect._isInitialized。
        private bool _isInitialized;

        // 对应官方只读属性。
        public bool IsInitialized
        {
            get
            {
                return _isInitialized;
            }
        }

        public DofDiffusionBloomOverlayParam
            DofDiffusionBloomOverlayParam
        {
            get
            {
                return _dofDiffusionBloomOverlayParam;
            }
        }

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            // ExecuteInEditMode 或组件重新启用时，
            // 确保运行时 Volume 已经初始化。
            if (!_isInitialized)
                Initialize();
        }

        private void LateUpdate()
        {
            if (!_isInitialized)
                return;

            ApplyBloomParameter();
        }

        /// <summary>
        /// 对应官方 GallopImageEffect.Initialize() 的当前 URP 适配。
        /// </summary>
        public virtual void Initialize()
        {
            if (_isInitialized)
                return;

            InitializeVolume();

            _isInitialized =
                _volume != null &&
                _runtimeProfile != null &&
                _bloom != null;

            if (!_isInitialized)
            {
                Debug.LogError(
                    $"[{nameof(GallopImageEffect)}] " +
                    $"Initialize failed: {name}",
                    this);
            }
        }

        public void InitializeVolume()
        {
            if (_volume == null)
                _volume = GetComponent<Volume>();

            if (_volume == null)
                _volume = gameObject.AddComponent<Volume>();

            _volume.isGlobal = false;
            _volume.priority = 100f;
            _volume.weight = 1f;

            /*
             * 已经创建过运行时 Profile 时不要重复 Instantiate，
             * 避免每次 Initialize 都生成新的 Profile。
             */
            if (_runtimeProfile == null)
            {
                if (_volume.sharedProfile != null)
                {
                    _runtimeProfile =
                        Instantiate(_volume.sharedProfile);
                }
                else
                {
                    _runtimeProfile =
                        ScriptableObject.CreateInstance<VolumeProfile>();
                }

                _runtimeProfile.name =
                    $"{name}_RuntimePostEffectProfile";
            }

            _volume.profile = _runtimeProfile;

            if (!_runtimeProfile.TryGet(out _bloom))
                _bloom = _runtimeProfile.Add<Bloom>(true);
        }

        public void ApplyBloomParameter()
        {
            if (!_isInitialized)
                Initialize();

            if (!_isInitialized || _bloom == null)
                return;

            DofDiffusionBloomOverlayParam param =
                _dofDiffusionBloomOverlayParam;

            if (param == null)
            {
                _bloom.active = false;
                return;
            }

            bool enabled =
                param.IsEnableBloom &&
                param.BloomIntensity > 0f;

            _bloom.active = enabled;

            _bloom.threshold.overrideState = true;
            _bloom.intensity.overrideState = true;
            _bloom.scatter.overrideState = true;

            _bloom.threshold.value =
                Mathf.Max(0f, param.BloomThreshold);

            _bloom.intensity.value =
                Mathf.Max(0f, param.BloomIntensity);

            /*
             * 官方 BloomBlurSize 范围为 0～10。
             * URP Bloom.scatter 范围为 0～1。
             * 这里只进行渲染后端适配，不改变 Timeline 参数算法。
             */
            _bloom.scatter.value =
                Mathf.Clamp01(param.BloomBlurSize / 10f);
        }

        protected virtual void OnDestroy()
        {
            _isInitialized = false;
            _bloom = null;

            /*
             * _runtimeProfile 是运行时 Instantiate/CreateInstance
             * 创建的副本，需要主动释放。
             */
            if (_runtimeProfile != null)
            {
                if (Application.isPlaying)
                    Destroy(_runtimeProfile);
                else
                    DestroyImmediate(_runtimeProfile);

                _runtimeProfile = null;
            }
        }
    }
}

