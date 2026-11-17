Shader "Hidden/ColdCar/PortalWindow"
{
    Properties { _PortalTex ("Portal Render Texture", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry+1" "RenderType"="Opaque" }
        Pass
        {
            Name "PortalURP"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off ZWrite Off ZTest LEqual
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_PortalTex); SAMPLER(sampler_PortalTex);
            float4 _PortalTex_TexelSize;
            float4x4 _PortalVP;
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionHCS : SV_POSITION; float4 portalClip : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.portalClip = mul(_PortalVP, float4(positionWS, 1.0));
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float invW = rcp(max(input.portalClip.w, 1e-5));
                float2 uv = input.portalClip.xy * invW * 0.5 + 0.5;
                // The portal RT is rendered by a camera targeting a RenderTexture.
                // Its vertical texture origin is opposite to the aperture's UV origin
                // in this project's D3D11/URP path; flip V exactly once here.
                uv.y = 1.0 - uv.y;
                return SAMPLE_TEXTURE2D(_PortalTex, sampler_PortalTex, saturate(uv));
            }
            ENDHLSL
        }
    }
    SubShader
    {
        Tags { "Queue"="Geometry+1" "RenderType"="Opaque" }
        Pass
        {
            Cull Off ZWrite Off ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _PortalTex;
            float4 _PortalTex_TexelSize;
            float4x4 _PortalVP;
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float4 portalClip : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o;
                float4 world = mul(unity_ObjectToWorld, v.vertex);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.portalClip = mul(_PortalVP, world);
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.portalClip.xy / max(i.portalClip.w, 1e-5) * 0.5 + 0.5;
                uv.y = 1.0 - uv.y;
                return tex2D(_PortalTex, saturate(uv));
            }
            ENDCG
        }
    }
    Fallback Off
}
