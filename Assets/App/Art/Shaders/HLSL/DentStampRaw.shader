Shader "Custom/DentStampRaw"
{
    Properties
    {
        _PrevDentMap ("Prev Dent Map", 2D) = "black" {}
        _Decay ("Decay", Range(0,1)) = 0.98

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
            float _FlipY;
            float _DebugStampAll;

            struct Attributes
            {
                float3 positionOS : POSITION;
                // Index-mapping UV from DentVertexUVGenerator.
                // TEXCOORD2 == Mesh.uv3 == Shader Graph "UV2".
                float2 indexUV    : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 worldPos   : TEXCOORD1;
                float  pointSize  : PSIZE;   // ignored on D3D, drives gl_PointSize on GL/WebGL
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.worldPos = TransformObjectToWorld(IN.positionOS);

                // Rasterise straight into texel space: no MVP transform, so this is
                // completely camera independent.
                float2 ndc = IN.indexUV * 2.0 - 1.0;
                ndc.y = lerp(ndc.y, -ndc.y, saturate(_FlipY));
                OUT.positionCS = float4(ndc, 0.0, 1.0);

                OUT.uv = IN.indexUV;
                OUT.pointSize = 1.0;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float3 disp;
                CalculateDentVector_float(IN.worldPos, disp);

                // Debug override: push everything along object-space up.
                if (_DebugStampAll > 0.5)
                    disp = TransformWorldToObjectDir(float3(0, 1, 0), false);

                // LOD 0 explicitly: point primitives have no useful screen-space derivatives.
                float4 prev    = SAMPLE_TEXTURE2D_LOD(_PrevDentMap, sampler_PrevDentMap, IN.uv, 0);
                float3 decayed = prev.rgb * _Decay;

                // Strongest wins. Swap for (decayed + disp) if you'd rather dents accumulate.
                float3 result = (dot(disp, disp) > dot(decayed, decayed)) ? disp : decayed;

                // RGB = object space displacement vector. A is free for later use.
                return float4(result, 0.0);
            }
            ENDHLSL
        }
    }
}
