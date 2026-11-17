
Shader "ColdCar/URP/Spherical Clip"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)

        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5

        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Range(0,2)) = 1

        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,0)
        _EmissionMap ("Emission Map", 2D) = "white" {}

        [Header(Spherical Clipping)]
        _ClipSphere ("Clip Sphere", Vector) = (0,0,0,0.5)
        _Feather ("Edge Softness", Range(0.001, 2)) = 0.15

        [Header(Transparency)]
        _Opacity ("Opacity", Range(0,1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM

            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _EMISSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _EmissionColor;
                float4 _ClipSphere;

                half _Metallic;
                half _Smoothness;
                half _BumpScale;
                half _Feather;
                half _Opacity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float2 uv : TEXCOORD3;
                half3 vertexSH : TEXCOORD4;
                half fogFactor : TEXCOORD5;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs pos =
                    GetVertexPositionInputs(input.positionOS.xyz);

                VertexNormalInputs normal =
                    GetVertexNormalInputs(
                        input.normalOS,
                        input.tangentOS
                    );

                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = normal.normalWS;

                output.tangentWS = float4(
                    normal.tangentWS,
                    input.tangentOS.w * GetOddNegativeScale()
                );

                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.vertexSH = SampleSH(normal.normalWS);
                output.fogFactor = ComputeFogFactor(pos.positionCS.z);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Distance from spherical clipping boundary.
                float distanceFromCenter =
                    distance(input.positionWS, _ClipSphere.xyz);

                float edge =
                    distanceFromCenter - _ClipSphere.w;

                // Smooth alpha transition.
                half sphereAlpha = smoothstep(
                    0.0,
                    max(_Feather, 0.001),
                    edge
                );

                half4 baseSample = SAMPLE_TEXTURE2D(
                    _BaseMap,
                    sampler_BaseMap,
                    input.uv
                ) * _BaseColor;

                half alpha =
                    baseSample.a * _Opacity * sphereAlpha;

                // Fully invisible pixels are discarded.
                clip(alpha - 0.001);

                // Normal mapping.
                half3 normalWS = normalize(input.normalWS);

                #if defined(_NORMALMAP)
                    half3 normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(
                            _BumpMap,
                            sampler_BumpMap,
                            input.uv
                        ),
                        _BumpScale
                    );

                    half3 bitangentWS =
                        cross(normalWS, input.tangentWS.xyz)
                        * input.tangentWS.w;

                    half3x3 tangentToWorld = half3x3(
                        normalize(input.tangentWS.xyz),
                        normalize(bitangentWS),
                        normalWS
                    );

                    normalWS = normalize(
                        TransformTangentToWorld(
                            normalTS,
                            tangentToWorld
                        )
                    );
                #endif

                SurfaceData surfaceData = (SurfaceData)0;

                surfaceData.albedo = baseSample.rgb;
                surfaceData.metallic = _Metallic;
                surfaceData.specular = half3(0.5, 0.5, 0.5);
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = half3(0, 0, 1);
                surfaceData.occlusion = 1;
                surfaceData.emission = SAMPLE_TEXTURE2D(
                    _EmissionMap,
                    sampler_EmissionMap,
                    input.uv
                ).rgb * _EmissionColor.rgb;
                surfaceData.alpha = alpha;

                InputData inputData = (InputData)0;

                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS =
                    GetWorldSpaceNormalizeViewDir(input.positionWS);

                inputData.shadowCoord =
                    TransformWorldToShadowCoord(input.positionWS);

                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(
                    input.positionWS,
                    normalWS
                );

                inputData.bakedGI = input.vertexSH;
                inputData.normalizedScreenSpaceUV =
                    GetNormalizedScreenSpaceUV(input.positionCS);

                inputData.shadowMask = half4(1,1,1,1);

                half4 color = UniversalFragmentPBR(
                    inputData,
                    surfaceData
                );

                color.rgb = MixFog(color.rgb, input.fogFactor);
                color.a = alpha;

                return color;
            }

            ENDHLSL
        }
    }

    FallBack Off
}
