Shader "Hidden/ColdCar/PS1Post"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "PS1Post"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // URP Core first (XR/platform deps), then the core blit vertex shader
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _Quantize, _Dither, _Scanline, _Noise, _TimeN;
            float _EdgeSmoothing; // set from Ps1Filter.edgeSmoothing
            float4 _Res;
            float _DarkNoiseMul;  // noise/dither multiplier in dark areas (0 = clean dark)
            float _DarkLum;       // luminance below which a pixel counts as "dark"

            // 4x4 Bayer matrix
            static const float bayer4[16] =
            {
                 0,  8,  2, 10,
                12,  4, 14,  6,
                 3, 11,  1,  9,
                15,  7, 13,  5
            };

            float3 DitherAndQuantize(float3 c, float2 uv, float noiseMul)
            {
                int2 px = (int2)floor(uv * _Res.xy);
                float levels = pow(2.0, _Quantize) - 1.0;
                // dither amplitude must not exceed one quantization step
                float b = (bayer4[(px.x & 3) + (px.y & 3) * 4] / 16.0 - 0.5) * _Dither * noiseMul / levels;
                return floor((c + b) * levels + 0.5) / levels;
            }

            float rand(float2 co)
            {
                return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                // point-sampled pixelated color
                float2 snapped = (floor(uv * _Res.xy) + 0.5) * _Res.zw;
                float3 c = SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, snapped).rgb;

                // luminance-based noise control: kill grain/dither in the dark,
                // keep it in the lit areas
                float lum = dot(c, float3(0.299, 0.587, 0.114));
                float darkMask = 1.0 - smoothstep(_DarkLum * 0.4, _DarkLum, lum);
                float noiseMul = lerp(1.0, _DarkNoiseMul, darkMask);

                c = DitherAndQuantize(c, uv, noiseMul);

                // Small edge-aware blend. Flat regions keep their crisp pixels;
                // only strong local luminance transitions are softened to tame
                // ugly staircase edges without turning the whole image to blur.
                if (_EdgeSmoothing > 0.001)
                {
                    float2 px = _Res.zw;
                    float3 n = DitherAndQuantize(SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, snapped + float2(0, px.y)).rgb, uv + float2(0, px.y), noiseMul);
                    float3 s = DitherAndQuantize(SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, snapped - float2(0, px.y)).rgb, uv - float2(0, px.y), noiseMul);
                    float3 e = DitherAndQuantize(SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, snapped + float2(px.x, 0)).rgb, uv + float2(px.x, 0), noiseMul);
                    float3 w = DitherAndQuantize(SAMPLE_TEXTURE2D(_BlitTexture, sampler_PointClamp, snapped - float2(px.x, 0)).rgb, uv - float2(px.x, 0), noiseMul);
                    float lumN = dot(n, float3(0.299, 0.587, 0.114));
                    float lumS = dot(s, float3(0.299, 0.587, 0.114));
                    float lumE = dot(e, float3(0.299, 0.587, 0.114));
                    float lumW = dot(w, float3(0.299, 0.587, 0.114));
                    float localMin = min(lum, min(min(lumN, lumS), min(lumE, lumW)));
                    float localMax = max(lum, max(max(lumN, lumS), max(lumE, lumW)));
                    float edge = smoothstep(0.035, 0.22, localMax - localMin);
                    float3 softened = (c * 2.0 + n + s + e + w) / 6.0;
                    c = lerp(c, softened, edge * _EdgeSmoothing * 0.72);
                }

                // scanlines
                float scan = sin(uv.y * _Res.y * 3.14159) * 0.5 + 0.5;
                c *= 1.0 - _Scanline * scan * 0.9;

                // subtle noise (scaled by the same dark mask)
                c += (rand(uv * _TimeN) - 0.5) * _Noise * noiseMul;

                return float4(saturate(c), 1);
            }
            ENDHLSL
        }
    }
}
