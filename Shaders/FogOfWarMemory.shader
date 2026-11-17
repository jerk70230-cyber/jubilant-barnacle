Shader "Hidden/ColdCar/FogOfWarMemory"
{
    // Accumulates the explored fog-of-war memory: every frame the current
    // first-person vision (cone + near radius + wall LOS) is written into the
    // world-anchored explored RT with MAX blending, so anything once seen
    // stays revealed. The main fog overlay samples this RT by world XZ.
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        BlendOp Max
        Blend One One

        Pass
        {
            Name "FogMemoryAccumulate"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #define MAX_WALL_BOXES 64

            float3 _EyePos;                 // eye (character head), world
            float3 _ViewDir;                // facing, normalized, horizontal
            float _ConeCosIn;               // cos(inner cone edge) - full reveal
            float _ConeCosOut;              // cos(outer cone edge) - zero reveal
            float _ConeRange;               // vision range, meters
            float _ConeRangeEdge;           // soft edge of the range, meters
            float _NearRadius;              // always-visible radius around the character
            float _NearFeather;             // soft edge of the near radius, meters
            float2 _MemOrigin;              // world XZ of the explored RT corner
            float _MemSize;                 // world extent of the explored RT
            int _WallBoxCount;
            float4 _WallBoxCenter[MAX_WALL_BOXES];
            float4 _WallBoxHalfSize[MAX_WALL_BOXES];
            float4x4 _WallRootWorldToLocal;
            float _WallSurfTol;
            float _GroundY;                 // ground height for memory texels

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(float4 v : POSITION, float2 uv : TEXCOORD0)
            {
                v2f o;
                o.pos = float4(v.xy, 0.0, 1.0); // the quad mesh is already in NDC
                o.uv = uv;
                return o;
            }

            bool SegmentBlockedByWallBox(float3 startRel, float3 endRel, float3 halfSize, float tol)
            {
                if (all(abs(startRel) <= halfSize + 1e-4)) return false;
                if (all(abs(endRel) <= halfSize + max(tol, 0.0))) return false;

                float3 delta = endRel - startRel;
                bool3 parallel = abs(delta) < 1e-6;
                if (parallel.x && abs(startRel.x) > halfSize.x) return false;
                if (parallel.y && abs(startRel.y) > halfSize.y) return false;
                if (parallel.z && abs(startRel.z) > halfSize.z) return false;

                float3 safeDelta = float3(
                    parallel.x ? (delta.x < 0.0 ? -1e-6 : 1e-6) : delta.x,
                    parallel.y ? (delta.y < 0.0 ? -1e-6 : 1e-6) : delta.y,
                    parallel.z ? (delta.z < 0.0 ? -1e-6 : 1e-6) : delta.z);

                float3 t0 = (-halfSize - startRel) / safeDelta;
                float3 t1 = ( halfSize - startRel) / safeDelta;
                float3 tNear = min(t0, t1);
                float3 tFar = max(t0, t1);
                float tEnter = max(max(tNear.x, tNear.y), max(tNear.z, 0.0));
                float tExit = min(min(tFar.x, tFar.y), min(tFar.z, 1.0));
                return tExit >= tEnter && tEnter <= 1.0;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 worldXZ = _MemOrigin + i.uv * _MemSize;
                float3 worldPos = float3(worldXZ.x, _GroundY, worldXZ.y);

                float3 toP = worldPos - _EyePos;
                float dist = length(toP) + 1e-5;

                // horizontal view cone
                float2 hx = toP.xz / max(dist, 1e-4);
                float cone = dot(hx, normalize(_ViewDir.xz + float2(1e-5, 0)));
                float angleMask = smoothstep(_ConeCosOut, _ConeCosIn, cone);
                float rangeFade = 1.0 - smoothstep(_ConeRange - _ConeRangeEdge, _ConeRange, dist);
                float los = 1.0;

                if (_WallBoxCount > 0)
                {
                    float3 startLocal = mul(_WallRootWorldToLocal, float4(_EyePos, 1.0)).xyz;
                    float3 endLocal = mul(_WallRootWorldToLocal, float4(worldPos, 1.0)).xyz;
                    if (distance(endLocal.xz, startLocal.xz) > 1e-4)
                    {
                        [loop]
                        for (int k = 0; k < MAX_WALL_BOXES; k++)
                        {
                            if (k >= _WallBoxCount) break;
                            float3 halfSize = max(_WallBoxHalfSize[k].xyz, float3(1e-4, 1e-4, 1e-4));
                            float3 startRel = startLocal - _WallBoxCenter[k].xyz;
                            float3 endRel = endLocal - _WallBoxCenter[k].xyz;
                            if (all(abs(startRel) <= halfSize + 1e-4)) continue;
                            if (all(abs(endRel) <= halfSize + 0.06)) continue;
                            bool3 parallel = abs(endRel - startRel) < 1e-6;
                            if (parallel.x && abs(startRel.x) > halfSize.x) continue;
                            if (parallel.y && abs(startRel.y) > halfSize.y) continue;
                            if (parallel.z && abs(startRel.z) > halfSize.z) continue;
                            float3 d = endRel - startRel;
                            float3 safeDelta = float3(
                                parallel.x ? (d.x < 0.0 ? -1e-6 : 1e-6) : d.x,
                                parallel.y ? (d.y < 0.0 ? -1e-6 : 1e-6) : d.y,
                                parallel.z ? (d.z < 0.0 ? -1e-6 : 1e-6) : d.z);
                            float3 t0 = (-halfSize - startRel) / safeDelta;
                            float3 t1 = ( halfSize - startRel) / safeDelta;
                            float3 tNear = min(t0, t1);
                            float3 tFar = max(t0, t1);
                            float tEnter = max(max(tNear.x, tNear.y), max(tNear.z, 0.0));
                            float tExit = min(min(tFar.x, tFar.y), min(tFar.z, 1.0));
                            if (tExit >= tEnter && tEnter <= 1.0) { los = 0.0; break; }
                        }
                    }
                }

                float coneVis = angleMask * rangeFade * los;
                float nearVis = 1.0 - smoothstep(_NearRadius - _NearFeather, _NearRadius, dist);
                float vis = max(coneVis, nearVis);
                return float4(saturate(vis), 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
}
