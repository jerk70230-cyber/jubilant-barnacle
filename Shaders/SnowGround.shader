Shader "Hidden/ColdCar/SnowGround"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        // Geometry queue + ZWrite On: floor and wall tilemap surfaces MUST write
        // camera depth. The fog-of-war overlay reconstructs the world position of
        // every pixel from depth - with ZWrite Off the walls had no depth, the
        // world position fell back to the ground plane BEHIND walls, and the
        // shelter/cone logic leaked through them.
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Off

        Pass
        {
            Name "SnowGround"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_ST;
            float4 _BaseColor;
            int _IndoorZoneCount;
            float4x4 _IndoorWorldToLocal[32];
            float4 _IndoorBoxCenter[32];
            float4 _IndoorBoxHalfSize[32];

            bool IsInsideIndoorZone(float3 worldPosition)
            {
                [loop]
                for (int z = 0; z < _IndoorZoneCount && z < 32; z++)
                {
                    float3 localPosition = mul(_IndoorWorldToLocal[z], float4(worldPosition, 1.0)).xyz;
                    float3 delta = abs(localPosition - _IndoorBoxCenter[z].xyz);
                    if (all(delta <= _IndoorBoxHalfSize[z].xyz + 0.001)) return true;
                }
                return false;
            }

            struct appdata
            {
                float4 pos : POSITION;
                float4 color : COLOR;   // baked relief shading
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD1;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(v.pos.xyz);
                o.pos = positionInputs.positionCS;
                o.worldPos = positionInputs.positionWS;
                o.color = v.color;
                o.uv = v.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (IsInsideIndoorZone(i.worldPos)) discard;
                // texture * baked vertex shading * tint - relief is guaranteed visible
                float4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * i.color * _BaseColor;
                return c;
            }
            ENDHLSL
        }
    }
}
