using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

// Gathers every blob shadow caster (ECS + GameObject), draws one soft capsule per caster
// into a top-down R8 texture (Max blending), and publishes it as a global texture.
[UpdateInGroup(typeof(PresentationSystemGroup))]
public partial class BlobShadowSystem : SystemBase
{
    // --- Tuning ---
    const int   Resolution = 512;   // texture size
    const float WorldSize  = 80f;   // world units covered by the texture
    const float HeightFade = 3f;    // casters whose ground position is this high cast nothing (0 = off)

    static readonly int TexId     = Shader.PropertyToID("_BlobShadowTex");
    static readonly int ParamsId  = Shader.PropertyToID("_BlobShadowParams");
    static readonly int LightId   = Shader.PropertyToID("_BlobShadowLight");
    static readonly int CastersId = Shader.PropertyToID("_BlobCasters");

    RenderTexture  _rt;
    GraphicsBuffer _buffer;
    int            _capacity;
    Material       _material;
    CommandBuffer  _cmd;
    EntityQuery    _query;

    Light   _sun;
    bool    _lightSet;
    Vector3 _lastLightForward;

    protected override void OnCreate()
    {
        _query = GetEntityQuery(
            ComponentType.ReadOnly<LocalToWorld>(),
            ComponentType.ReadOnly<BlobShadowCaster>());

        // Hidden shaders must be in Always Included Shaders (or referenced) for builds.
        _material = new Material(Shader.Find("Hidden/BlobShadowSplat"));

        _rt = new RenderTexture(Resolution, Resolution, GraphicsFormat.R8_UNorm, GraphicsFormat.None)
        {
            name       = "BlobShadowTex",
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };
        _rt.Create();

        _cmd = new CommandBuffer { name = "Blob Shadows" };
        EnsureCapacity(1024);
        Shader.SetGlobalTexture(TexId, _rt);
        Shader.SetGlobalVector(LightId, Vector4.zero);
    }

    void EnsureCapacity(int count)
    {
        if (_buffer != null && count <= _capacity) return;
        _buffer?.Release();
        _capacity = math.max(count, _capacity * 2);
        _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity,
                                     UnsafeUtility.SizeOf<BlobCasterData>());
        _material.SetBuffer(CastersId, _buffer);
    }

    // Only recomputes the shadow direction/length when the light actually rotates.
    void UpdateLight()
    {
        if (_sun == null && (!_lightSet || UnityEngine.Time.frameCount % 30 == 0))
            _sun = BlobShadowLight.FindSun();

        Vector3 forward = _sun != null ? _sun.transform.forward : Vector3.down;
        if (_lightSet && forward == _lastLightForward) return;

        _lightSet = true;
        _lastLightForward = forward;
        float2 o = BlobShadowLight.ComputeOffsetPerHeight(forward);
        Shader.SetGlobalVector(LightId, new Vector4(o.x, o.y, 0f, 0f));
    }

    protected override void OnUpdate()
    {
        var cam = Camera.main;
        if (cam == null) return;

        UpdateLight();

        // Centre the texture where the camera looks at the ground plane (y = 0).
        Vector3 camPos = cam.transform.position;
        Vector3 fwd    = cam.transform.forward;
        float2 centre  = new float2(camPos.x, camPos.z);
        if (fwd.y < -0.01f)
        {
            float t = -camPos.y / fwd.y;
            centre = new float2(camPos.x + fwd.x * t, camPos.z + fwd.z * t);
        }

        // Snap to whole texels so shadows don't shimmer as the camera moves.
        float texel = WorldSize / Resolution;
        centre = math.floor(centre / texel) * texel;
        float2 origin = centre - WorldSize * 0.5f;

        Shader.SetGlobalVector(ParamsId, new Vector4(origin.x, origin.y, 1f / WorldSize, 0f));

        // Gather caster data: ECS casters via a Burst job, GameObject casters on the main thread.
        int ecsCount  = _query.CalculateEntityCount();
        var goCasters = BlobShadowCasterObject.All;
        int count     = ecsCount + goCasters.Count;
        EnsureCapacity(count);

        if (count > 0)
        {
            var casters = new NativeArray<BlobCasterData>(count, Allocator.TempJob);

            if (ecsCount > 0)
                new FillJob { Out = casters, HeightFade = HeightFade }
                    .ScheduleParallel(_query, Dependency).Complete();

            for (int i = 0; i < goCasters.Count; i++)
                casters[ecsCount + i] = goCasters[i].GetCasterData(HeightFade);

            _buffer.SetData(casters, 0, 0, count);
            casters.Dispose();
        }

        // Splat.
        _cmd.Clear();
        _cmd.SetRenderTarget(_rt);
        _cmd.ClearRenderTarget(false, true, Color.clear);
        if (count > 0)
            _cmd.DrawProcedural(Matrix4x4.identity, _material, 0, MeshTopology.Triangles, 6, count);
        Graphics.ExecuteCommandBuffer(_cmd);
    }

    protected override void OnDestroy()
    {
        _buffer?.Release();
        _cmd?.Release();
        if (_rt != null) _rt.Release();
        if (_material != null) Object.Destroy(_material);
    }

    [BurstCompile]
    partial struct FillJob : IJobEntity
    {
        [NativeDisableParallelForRestriction] public NativeArray<BlobCasterData> Out;
        public float HeightFade;

        void Execute([EntityIndexInQuery] int i, in LocalToWorld ltw, in BlobShadowCaster caster)
        {
            float3 g = math.transform(ltw.Value, caster.GroundOffset);
            float fade = HeightFade > 0f ? math.saturate(1f - g.y / HeightFade) : 1f;
            Out[i] = new BlobCasterData
            {
                GroundAndRadius = new float4(g, caster.Radius),
                HeightStrength  = new float4(caster.Height, caster.Strength * fade, 0f, 0f)
            };
        }
    }
}
