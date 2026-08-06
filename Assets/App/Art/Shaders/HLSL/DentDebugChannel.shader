Shader "Hidden/DentDebugChannel"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Channel ("Channel", Float) = 0
        _Absolute ("Absolute", Float) = 1
    }

    SubShader
    {
        Tags { "PreviewType" = "Plane" }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            float _Channel;    // 0 = RGB, 1 = R, 2 = G, 3 = B, 4 = A
            float _Absolute;   // 1 = show magnitude, so negative values are visible

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                // Point sampled: the dent map holds one texel per vertex, so filtering
                // between them would blend unrelated values.
                float4 c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, IN.uv, 0);

                // The map stores a SIGNED displacement vector, so half the data sits below
                // zero and would otherwise clip to black and look like nothing is there.
                c = lerp(c, abs(c), saturate(_Absolute));

                if (_Channel < 0.5) return float4(c.rgb, 1);
                if (_Channel < 1.5) return float4(c.rrr, 1);
                if (_Channel < 2.5) return float4(c.ggg, 1);
                if (_Channel < 3.5) return float4(c.bbb, 1);
                return float4(c.aaa, 1);
            }
            ENDHLSL
        }
    }
}
