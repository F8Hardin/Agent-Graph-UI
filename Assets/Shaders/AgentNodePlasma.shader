// Plasma Dome: a glass ball with bright motes drifting inside it. Unlit, transparent, ZWrite Off for the same
// reason as AgentConsole/NodeSphere: the label is a separate transparent object sorted after its sphere, so the
// sphere must not enter the depth buffer or the text would be clipped by it.
//
// Everything is one pass, composed by hand with an "over" operator in a fixed order (body, inner glow, motes,
// specular, rim), so a layer can carry HDR colour (>1, which the URP bloom turns into a halo) and still blend
// correctly with plain alpha blending. That is also what makes it work on a white theme: nothing here relies on
// additive blending. The C# side hands a light theme darker, denser colours and gains of 1, and a dark theme pale
// colours with gains above 1. There is no light/dark branch in the shader.
//
// The motes are procedural: the view-space normal gives disc coordinates, and each mote is a gaussian dot on its
// own tilted circular orbit (a point inside the ball projected onto the disc). _MotePhase is accumulated on the C#
// side so the speed can change with glow without the dots jumping, which a _Time * speed term cannot do.
Shader "AgentConsole/NodePlasma"
{
    Properties
    {
        _BaseColor ("Body Colour", Color) = (0.2, 0.8, 0.4, 1)
        _HotColor ("Hot Colour", Color) = (0.8, 1, 0.9, 1)
        _RimColor ("Rim Colour", Color) = (0.6, 1, 0.75, 1)
        _InnerColor ("Inner Glow Colour", Color) = (0.5, 1, 0.7, 1)
        _MoteColor ("Mote Colour", Color) = (0.9, 1, 0.95, 1)
        _RimGain ("Rim Gain (HDR)", Range(0, 6)) = 2.3
        _InnerGain ("Inner Glow Gain (HDR)", Range(0, 6)) = 1.5
        _MoteGain ("Mote Gain (HDR)", Range(0, 6)) = 2.6
        _Fill ("Centre Alpha", Range(0, 1)) = 0.2
        _LitFill ("Centre Alpha When Lit", Range(0, 1)) = 0.62
        _RimFill ("Body Alpha At The Rim", Range(0, 1)) = 0.62
        _RimWidth ("Rim Width (fraction of radius)", Range(0.01, 0.2)) = 0.05
        _Glow ("Glow", Range(0, 1)) = 0
        _Selected ("Selected", Range(0, 1)) = 0
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Seed ("Seed", Float) = 0
        _MotePhase ("Mote Phase", Float) = 0
        _MoteCount ("Mote Count", Range(0, 12)) = 8
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
                float4 _RimColor;
                float4 _InnerColor;
                float4 _MoteColor;
                float _RimGain;
                float _InnerGain;
                float _MoteGain;
                float _Fill;
                float _LitFill;
                float _RimFill;
                float _RimWidth;
                float _Glow;
                float _Selected;
                float _Alpha;
                float _Seed;
                float _MotePhase;
                float _MoteCount;
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

            float Hash11(float x)
            {
                return frac(sin(x * 127.1) * 43758.5453);
            }

            // Composite a layer over the running result. col is straight (not premultiplied) colour, which is what
            // the SrcAlpha blend at the end expects.
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
                float glowC = saturate(glow * 1.4);

                // Disc coordinates: the normal's components along the camera's right and up axes. Rescaled to the true
                // disc radius sqrt(1 - n.v^2), because under perspective an off-axis sphere's silhouette is where
                // n.v = 0, not where the view-space normal's xy reaches length 1, and the rim must sit on the silhouette.
                float rho = sqrt(saturate(1.0 - ndv * ndv));
                float2 p = float2(dot(n, UNITY_MATRIX_V[0].xyz), dot(n, UNITY_MATRIX_V[1].xyz));
                p *= rho / max(length(p), 1e-4);
                float aa = max(fwidth(rho), 1e-4);

                // ---- body: clearest at the centre, denser toward the rim
                float fillC = lerp(_Fill, _LitFill, glow);
                float3 bodyCol = lerp(_BaseColor.rgb, _HotColor.rgb, glowC);
                float bodyA = lerp(fillC, _RimFill, pow(rho, 2.6));
                float3 col = bodyCol;
                float a = bodyA;

                // ---- inner glow, offset toward the upper left like a lit glass ball
                float2 gc = float2(-0.38, 0.40);
                float2 gd = p - gc;
                float ig = exp(-dot(gd, gd) * 3.4);
                float3 igCol = lerp(_InnerColor.rgb, _HotColor.rgb, glowC) * _InnerGain * (1.0 + glow * 0.9);
                Over(col, a, igCol, ig * (0.24 + 0.45 * glow));

                // ---- motes, drifting inside the ball
                float moteSum = 0.0;
                float px = max(fwidth(p.x), 1e-4);
                [loop]
                for (int k = 0; k < 12; k++)
                {
                    if (k >= (int)(_MoteCount + 0.5)) break;
                    float fk = (float)k;
                    float h1 = Hash11(fk * 1.37 + _Seed);
                    float h2 = Hash11(fk * 2.91 + _Seed + 3.1);
                    float h3 = Hash11(fk * 4.73 + _Seed + 7.7);
                    float h4 = Hash11(fk * 6.11 + _Seed + 11.3);
                    float orbit = 0.18 + 0.62 * h1;
                    float speed = lerp(0.5, 1.2, h2) * (h4 < 0.5 ? 1.0 : -1.0);
                    float th = _MotePhase * speed + h3 * 6.2832;
                    float tilt = h2 * 3.1416;
                    // A point on a tilted circle inside the unit ball, flattened onto the disc. z only sizes and
                    // brightens it, so near motes read as nearer.
                    float cx = cos(th);
                    float sy = sin(th);
                    float3 mp = orbit * float3(cx, sy * cos(tilt), sy * sin(tilt));
                    float depth = mp.z / max(orbit, 1e-3);
                    float size0 = lerp(0.030, 0.052, h4) * (0.85 + 0.25 * depth);
                    float sigma = max(size0, px * 0.8);
                    float2 md = p - mp.xy;
                    float g = exp(-dot(md, md) / (2.0 * sigma * sigma)) * min(1.0, size0 / sigma);
                    float edgeFade = 1.0 - smoothstep(0.66, 0.90, length(mp.xy));
                    moteSum += g * edgeFade * (0.75 + 0.25 * depth);
                }
                // Dimmer behind the label, so the text keeps its contrast.
                moteSum *= lerp(0.55, 1.0, smoothstep(0.10, 0.50, rho));
                float3 moteCol = lerp(_MoteColor.rgb, _HotColor.rgb, glow * 0.4) * _MoteGain * (1.0 + glow * 0.5);
                Over(col, a, moteCol, saturate(moteSum * (1.1 + 0.4 * glow)));

                // ---- specular: a thin arc near the upper-left rim and a small bright dot inside it
                float2 pn = p / max(rho, 1e-4);
                float arcAng = smoothstep(0.80, 0.97, dot(pn, normalize(float2(-0.72, 0.69))));
                float arcRing = 1.0 - smoothstep(0.012, 0.038, abs(rho - 0.80));
                float3 white = float3(1.0, 1.0, 1.0);
                Over(col, a, white * 1.6, saturate(arcAng * arcRing * 0.85));
                float2 sd = p - float2(-0.42, 0.46);
                Over(col, a, white * 1.6, saturate(exp(-dot(sd, sd) * 260.0) * 0.8));

                // ---- rim: crisp ring plus a soft fresnel falloff, a lower-right bounce, brighter when selected
                float w = _RimWidth * (1.0 + 0.9 * _Selected);
                float edge = 1.0 - w;
                float ring = smoothstep(edge - aa, edge + aa, rho);
                float soft = pow(rho, 4.0) * 0.55;
                float bounce = pow(saturate(dot(pn, float2(0.70, -0.70))), 2.0) * smoothstep(0.55, 1.0, rho) * 0.35;
                float3 rimCol = lerp(_RimColor.rgb, _HotColor.rgb, glow * 0.6) * _RimGain
                    * (1.0 + glow * 0.7 + _Selected * 0.9);
                float rimA = saturate(ring * 0.95 + soft + bounce);
                Over(col, a, rimCol, rimA);

                a *= _Alpha;
                col = MixFog(col, i.fogFactor);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
