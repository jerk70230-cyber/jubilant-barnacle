Shader "Hidden/ColdCar/TileSurfaceLitFPS"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.08
        _CutawayEnabled ("Indoor Wall Cutaway", Float) = 0
        _CutawayPlayerPosition ("Cutaway Player Position", Vector) = (0,0,0,0)
        _CutawayCameraPosition ("Cutaway Camera Position", Vector) = (0,0,0,0)
        _CutawayWorldToLocal0 ("Cutaway Zone World To Local Row 0", Vector) = (1,0,0,0)
        _CutawayWorldToLocal1 ("Cutaway Zone World To Local Row 1", Vector) = (0,1,0,0)
        _CutawayWorldToLocal2 ("Cutaway Zone World To Local Row 2", Vector) = (0,0,1,0)
        _CutawayZoneCenter ("Cutaway Zone Center", Vector) = (0,0,0,0)
        _CutawayZoneHalfSize ("Cutaway Zone Half Size", Vector) = (0,0,0,0)
        _CutawayDistance ("Cutaway Distance", Float) = 0.04

        // Per-renderer indoor reveal values are explicit ShaderLab properties so
        // the combined wall Renderer receives its MaterialPropertyBlock overrides.
        _FogWallRevealEnabled ("Indoor Wall Reveal Enabled", Float) = 0
        _FogWallZoneWorldToLocal0 ("Indoor Wall World To Local Row 0", Vector) = (1,0,0,0)
        _FogWallZoneWorldToLocal1 ("Indoor Wall World To Local Row 1", Vector) = (0,1,0,0)
        _FogWallZoneWorldToLocal2 ("Indoor Wall World To Local Row 2", Vector) = (0,0,1,0)
        _FogWallZoneCenter ("Indoor Wall Zone Center", Vector) = (0,0,0,0)
        _FogWallZoneHalfSize ("Indoor Wall Zone Half Size", Vector) = (0,0,0,0)
        _FogWallZoneScale ("Indoor Wall Zone Scale", Vector) = (1,1,1,0)
        _FogWallRevealFactor ("Indoor Wall Reveal Factor", Float) = 0
        _FogWallEdgeFeather ("Indoor Wall Edge Feather", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        // Main colour pass. Alpha is treated as a cutout, not translucency, so
        // every visible wall texel writes a stable opaque depth value.
        Pass
        {
            Name "TileSurfaceForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _CutawayEnabled;
            float4 _CutawayPlayerPosition;
            float4 _CutawayCameraPosition;
            float4 _CutawayWorldToLocal0;
            float4 _CutawayWorldToLocal1;
            float4 _CutawayWorldToLocal2;
            float4 _CutawayZoneCenter;
            float4 _CutawayZoneHalfSize;
            float _CutawayDistance;

            void ApplyIndoorWallCutaway(float3 positionWS, float3 normalWS)
            {
                if (_CutawayEnabled < 0.5) return;

                // Restrict this operation to the wall mesh associated with this
                // indoor zone. The renderer can contain several connected walls.
                float4 position = float4(positionWS, 1.0);
                float3 zoneLocal = float3(
                    dot(_CutawayWorldToLocal0, position),
                    dot(_CutawayWorldToLocal1, position),
                    dot(_CutawayWorldToLocal2, position));
                float3 zoneRelative = abs(zoneLocal - _CutawayZoneCenter.xyz);
                if (any(zoneRelative > _CutawayZoneHalfSize.xyz + float3(0.25, 0.10, 0.25))) return;

                // Only vertical wall faces participate; never cut horizontal
                // floors, caps, or roofs that happen to share this material.
                if (abs(normalWS.y) > 0.35) return;

                // The requested cutaway is deliberately fixed to the building's
                // local EAST (+X) side. Do not infer the side from camera-facing
                // normals: a double-sided/combined wall mesh can have normals on
                // both sides and accidentally remove both East and West walls.
                float eastX = _CutawayZoneCenter.x + _CutawayZoneHalfSize.x;
                const float EAST_WALL_BAND = 0.45;
                float distanceToEastFace = abs(zoneLocal.x - eastX);
                if (distanceToEastFace > EAST_WALL_BAND) return;

                // Discard only this narrow vertical +X boundary band. The West
                // (-X) wall and the two remaining sides stay visible.
                clip(-1.0);
            }

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                half3 vertexSH : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = pos.positionCS;
                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                output.vertexSH = SampleSH(output.normalWS);
                output.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(baseColor.a - _Cutoff);
                ApplyIndoorWallCutaway(input.positionWS, normalize(input.normalWS));

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseColor.rgb;
                surfaceData.metallic = 0.0h;
                surfaceData.specular = half3(0.04h, 0.04h, 0.04h);
                surfaceData.smoothness = 0.12h;
                surfaceData.normalTS = half3(0.0h, 0.0h, 1.0h);
                surfaceData.occlusion = 1.0h;
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                surfaceData.alpha = 1.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalize(input.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(input.positionWS, inputData.normalWS);
                inputData.bakedGI = input.vertexSH;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

                half4 litColor = UniversalFragmentPBR(inputData, surfaceData);
                litColor.rgb = MixFog(litColor.rgb, input.fogFactor);
                litColor.a = 1.0h;
                return litColor;
            }
            ENDHLSL
        }

        // Keep wall surfaces in URP's regular camera depth texture as well.
        // The dedicated wall-only line-of-sight pass below uses its own depth target.
        Pass
        {
            Name "TileSurfaceDepthOnly"
            Tags { "LightMode"="DepthOnly" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _CutawayEnabled;
            float4 _CutawayPlayerPosition;
            float4 _CutawayCameraPosition;
            float4 _CutawayWorldToLocal0;
            float4 _CutawayWorldToLocal1;
            float4 _CutawayWorldToLocal2;
            float4 _CutawayZoneCenter;
            float4 _CutawayZoneHalfSize;
            float _CutawayDistance;

            void ApplyIndoorWallCutaway(float3 positionWS, float3 normalWS)
            {
                if (_CutawayEnabled < 0.5) return;

                // Restrict this operation to the wall mesh associated with this
                // indoor zone. The renderer can contain several connected walls.
                float4 position = float4(positionWS, 1.0);
                float3 zoneLocal = float3(
                    dot(_CutawayWorldToLocal0, position),
                    dot(_CutawayWorldToLocal1, position),
                    dot(_CutawayWorldToLocal2, position));
                float3 zoneRelative = abs(zoneLocal - _CutawayZoneCenter.xyz);
                if (any(zoneRelative > _CutawayZoneHalfSize.xyz + float3(0.25, 0.10, 0.25))) return;

                // Only vertical wall faces participate; never cut horizontal
                // floors, caps, or roofs that happen to share this material.
                if (abs(normalWS.y) > 0.35) return;

                // The requested cutaway is deliberately fixed to the building's
                // local EAST (+X) side. Do not infer the side from camera-facing
                // normals: a double-sided/combined wall mesh can have normals on
                // both sides and accidentally remove both East and West walls.
                float eastX = _CutawayZoneCenter.x + _CutawayZoneHalfSize.x;
                const float EAST_WALL_BAND = 0.45;
                float distanceToEastFace = abs(zoneLocal.x - eastX);
                if (distanceToEastFace > EAST_WALL_BAND) return;

                // Discard only this narrow vertical +X boundary band. The West
                // (-X) wall and the two remaining sides stay visible.
                clip(-1.0);
            }

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyIndoorWallCutaway(input.positionWS, normalize(input.normalWS));
                return 0;
            }
            ENDHLSL
        }


        // Dedicated wall-only linear-depth pass. FogOfWarController draws only
        // renderers on the configured wall layer using LightMode="WallOcclusion".
        // The color target stores positive camera-eye depth in R32_SFloat; a
        // separate hardware depth attachment resolves overlapping wall meshes.
        Pass
        {
            Name "WallOcclusion"
            Tags { "LightMode"="WallOcclusion" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask R

            HLSLPROGRAM
            #pragma vertex WallVert
            #pragma fragment WallFrag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _CutawayEnabled;
            float4 _CutawayPlayerPosition;
            float4 _CutawayCameraPosition;
            float4 _CutawayWorldToLocal0;
            float4 _CutawayWorldToLocal1;
            float4 _CutawayWorldToLocal2;
            float4 _CutawayZoneCenter;
            float4 _CutawayZoneHalfSize;
            float _CutawayDistance;

            void ApplyIndoorWallCutaway(float3 positionWS, float3 normalWS)
            {
                if (_CutawayEnabled < 0.5) return;

                // Restrict this operation to the wall mesh associated with this
                // indoor zone. The renderer can contain several connected walls.
                float4 position = float4(positionWS, 1.0);
                float3 zoneLocal = float3(
                    dot(_CutawayWorldToLocal0, position),
                    dot(_CutawayWorldToLocal1, position),
                    dot(_CutawayWorldToLocal2, position));
                float3 zoneRelative = abs(zoneLocal - _CutawayZoneCenter.xyz);
                if (any(zoneRelative > _CutawayZoneHalfSize.xyz + float3(0.25, 0.10, 0.25))) return;

                // Only vertical wall faces participate; never cut horizontal
                // floors, caps, or roofs that happen to share this material.
                if (abs(normalWS.y) > 0.35) return;

                // The requested cutaway is deliberately fixed to the building's
                // local EAST (+X) side. Do not infer the side from camera-facing
                // normals: a double-sided/combined wall mesh can have normals on
                // both sides and accidentally remove both East and West walls.
                float eastX = _CutawayZoneCenter.x + _CutawayZoneHalfSize.x;
                const float EAST_WALL_BAND = 0.45;
                float distanceToEastFace = abs(zoneLocal.x - eastX);
                if (distanceToEastFace > EAST_WALL_BAND) return;

                // Discard only this narrow vertical +X boundary band. The West
                // (-X) wall and the two remaining sides stay visible.
                clip(-1.0);
            }

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;

            struct WallAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct WallVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
            };

            WallVaryings WallVert(WallAttributes input)
            {
                WallVaryings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return output;
            }

            float4 WallFrag(WallVaryings input) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyIndoorWallCutaway(input.positionWS, normalize(input.normalWS));
                float eyeDepth = max(-TransformWorldToView(input.positionWS).z, 0.001);
                return float4(eyeDepth, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }

        // Draw the actual generated wall mesh into the indoor reveal mask.
        // The renderer list uses this LightMode tag and the camera's exact
        // projection. The read-only camera depth attachment rejects fragments
        // hidden by foreground geometry using the hardware depth test.
        Pass
        {
            Name "FogWallRevealMask"
            Tags { "LightMode"="FogWallRevealMask" }
            Cull Off
            ZWrite Off
            // The pass is attached to the camera's read-only depth buffer by
            // RenderGraph. Let the fixed-function depth test retain only the
            // wall fragments actually visible to this exact camera projection.
            ZTest LEqual
            ColorMask R

            HLSLPROGRAM
            #pragma vertex FogWallMaskVert
            #pragma fragment FogWallMaskFrag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _Cutoff;

            float _CutawayEnabled;
            float4 _CutawayWorldToLocal0;
            float4 _CutawayWorldToLocal1;
            float4 _CutawayWorldToLocal2;
            float4 _CutawayZoneCenter;
            float4 _CutawayZoneHalfSize;

            float _FogWallRevealEnabled;
            float4 _FogWallZoneWorldToLocal0;
            float4 _FogWallZoneWorldToLocal1;
            float4 _FogWallZoneWorldToLocal2;
            float4 _FogWallZoneCenter;
            float4 _FogWallZoneHalfSize;
            float4 _FogWallZoneScale;
            float _FogWallRevealFactor;
            float _FogWallEdgeFeather;

            struct FogWallMaskAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FogWallMaskVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            FogWallMaskVaryings FogWallMaskVert(FogWallMaskAttributes input)
            {
                FogWallMaskVaryings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return output;
            }

            void ApplyMaskCutaway(float3 positionWS)
            {
                if (_CutawayEnabled < 0.5) return;
                float4 p = float4(positionWS, 1.0);
                float3 local = float3(
                    dot(_CutawayWorldToLocal0, p),
                    dot(_CutawayWorldToLocal1, p),
                    dot(_CutawayWorldToLocal2, p));
                float3 relative = abs(local - _CutawayZoneCenter.xyz);
                if (any(relative > _CutawayZoneHalfSize.xyz + float3(0.25, 0.10, 0.25))) return;
                float eastX = _CutawayZoneCenter.x + _CutawayZoneHalfSize.x;
                if (abs(local.x - eastX) <= 0.45)
                    clip(-1.0);
            }

            half4 FogWallMaskFrag(FogWallMaskVaryings input) : SV_Target
            {
                clip(_FogWallRevealEnabled - 0.5);
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                ApplyMaskCutaway(input.positionWS);

                // Visibility is resolved by the hardware ZTest LEqual against the
                // cameraDepth attachment. Do not sample _CameraDepthTexture here:
                // this avoids an unbound/stale global depth sampler in this pass.

                float4 position = float4(input.positionWS, 1.0);
                float3 local = float3(
                    dot(_FogWallZoneWorldToLocal0, position),
                    dot(_FogWallZoneWorldToLocal1, position),
                    dot(_FogWallZoneWorldToLocal2, position));
                float3 relative = abs(local - _FogWallZoneCenter.xyz);
                float3 halfSize = _FogWallZoneHalfSize.xyz;
                float3 scale = max(_FogWallZoneScale.xyz, float3(0.0001, 0.0001, 0.0001));
                float heightEdge = (halfSize.y - abs(local.y - _FogWallZoneCenter.y)) * scale.y;
                float heightMask = step(-0.02, heightEdge);

                // A wall fragment must lie on one of the four real footprint
                // planes. This check is against the mesh's interpolated world
                // position, not against reconstructed screen depth.
                float edgeX = (halfSize.x - relative.x) * scale.x;
                float edgeZ = (halfSize.z - relative.z) * scale.z;
                float planeDistance = min(abs(edgeX), abs(edgeZ));
                float wallPlaneMask = 1.0 - smoothstep(0.015, 0.14, planeDistance);

                // Reuse Fog Edge Feather only at the two ends of each wall. Do
                // not fade the wall's top/bottom: the entire visible wall height
                // receives the same configured 65% reveal.
                float alongXWall = (halfSize.z - relative.z) * scale.z;
                float alongZWall = (halfSize.x - relative.x) * scale.x;
                float alongWall = abs(edgeX) <= abs(edgeZ) ? alongXWall : alongZWall;
                float feather = max(_FogWallEdgeFeather, 0.0);
                float cornerFade = feather > 1e-4
                    ? smoothstep(0.0, feather, max(alongWall, 0.0))
                    : 1.0;

                float reveal = saturate(_FogWallRevealFactor) * wallPlaneMask * cornerFade * heightMask;
                return half4(reveal, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
