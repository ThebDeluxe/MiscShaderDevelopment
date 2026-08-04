Shader "Custom/DentStampRaw"
{
    Properties
    {
        _PrevDentMap ("Prev Dent Map", 2D) = "black" {}
        _Decay ("Decay", Range(0,1)) = 0.98

        // Extra decay proportional to how deep a dent is, so deep dents do not linger
        // long after shallow ones have faded.
        _DecayDepthBias ("Decay Depth Bias", Float) = 0

        // 1 = flip Y when writing (needed on D3D / anything where UVs start at top).
        // DentManager sets this automatically from SystemInfo.graphicsUVStartsAtTop.
        _FlipY ("Flip Y On Write", Float) = 1

        // 1 = ignore dent sources and push EVERY vertex, to verify the vertex->texel mapping.
        _DebugStampAll ("Debug: Stamp All", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Stamp"

            Cull Off
            ZWrite Off
            ZTest Always
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/App/Art/Shaders/HLSL/DentStamp.hlsl"

            TEXTURE2D(_PrevDentMap);
            SAMPLER(sampler_PrevDentMap);

            float _Decay;
            float _DecayDepthBias;
            float _FlipY;
            float _DebugStampAll;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                // Index mapping from DentVertexUVGenerator.
                // xy = this vertex's texel centre, z = island id.
                // TEXCOORD2 == Shader Graph "UV2".
                float3 indexUV    : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                float3 worldNormal: TEXCOORD2;
                float  islandId   : TEXCOORD3;
                float  pointSize  : PSIZE;   // ignored on D3D, drives gl_PointSize on GL/WebGL
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.worldPos = TransformObjectToWorld(IN.positionOS);
                // The rim bulge follows the surface, so it needs the vertex's own normal.
                OUT.worldNormal = normalize(TransformObjectToWorldNormal(IN.normalOS));

                // Rasterise straight into texel space: no MVP transform, so this is
                // completely camera independent.
                float2 ndc = IN.indexUV.xy * 2.0 - 1.0;
                ndc.y = lerp(ndc.y, -ndc.y, saturate(_FlipY));
                OUT.positionCS = float4(ndc, 0.0, 1.0);

                OUT.uv = IN.indexUV.xy;
                OUT.islandId = IN.indexUV.z;
                OUT.pointSize = 1.0;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float3 disp;
                float  dispDecayMul;
                CalculateDentVector_float(IN.worldPos, IN.worldNormal, IN.islandId, disp, dispDecayMul);

                // Debug override: push everything along object-space up.
                if (_DebugStampAll > 0.5)
                {
                    disp = TransformWorldToObjectDir(float3(0, 1, 0), false);
                    dispDecayMul = 1.0;
                }

                // LOD 0 explicitly: point primitives have no useful screen-space derivatives.
                float4 prev = SAMPLE_TEXTURE2D_LOD(_PrevDentMap, sampler_PrevDentMap, IN.uv, 0);

                // Alpha carries the decay multiplier of whatever wrote this texel. A cleared
                // buffer has alpha 0, which means "no stamp yet", so fall back to 1.
                float storedMul = (prev.a > 1e-5) ? prev.a : 1.0;

                // Deeper dents fade faster when the bias is up, which is what stops a deep
                // press outliving the shallow ones around it.
                float prevMag = length(prev.rgb);
                float rate = storedMul * (1.0 + max(_DecayDepthBias, 0.0) * prevMag);
                float3 decayed = prev.rgb * pow(_Decay, rate);

                // Strongest wins. Swap for (decayed + disp) if you'd rather dents accumulate.
                bool newWins = dot(disp, disp) > dot(decayed, decayed);

                float3 result    = newWins ? disp : decayed;
                float  resultMul = newWins ? dispDecayMul : storedMul;

                // RGB = object space displacement vector. A = this texel's decay multiplier.
                return float4(result, resultMul);
            }
            ENDHLSL
        }
    }
}
