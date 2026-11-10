// All edges in ONE mesh and ONE draw call. Each edge is a 4-vertex ribbon expanded in the vertex
// shader perpendicular to the view direction, so the mesh only needs rebuilding when the layout moves.
//   POSITION  : this end of the edge (object space)
//   NORMAL    : the edge vector A->B (object space), the same on all four vertices
//   TEXCOORD0 : (t along the edge 0..1, side -1/+1)
//   TEXCOORD1 : (tier + 0.5 if the link is down, edge length)
//   TEXCOORD2 : (hot 0..1, pulse head 0 or -1, pulse head 1 or -1, memory flag)
// The pulse heads are written by the CPU pool each frame a pulse is in flight; the shader just draws
// a bright bead at each head and brightens the whole edge by `hot`.
Shader "AgentConsole/Edge"
{
    Properties
    {
        _LinkColor1 ("Agent-Server Link", Color) = (0.1, 0.4, 0.2, 0.9)
        _LinkColor2 ("Server-Tool Link", Color) = (0.08, 0.3, 0.15, 0.75)
        _FlowColor ("Flow", Color) = (0.7, 1, 0.8, 1)
        _MemColor ("Memory Flow", Color) = (0.8, 1, 1, 1)
        _DownColor ("Down", Color) = (1, 0.37, 0.34, 0.6)
        _Width ("Width (world)", Float) = 0.3
        _Fill ("Centre Fill Alpha", Range(0, 1)) = 0.5
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.2
        _PulseLength ("Pulse Length (world)", Float) = 0.45
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent-10" }
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
                float4 _LinkColor1;
                float4 _LinkColor2;
                float4 _FlowColor;
                float4 _MemColor;
                float4 _DownColor;
                float _Width;
                float _PulseLength;
                float _Fill;
                float _RimPower;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 dirOS : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float4 pulse : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float4 pulse : TEXCOORD2;
                float fogFactor : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 posWS = TransformObjectToWorld(input.positionOS);
                float3 dirWS = TransformObjectToWorldDir(input.dirOS, true);
                float3 viewWS = GetWorldSpaceViewDir(posWS);
                float3 side = cross(dirWS, viewWS);
                side *= rsqrt(max(dot(side, side), 1e-8));
                float w = _Width * (1.0 + saturate(input.pulse.x) * 0.8);
                // Never thinner than about two pixels: a far edge would otherwise fall under a pixel and flicker out (the floor scales with resolution so a 4K frame keeps the same apparent weight),
                // which is how edges went missing at the default framing. World size of a pixel at this depth.
                float4 cs0 = TransformWorldToHClip(posWS);
                float px = cs0.w * 2.0 / (_ScreenParams.y * abs(UNITY_MATRIX_P[1][1]));
                w = max(w, px * max(2.0, _ScreenParams.y / 540.0));
                posWS += side * (input.uv0.y * 0.5 * w);
                o.positionCS = TransformWorldToHClip(posWS);
                o.uv0 = input.uv0;
                o.uv1 = input.uv1;
                o.pulse = input.pulse;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float tier = floor(i.uv1.x + 0.01);
                float down = step(0.25, frac(i.uv1.x + 0.01));
                float len = max(i.uv1.y, 0.001);
                float t = i.uv0.x;

                float3 baseCol = tier < 1.5 ? _LinkColor1.rgb : _LinkColor2.rgb;
                float baseA = tier < 1.5 ? _LinkColor1.a : _LinkColor2.a;
                baseCol = lerp(baseCol, _DownColor.rgb, down);
                baseA *= lerp(1.0, 0.55, down);

                float3 flow = lerp(_FlowColor.rgb, _MemColor.rgb, saturate(i.pulse.w));
                float hot = saturate(i.pulse.x);
                float3 col = lerp(baseCol, flow, saturate(hot * 1.5));
                float a = lerp(baseA, 1.0, hot);

                float pl = max(_PulseLength, 0.01);
                float d0 = (t - i.pulse.y) * len;
                float d1 = (t - i.pulse.z) * len;
                float b0 = i.pulse.y >= 0.0 ? exp(-(d0 * d0) / (pl * pl)) : 0.0;
                float b1 = i.pulse.z >= 0.0 ? exp(-(d1 * d1) / (pl * pl)) : 0.0;
                float bead = saturate(b0 + b1);
                col += flow * bead * 2.4;
                a = saturate(a + bead);

                // A glass tube, lit the way the node domes are: the ribbon's cross-section is treated as a cylinder seen
                // side-on, so rho (0 centre, 1 edge) gives n.v = sqrt(1 - rho^2) and the same fresnel rim the spheres use.
                // Clear down the middle, bright along both edges, one thin specular streak off-centre. Rejected: a real
                // tube mesh per edge, which costs vertices and a second draw for what is a 1D profile.
                float rho = abs(i.uv0.y);
                float ndv = sqrt(saturate(1.0 - rho * rho));
                float rim = pow(1.0 - ndv, _RimPower);
                float body = _Fill + rim * (1.0 - _Fill);
                float streak = exp(-pow((i.uv0.y + 0.42) / 0.10, 2.0)) * 0.35;
                col *= 1.0 + rim * 1.2 + streak;
                // A lit edge fills in, like a node whose glow rises; the bead is solid across the tube.
                body = lerp(body, 1.0, saturate(hot * 0.75 + bead));
                float aa = max(fwidth(rho), 1e-4);
                a *= body * (1.0 - smoothstep(1.0 - aa * 1.5, 1.0, rho));

                col = MixFog(col, i.fogFactor);
                return half4(col, a);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
