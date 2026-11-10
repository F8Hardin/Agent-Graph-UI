// Orbital Core, body: a dark planet with a hot core and a thin accent outline. Unlit transparent, ZWrite Off, for the
// same reason as the other node shaders (the label is a separate transparent object sorted after the sphere and must
// not be clipped by it). The ring and satellite are a second quad, AgentNodeOrbitRing.shader, because they leave
// this sphere's silhouette.
//
// Composed with an "over" operator in a fixed order (body, core halo, core, outline), so the core and outline can
// carry HDR colour (>1, which the URP bloom turns into a halo on dark themes) and still blend with plain alpha
// blending. On a white theme the C# side hands darker alpha-blended tints and gains of 1; there is no light/dark
// branch here.
//
// The body is deliberately a plain two-tone shaded sphere: the "opaque dark planet" read comes from being close to the
// stage colour with a lit and a shaded side, and the accent is kept for the outline and the core.
Shader "AgentConsole/NodeOrbital"
{
    Properties
    {
        _BodyLit ("Body Colour, Lit Side", Color) = (0.1, 0.12, 0.12, 1)
        _BodyShade ("Body Colour, Shaded Side", Color) = (0.02, 0.03, 0.03, 1)
        _OutlineColor ("Outline Colour", Color) = (0.3, 0.9, 0.5, 1)
        _OutlineGain ("Outline Gain (HDR)", Range(0, 6)) = 1.5
        _CoreColor ("Core Colour", Color) = (0.5, 1, 0.7, 1)
        _CoreGain ("Core Gain (HDR)", Range(0, 6)) = 1.9
        _CoreRest ("Core Alpha At Rest", Range(0, 1)) = 0.15
        _CoreLit ("Core Alpha When Lit", Range(0, 1)) = 0.27
        _OutlineWidth ("Outline Width (pixels)", Range(0.5, 4)) = 1.4
        _Glow ("Glow", Range(0, 1)) = 0
        _Selected ("Selected", Range(0, 1)) = 0
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Seed ("Seed", Float) = 0
        _Phase ("Phase", Float) = 0
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
                float4 _BodyLit;
                float4 _BodyShade;
                float4 _OutlineColor;
                float _OutlineGain;
                float4 _CoreColor;
                float _CoreGain;
                float _CoreRest;
                float _CoreLit;
                float _OutlineWidth;
                float _Glow;
                float _Selected;
                float _Alpha;
                float _Seed;
                float _Phase;
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

            void Over(inout float3 col, inout float a, float3 layerCol, float layerA)
            {
                float na = layerA + a * (1.0 - layerA);
                col = (layerCol * layerA + col * a * (1.0 - layerA)) / max(na, 1e-4);
                a = na;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float ndv = saturate(dot(n, v));
                float glow = saturate(_Glow);

                // Disc coordinates on the camera's axes, rescaled to the true silhouette radius (see Plasma for why).
                float rho = sqrt(saturate(1.0 - ndv * ndv));
                float2 p = float2(dot(n, UNITY_MATRIX_V[0].xyz), dot(n, UNITY_MATRIX_V[1].xyz));
                p *= rho / max(length(p), 1e-4);
                float aa = max(fwidth(rho), 1e-4);

                // ---- body: lit from the upper left in view space, so the shading does not swing as the camera orbits
                float3 nv = float3(p, ndv);
                float lit = saturate(dot(nv, normalize(float3(-0.45, 0.55, 0.70))) * 0.5 + 0.5);
                float3 col = lerp(_BodyShade.rgb, _BodyLit.rgb, pow(lit, 1.4));
                float a = 1.0;

                // ---- core: a soft glow in the middle. The alpha is capped at the centre (where the label is), so the
                // text keeps its contrast; the brightness comes from the colour being HDR, not from the alpha.
                float breath = 1.0 + 0.07 * sin(_Phase * 1.0 + _Seed);
                float cA = lerp(_CoreRest, _CoreLit, glow) * breath;
                float r2 = dot(p, p);
                float3 coreCol = _CoreColor.rgb * _CoreGain * (1.0 + glow * 0.5);
                // Wide halo, then a tighter core that is the hot part.
                Over(col, a, coreCol * 0.7, exp(-r2 * 6.5) * cA * 0.7);
                Over(col, a, coreCol, exp(-r2 * 24.0) * cA * 1.6);

                // ---- outline: a crisp ring on the silhouette plus a faint atmosphere inside it
                float w = _OutlineWidth * (1.0 + 0.9 * _Selected);
                float px = aa;
                float ring = saturate((w * px - (1.0 - rho)) / px + 0.5);
                float soft = pow(rho, 6.0) * (0.22 + 0.25 * glow);
                float3 olCol = lerp(_OutlineColor.rgb, _CoreColor.rgb, glow * 0.5) * _OutlineGain
                    * (1.0 + glow * 0.5 + _Selected * 0.9);
                Over(col, a, olCol, saturate(ring * 0.95 + soft));

                a *= _Alpha;
                col = MixFog(col, i.fogFactor);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
