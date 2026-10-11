using System;
using UnityEngine;

/// <summary>在导出颜色空间中读取眼部贴图，保留各轴 wrap 和 point/bilinear 采样。</summary>
internal sealed class PMXEyeTextureSampler
{
    internal readonly int Width, Height;
    private readonly Color[] _pixels;
    private readonly TextureWrapMode _wrapU, _wrapV;
    private readonly bool _point;

    internal PMXEyeTextureSampler(Texture2D source)
    {
        if (source == null) throw new InvalidOperationException("缺少主眼纹理。");
        Width = source.width; Height = source.height;
        _wrapU = source.wrapModeU; _wrapV = source.wrapModeV;
        _point = source.filterMode == FilterMode.Point;
        if (source.isReadable)
        {
            _pixels = source.GetPixels();
            if (QualitySettings.activeColorSpace == ColorSpace.Linear && source.isDataSRGB)
                for (int i = 0; i < _pixels.Length; i++) _pixels[i] = _pixels[i].linear;
        }
        else
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("不可读眼部贴图需要有效图形设备才能回读。");
            Texture2D copy = null;
            RenderTexture target = RenderTexture.GetTemporary(Width, Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                copy = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
                copy.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                copy.Apply();
                _pixels = copy.GetPixels();
            }
            finally
            {
                GL.sRGBWrite = previousSrgb;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                PMXEyeTextureExporter.DestroyTemporary(copy);
            }
        }
    }

    internal Color Sample(Vector2 uv)
    {
        if (_point) return Pixel(Mathf.FloorToInt(uv.x * Width), Mathf.FloorToInt(uv.y * Height));
        float x = uv.x * Width - 0.5f, y = uv.y * Height - 0.5f;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        Color lower = Color.LerpUnclamped(Pixel(x0, y0), Pixel(x0 + 1, y0), x - x0);
        Color upper = Color.LerpUnclamped(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), x - x0);
        return Color.LerpUnclamped(lower, upper, y - y0);
    }

    private Color Pixel(int x, int y) => _pixels[Wrap(y, Height, _wrapV) * Width + Wrap(x, Width, _wrapU)];

    private static int Wrap(int index, int size, TextureWrapMode mode)
    {
        if (mode == TextureWrapMode.Clamp) return Mathf.Clamp(index, 0, size - 1);
        if (mode == TextureWrapMode.MirrorOnce) return Mathf.Clamp(index < 0 ? -index - 1 : index, 0, size - 1);
        int period = mode == TextureWrapMode.Mirror ? size * 2 : size;
        index = (index % period + period) % period;
        return mode == TextureWrapMode.Mirror && index >= size ? period - 1 - index : index;
    }
}
