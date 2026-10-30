Shader "Hidden/BlobShadowSplat"
{
    Properties
    {
        _BlobSoftness ("Softness (inner radius)", Range(0, 1)) = 0.2
        _BlobContactEmphasis ("Contact Emphasis (darker feet)", Range(0, 1)) = 0.4
        _BlobTipFade ("Fade Toward Tip", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            // Max blending: overlapping shadows don't stack into black patches.
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            struct Caster
            {
                float4 groundRadius;   // xyz = ground position, w = radius
                float4 heightStrength; // x = caster height, y = strength
            };

            StructuredBuffer<Caster> _BlobCasters;
            float4 _BlobShadowParams; // xy = origin (xz), z = 1 / worldSize
            float4 _BlobShadowLight;  // xy = ground offset per metre of height
            float  _BlobSoftness;
            float  _BlobContactEmphasis;
            float  _BlobTipFade;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 local : TEXCOORD0;                  // x = along shadow, y = across (world units)
                nointerpolation float3 shape : TEXCOORD1;  // x = length, y = radius, z = strength
            };

            static const float2 kCorners[6] =
            {
                float2(0, 0), float2(1, 0), float2(1, 1),
                float2(0, 0), float2(1, 1), float2(0, 1)
            };

            v2f vert(uint vid : SV_VertexID, uint iid : SV_InstanceID)
            {
                Caster c = _BlobCasters[iid];
                float3 g = c.groundRadius.xyz;
                float  r = max(c.groundRadius.w, 1e-3);
                float  h = c.heightStrength.x;

                // Project the feet and the top of the caster onto the ground along the light.
                float2 offsetPerHeight = _BlobShadowLight.xy;
                float2 feet = g.xz + offsetPerHeight * g.y;
                float2 tip  = g.xz + offsetPerHeight * (g.y + h);

                float2 axis = tip - feet;
                float  len  = length(axis);
                float2 dir  = len > 1e-4 ? axis / len : float2(1, 0);
                float2 perp = float2(-dir.y, dir.x);

                // Oriented quad that covers the whole capsule.
                float2 k      = kCorners[vid];
                float  along  = lerp(-r, len + r, k.x);
                float  across = lerp(-r, r, k.y);
                float2 world  = feet + dir * along + perp * across;

                float2 uv01 = (world - _BlobShadowParams.xy) * _BlobShadowParams.z;
                float2 clip = uv01 * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif

                v2f o;
                o.pos   = float4(clip, 0.5, 1.0);
                o.local = float2(along, across);
                o.shape = float3(len, r, c.heightStrength.y);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float len = i.shape.x;
                float r   = i.shape.y;

                // Capsule: distance to the feet-to-tip segment.
                float alongC = clamp(i.local.x, 0.0, len);
                float dBody  = length(float2(i.local.x - alongC, i.local.y)) / r;
                float body   = 1.0 - smoothstep(_BlobSoftness, 1.0, dBody);
                float t      = len > 1e-4 ? alongC / len : 0.0;
                body *= 1.0 - t * _BlobTipFade;

                // Small dark blob right under the feet.
                float dContact = length(i.local) / r;
                float contact  = 1.0 - smoothstep(_BlobSoftness, 1.0, dContact);

                float a = saturate(body * (1.0 - _BlobContactEmphasis) + contact * _BlobContactEmphasis);
                a *= i.shape.z;
                return a.xxxx;
            }
            ENDHLSL
        }
    }
}
