// Translucent unlit node sphere. ZWrite is Off on purpose: the label is a separate transparent
// object drawn after the sphere (it is nudged toward the camera so it sorts later), so there is no
// depth buffer entry for the text to z-fight with or be clipped by. A fresnel rim keeps the silhouette
// readable while the centre stays clear enough to read the label through.
//
// Border: a thick, opaque ring in a darker shade of the node's colour, drawn by the SAME pass as a
// widened rim term (not an inverted hull). An inverted hull on a translucent, non-depth-writing sphere
// would show its back faces through the clear centre as a dark disc; the rim term has no such problem,
// costs no extra draw call, and its thickness is a constant fraction of the radius at any distance.
// _BorderColor / _BorderHotColor are set from C# per theme, so a theme switch recolours it.
Shader "AgentConsole/NodeSphere"
{
    Properties
    {
        _BaseColor ("Base Colour", Color) = (0.2, 0.8, 0.4, 1)
        _HotColor ("Hot Colour", Color) = (0.8, 1, 0.9, 1)
        _BorderColor ("Border Colour", Color) = (0.1, 0.4, 0.2, 1)
        _BorderHotColor ("Border Hot Colour", Color) = (0.4, 0.5, 0.45, 1)
        _BorderWidth ("Border Width (fraction of radius)", Range(0, 0.4)) = 0.14
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
                float4 _BorderColor;
                float4 _BorderHotColor;
                float _BorderWidth;
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
                float ndv = saturate(dot(n, v));
                float rim = pow(1.0 - ndv, _RimPower);

                float glow = saturate(_Glow);
                float3 col = lerp(_BaseColor.rgb, _HotColor.rgb, saturate(glow * 1.4));
                col *= 1.0 + glow * 1.1;              // >1 so Bloom picks the lit node up
                col = lerp(col, col * 1.6, rim);

                float a = _Fill + rim * (1.0 - _Fill);
                a = lerp(a, max(a, 0.92), glow);

                // Border. rho is the distance from the disc centre as a fraction of the radius (0 centre, 1 edge):
                // sqrt(1 - n.v^2) for a sphere. The ring covers rho in [1 - width, 1], so its on-screen thickness
                // is the same fraction of the sphere's size whatever the camera distance.
                float rho = sqrt(saturate(1.0 - ndv * ndv));
                float edge = 1.0 - _BorderWidth;
                float aa = max(fwidth(rho), 1e-4);
                float ring = smoothstep(edge - aa, edge + aa, rho);
                float3 bcol = lerp(_BorderColor.rgb, _BorderHotColor.rgb, saturate(glow * 1.4));
                bcol *= 1.0 + glow * 0.5;
                col = lerp(col, bcol, ring);
                a = lerp(a, 0.97, ring);

                a *= _Alpha;

                col = MixFog(col, i.fogFactor);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
