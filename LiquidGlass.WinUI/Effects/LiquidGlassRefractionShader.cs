using ComputeSharp;
using ComputeSharp.D2D1;

namespace LiquidGlass.WinUI.Effects;

/// <summary>
/// ComputeSharp.D2D1 编译期预编译 HLSL 像素着色器（ps_4_0）：
/// 单 Pass 融合解析圆角 SDF、非线性 Snell 弯月面折射位移、R/G/B 三波长色散、
/// 微饱与凝露底釉、Schlick Fresnel 反光、左上迎光白弧、右下冰青焦散与内凹暗槽，
/// 彻底消除 CPU 纹理生成及 10 节点 Direct2D 多路特效图开销。
/// </summary>
[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader40)]
[D2DCompileOptions(D2D1CompileOptions.OptimizationLevel3)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct LiquidGlassRefractionShader(
    float2 controlSize,
    float radius,
    float bezel,
    float strength,
    float dispersion,
    float4 tint,
    float specGain,
    float troughMax) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 pos = D2D.GetScenePosition().XY;
        float2 halfSize = controlSize * 0.5f;
        float2 p = pos - halfSize;

        float minHalf = Hlsl.Min(halfSize.X, halfSize.Y);
        float r = Hlsl.Clamp(radius, 2.0f, minHalf);
        float maxEffR = Hlsl.Clamp(r, 2.0f, minHalf);
        float bw = Hlsl.Clamp(bezel, 4.0f, maxEffR);

        // 1. 解析圆角矩形 SDF 与 C1 连续单位外法线
        float2 innerHalf = halfSize - new float2(maxEffR, maxEffR);
        float2 q = Hlsl.Abs(p) - innerHalf;
        float2 qPos = Hlsl.Max(q, new float2(0.0f, 0.0f));
        float qLen = Hlsl.Length(qPos);
        float dSigned = Hlsl.Min(Hlsl.Max(q.X, q.Y), 0.0f) + qLen - maxEffR;

        float cov = Hlsl.Saturate(0.5f - dSigned);
        if (cov <= 0.002f)
        {
            return new float4(0.0f, 0.0f, 0.0f, 0.0f);
        }

        float dInside = -dSigned;
        float u = dInside <= 0.0f ? 1.0f : Hlsl.Saturate(1.0f - dInside / bw);
        if (u <= 0.015f)
        {
            // 平坦中心 100% 透明，直透底层零延迟原生 DWM CompositionBackdropBrush
            return new float4(0.0f, 0.0f, 0.0f, 0.0f);
        }

        float signX = p.X >= 0.0f ? 1.0f : -1.0f;
        float signY = p.Y >= 0.0f ? 1.0f : -1.0f;
        float2 n;
        if (q.X > 0.0f && q.Y > 0.0f)
        {
            n = qLen > 0.0001f
                ? new float2(signX * q.X / qLen, signY * q.Y / qLen)
                : new float2(signX * 0.7071f, signY * 0.7071f);
        }
        else if (q.X >= q.Y)
        {
            n = new float2(signX, 0.0f);
        }
        else
        {
            n = new float2(0.0f, signY);
        }

        // 2. 非线性 Snell 凸液面位移 + R/G/B 三波长光谱色散采样
        float u2 = u * u;
        float mag = 0.26f * u2 + 0.74f * u2 * u2;
        float2 disp = -n * (mag * strength);

        float colR = D2D.SampleInputAtPosition(0, pos + disp * (1.0f - dispersion)).R;
        float colG = D2D.SampleInputAtPosition(0, pos + disp).G;
        float colB = D2D.SampleInputAtPosition(0, pos + disp * (1.0f + dispersion * 1.2f)).B;
        float3 rgb = new float3(colR, colG, colB);

        // 3. 2.0 级别鲜亮高饱和微提色 + 轻薄纯白凝露底釉
        float luma = Hlsl.Dot(rgb, new float3(0.2126f, 0.7152f, 0.0722f));
        rgb = Hlsl.Saturate(new float3(luma, luma, luma) + (rgb - new float3(luma, luma, luma)) * 1.90f);
        rgb = Hlsl.Lerp(rgb, tint.RGB, tint.W);

        // 4. 克制中性纯白三维折射高光（无青绿粗边叠绘，外沿描边交由 XAML 1px 渐变 Border）
        float uClamp = Hlsl.Clamp(u, 0.0f, 0.995f);
        float nz = Hlsl.Sqrt(Hlsl.Max(0.0f, 1.0f - uClamp * uClamp));
        float3 n3 = new float3(uClamp * n.X, uClamp * n.Y, nz);

        float oneMinusNz = 1.0f - nz;
        float om2 = oneMinusNz * oneMinusNz;
        float fresnel = om2 * om2 * 0.05f;

        float dot1 = Hlsl.Max(0.0f, Hlsl.Dot(n3, new float3(-0.578f, -0.718f, 0.388f)));
        float d1_2 = dot1 * dot1;
        float d1_4 = d1_2 * d1_2;
        float d1_8 = d1_4 * d1_4;
        float spec1 = d1_8 * d1_8 * d1_4 * 0.16f;

        float dot2 = Hlsl.Max(0.0f, Hlsl.Dot(n3, new float3(0.578f, 0.718f, 0.340f)));
        float d2_2 = dot2 * dot2;
        float d2_4 = d2_2 * d2_2;
        float d2_8 = d2_4 * d2_4;
        float caustic2 = d2_8 * d2_4 * d2_2 * 0.10f;

        float tDelta = (u - 0.66f) / 0.14f;
        float trough = Hlsl.Exp(-tDelta * tDelta) * troughMax * (1.0f - spec1 * 0.85f);
        rgb *= (1.0f - trough);

        float specAdd = Hlsl.Saturate((fresnel + spec1 + caustic2) * specGain);
        rgb = Hlsl.Saturate(rgb + new float3(specAdd, specAdd, specAdd));

        // 5. 空心 Bevel 边缘羽化混合（中心 u <= 0.015 为 0，直透 DWM 实时中心）
        float tMask = Hlsl.Saturate((u - 0.015f) / 0.28f);
        float bevelAlpha = tMask * tMask * (3.0f - 2.0f * tMask) * cov;
        float outA = Hlsl.Saturate(Hlsl.Max(bevelAlpha, specAdd * cov));
        return new float4(rgb * outA, outA);
    }
}
