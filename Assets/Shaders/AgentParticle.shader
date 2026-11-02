// Soft additive round billboard for the ambient dust. No texture: the disc is computed from the quad UV.
// Fog fades the additive colour toward black, so distant motes disappear into the background.
Shader "AgentConsole/Particle"
{
    Properties
    {
        _Intensity ("Intensity", Range(0, 4)) = 1.4
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+10" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float fogFactor : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS);
                o.color = input.color;
                o.uv = input.uv;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float d = length(i.uv * 2.0 - 1.0);
                float disc = saturate(1.0 - d);
                disc *= disc;
                float3 col = i.color.rgb * _Intensity;
                col = MixFogColor(col, float3(0, 0, 0), i.fogFactor);
                return half4(col, disc * i.color.a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
