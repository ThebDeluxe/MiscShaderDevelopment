Shader "Hidden/BlobShadowSplat"
{
    Properties
    {
        _BlobSoftness ("Softness (inner radius)", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            // Max blending: overlapping blobs don't stack into black patches.
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            StructuredBuffer<float4> _BlobCasters; // x,z position | radius | strength
            float4 _BlobShadowParams;              // xy = origin (xz), z = 1 / worldSize
            float  _BlobSoftness;

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float  strength : TEXCOORD1;
            };

            static const float2 kCorners[6] =
            {
                float2(-1, -1), float2( 1, -1), float2( 1,  1),
                float2(-1, -1), float2( 1,  1), float2(-1,  1)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                float4 c      = _BlobCasters[iid];
                float2 corner = kCorners[vid];
                float2 world  = c.xy + corner * c.z;

                float2 uv01 = (world - _BlobShadowParams.xy) * _BlobShadowParams.z;
                float2 clip = uv01 * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif

                v2f o;
                o.pos      = float4(clip, 0.5, 1.0);
                o.uv       = corner;
                o.strength = c.w;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float d = length(i.uv);
                float a = (1.0 - smoothstep(_BlobSoftness, 1.0, d)) * i.strength;
                return a.xxxx;
            }
            ENDHLSL
        }
    }
}
