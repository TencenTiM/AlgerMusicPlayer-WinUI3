using System.Runtime.InteropServices.WindowsRuntime;
using LiquidGlass.WinUI.Materials;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace LiquidGlass.WinUI.Effects;

/// <summary>
/// 成对的逐像素 SDF 水滴光学纹理：
/// - DisplacementMap：R 通道编码水平法向位移、G 通道编码垂直法向位移（128 为零位移基准）
/// - SpecularOverlayMap：预乘 Alpha 的 3D 解析法线 Fresnel 边缘反光、左上主高光弧、右下焦散亮唇与内侧凹液面暗槽
/// </summary>
public sealed class SdfLensTexturePair : IDisposable
{
    public required CanvasBitmap DisplacementMap { get; init; }
    public required CanvasBitmap SpecularOverlayMap { get; init; }
    public required CanvasBitmap BevelMaskMap { get; init; }
    public required int EffectiveBevelWidth { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    public void Dispose()
    {
        DisplacementMap.Dispose();
        SpecularOverlayMap.Dispose();
        BevelMaskMap.Dispose();
    }
}

/// <summary>
/// 逐像素圆角有符号距离场 (SDF) 3D 凸液面折射向量图与物理光学高光纹理生成器（带 LRU GPU 纹理缓存）。
/// </summary>
public sealed class SdfLiquidLensMapGenerator : IDisposable
{
    private readonly record struct CacheKey(
        int Width,
        int Height,
        int Radius,
        int BevelWidth,
        bool IsLight,
        LiquidGlassMaterialKind Kind);

    private const int MaxCacheEntries = 36;
    private readonly Dictionary<CacheKey, SdfLensTexturePair> _cache = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly object _gate = new();
    private CanvasDevice? _boundDevice;

    public SdfLensTexturePair GetOrCreate(
        CanvasDevice device,
        double width,
        double height,
        double cornerRadius,
        int bevelWidth,
        bool isLight,
        LiquidGlassMaterialKind kind)
    {
        // 按 2px 步进量化尺寸以在尺寸微调与动画时保持高缓存命中率
        int qw = Math.Max(8, ((int)Math.Round(width) + 1) & ~1);
        int qh = Math.Max(8, ((int)Math.Round(height) + 1) & ~1);
        int maxR = Math.Max(2, Math.Min(qw, qh) / 2);
        int qr = Math.Clamp((int)Math.Round(cornerRadius), 2, maxR);
        // 保证倒角宽度严格不超过有效半径与半边长，消除 45° 对角法线折痕，确保倒角带 C1 光滑
        int effR = Math.Clamp(Math.Max(qr, Math.Min(bevelWidth, maxR)), 2, maxR);
        int qBevel = Math.Clamp(bevelWidth, 4, effR);

        var key = new CacheKey(qw, qh, effR, qBevel, isLight, kind);

        lock (_gate)
        {
            if (!ReferenceEquals(_boundDevice, device))
            {
                ClearCacheUnsafe();
                _boundDevice = device;
            }

            if (_cache.TryGetValue(key, out var existing))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return existing;
            }

            var created = GeneratePair(device, qw, qh, effR, qBevel, isLight, kind);
            _cache[key] = created;
            _lru.AddFirst(key);

            while (_lru.Count > MaxCacheEntries && _lru.Last != null)
            {
                var victim = _lru.Last.Value;
                _lru.RemoveLast();
                if (_cache.TryGetValue(victim, out var oldPair))
                {
                    _cache.Remove(victim);
                    oldPair.Dispose();
                }
            }

            return created;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCacheUnsafe();
            _boundDevice = null;
        }
    }

    public void Dispose()
    {
        Clear();
    }

    private void ClearCacheUnsafe()
    {
        foreach (var pair in _cache.Values)
        {
            pair.Dispose();
        }
        _cache.Clear();
        _lru.Clear();
    }

    private static SdfLensTexturePair GeneratePair(
        CanvasDevice device,
        int w,
        int h,
        int radius,
        int bevelW,
        bool isLight,
        LiquidGlassMaterialKind kind)
    {
        int totalBytes = w * h * 4;
        byte[] dispBytes = new byte[totalBytes];
        byte[] specBytes = new byte[totalBytes];
        byte[] maskBytes = new byte[totalBytes];

        float halfW = w * 0.5f;
        float halfH = h * 0.5f;
        float r = Math.Min(radius, Math.Min(halfW, halfH));
        float bw = MathF.Max(4f, MathF.Min(bevelW, r));
        float innerExtentX = halfW - r;
        float innerExtentY = halfH - r;

        // 左上主高光向量 H1 与右下焦散透射向量 H2（均预归一化）
        const float h1x = -0.578f, h1y = -0.718f, h1z = 0.388f;
        const float h2x = 0.578f, h2y = 0.718f, h2z = 0.340f;

        bool isDock = kind == LiquidGlassMaterialKind.Dock;
        float specGain = isDock ? 1.0f : (kind == LiquidGlassMaterialKind.Button ? 0.95f : 0.86f);
        float troughMax = isLight ? 0.11f : 0.22f;

        for (int py = 0; py < h; py++)
        {
            float cy = py + 0.5f - halfH;
            float ay = MathF.Abs(cy);
            float qy = ay - innerExtentY;
            float signY = cy >= 0f ? 1f : -1f;

            int rowOffset = py * w * 4;
            for (int px = 0; px < w; px++)
            {
                float cx = px + 0.5f - halfW;
                float ax = MathF.Abs(cx);
                float qx = ax - innerExtentX;
                float signX = cx >= 0f ? 1f : -1f;

                // 计算圆角矩形精确有符号距离 dSigned (<0 在玻璃内部) 及单位外法线 (nx, ny)
                float dSigned;
                float nx;
                float ny;
                if (qx > 0f && qy > 0f)
                {
                    float len = MathF.Sqrt(qx * qx + qy * qy);
                    dSigned = len - r;
                    if (len > 1e-4f)
                    {
                        float inv = 1f / len;
                        nx = signX * qx * inv;
                        ny = signY * qy * inv;
                    }
                    else
                    {
                        nx = signX * 0.7071f;
                        ny = signY * 0.7071f;
                    }
                }
                else if (qx >= qy)
                {
                    dSigned = qx - r;
                    nx = signX;
                    ny = 0f;
                }
                else
                {
                    dSigned = qy - r;
                    nx = 0f;
                    ny = signY;
                }

                float dInside = -dSigned;
                float u = dInside <= 0f
                    ? (dInside > -1.5f ? 1f : 0f)
                    : Math.Clamp(1f - dInside / bw, 0f, 1f);

                int idx = rowOffset + px * 4;

                // 1. DisplacementMap (B8G8R8A8: [0]=B, [1]=G(垂直Y), [2]=R(水平X), [3]=A)
                // 非线性凸液面 Snell 位移斜率：中心平坦 (0)，近边缘急剧陡峭 (u^2 + u^4) 沿内法线 (-nx, -ny) 拉拽采样
                if (u > 0.001f)
                {
                    float mag = (0.26f * u * u + 0.74f * u * u * u * u);
                    float dx = -nx * mag;
                    float dy = -ny * mag;

                    dispBytes[idx + 0] = 128;
                    dispBytes[idx + 1] = (byte)Math.Clamp((int)MathF.Round(128f + dy * 127f), 0, 255);
                    dispBytes[idx + 2] = (byte)Math.Clamp((int)MathF.Round(128f + dx * 127f), 0, 255);
                    dispBytes[idx + 3] = 255;
                }
                else
                {
                    dispBytes[idx + 0] = 128;
                    dispBytes[idx + 1] = 128;
                    dispBytes[idx + 2] = 128;
                    dispBytes[idx + 3] = 255;
                }

                // 2. SpecularOverlayMap (Premultiplied Alpha)：边缘抗锯齿覆盖 + Fresnel + 左上高光弧 + 右下焦散 + 凹液面暗槽
                float cov = Math.Clamp(0.5f - dSigned, 0f, 1f);
                if (cov > 0.002f && u > 0.002f)
                {
                    float uClamped = Math.Clamp(u, 0f, 0.995f);
                    float nz = MathF.Sqrt(MathF.Max(0f, 1f - uClamped * uClamped));
                    float n3x = uClamped * nx;
                    float n3y = uClamped * ny;

                    // Schlick Fresnel 湿润边缘反光
                    float oneMinusNz = 1f - nz;
                    float fresnel = (oneMinusNz * oneMinusNz) * (oneMinusNz * oneMinusNz) * 0.32f;

                    // 左上锐利白色水滴镜面弧线
                    float dot1 = MathF.Max(0f, n3x * h1x + n3y * h1y + nz * h1z);
                    float spec1 = MathF.Pow(dot1, 22f) * 0.92f;

                    // 右下冰青色透射焦散亮唇
                    float dot2 = MathF.Max(0f, n3x * h2x + n3y * h2y + nz * h2z);
                    float caustic2 = MathF.Pow(dot2, 14f) * 0.66f;

                    // 极其精细的最外 1.2px 晶体包边，勾勒水珠轮廓
                    float outerRim = dInside >= 0f && dInside < 1.35f
                        ? (1f - dInside / 1.35f) * 0.34f
                        : 0f;

                    float bright = Math.Clamp((fresnel + spec1 + outerRim) * specGain, 0f, 1f);
                    float cyanLight = Math.Clamp(caustic2 * specGain, 0f, 0.95f);

                    // 凹液面立体厚度微暗吸收槽 (位于 u ~ 0.68 内侧)
                    float troughDelta = (u - 0.66f) / 0.14f;
                    float trough = MathF.Exp(-troughDelta * troughDelta) * troughMax * (1f - spec1 * 0.85f);

                    // 混合亮白、冰青焦散与微暗厚度槽，并输出为直觉准确的 Premultiplied BGRA
                    float totalLight = Math.Clamp(bright + cyanLight, 0f, 1f);
                    float rLin = bright * 255f + cyanLight * 212f;
                    float gLin = bright * 255f + cyanLight * 246f;
                    float bLin = bright * 255f + cyanLight * 255f;

                    float normDenom = MathF.Max(1f, MathF.Max(rLin, MathF.Max(gLin, bLin)));
                    float srcR = MathF.Min(255f, rLin * 255f / normDenom);
                    float srcG = MathF.Min(255f, gLin * 255f / normDenom);
                    float srcB = MathF.Min(255f, bLin * 255f / normDenom);

                    float lightAlpha = totalLight * cov;
                    float darkAlpha = trough * cov * (1f - lightAlpha);
                    float outAlpha = Math.Clamp(lightAlpha + darkAlpha, 0f, 1f);

                    // Premultiplied alpha: dark component contributes (0,0,0), light contributes lightAlpha * srcColor
                    specBytes[idx + 0] = (byte)Math.Clamp((int)MathF.Round(srcB * lightAlpha), 0, 255);
                    specBytes[idx + 1] = (byte)Math.Clamp((int)MathF.Round(srcG * lightAlpha), 0, 255);
                    specBytes[idx + 2] = (byte)Math.Clamp((int)MathF.Round(srcR * lightAlpha), 0, 255);
                    specBytes[idx + 3] = (byte)Math.Clamp((int)MathF.Round(outAlpha * 255f), 0, 255);
                }

                // 3. BevelMaskMap (Premultiplied Alpha 空心弯月面遮罩)：中心平坦区 (u <= 0.02) 保持完全透明，直接透出底层 0ms 原生 DWM 实时中心
                if (cov > 0.002f && u > 0.02f)
                {
                    float tMask = Math.Clamp((u - 0.02f) / 0.30f, 0f, 1f);
                    float bevelAlpha = tMask * tMask * (3f - 2f * tMask) * cov;
                    byte mByte = (byte)Math.Clamp((int)MathF.Round(bevelAlpha * 255f), 0, 255);
                    maskBytes[idx + 0] = mByte;
                    maskBytes[idx + 1] = mByte;
                    maskBytes[idx + 2] = mByte;
                    maskBytes[idx + 3] = mByte;
                }
            }
        }

        var dispBmp = CanvasBitmap.CreateFromBytes(device, dispBytes, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var specBmp = CanvasBitmap.CreateFromBytes(device, specBytes, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var maskBmp = CanvasBitmap.CreateFromBytes(device, maskBytes, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized);

        int effBevelBand = (int)MathF.Ceiling(MathF.Max(bw, 0.293f * r + 0.708f * bw));

        return new SdfLensTexturePair
        {
            DisplacementMap = dispBmp,
            SpecularOverlayMap = specBmp,
            BevelMaskMap = maskBmp,
            EffectiveBevelWidth = effBevelBand,
            Width = w,
            Height = h
        };
    }
}
