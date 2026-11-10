// Orbital Core, ring: a thin tilted ellipse around the body with a small satellite travelling along it. Drawn on a
// camera-facing quad about 3.5 radii wide, because the ring leaves the sphere's silhouette; the quad is billboarded in
// the vertex shader, and the ring's 3D tilt (a per-node world-space normal) is projected to an ellipse in the
// fragment shader, so the ring keeps its orientation when the camera orbits.
//
// ZWrite Off like every node style. The half of the ring behind the body is hidden analytically: for each pixel on the
// ellipse the depth along the view axis is known in closed form, and a back-half pixel inside the body's silhouette
// is simply not drawn. That is what stands in for the depth test a depth-writing body would give (and which would
// clip the label). Back-half pixels outside the silhouette are drawn fainter, for depth.
//
// Colour comes from C#: a pale HDR tint on dark themes, a darker alpha-blended tint with gain 1 on white ones. No
// light/dark branch here. All lines are anti-aliased with fwidth.
Shader "AgentConsole/NodeOrbitRing"
{
    Properties
    {
        _RingColor ("Ring Colour", Color) = (0.3, 0.9, 0.5, 1)
        _RingGain ("Ring Gain (HDR)", Range(0, 6)) = 1.25
        _SatColor ("Satellite Colour", Color) = (0.8, 1, 0.9, 1)
        _SatGain ("Satellite Gain (HDR)", Range(0, 6)) = 3
        _RingNormal ("Ring Normal (world)", Vector) = (0, 1, 0, 0)
        _SatPhase ("Satellite Phase", Float) = 0
        _Glow ("Glow", Range(0, 1)) = 0
        _Selected ("Selected", Range(0, 1)) = 0
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Seed ("Seed", Float) = 0
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _RingColor;
                float _RingGain;
                float4 _SatColor;
                float _SatGain;
                float4 _RingNormal;
                float _SatPhase;
                float _Glow;
                float _Selected;
                float _Alpha;
                float _Seed;
            CBUFFER_END

            // The quad's half-extent in body radii. Must match OrbitalCoreVisual.QuadHalf.
            static const float QUAD_HALF = 1.75;
            // The ring's radius in body radii.
            static const float RING_R = 1.38;

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q : TEXCOORD0;
                float3 nView : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float2 corner = input.positionOS.xy * 2.0;
                float3 centre = TransformObjectToWorld(float3(0, 0, 0));
                // The object's uniform scale is the quad's full width.
                float halfW = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20)) * 0.5;
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up = UNITY_MATRIX_V[1].xyz;
                float3 posWS = centre + (right * corner.x + up * corner.y) * halfW;
                o.positionCS = TransformWorldToHClip(posWS);
                o.q = corner * QUAD_HALF;
                // The ring normal into view space, +z toward the camera (Unity's view space looks down -z, so a
                // camera-facing normal has positive z, which is what the depth sign below assumes).
                o.nView = normalize(mul((float3x3)UNITY_MATRIX_V, normalize(_RingNormal.xyz)));
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            void Over(inout float3 col, inout float a, float3 layerCol, float layerA)
            {
                float na = layerA + a * (1.0 - layerA);
                col = (layerCol * layerA + col * a * (1.0 - layerA)) / max(na, 1e-4);
                a = na;
            }

            // A point on the ring at angle th, as view-plane xy and depth z (+ toward the camera), in body radii.
            // Ring axes: U lies in the view plane (the line of nodes), V = n x U. See the note in the C# header.
            float3 RingPoint(float th, float3 n, float2 u, float2 w, float c, float sinT)
            {
                float2 xy = RING_R * (cos(th) * u + (-c * sin(th)) * w);
                return float3(xy, RING_R * sin(th) * sinT);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.nView);
                float2 q = i.q;
                float px = max(fwidth(q.x), 1e-4);
                float glow = saturate(_Glow);

                // Ring frame in the view plane: w along the projected normal, u across it.
                float nxy = length(n.xy);
                float2 w = nxy > 1e-3 ? n.xy / nxy : float2(0, 1);
                float2 u = float2(-w.y, w.x);
                float sinT = nxy;
                // c is the normal's component toward the camera; it sets the ellipse's minor axis. Clamped away from
                // zero so an edge-on ring stays a thin ellipse instead of a division by zero.
                float c = abs(n.z) < 0.07 ? (n.z < 0.0 ? -0.07 : 0.07) : n.z;
                float aMaj = RING_R;
                float aMin = abs(c) * RING_R;

                float qu = dot(q, u);
                float qw = dot(q, w);

                // ---- the ellipse line. Distance by the standard gradient-normalised implicit form.
                float2 e = float2(qu / aMaj, qw / aMin);
                float k0 = length(e);
                float2 e1 = float2(qu / (aMaj * aMaj), qw / (aMin * aMin));
                float k1 = length(e1);
                float d = k0 * (k0 - 1.0) / max(k1, 1e-4);

                float baseW = 0.022 * (1.0 + 0.8 * _Selected + 0.4 * glow);
                float hw = max(baseW, px * 0.7);
                // A line thinner than a pixel is thinned in alpha instead of vanishing.
                float cover = baseW / hw;
                float lineA = (1.0 - smoothstep(hw - px, hw + px, abs(d))) * min(1.0, cover * 1.6);

                // Depth of the ring point under this pixel: sin(th) = -qw / (c * R), z = R sin(th) sin(T).
                float zr = -qw * sinT / c;
                float front = smoothstep(-0.05, 0.05, zr / RING_R);
                float rq = length(q);
                // Behind the body and inside its silhouette: hidden. Elsewhere behind: fainter.
                float hidden = (1.0 - front) * (1.0 - smoothstep(1.0 - px, 1.0 + px, rq));
                float depthFade = lerp(0.42, 1.0, front);
                float ringA = lineA * depthFade * (1.0 - hidden);

                float3 ringCol = lerp(_RingColor.rgb, _SatColor.rgb, glow * 0.5) * _RingGain * (1.0 + glow * 0.6 + _Selected * 0.8);
                ringA *= (0.62 + 0.38 * glow + 0.25 * _Selected);

                float3 col = ringCol;
                float a = saturate(ringA);

                // ---- satellite: a bright disc with a short fading trail behind it, and a soft halo
                float3 satCol = _SatColor.rgb * _SatGain;
                [unroll]
                for (int k = 0; k < 5; k++)
                {
                    float th = _SatPhase - (float)k * 0.11;
                    float3 sp = RingPoint(th, n, u, w, c, sinT);
                    float depth = sp.z / RING_R;
                    float vis = (sp.z >= 0.0) ? 1.0 : 1.0 - (1.0 - smoothstep(1.0 - px, 1.0 + px, length(sp.xy)));
                    float rad = lerp(0.068, 0.034, (float)k / 4.0) * (0.85 + 0.25 * depth) * (1.0 + 0.3 * glow);
                    float dd = length(q - sp.xy);
                    float disc = 1.0 - smoothstep(rad - px, rad + px, dd);
                    float halo = exp(-(dd * dd) / (2.0 * rad * rad * 4.0)) * 0.22;
                    float fade = 1.0 - 0.17 * (float)k;
                    float la = saturate(disc + halo) * vis * fade * lerp(0.55, 1.0, step(0.0, sp.z));
                    Over(col, a, satCol, la);
                }

                a *= _Alpha;
                // Half fog: the ring is a thin line, and full fog on a 1 px line leaves it invisible at the default
                // distance (the body, which is a large shape, still fades fully so depth reads).
                col = lerp(col, MixFog(col, i.fogFactor), 0.5);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
