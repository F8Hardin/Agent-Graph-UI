// Translucent unlit node sphere. ZWrite is Off on purpose: the label is a separate transparent
// object drawn after the sphere (it is nudged toward the camera so it sorts later), so there is no
// depth buffer entry for the text to z-fight with or be clipped by. A fresnel rim keeps the silhouette
// readable while the centre stays clear enough to read the label through.
Shader "AgentConsole/NodeSphere"
{
    Properties
    {
        _BaseColor ("Base Colour", Color) = (0.2, 0.8, 0.4, 1)
        _HotColor ("Hot Colour", Color) = (0.8, 1, 0.9, 1)
        _Glow ("Glow", Range(0, 1)) = 0
        _Fill ("Centre Fill Alpha", Range(0, 1)) = 0.3
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _Alpha ("Alpha", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _HotColor;
                float _Glow;
                float _Fill;
                float _RimPower;
                float _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS);
                o.positionCS = p.positionCS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.viewWS = GetWorldSpaceViewDir(p.positionWS);
                o.fogFactor = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float rim = pow(1.0 - saturate(dot(n, v)), _RimPower);

                float glow = saturate(_Glow);
                float3 col = lerp(_BaseColor.rgb, _HotColor.rgb, saturate(glow * 1.4));
                col *= 1.0 + glow * 1.1;              // >1 so Bloom picks the lit node up
                col = lerp(col, col * 1.6, rim);

                float a = _Fill + rim * (1.0 - _Fill);
                a = lerp(a, max(a, 0.92), glow);
                a *= _Alpha;

                col = MixFog(col, i.fogFactor);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
