// Hologram Wire: a terminal-phosphor wireframe globe. One draw call, Cull Back, no second mesh, and no bloom: every
// colour the C# side hands in is LDR (<= 1), so the style looks finished with post-processing off, which is the cheap
// style for the Quest 3 headset.
//
// The back hemisphere is NOT a second fragment (rejected: Cull Off, which shades every pixel of the sphere twice and
// doubles the overdraw on exactly the hardware that picks this style). Instead the fragment finds where its view ray
// leaves the sphere (for a sphere centred on the origin that is p2 = p1 - 2 (p1 . dir) dir in object space) and
// evaluates the same grid there, fainter. One fragment per pixel, two grid evaluations.
//
// Latitude and longitude come from the object-space direction. Lines are drawn as a distance in pixels (fwidth of
// the line coordinate), so they stay crisp and one pixel wide at any size, and fade out where they would crowd
// closer than a couple of pixels (the poles, a far-away node) instead of shimmering. Longitude turns with a phase
// that C# accumulates, so the speed can follow glow without the lines jumping.
//
// Light vs dark is entirely in the colours: dark themes get phosphor-bright lines, light themes darker tints
// with alpha blending. There is no light/dark branch here.
Shader "AgentConsole/NodeHologram"
{
    Properties
    {
        _LineColor ("Line Colour", Color) = (0.3, 0.95, 0.5, 1)
        _OutlineColor ("Outline Colour", Color) = (0.5, 1, 0.7, 1)
        _FillColor ("Fill Colour", Color) = (0.3, 0.95, 0.5, 1)
        _HotColor ("Hot Colour", Color) = (0.8, 1, 0.9, 1)
        _Fill ("Fill At Rest", Range(0, 1)) = 0.02
        _LitFill ("Fill When Lit", Range(0, 1)) = 0.2
        _BackStrength ("Back Hemisphere Strength", Range(0, 1)) = 0.38
        _LineWidth ("Line Width (pixels)", Range(0.5, 3)) = 1.2
        _OutlineWidth ("Outline Width (pixels)", Range(0.5, 4)) = 1.6
        _Glow ("Glow", Range(0, 1)) = 0
        _Selected ("Selected", Range(0, 1)) = 0
        _Alpha ("Alpha", Range(0, 1)) = 1
        _Phase ("Longitude Phase", Float) = 0
        _Scan ("Scanline Phase (turns)", Float) = 0
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
                float4 _LineColor;
                float4 _OutlineColor;
                float4 _FillColor;
                float4 _HotColor;
                float _Fill;
                float _LitFill;
                float _BackStrength;
                float _LineWidth;
                float _OutlineWidth;
                float _Glow;
                float _Selected;
                float _Alpha;
                float _Phase;
                float _Scan;
            CBUFFER_END

            static const float LAT_STEP = 0.39269908; // pi / 8: seven parallels, the equator among them
            static const float LON_STEP = 0.52359878; // pi / 6: twelve meridians

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 posOS : TEXCOORD0;
                float3 camOS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 viewWS : TEXCOORD3;
                float fogFactor : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS);
                o.positionCS = p.positionCS;
                o.posOS = input.positionOS;
                o.camOS = TransformWorldToObject(GetCameraPositionWS());
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

            // Coverage of a line at integer values of v, as distance in pixels. The seam of atan2 makes fwidth(v) spike
            // along one meridian, so the smaller of it and the fwidth of a copy shifted half a step is used.
            float LineCover(float v, float fw, float widthPx)
            {
                float dist = abs(v - round(v)) / max(fw, 1e-4);
                float cover = saturate(widthPx * 0.5 + 0.5 - dist);
                // Fade lines that would sit closer than ~2.5 pixels apart.
                float crowd = saturate((1.0 / max(fw, 1e-4)) * 0.33 - 0.8);
                return cover * crowd;
            }

            float Grid(float3 d, float lonShift, float widthPx)
            {
                float lat = asin(clamp(d.y, -1.0, 1.0)) / LAT_STEP;
                float latFw = fwidth(lat);
                float parallels = LineCover(lat, latFw, widthPx * (abs(round(lat)) < 0.5 ? 1.6 : 1.0));
                // The poles are the one place every meridian meets; no parallel is drawn there.
                parallels *= step(abs(round(lat)), 3.5);

                float lon = (atan2(d.z, d.x) + lonShift) / LON_STEP;
                float lonFw = min(fwidth(lon), fwidth(frac(lon + 0.5)));
                float meridians = LineCover(lon, lonFw, widthPx);
                return saturate(parallels + meridians);
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                float ndv = saturate(dot(n, v));
                float glow = saturate(_Glow);

                float rho = sqrt(saturate(1.0 - ndv * ndv));
                float2 p = float2(dot(n, UNITY_MATRIX_V[0].xyz), dot(n, UNITY_MATRIX_V[1].xyz));
                p *= rho / max(length(p), 1e-4);
                float px = max(fwidth(rho), 1e-4);

                // Front point is this fragment; the back point is where the view ray leaves the sphere.
                float3 p1 = i.posOS;
                float3 dir = normalize(p1 - i.camOS);
                float3 p2 = p1 - 2.0 * dot(p1, dir) * dir;
                float3 d1 = normalize(p1);
                float3 d2 = normalize(p2);

                float wpx = _LineWidth * (1.0 + 0.5 * _Selected);
                float front = Grid(d1, _Phase, wpx);
                float back = Grid(d2, _Phase, wpx) * _BackStrength;
                // Fainter behind the label, so the text keeps its contrast; full strength toward the rim.
                float centreDim = lerp(0.30, 1.0, smoothstep(0.12, 0.62, rho));
                float grid = saturate(front + back) * centreDim * (0.78 + 0.35 * rho);

                float3 lineCol = lerp(_LineColor.rgb, _HotColor.rgb, glow * 0.7);

                // ---- fill: almost nothing at rest, a faint accent wash that rises with glow, denser toward the rim
                float fillA = lerp(_Fill, _LitFill, glow) * lerp(0.35, 1.0, rho * rho);
                float3 col = lerp(_FillColor.rgb, _HotColor.rgb, saturate(glow * 1.4));
                float a = fillA;

                Over(col, a, lineCol, saturate(grid * (0.85 + 0.15 * glow + 0.15 * _Selected)));

                // ---- scanline: a crisp horizontal line sweeping up the disc, with a faint trail beneath it
                float yS = frac(_Scan) * 2.0 - 1.0;
                float ppx = max(fwidth(p.y), 1e-4);
                float dy = p.y - yS;
                float core = 1.0 - smoothstep(0.7 * ppx, 1.7 * ppx, abs(dy));
                float trail = dy < 0.0 ? exp(dy * 5.0) * 0.30 : 0.0;
                float edgeFade = 1.0 - 0.85 * pow(abs(yS), 6.0);
                float3 scanCol = lerp(_OutlineColor.rgb, _HotColor.rgb, glow * 0.5);
                Over(col, a, scanCol, saturate((core * 0.95 + trail) * edgeFade));

                // ---- outline: one crisp circle on the silhouette
                float ow = _OutlineWidth * (1.0 + 0.8 * _Selected);
                float ring = saturate((ow * px - (1.0 - rho)) / px + 0.5);
                float3 olCol = lerp(_OutlineColor.rgb, _HotColor.rgb, glow * 0.6);
                Over(col, a, olCol, saturate(ring * (0.9 + 0.1 * _Selected)));

                a *= _Alpha;
                // Reduced fog (35%). The other styles carry HDR colour that survives full fog (and bloom); this one is
                // LDR on purpose, so at the default distance a fully fogged wire would be almost the stage colour.
                col = lerp(col, MixFog(col, i.fogFactor), 0.35);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
