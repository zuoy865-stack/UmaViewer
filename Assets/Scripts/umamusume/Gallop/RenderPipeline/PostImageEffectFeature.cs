using System;
using System.Collections.Generic;
using System.Reflection;
using Gallop.ImageEffect;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop.RenderPipeline
{
    /// <summary>
    /// DofDiffusionBloomOverlayPass 专用的 PostImageEffectFeature
    ///
    /// 当前只启用 Dof / Diffusion / Bloom / Overlay 所需链路：SourceSetupPass -> DofDiffusionBloomOverlayPass -> FinalBlitPass。
    /// 原脚本中其余未使用实现没有删除，完整保留在文件末尾的
    /// #if false 区域内
    /// </summary>
    public class PostImageEffectFeature : ScriptableRendererFeature
    {
        private const int INVALID_NAME_ID = -2;
        private const int REGISTER_PASS_SIZE = 3;
        private const string SOURCE_SETUP_POOL_NAME =
            "PostImageEffectFeature.SourceSetup";
        private const string FINAL_BLIT_POOL_NAME =
            "PostImageEffectFeature.FinalBlit";

        // 只保留 DofDiffusionBloomOverlayPass 实际需要的 Pass。
        private SourceSetupPass _sourceSetupPass;
        private DofDiffusionBloomOverlayPass
            _dofDiffusionBloomOverlayPass;
        private FinalBlitPass _finalBlitPass;

        private RenderTextureHandle _sourceRT;
        private List<ScriptableRenderPass> _registerPass;

        public RenderTextureHandle SourceRT
        {
            get { return _sourceRT; }
        }

        public override void Create()
        {
            Dispose();

            RenderPassEvent passEvent =
                RenderPassEvent.BeforeRenderingPostProcessing;

            _sourceSetupPass = new SourceSetupPass(this, passEvent);
            _dofDiffusionBloomOverlayPass = new DofDiffusionBloomOverlayPass(passEvent);
            _finalBlitPass = new FinalBlitPass(this, passEvent);

            _registerPass = new List<ScriptableRenderPass>(REGISTER_PASS_SIZE);

            _sourceRT = RenderTextureHandle.Make(new RenderTargetIdentifier(INVALID_NAME_ID), 0, 0);

            // 让 Dof Pass 直接使用同一套 CameraData 参数解析逻辑。
            DofDiffusionBloomOverlayPass.FeatureParameterResolver = ResolveFeatureParameter;
        }

        public override void AddRenderPasses( ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera == null)
                return;

            CameraData cameraData;
            if (!camera.TryGetComponent(out cameraData))
                return;

            Parameter parameter = ResolveFeatureParameter(cameraData);
            if (parameter == null || !parameter.IsEnable)
                return;

            if (_registerPass == null)
            {
                _registerPass =  new List<ScriptableRenderPass>(REGISTER_PASS_SIZE);
            }

            _registerPass.Clear();

            if (_sourceSetupPass == null || _dofDiffusionBloomOverlayPass == null || _finalBlitPass == null)
            {
                return;
            }

            if (!_dofDiffusionBloomOverlayPass.Setup(
                    cameraData,
                    this))
            {
                return;
            }

            RenderPassEvent effectEvent = _dofDiffusionBloomOverlayPass.renderPassEvent;

            _sourceSetupPass.renderPassEvent = effectEvent;
            _finalBlitPass.renderPassEvent = effectEvent;

            // 同一 RenderPassEvent 下按 Enqueue 顺序执行。
            _registerPass.Add(_sourceSetupPass);
            _registerPass.Add(_dofDiffusionBloomOverlayPass);

            // Dof Pass在 <= 550 时把结果交回 Feature,因此还需要最终 Blit 写回 Camera Color
            if ((int)effectEvent <= (int)RenderPassEvent.BeforeRenderingPostProcessing)
            {
                _registerPass.Add(_finalBlitPass);
            }

            for (int i = 0; i < _registerPass.Count; i++)
                renderer.EnqueuePass(_registerPass[i]);
        }

        /// <summary>
        /// 将当前源设置为相机颜色目标
        /// 这替代原版SetSourceRTPass对 Dof-only 链路的作用
        /// </summary>
        public void ResetSourceRT( CommandBuffer cmd, ref RenderingData renderingData)
        {
            ScriptableRenderer renderer = renderingData.cameraData.renderer;

            if (renderer == null)
                return;

#pragma warning disable CS0618
            RenderTargetIdentifier cameraColorTarget = renderer.cameraColorTarget;
#pragma warning restore CS0618

            RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;

            if (cmd != null && !_sourceRT.RtId.Equals(cameraColorTarget) && _sourceRT.NameId != INVALID_NAME_ID)
            {
                cmd.ReleaseTemporaryRT(_sourceRT.NameId);
            }

            _sourceRT = RenderTextureHandle.Make( cameraColorTarget, descriptor.width, descriptor.height);
        }

        /// <summary>
        /// DofDiffusionBloomOverlayPass.Execute 在完成后调用此函数，
        /// 把输出临时 RT 交回 Feature。
        /// </summary>
        public void SetSourceRT(RenderTextureHandle sourceHandle, CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (_sourceRT.NameId == sourceHandle.NameId)
                return;

            ReleaseCurrentTemporaryRT(cmd, ref renderingData);

            _sourceRT = sourceHandle;
        }

        private void ReleaseCurrentTemporaryRT(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ScriptableRenderer renderer = renderingData.cameraData.renderer;

            if (renderer == null)
                return;

#pragma warning disable CS0618
            RenderTargetIdentifier cameraColorTarget = renderer.cameraColorTarget;
#pragma warning restore CS0618

            if (!_sourceRT.RtId.Equals(cameraColorTarget) && _sourceRT.NameId != INVALID_NAME_ID && cmd != null)
            {
                cmd.ReleaseTemporaryRT(_sourceRT.NameId);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _registerPass = null;

            if (_dofDiffusionBloomOverlayPass != null)
            {
                _dofDiffusionBloomOverlayPass.Dispose();
                _dofDiffusionBloomOverlayPass = null;
            }

            _sourceSetupPass = null;
            _finalBlitPass = null;

            base.Dispose(disposing);
        }

        /// <summary>
        /// CameraData 原生偏移 +0x150 对应的 Parameter 解析。
        /// 字段真实名称确定后，可以直接替换为明确字段访问。
        /// </summary>
        private static Parameter ResolveFeatureParameter(CameraData cameraData)
        {
            if (cameraData == null)
                return null;

            object instance = cameraData;
            Type type = instance.GetType();
            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            while (type != null)
            {
                FieldInfo[] fields = type.GetFields(flags);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (typeof(Parameter)
                        .IsAssignableFrom(fields[i].FieldType))
                    {
                        return fields[i].GetValue(instance)
                            as Parameter;
                    }
                }

                PropertyInfo[] properties = type.GetProperties(flags);

                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo property = properties[i];
                    if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    if (!typeof(Parameter)
                        .IsAssignableFrom(property.PropertyType))
                    {
                        continue;
                    }

                    try
                    {
                        return property.GetValue(instance, null)
                            as Parameter;
                    }
                    catch
                    {
                        // 继续搜索基类中的参数字段。
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>
        /// 在 Dof Pass 前，把 Feature.SourceRT 指向 Camera Color。
        /// </summary>
        private sealed class SourceSetupPass : ScriptableRenderPass
        {
            private readonly PostImageEffectFeature _owner;

            public SourceSetupPass( PostImageEffectFeature owner, RenderPassEvent passEvent)
            {
                _owner = owner;
                renderPassEvent = passEvent;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_owner == null)
                    return;

                CommandBuffer cmd =
                    CommandBufferPool.Get(SOURCE_SETUP_POOL_NAME);

                try
                {
                    _owner.ResetSourceRT(cmd, ref renderingData);

                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }
        }

        /// <summary>
        /// Dof Pass 在 550 或更早执行时，只更新 Feature.SourceRT。
        /// 此 Pass 将结果写回 Camera Color，并释放 Dof 临时 RT。
        /// </summary>
        private sealed class FinalBlitPass : ScriptableRenderPass
        {
            private readonly PostImageEffectFeature _owner;

            public FinalBlitPass( PostImageEffectFeature owner, RenderPassEvent passEvent)
            {
                _owner = owner;
                renderPassEvent = passEvent;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (_owner == null)
                    return;

                RenderTextureHandle source = _owner.SourceRT;
                if (source.NameId == INVALID_NAME_ID)
                    return;

                ScriptableRenderer renderer = renderingData.cameraData.renderer;

                if (renderer == null)
                    return;

#pragma warning disable CS0618
                RenderTargetIdentifier cameraColorTarget = renderer.cameraColorTarget;
#pragma warning restore CS0618

                CommandBuffer cmd =
                    CommandBufferPool.Get(FINAL_BLIT_POOL_NAME);

                try
                {
                    if (!source.RtId.Equals(cameraColorTarget))
                    {
                        cmd.Blit(
                            source.RtId,
                            cameraColorTarget);
                    }

                    // Blit 后释放 Dof 输出临时 RT,并把 SourceRT 恢复为 Camera Color
                    _owner.ResetSourceRT(cmd, ref renderingData);

                    context.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }
        }

        public class Parameter
        {
            private Camera _targetCamera;
            private CameraData _targetCameraData;

            public bool IsEnable;

            // DofDiffusionBloomOverlayPass 实际使用的参数。
            public DofDiffusionBloomOverlayPass.Parameter
                DofDiffuionBloomOverlay;

            public bool IsRequestDepthPass;

            public Camera TargetCamera
            {
                get { return _targetCamera; }
                set
                {
                    _targetCamera = value;
                    DofDiffuionBloomOverlay.TargetCamera = value;

                    if (_targetCamera == null)
                        return;

                    if (!_targetCamera.TryGetComponent(
                            out _targetCameraData))
                    {
                        _targetCameraData = _targetCamera.gameObject.AddComponent<CameraData>();

                        InvokeCameraDataInitialize( _targetCameraData);
                    }
                }
            }

            public bool IsUseDepthTexture
            {
                get
                {
                    return DofDiffuionBloomOverlay
                        .IsDepthTexture;
                }
            }

            public Parameter()
            {
                IsEnable = true;
                DofDiffuionBloomOverlay = DofDiffusionBloomOverlayPass.Parameter.Default();
                IsRequestDepthPass = true;
            }

            public void ShallowCopyFrom(Parameter src)
            {
                if (src == null)
                    throw new ArgumentNullException(nameof(src));

                ShallowCopyParameters(src);
                IsRequestDepthPass = src.IsRequestDepthPass;
            }

            public void ShallowCopyParameters(Parameter src)
            {
                if (src == null)
                    throw new ArgumentNullException(nameof(src));

                DofDiffuionBloomOverlay =
                    src.DofDiffuionBloomOverlay;
            }

            public void CopyFromGallopImageEffect(
                GallopImageEffectParameter imageEffectParam)
            {
                if (imageEffectParam == null)
                {
                    throw new ArgumentNullException( nameof(imageEffectParam));
                }

                DofDiffuionBloomOverlay.Setup(imageEffectParam.DofDiffusionBloomOverlayParam);
            }

            private static void InvokeCameraDataInitialize(
                CameraData cameraData)
            {
                if (cameraData == null)
                    return;

                MethodInfo[] methods =
                    cameraData.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "Initialize")
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();

                    if (parameters.Length != 1)
                        continue;

                    Type parameterType = parameters[0].ParameterType;

                    if (parameterType == typeof(bool))
                    {
                        method.Invoke(cameraData, new object[] { false });
                        return;
                    }

                    if (parameterType == typeof(int))
                    {
                        method.Invoke(cameraData, new object[] { 0 });
                        return;
                    }
                }
            }
        }
    }
}


/*
 * ============================================================================
 * 以下为用户原始 PostImageEffectFeature 完整实现。
 * 当前 Dof-only 版本没有删除这些内容，只通过 #if false 停止编译。
 * 将来补齐其他 Pass 后，可以从这里逐段恢复。
 * ============================================================================
 */
#if false
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Gallop.RenderPipeline
{
    public class PostImageEffectFeature : ScriptableRendererFeature
    {
        private const int INVALID_NAME_ID = -2;
        private const int REGISTER_PASS_SIZE = 32;

        // private SetSourceRTPass _setSourceRTPass;
        // private BlurOptimizePass _blurOptimizedPass;
        // private GlobalFogPass _globalFogPass;
        // private SunShaftsPass _sunShaftsPass;
        // private TransmittedLightPass _transmittedLightPass;
        // private IndirectLightShaftsPass _indirectLightShaftsPass;
        // private ImageEffectCallbackPass _prepareScreenMakeup;
        private DofDiffusionBloomOverlayPass _dofDiffusionBloomOverlayPass;
        private TiltShiftPass _tiltShiftPass;
        private RadialBlurPass _radialBlurPass;
        private FluctuationPass _flucturationPass;
        private LensDistortionPass _lensDistortionPass;
        private ChromaticAberrationPass _chromaticAberrationPass;
        private ToneCurvePass _toneCurvePass;
        private ExposurePass _exposurePass;
        private ColorCorrectionPass _colorCorrectionPass;
        private ImageEffectCallbackPass _prepareLastScreenMakeup;
        private ColorGradingPass _colorGradingPass;
        private BgBlurPass _bgBlurPass;
        private VortexPass _vortexPass;
        private AuraPass _auraPass;
        private FilmRollPass _filmRollPass;
        private HatchingPass _hatchingPass;
        private LetterBoxPass _letterBoxPass;
        private RainSplashPass _rainSplashPass;
        private ImageEffectBlitPass _imageEffectBlitPass;

        private RenderTextureHandle _sourceRT;
        private List<ScriptableRenderPass> _registerPass;

        /*
         * Native code checks a global capability byte before enabling
         * TransmittedLightPass. Set this resolver to that official global flag
         * when its owning managed type is identified.
         */
        public static Func<bool> TransmittedLightCapabilityResolver;

        /*
         * The official feature performs TransmittedLight setup inline instead
         * of calling a Setup method. A project-specific implementation can be
         * supplied here. The default adapter below also attempts to wire it.
         */
        public static Func<
            TransmittedLightPass,
            CameraData,
            PostImageEffectFeature,
            bool> TransmittedLightSetupResolver;

        public RenderTextureHandle SourceRT
        {
            get { return _sourceRT; }
        }

        public override void Create()
        {
            Dispose();

            RenderPassEvent passEvent =
                RenderPassEvent.BeforeRenderingPostProcessing;

            // _setSourceRTPass = new SetSourceRTPass(passEvent);
            // _blurOptimizedPass = new BlurOptimizePass(passEvent);
            // _globalFogPass = new GlobalFogPass(passEvent);
            // _sunShaftsPass = new SunShaftsPass(passEvent);
            // _transmittedLightPass = new TransmittedLightPass(passEvent);
            // _indirectLightShaftsPass = new IndirectLightShaftsPass(passEvent);
            // _prepareScreenMakeup = new ImageEffectCallbackPass(passEvent);
            _dofDiffusionBloomOverlayPass =
                new DofDiffusionBloomOverlayPass(passEvent);
            // _tiltShiftPass = new TiltShiftPass(passEvent);
            // _radialBlurPass = new RadialBlurPass(passEvent);
            // _flucturationPass = new FluctuationPass(passEvent);
            // _lensDistortionPass = new LensDistortionPass(passEvent);
            // _chromaticAberrationPass =
            //     new ChromaticAberrationPass(passEvent);
            // _toneCurvePass = new ToneCurvePass(passEvent);
            // _exposurePass = new ExposurePass(passEvent);
            // _colorCorrectionPass = new ColorCorrectionPass(passEvent);
            // _prepareLastScreenMakeup =
            //     new ImageEffectCallbackPass(passEvent);
            // _colorGradingPass = new ColorGradingPass(passEvent);
            // _bgBlurPass = new BgBlurPass(passEvent);
            // _vortexPass = new VortexPass(passEvent);
            // _auraPass = new AuraPass(passEvent);
            // _filmRollPass = new FilmRollPass();
            // _hatchingPass = new HatchingPass(passEvent);
            // _letterBoxPass = new LetterBoxPass(passEvent);
            // _rainSplashPass = new RainSplashPass(passEvent);
            // _imageEffectBlitPass = new ImageEffectBlitPass(passEvent);

            _registerPass =
                new List<ScriptableRenderPass>(REGISTER_PASS_SIZE);
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera == null)
                return;

            CameraData cameraData;
            if (!camera.TryGetComponent(out cameraData))
                return;

            if (_registerPass == null)
            {
                _registerPass =
                    new List<ScriptableRenderPass>(REGISTER_PASS_SIZE);
            }

            _registerPass.Clear();

            bool updateDepthRequest = RegisterPass(
                cameraData,
                ref renderingData,
                _registerPass);

            for (int i = 0; i < _registerPass.Count; i++)
                renderer.EnqueuePass(_registerPass[i]);

            /*
             * Official behavior:
             * only the full-image-effect RegisterPass path returns true.
             * Simple mode / foreground-only / disabled-image-effect paths do
             * not update this depth flag even if they enqueue final blit.
             */
            if (updateDepthRequest)
            {
                Parameter parameter = ResolveFeatureParameter(cameraData);
                if (parameter == null)
                {
                    throw new NullReferenceException(
                        "CameraData.PostImageEffectFeature.Parameter");
                }

                bool requestDepth =
                    parameter.IsUseDepthTexture &&
                    parameter.IsRequestDepthPass;

                SetSourcePassDepthRequest(
                    _setSourceRTPass,
                    requestDepth);
            }
        }

        public bool RegisterPass(
            CameraData cameraData,
            ref RenderingData renderingData,
            List<ScriptableRenderPass> registerPass)
        {
            if (!GraphicSettings.HasInstance)
                return false;

            if (cameraData == null)
                throw new NullReferenceException(nameof(cameraData));

            Parameter parameter = ResolveFeatureParameter(cameraData);
            if (parameter == null)
            {
                throw new NullReferenceException(
                    "CameraData.PostImageEffectFeature.Parameter");
            }

            if (!parameter.IsEnable)
                return false;

            /*
             * Official initialization:
             * NameId = -2, size = 0 x 0 and identifier = -2.
             */
            _sourceRT = RenderTextureHandle.Make(
                new RenderTargetIdentifier(INVALID_NAME_ID),
                0,
                0);

            bool hasImageEffect = false;

            TryRegister(
                registerPass,
                _setSourceRTPass,
                cameraData,
                this);

            if (TryRegister(
                    registerPass,
                    _filmRollPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _bgBlurPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            GraphicSettings graphicSettings = GraphicSettings.Instance;
            if (graphicSettings == null)
                throw new NullReferenceException("GraphicSettings.Instance");

            bool isUseImageEffect =
                graphicSettings.IsUseImageEffect();

            /*
             * Foreground-only mode bypasses the normal effect chain and goes
             * directly to the final blit. RegisterPass returns false here.
             */
            if (parameter.IsForegroundTextureOnly &&
                parameter.ForegroundTexture != null)
            {
                hasImageEffect = true;

                TryRegister(
                    registerPass,
                    _imageEffectBlitPass,
                    cameraData,
                    this,
                    hasImageEffect);

                return false;
            }

            /*
             * Image effects disabled:
             * FilmRoll/BgBlur may already have registered. If neither did,
             * return immediately. Otherwise append final blit and return false.
             */
            if (!isUseImageEffect)
            {
                if (!hasImageEffect)
                    return false;

                TryRegister(
                    registerPass,
                    _imageEffectBlitPass,
                    cameraData,
                    this,
                    hasImageEffect);

                return false;
            }

            /*
             * Official simple mode:
             * only ColorCorrection and ColorGrading are considered before
             * final blit. This branch also returns false.
             */
            if (parameter.IsSimpleMode)
            {
                if (TryRegister(
                        registerPass,
                        _colorCorrectionPass,
                        cameraData,
                        this))
                {
                    hasImageEffect = true;
                }

                if (TryRegister(
                        registerPass,
                        _colorGradingPass,
                        cameraData,
                        this))
                {
                    hasImageEffect = true;
                }

                if (!hasImageEffect)
                    return false;

                TryRegister(
                    registerPass,
                    _imageEffectBlitPass,
                    cameraData,
                    this,
                    hasImageEffect);

                return false;
            }

            /*
             * Full official pass order.
             */
            if (TryRegister(
                    registerPass,
                    _auraPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _blurOptimizedPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _globalFogPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _sunShaftsPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            // if (SetupTransmittedLight(
            //         cameraData,
            //         parameter))
            // {
            //     registerPass.Add(_transmittedLightPass);
            //     hasImageEffect = true;
            // }

            if (TryRegister(
                    registerPass,
                    _indirectLightShaftsPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _vortexPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            // if (TryRegister(
            //         registerPass,
            //         _prepareScreenMakeup,
            //         cameraData,
            //         this,
            //         parameter.OnPrepareScreenMakeup))
            // {
            //     hasImageEffect = true;
            // }

            if (TryRegister(
                    registerPass,
                    _dofDiffusionBloomOverlayPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _tiltShiftPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _hatchingPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _radialBlurPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _flucturationPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _lensDistortionPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _chromaticAberrationPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _toneCurvePass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _exposurePass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _colorCorrectionPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            // if (TryRegister(
            //         registerPass,
            //         _prepareLastScreenMakeup,
            //         cameraData,
            //         this,
            //         parameter.OnPrepareLastScreenMarkup))
            // {
            //     hasImageEffect = true;
            // }

            if (TryRegister(
                    registerPass,
                    _colorGradingPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _letterBoxPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            if (TryRegister(
                    registerPass,
                    _rainSplashPass,
                    cameraData,
                    this))
            {
                hasImageEffect = true;
            }

            /*
             * In the full branch the result of ImageEffectBlitPass.Setup does
             * not modify the return value. The method returns hasImageEffect.
             */
            TryRegister(
                registerPass,
                _imageEffectBlitPass,
                cameraData,
                this,
                hasImageEffect);

            return hasImageEffect;
        }

        public void ResetSourceRT(
            CommandBuffer cmd,
            ref RenderingData renderingData)
        {
            ScriptableRenderer renderer =
                renderingData.cameraData.renderer;

            if (renderer == null)
                throw new NullReferenceException(
                    "renderingData.cameraData.renderer");

#pragma warning disable CS0618
            RenderTargetIdentifier cameraColorTarget =
                renderer.cameraColorTarget;
#pragma warning restore CS0618

            RenderTextureDescriptor descriptor =
                renderingData.cameraData.cameraTargetDescriptor;

            if (cmd != null &&
                !_sourceRT.RtId.Equals(cameraColorTarget) &&
                _sourceRT.NameId != INVALID_NAME_ID)
            {
                cmd.ReleaseTemporaryRT(_sourceRT.NameId);
            }

            _sourceRT = RenderTextureHandle.Make(
                cameraColorTarget,
                descriptor.width,
                descriptor.height);
        }

        public void SetSourceRT(
            RenderTextureHandle sourceHandle,
            CommandBuffer cmd,
            ref RenderingData renderingData)
        {
            if (_sourceRT.NameId == sourceHandle.NameId)
                return;

            ReleaseCurrentTemporaryRT(
                cmd,
                ref renderingData);

            _sourceRT = sourceHandle;
        }

        public void SetSourceRT(
            int renderTextureName,
            CommandBuffer cmd,
            ref RenderingData renderingData)
        {
            if (_sourceRT.NameId == renderTextureName)
                return;

            ReleaseCurrentTemporaryRT(
                cmd,
                ref renderingData);

            RenderTextureDescriptor descriptor =
                renderingData.cameraData.cameraTargetDescriptor;

            _sourceRT = RenderTextureHandle.Make(
                renderTextureName,
                descriptor.width,
                descriptor.height,
                0);
        }

        public void SetSourceRT(
            int renderTextureName,
            int width,
            int height,
            CommandBuffer cmd,
            ref RenderingData renderingData)
        {
            if (_sourceRT.NameId == renderTextureName)
                return;

            ReleaseCurrentTemporaryRT(
                cmd,
                ref renderingData);

            _sourceRT = RenderTextureHandle.Make(
                renderTextureName,
                width,
                height,
                0);
        }

        private void ReleaseCurrentTemporaryRT(
            CommandBuffer cmd,
            ref RenderingData renderingData)
        {
            ScriptableRenderer renderer =
                renderingData.cameraData.renderer;

            if (renderer == null)
                throw new NullReferenceException(
                    "renderingData.cameraData.renderer");

#pragma warning disable CS0618
            RenderTargetIdentifier cameraColorTarget =
                renderer.cameraColorTarget;
#pragma warning restore CS0618

            if (!_sourceRT.RtId.Equals(cameraColorTarget) &&
                _sourceRT.NameId != INVALID_NAME_ID)
            {
                if (cmd == null)
                    throw new NullReferenceException(nameof(cmd));

                cmd.ReleaseTemporaryRT(_sourceRT.NameId);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _registerPass = null;

            DisposePass(_setSourceRTPass);
            DisposePass(_blurOptimizedPass);
            DisposePass(_globalFogPass);
            DisposePass(_sunShaftsPass);
            DisposePass(_transmittedLightPass);
            DisposePass(_indirectLightShaftsPass);
            DisposePass(_prepareScreenMakeup);
            DisposePass(_dofDiffusionBloomOverlayPass);
            DisposePass(_tiltShiftPass);
            DisposePass(_radialBlurPass);
            DisposePass(_flucturationPass);
            DisposePass(_lensDistortionPass);
            DisposePass(_chromaticAberrationPass);
            DisposePass(_toneCurvePass);
            DisposePass(_exposurePass);
            DisposePass(_colorCorrectionPass);
            DisposePass(_prepareLastScreenMakeup);
            DisposePass(_colorGradingPass);
            DisposePass(_bgBlurPass);
            DisposePass(_vortexPass);
            DisposePass(_auraPass);
            DisposePass(_filmRollPass);
            DisposePass(_hatchingPass);
            DisposePass(_letterBoxPass);
            DisposePass(_rainSplashPass);
            DisposePass(_imageEffectBlitPass);

            base.Dispose(disposing);
        }

        private static bool TryRegister(
            List<ScriptableRenderPass> destination,
            ScriptableRenderPass pass,
            params object[] setupArguments)
        {
            if (pass == null)
                throw new NullReferenceException("render pass");

            if (!InvokeBooleanMethod(
                    pass,
                    "Setup",
                    setupArguments))
            {
                return false;
            }

            if (destination == null)
                throw new NullReferenceException(nameof(destination));

            destination.Add(pass);
            return true;
        }

        private static bool InvokeBooleanMethod(
            object target,
            string methodName,
            object[] arguments)
        {
            MethodInfo[] methods = target.GetType().GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != methodName)
                    continue;

                ParameterInfo[] parameters =
                    method.GetParameters();

                if (parameters.Length != arguments.Length)
                    continue;

                bool compatible = true;
                for (int j = 0; j < parameters.Length; j++)
                {
                    object argument = arguments[j];
                    Type parameterType =
                        parameters[j].ParameterType;

                    if (parameterType.IsByRef)
                        parameterType =
                            parameterType.GetElementType();

                    if (argument != null &&
                        !parameterType.IsInstanceOfType(argument))
                    {
                        compatible = false;
                        break;
                    }
                }

                if (!compatible)
                    continue;

                object result =
                    method.Invoke(target, arguments);

                return result is bool && (bool)result;
            }

            return false;
        }

        // private bool SetupTransmittedLight(CameraData cameraData, Parameter parameter)
        // {
        //     if (!GetTransmittedLightEnabled(
        //             parameter.TransmittedLight))
        //     {
        //         return false;
        //     }

        //     if (TransmittedLightCapabilityResolver != null &&
        //         !TransmittedLightCapabilityResolver())
        //     {
        //         return false;
        //     }

        //     if (TransmittedLightSetupResolver != null)
        //     {
        //         return TransmittedLightSetupResolver(
        //             _transmittedLightPass,
        //             cameraData,
        //             this);
        //     }

        //     /*
        //      * Native TransmittedLight setup is inline:
        //      * +0xE0 CameraData
        //      * +0xE8 PostImageEffectFeature
        //      * +0xF0 Material
        //      */
        //     Type type = _transmittedLightPass.GetType();
        //     const BindingFlags flags =
        //         BindingFlags.Instance |
        //         BindingFlags.Public |
        //         BindingFlags.NonPublic;

        //     FieldInfo cameraField = null;
        //     FieldInfo featureField = null;
        //     FieldInfo materialField = null;

        //     FieldInfo[] fields = type.GetFields(flags);
        //     for (int i = 0; i < fields.Length; i++)
        //     {
        //         FieldInfo field = fields[i];

        //         if (cameraField == null &&
        //             typeof(CameraData).IsAssignableFrom(field.FieldType))
        //         {
        //             cameraField = field;
        //         }
        //         else if (featureField == null &&
        //                  typeof(PostImageEffectFeature)
        //                      .IsAssignableFrom(field.FieldType))
        //         {
        //             featureField = field;
        //         }
        //         else if (materialField == null &&
        //                  typeof(Material)
        //                      .IsAssignableFrom(field.FieldType))
        //         {
        //             materialField = field;
        //         }
        //     }

        //     if (cameraField == null ||
        //         featureField == null ||
        //         materialField == null)
        //     {
        //         return false;
        //     }

        //     cameraField.SetValue(_transmittedLightPass, null);
        //     featureField.SetValue(_transmittedLightPass, null);

        //     Material material =
        //         materialField.GetValue(
        //             _transmittedLightPass) as Material;

        //     if (material == null)
        //     {
        //         Shader shader = TryGetShaderByOfficialIndex(163);
        //         if (shader == null || !shader.isSupported)
        //             return false;

        //         material = new Material(shader);
        //         materialField.SetValue(
        //             _transmittedLightPass,
        //             material);
        //     }

        //     cameraField.SetValue(
        //         _transmittedLightPass,
        //         cameraData);

        //     featureField.SetValue(
        //         _transmittedLightPass,
        //         this);

        //     return true;
        // }

        private static Shader TryGetShaderByOfficialIndex(
            int shaderIndex)
        {
            Type shaderManagerType =
                typeof(PostImageEffectFeature).Assembly.GetType(
                    "Gallop.ShaderManager");

            if (shaderManagerType == null)
                return null;

            MethodInfo[] methods =
                shaderManagerType.GetMethods(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "GetShader")
                    continue;

                ParameterInfo[] parameters =
                    method.GetParameters();

                if (parameters.Length != 1)
                    continue;

                Type argumentType =
                    parameters[0].ParameterType;

                object argument;
                if (argumentType.IsEnum)
                {
                    argument = Enum.ToObject(
                        argumentType,
                        shaderIndex);
                }
                else if (argumentType == typeof(int))
                {
                    argument = shaderIndex;
                }
                else
                {
                    continue;
                }

                return method.Invoke(
                    null,
                    new[] { argument }) as Shader;
            }

            return null;
        }

        private static bool GetTransmittedLightEnabled(
            TransmittedLightPass.Parameter parameter)
        {
            object boxed = parameter;
            Type type = boxed.GetType();
            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            string[] names =
            {
                "IsEnable",
                "isEnable",
                "_isEnable",
                "Enable",
                "enable",
                "_enable"
            };

            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo field =
                    type.GetField(names[i], flags);

                if (field != null &&
                    field.FieldType == typeof(bool))
                {
                    return (bool)field.GetValue(boxed);
                }

                PropertyInfo property =
                    type.GetProperty(names[i], flags);

                if (property != null &&
                    property.PropertyType == typeof(bool) &&
                    property.CanRead)
                {
                    return (bool)property.GetValue(
                        boxed,
                        null);
                }
            }

            /*
             * Official field is the first bool at struct offset +0x10.
             */
            FieldInfo[] fields = type.GetFields(flags);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType == typeof(bool))
                    return (bool)fields[i].GetValue(boxed);
            }

            return false;
        }

        private static Parameter ResolveFeatureParameter(
            CameraData cameraData)
        {
            if (cameraData == null)
                return null;

            object instance = cameraData;
            Type type = instance.GetType();
            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            while (type != null)
            {
                FieldInfo[] fields =
                    type.GetFields(flags);

                for (int i = 0; i < fields.Length; i++)
                {
                    if (typeof(Parameter)
                        .IsAssignableFrom(fields[i].FieldType))
                    {
                        return fields[i].GetValue(instance)
                            as Parameter;
                    }
                }

                PropertyInfo[] properties =
                    type.GetProperties(flags);

                for (int i = 0;
                     i < properties.Length;
                     i++)
                {
                    PropertyInfo property =
                        properties[i];

                    if (!property.CanRead ||
                        property.GetIndexParameters().Length != 0)
                    {
                        continue;
                    }

                    if (!typeof(Parameter)
                        .IsAssignableFrom(property.PropertyType))
                    {
                        continue;
                    }

                    return property.GetValue(
                        instance,
                        null) as Parameter;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static void SetSourcePassDepthRequest(SetSourceRTPass pass, bool value)
        {
            if (pass == null)
                throw new NullReferenceException(nameof(pass));

            Type type = pass.GetType();
            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;

            string[] names =
            {
                "IsRequestDepthPass",
                "_isRequestDepthPass",
                "isRequestDepthPass",
                "IsRequireDepthTexture",
                "_isRequireDepthTexture",
                "RequireDepthTexture",
                "_requireDepthTexture"
            };

            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo field =
                    type.GetField(names[i], flags);

                if (field != null &&
                    field.FieldType == typeof(bool))
                {
                    field.SetValue(pass, value);
                    return;
                }

                PropertyInfo property =
                    type.GetProperty(names[i], flags);

                if (property != null &&
                    property.PropertyType == typeof(bool) &&
                    property.CanWrite)
                {
                    property.SetValue(pass, value, null);
                    return;
                }
            }

            FieldInfo[] fields = type.GetFields(flags);
            for (int i = fields.Length - 1; i >= 0; i--)
            {
                if (fields[i].FieldType == typeof(bool))
                {
                    fields[i].SetValue(pass, value);
                    return;
                }
            }

            throw new MissingFieldException(
                type.FullName,
                "depth request bool at native +0xC8");
        }

        private static void DisposePass(object pass)
        {
            if (pass == null)
                return;

            if (pass is IDisposable disposable)
            {
                disposable.Dispose();
                return;
            }

            MethodInfo dispose =
                pass.GetType().GetMethod(
                    "Dispose",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

            if (dispose != null)
                dispose.Invoke(pass, null);
        }

        public class Parameter
        {
            private Camera _targetCamera;
            private CameraData _targetCameraData;

            public bool IsEnable;
            // public BlurOptimizePass.Parameter BlurOptimized;
            // public GlobalFogPass.Parameter GlobalFog;
            // public SunShaftsPass.Parameter SunShafts;
            // public IndirectLightShaftsPass.Parameter IndirectLightShafts;
            // public TransmittedLightPass.Parameter TransmittedLight;
            public DofDiffusionBloomOverlayPass.Parameter
                DofDiffuionBloomOverlay;
            // public TiltShiftPass.Parameter TiltShift;
            // public RadialBlurPass.Parameter RadialBlur;
            // public FluctuationPass.Parameter Fluctuation;
            // public LensDistortionPass.Parameter LensDistortion;
            // public ChromaticAberrationPass.Parameter
                //ChromaticAberration;
            // public ToneCurvePass.Parameter ToneCurve;
            // public ExposurePass.Parameter Exposure;
            // public ColorCorrectionPass.Parameter ColorCorrection;
            // public ColorGradingPass.Parameter ColorGrading;
            // public BgBlurPass.Parameter BgBlur;
            // public VortexPass.Parameter Vortex;
            // public AuraPass.Parameter Aura;
            // public FilmRollPass.Parameter FilmRoll;
            // public HatchingPass.Parameter Hatching;
            // public LetterBoxPass.Parameter LetterBox;
            // public RainSplashPass.Parameter RainSplash;

            // public ImageEffectCallbackPass.ImageEffectCallback
            //     OnPrepareScreenMakeup;
            // public ImageEffectCallbackPass.ImageEffectCallback
            //     OnPrepareLastScreenMarkup;
            // public ImageEffectCallbackPass.ImageEffectCallback
            //     OnAfterImageEffect;

            public bool IsSimpleMode;
            public Texture ForegroundTexture;
            public bool IsForegroundTextureOnly;
            public RenderTexture PreEffectTexture;
            public bool IsRequestDepthPass;

            public Camera TargetCamera
            {
                get { return _targetCamera; }
                set
                {
                    _targetCamera = value;

                    DofDiffuionBloomOverlay.TargetCamera =
                        _targetCamera;
                    // SunShafts.TargetCamera = _targetCamera;
                    // GlobalFog.TargetCamera = _targetCamera;
                    // RadialBlur.TargetCamera = _targetCamera;
                    // TransmittedLight.TargetCamera = _targetCamera;

                    // // Official duplicate assignment.
                    // SunShafts.TargetCamera = _targetCamera;

                    // RainSplash.TargetCamera = _targetCamera;

                    if (_targetCamera != null)
                    {
                        if (!_targetCamera.TryGetComponent(
                                out _targetCameraData))
                        {
                            _targetCameraData =
                                _targetCamera.gameObject
                                    .AddComponent<CameraData>();

                            InvokeCameraDataInitialize(
                                _targetCameraData);
                        }
                    }

                    TransmittedLight.TargetCameraData =
                        _targetCameraData;
                    RainSplash.TargetCameraData =
                        _targetCameraData;
                }
            }

            public bool IsUseDepthTexture
            {
                get
                {
                    return
                        DofDiffuionBloomOverlay.IsDepthTexture
                        | SunShafts.IsValidity
                        | GetTransmittedLightEnabled(
                            TransmittedLight)
                        | ColorCorrection.IsValidity
                        | GlobalFog.IsValidity
                        | RadialBlur.IsValidity
                        | Fluctuation.IsValidity
                        | ToneCurve.IsValidity
                        | Exposure.IsValidity
                        | Vortex.IsValidity
                        | Aura.IsDepthTexture;
                }
            }

            public Parameter()
            {
                IsEnable = true;

                // BlurOptimized =
                //     BlurOptimizePass.Parameter.Default();
                // GlobalFog =
                //     GlobalFogPass.Parameter.Default();
                // SunShafts =
                //     SunShaftsPass.Parameter.Default();
                // IndirectLightShafts =
                //     IndirectLightShaftsPass.Parameter.Default();

                // TransmittedLight =
                //     CreateDefaultTransmittedLight();

                DofDiffuionBloomOverlay =
                    DofDiffusionBloomOverlayPass
                        .Parameter.Default();
                // TiltShift =
                //     TiltShiftPass.Parameter.Default();
                // RadialBlur =
                //     RadialBlurPass.Parameter.Default();
                // Fluctuation =
                //     FluctuationPass.Parameter.Default();
                // LensDistortion =
                //     LensDistortionPass.Parameter.Default();
                // ChromaticAberration =
                //     ChromaticAberrationPass.Parameter.Default();
                // ToneCurve =
                //     ToneCurvePass.Parameter.Default();
                // Exposure =
                //     ExposurePass.Parameter.Default();
                // ColorCorrection =
                //     ColorCorrectionPass.Parameter.Default();
                // ColorGrading =
                //     ColorGradingPass.Parameter.Default();
                // BgBlur =
                //     BgBlurPass.Parameter.Default();
                // Vortex =
                //     VortexPass.Parameter.Default();
                // Aura =
                //     AuraPass.Parameter.Default();
                // FilmRoll =
                //     FilmRollPass.Parameter.Default();
                // Hatching =
                //     HatchingPass.Parameter.Default();
                // LetterBox =
                //     LetterBoxPass.Parameter.Default();
                // RainSplash =
                //     RainSplashPass.Parameter.Default();

                IsRequestDepthPass = true;
            }

            public void ShallowCopyFrom(Parameter src)
            {
                if (src == null)
                    throw new ArgumentNullException(nameof(src));

                ShallowCopyParameters(src);

                // OnPrepareScreenMakeup =
                //     src.OnPrepareScreenMakeup;
                // OnPrepareLastScreenMarkup =
                //     src.OnPrepareLastScreenMarkup;
                // OnAfterImageEffect =
                //     src.OnAfterImageEffect;
                IsSimpleMode =
                    src.IsSimpleMode;
                ForegroundTexture =
                    src.ForegroundTexture;
                IsForegroundTextureOnly =
                    src.IsForegroundTextureOnly;
                PreEffectTexture =
                    src.PreEffectTexture;
                IsRequestDepthPass =
                    src.IsRequestDepthPass;
            }

            public void ShallowCopyParameters(Parameter src)
            {
                if (src == null)
                    throw new ArgumentNullException(nameof(src));

                // BlurOptimized = src.BlurOptimized;
                // GlobalFog = src.GlobalFog;
                // SunShafts = src.SunShafts;
                // TransmittedLight = src.TransmittedLight;
                // IndirectLightShafts =
                //     src.IndirectLightShafts;
                // DofDiffuionBloomOverlay =
                //     src.DofDiffuionBloomOverlay;
                // TiltShift = src.TiltShift;
                // RadialBlur = src.RadialBlur;
                // Fluctuation = src.Fluctuation;
                // LensDistortion = src.LensDistortion;
                // ChromaticAberration =
                //     src.ChromaticAberration;
                // ToneCurve = src.ToneCurve;
                // Exposure = src.Exposure;
                // ColorCorrection = src.ColorCorrection;
                // ColorGrading = src.ColorGrading;
                // BgBlur = src.BgBlur;
                // Vortex = src.Vortex;
                // Aura = src.Aura;
                // FilmRoll = src.FilmRoll;
                // Hatching = src.Hatching;
                // LetterBox = src.LetterBox;
                // RainSplash = src.RainSplash;
            }

            public void CopyFromGallopImageEffect(
                GallopImageEffectParameter imageEffectParam)
            {
                if (imageEffectParam == null)
                {
                    throw new ArgumentNullException(
                        nameof(imageEffectParam));
                }

                // GlobalFog.Setup(
                //     imageEffectParam.GlobalFog);
                // SunShafts.Setup(
                //     imageEffectParam.SunShafts);
                // TiltShift.Setup(
                //     imageEffectParam.TiltShift);
                // IndirectLightShafts.Setup(
                //     imageEffectParam.IndirectLightShafts);
                // RadialBlur.Setup(
                //     imageEffectParam.RadialBlur);
                DofDiffuionBloomOverlay.Setup(
                    imageEffectParam
                        .DofDiffusionBloomOverlayParam);
                // LensDistortion.Setup(
                //     imageEffectParam.LensDistortion);
                // ColorCorrection.Setup(
                //     imageEffectParam.ColorCorrection);
                // Fluctuation.Setup(
                //     imageEffectParam.Fluctuation);
                // ChromaticAberration.Setup(
                //     imageEffectParam.ChromaticAberration);
                // ToneCurve.Setup(
                //     imageEffectParam.ToneCurve);
                // Exposure.Setup(
                //     imageEffectParam.Exposure);
                // Hatching.Setup(
                //     imageEffectParam.Hatching);
                // LetterBox.Setup(
                //     imageEffectParam.LetterBox);
                // RainSplash.Setup(
                //     imageEffectParam.RainSplash);
            }

            private static void InvokeCameraDataInitialize(
                CameraData cameraData)
            {
                MethodInfo[] methods =
                    cameraData.GetType().GetMethods(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "Initialize")
                        continue;

                    ParameterInfo[] parameters =
                        method.GetParameters();

                    if (parameters.Length != 1)
                        continue;

                    object argument;
                    Type type = parameters[0].ParameterType;

                    if (type == typeof(bool))
                        argument = false;
                    else if (type == typeof(int))
                        argument = 0;
                    else
                        continue;

                    method.Invoke(
                        cameraData,
                        new[] { argument });
                    return;
                }

                throw new MissingMethodException(
                    cameraData.GetType().FullName,
                    "Initialize(bool/int)");
            }

            // private static TransmittedLightPass.Parameter
            //     CreateDefaultTransmittedLight()
            // {
            //     TransmittedLightPass.Parameter result =
            //         default(TransmittedLightPass.Parameter);

            //     object boxed = result;
            //     Type type = boxed.GetType();
            //     const BindingFlags flags =
            //         BindingFlags.Instance |
            //         BindingFlags.Public |
            //         BindingFlags.NonPublic;

            //     FieldInfo[] fields =
            //         type.GetFields(flags);

            //     Array.Sort(
            //         fields,
            //         delegate(FieldInfo left, FieldInfo right)
            //         {
            //             return left.MetadataToken.CompareTo(
            //                 right.MetadataToken);
            //         });

            //     int numericIndex = 0;

            //     for (int i = 0; i < fields.Length; i++)
            //     {
            //         FieldInfo field = fields[i];
            //         Type fieldType = field.FieldType;

            //         if (!fieldType.IsValueType)
            //         {
            //             field.SetValue(boxed, null);
            //             continue;
            //         }

            //         if (fieldType == typeof(bool))
            //         {
            //             field.SetValue(boxed, false);
            //             continue;
            //         }

            //         if (fieldType == typeof(float))
            //         {
            //             field.SetValue(
            //                 boxed,
            //                 numericIndex == 1 ? 1f : 0f);
            //             numericIndex++;
            //             continue;
            //         }

            //         if (fieldType.IsEnum)
            //         {
            //             int value =
            //                 numericIndex == 0 ||
            //                 numericIndex == 4
            //                     ? 1
            //                     : 0;

            //             field.SetValue(
            //                 boxed,
            //                 Enum.ToObject(
            //                     fieldType,
            //                     value));

            //             numericIndex++;
            //             continue;
            //         }

            //         if (fieldType == typeof(int))
            //         {
            //             int value =
            //                 numericIndex == 0 ||
            //                 numericIndex == 4
            //                     ? 1
            //                     : 0;

            //             field.SetValue(boxed, value);
            //             numericIndex++;
            //         }
            //     }

            //     return (TransmittedLightPass.Parameter)boxed;
            // }
        }
    }
}
#endif