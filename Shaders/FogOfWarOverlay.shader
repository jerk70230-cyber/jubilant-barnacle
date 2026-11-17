Shader "Hidden/ColdCar/FogOfWarOverlay"
{
    // FIRST-PERSON fog of war: visibility is computed from the CHARACTER'S EYES
    // in world space (the isometric camera is only a viewer, it does not take
    // part in the logic):
    //   - a world-space view cone from the eyes along the facing direction,
    //   - limited by range, softened at the angular and range edges,
    //   - occluded by the dedicated wall colliders as true 3D segment-vs-box
    //     tests with the walls' actual height,
    //   - a small always-visible radius around the character.
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "FogOfWarOverlay"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // Rasterized from the actual wall meshes with the active camera projection.
            TEXTURE2D_X(_FogWallRevealMask);

            #define MAX_WALL_BOXES 64
            #define MAX_INDOOR_ZONES 32

            // _BlitTexture and sampler_PointClamp are already declared by
            // Blit.hlsl / GlobalSamplers.hlsl - do not redeclare them here.

            float3 _EyePos;                 // eyes (character head), world
            float3 _ViewDir;                // facing, normalized; full 3D when enabled
            float _Use3DViewCone;           // 1 = actual camera pitch, 0 = legacy ground-plane cone
            float _ConeCosIn;               // cos(inner cone edge) - full reveal
            float _ConeCosOut;              // cos(outer cone edge) - zero reveal
            float _ConeRange;               // vision range, meters
            float _ConeRangeEdge;           // soft edge of the range, meters
            float _NearRadius;              // always-visible radius around the character
            float _NearFeather;             // soft edge of the near radius, meters

            float _BlockVisionWithWalls;
            int _WallBoxCount;
            float4 _WallBoxCenter[MAX_WALL_BOXES];
            float4 _WallBoxHalfSize[MAX_WALL_BOXES];
            float4x4 _WallRootWorldToLocal;
            float _WallSurfTol;             // target-on-wall-surface tolerance

            int _IndoorZoneCount;
            float4x4 _IndoorWorldToLocal[MAX_INDOOR_ZONES];
            float4 _IndoorBoxCenter[MAX_INDOOR_ZONES];
            float4 _IndoorBoxHalfSize[MAX_INDOOR_ZONES];
            // x = reveal amount, y = horizontal edge feather (world metres), z = footprint padding (world metres)
            float4 _IndoorFogRevealFactor[MAX_INDOOR_ZONES];
            float4 _IndoorBoxScale[MAX_INDOOR_ZONES];

            float _GroundY;                 // ground height for the no-depth fallback
            float _Darkness;
            float4 _DarkColor;
            float _RevealBrightness;
            float _RevealLift;
            float _DiagMode;                // 0 off, 1 vision (green), 2 wall LOS (blue)

            // TRUE 3D wall occlusion, exactly like a first-person game:
            // segment eye -> target against the real BoxCollider volumes with
            // their full height. Low walls block the shadow wedge behind them;
            // far points over a low wall stay visible (you look over it).
            // Doorways are gaps between footprints. The eye inside a wall box
            // (hugging it) never blinds the player. No scene depth, floor, snow,
            // furniture or character geometry participates in this test.
            bool SegmentBlockedByWallBox(float3 startRel, float3 endRel, float3 halfSize, float tol)
            {
                // If the eye is already inside a wall collider (for example, because
                // the eye anchor is grazing the collider), do not self-occlude.
                if (all(abs(startRel) <= halfSize + 1e-4)) return false;

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
                if (tExit < tEnter || tEnter > 1.0) return false;

                // A pixel on the NEAR face of the target wall is allowed to be
                // visible. A pixel on its far/back face must not be: the segment
                // has travelled through a non-trivial part of the wall volume.
                // The old endpoint-inside shortcut incorrectly allowed every
                // back-face pixel inside the same BoxCollider to shine through.
                float distanceFromEntryToTarget = (1.0 - tEnter) * length(delta);
                float surfaceAllowance = max(max(tol, 0.0), 0.015);
                return distanceFromEntryToTarget > surfaceAllowance;
            }

            bool IsBlockedByWall(float3 targetWorldPosition)
            {
                if (_BlockVisionWithWalls < 0.5 || _WallBoxCount <= 0)
                    return false;

                float3 startLocal = mul(_WallRootWorldToLocal, float4(_EyePos, 1.0)).xyz;
                float3 endLocal = mul(_WallRootWorldToLocal, float4(targetWorldPosition, 1.0)).xyz;
                if (distance(endLocal, startLocal) <= 1e-4)
                    return false;

                [loop]
                for (int i = 0; i < _WallBoxCount && i < MAX_WALL_BOXES; i++)
                {
                    float3 halfSize = max(_WallBoxHalfSize[i].xyz, float3(1e-4, 1e-4, 1e-4));
                    float3 startRel = startLocal - _WallBoxCenter[i].xyz;
                    float3 endRel = endLocal - _WallBoxCenter[i].xyz;
                    if (SegmentBlockedByWallBox(startRel, endRel, halfSize, _WallSurfTol))
                        return true;
                }
                return false;
            }

            // The indoor reveal is intentionally independent of the view cone:
            // when the eye is inside a registered building zone, every visible
            // surface inside that zone receives the configured partial reveal.
            // The C# side only uploads zones containing the player's eye.
            float GetIndoorReveal(float3 worldPosition, float wallSurface)
            {
                float reveal = 0.0;
                [loop]
                for (int i = 0; i < _IndoorZoneCount && i < MAX_INDOOR_ZONES; i++)
                {
                    float3 localPos = mul(_IndoorWorldToLocal[i], float4(worldPosition, 1.0)).xyz;
                    float3 center = _IndoorBoxCenter[i].xyz;
                    float3 halfSize = max(_IndoorBoxHalfSize[i].xyz, float3(1e-4, 1e-4, 1e-4));
                    float3 relative = abs(localPos - center);

                    // Vertical coverage includes the wall height. The footprint
                    // edge is evaluated separately for horizontal ground and
                    // vertical wall surfaces.
                    float verticalInside = 1.0 - step(halfSize.y, relative.y);
                    float3 scale = max(_IndoorBoxScale[i].xyz, float3(1e-4, 1e-4, 1e-4));
                    float padding = max(_IndoorFogRevealFactor[i].z, 0.0);
                    float edgeX = (halfSize.x - relative.x) * scale.x - padding;
                    float edgeZ = (halfSize.z - relative.z) * scale.z - padding;
                    float edgeDistance = min(edgeX, edgeZ);
                    float feather = max(_IndoorFogRevealFactor[i].y, 0.0);

                    // Floor feather is one-sided: it fades inward and never
                    // reveals horizontal snow outside the building footprint.
                    float floorFade = feather > 1e-4
                        ? smoothstep(0.0, feather, edgeDistance)
                        : step(0.0, edgeDistance);

                    // Wall quads are built exactly on the shelter BoxCollider
                    // perimeter. edgeDistance already subtracts `padding`, so
                    // a wall on that perimeter evaluates to -padding. The old
                    // smoothstep(-padding, 0, edgeDistance) therefore returned
                    // ZERO for the entire wall face. Accept the true perimeter
                    // (with a small mesh/alignment tolerance) and the room side;
                    // the C# eye-position gate above guarantees this reveal is
                    // active only while the player is inside the shelter.
                    const float wallEdgeTolerance = 0.02;
                    float wallFade = step(-padding - wallEdgeTolerance, edgeDistance);
                    float edgeFade = lerp(floorFade, wallFade, saturate(wallSurface));

                    float zoneReveal = saturate(_IndoorFogRevealFactor[i].x) * edgeFade * verticalInside;
                    reveal = max(reveal, zoneReveal);
                }
                return saturate(reveal);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;
                float3 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv).rgb;

                // Blit UVs may include a render-target Y flip / scale bias. Camera
                // depth is in camera screen space, so sample and reconstruct it from
                // SV_POSITION instead. GetNormalizedScreenSpaceUV handles the active
                // viewport and dynamic-resolution scale, including orthographic views.
                float2 depthUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawDepth = SampleSceneDepth(depthUV);
                float3 worldPos = ComputeWorldSpacePosition(depthUV, rawDepth, UNITY_MATRIX_I_VP);
                float eyeDepth = max(-TransformWorldToView(worldPos).z, 0.001);
                bool hasDepth = eyeDepth < (_ProjectionParams.z * 0.98);
                if (!hasDepth)
                {
                    float3 nearW = ComputeWorldSpacePosition(depthUV, 0.0, UNITY_MATRIX_I_VP);
                    float3 farW = ComputeWorldSpacePosition(depthUV, 1.0, UNITY_MATRIX_I_VP);
                    float3 ray = farW - nearW;
                    if (abs(ray.y) > 1e-5)
                    {
                        float t = (_GroundY - nearW.y) / ray.y;
                        if (t >= 0.0) worldPos = nearW + ray * t;
                    }
                }

                // FIRST-PERSON VISION from the eyes
                float3 toP = worldPos - _EyePos;
                // Keep a planar distance for the small point-blank radius. The
                // directional cone below can use either the old ground-plane
                // calculation or the full 3D camera direction (including pitch).
                float planarDistance = length(toP.xz);
                float2 horizontalDirection = toP.xz / max(planarDistance, 1e-4);

                // True 3D eye-to-surface LOS: walls block both the cone and the
                // near radius. Previously nearVis bypassed LOS and leaked through walls.
                float los = 1.0;
                if (_BlockVisionWithWalls > 0.5 && _WallBoxCount > 0 && IsBlockedByWall(worldPos))
                    los = 0.0;

                float3 toDirection = toP / max(length(toP), 1e-4);
                float3 facing3D = normalize(_ViewDir);
                float cone3D = dot(toDirection, facing3D);
                float cone2D = dot(horizontalDirection, normalize(_ViewDir.xz + float2(1e-5, 0.0)));
                float cone = (_Use3DViewCone > 0.5) ? cone3D : cone2D;
                float angleMask = smoothstep(_ConeCosOut, _ConeCosIn, cone);
                float distanceForCone = (_Use3DViewCone > 0.5) ? length(toP) : planarDistance;
                float rangeFade = 1.0 - smoothstep(_ConeRange - _ConeRangeEdge, _ConeRange, distanceForCone);
                float coneVis = angleMask * rangeFade;

                // Point-blank reveal is also occluded by walls.
                float nearVis = 1.0 - smoothstep(_NearRadius - _NearFeather, _NearRadius, planarDistance);

                // Reconstruct surface orientation from depth derivatives.
                // Horizontal floors/snow have |normal.y| near 1; vertical walls near 0.
                float3 worldDx = ddx(worldPos);
                float3 worldDy = ddy(worldPos);
                float3 surfaceNormalRaw = cross(worldDx, worldDy);
                float surfaceNormalLenSq = dot(surfaceNormalRaw, surfaceNormalRaw);

                // With an orthographic/isometric camera, a thin wall can occupy
                // very few pixels. The old 1e-8 cutoff classified low-area but
                // valid vertical surfaces as horizontal floors (surfaceNormalY=1),
                // so their indoor reveal became zero right at the zone boundary.
                // Use a much smaller degeneracy threshold. If a depth-backed
                // surface still has no stable derivative normal, treat it as a
                // vertical edge for indoor reveal; pixels without scene depth
                // remain ground/sky fallback and must not reveal exterior snow.
                bool hasStableSurfaceNormal = surfaceNormalLenSq > 1e-12;
                float surfaceNormalY = hasStableSurfaceNormal
                    ? abs(surfaceNormalRaw.y) * rsqrt(surfaceNormalLenSq)
                    : (hasDepth ? 0.0 : 1.0);
                float wallSurface = 1.0 - smoothstep(0.35, 0.75, surfaceNormalY);

                // Building interior contributes its own soft 65% reveal while
                // the player is inside that building; it is not constrained by
                // gaze direction, unlike the directional cone.
                // Indoor reveal belongs to the building volume itself and is
                // always applied to surfaces inside that volume. It is deliberately
                // independent of player LOS; the building cutaway controls which
                // wall faces are presented to the isometric camera. The directional
                // cone and near-radius reveal remain fully wall-occluded above.
                float indoorVisFromFloorVolume = GetIndoorReveal(worldPos, wallSurface);
                // The wall mask is rasterized in camera pixel space, not with the source
                // Blit's scale/bias UV. Sample it with the same screen UV as depth.
                float wallMaskReveal = SAMPLE_TEXTURE2D_X(_FogWallRevealMask, sampler_PointClamp, depthUV).r;
                float indoorVis = max(indoorVisFromFloorVolume, wallMaskReveal);
                float vis = max(max(coneVis, nearVis) * los, indoorVis);

                float3 lit = pow(saturate(scene), 1.0 / max(_RevealBrightness, 0.01)) + _RevealLift * vis;
                float3 fogged = lerp(_DarkColor.rgb, lit, vis);
                fogged = lerp(scene, fogged, _Darkness);

                // Diagnostics:
                // 1 = final visibility (green)
                // 2 = wall LOS (blue)
                // 3 = indoor reveal (red)
                // 4 = R=wall-mesh raster reveal mask, G=final indoor reveal,
                //     B=legacy depth-derived wall classifier. Visible wall pixels
                //     should be red and green even in an orthographic camera.
                if (_DiagMode > 0.5)
                {
                    if (_DiagMode < 1.5) return float4(0.0, vis, 0.0, 1.0);
                    if (_DiagMode < 2.5) return float4(0.0, 0.0, los, 1.0);
                    if (_DiagMode < 3.5) return float4(indoorVis, 0.0, 0.0, 1.0);
                    return float4(wallMaskReveal, indoorVis, wallSurface, 1.0);
                }

                return float4(fogged, 1.0);
            }
            ENDHLSL
        }
    }
}
