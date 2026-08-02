using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the dent map: every frame it stamps all active DentSources into a
/// double-buffered RenderTexture, then publishes the result as a global texture
/// for the character shader to read in its vertex stage.
///
/// The map stores an OBJECT SPACE DISPLACEMENT VECTOR in RGB (A is unused for now),
/// so it needs a signed, higher-precision format - ARGBHalf. An 8-bit format cannot
/// store signed directions, and its quantisation also stalls the decay.
///
/// The stamp pass draws the mesh as POINTS into texel space (see DentVertexUVGenerator),
/// so each vertex writes exactly one texel. Camera independent, and independent of the
/// authored UV0 layout.
///
/// Play mode only, deliberately: creating mesh/RT instances in edit mode leaks assets.
/// </summary>
public class DentManager : MonoBehaviour
{
    [Header("Target")]
    public Renderer targetRenderer;
    public Material stampMaterial;

    [Tooltip("Must be a signed, floating point format. ARGBHalf is the sane default; " +
             "RGBA8 cannot store negative direction components.")]
    public RenderTextureFormat format = RenderTextureFormat.ARGBHalf;

    [Header("Decay")]
    [Tooltip("Fraction of the dent magnitude lost per second. 0 = dents are permanent.")]
    [Range(0f, 1f)] public float decayPerSecond = 0.15f;

    [Header("Output")]
    public string globalTextureName = "_CustomRT_Dents";

    [Header("Island Rigidity")]
    [Tooltip("How much disconnected mesh parts move as a rigid block instead of being\n" +
             "pressed per-vertex. 1 keeps a part's shape and its offset from whatever it\n" +
             "sits on; 0 is the old per-vertex behaviour.")]
    [Range(0f, 1f)] public float islandRigidity = 1f;

    [Tooltip("Islands with a local-space radius at or below this are FULLY rigid: the whole " +
             "piece translates by one push value and does not bend at all.\n" +
             "Meant for small detail parts that should keep their shape and their offset " +
             "from whatever they sit on.")]
    public float rigidBelowRadius = 0.1f;

    [Tooltip("Islands at or above this local-space radius are pressed fully per-vertex, so " +
             "they bend to the stamp.\n" +
             "Between the two radii rigidity ramps smoothly, which avoids a hard population " +
             "split where some pieces bend and others move as a solid block.")]
    public float flexibleAboveRadius = 0.35f;

    [Header("Debug")]
    [Tooltip("Push EVERY vertex along object-space up, ignoring dent sources.\n" +
             "If the mapping is correct the whole mesh should displace uniformly.\n" +
             "If only parts move, the vertex->texel mapping is wrong (try Override Flip Y).")]
    public bool debugStampAll = false;

    [Tooltip("Leave off to auto-detect from SystemInfo.graphicsUVStartsAtTop.\n" +
             "Turn on and change Flip Y Value if dents look scrambled across unrelated vertices.")]
    public bool overrideFlipY = false;
    public bool flipYValue = true;

    const int MAX_DENTS = 32;
    const int MAX_ISLANDS = 32;   // must match ISLAND_MAX in DentStamp.hlsl

    static readonly int DentPosID       = Shader.PropertyToID("_DentPos");
    static readonly int DentAxisID      = Shader.PropertyToID("_DentAxis");
    static readonly int DentRightID     = Shader.PropertyToID("_DentRight");
    static readonly int DentParamsID    = Shader.PropertyToID("_DentParams");
    static readonly int DentCountID     = Shader.PropertyToID("_DentCount");
    static readonly int PrevTexID       = Shader.PropertyToID("_PrevDentMap");
    static readonly int DecayID         = Shader.PropertyToID("_Decay");
    static readonly int FlipYID         = Shader.PropertyToID("_FlipY");
    static readonly int DebugStampAllID = Shader.PropertyToID("_DebugStampAll");
    static readonly int IslandPushID    = Shader.PropertyToID("_IslandPush");
    static readonly int IslandCountID   = Shader.PropertyToID("_IslandCount");

    static readonly List<DentSource> sources = new List<DentSource>();

    // xyz = world position of contact point, w = shape id (0 capsule, 1 cylinder, 2 square)
    readonly Vector4[] dentPos    = new Vector4[MAX_DENTS];
    // xyz = world press axis (+Z),          w = depth (max penetration)
    readonly Vector4[] dentAxis   = new Vector4[MAX_DENTS];
    // xyz = world right (+X, orients Square), w = flatten scale
    readonly Vector4[] dentRight  = new Vector4[MAX_DENTS];
    // x = inner radius, y = outer radius, z = strength, w = unused
    readonly Vector4[] dentParams = new Vector4[MAX_DENTS];
    // xyz = OBJECT space rigid push for this island, w = rigidity 0..1
    readonly Vector4[] islandPush = new Vector4[MAX_ISLANDS];

    DentVertexUVGenerator generator;
    RenderTexture rtA, rtB;
    bool aIsCurrent;
    CommandBuffer cmd;
    bool initialised;

    public RenderTexture CurrentDentMap => aIsCurrent ? rtA : rtB;
    public int TextureSize => generator != null ? generator.TextureSize : 0;
    public int ActiveDentCount => Mathf.Min(sources.Count, MAX_DENTS);

    public static void Register(DentSource s)
    {
        if (!sources.Contains(s)) sources.Add(s);
    }

    public static void Unregister(DentSource s) => sources.Remove(s);

    void OnEnable()
    {
        // Entering play mode: make sure nothing is left over from a previous session.
        Shader.SetGlobalTexture(globalTextureName, Texture2D.blackTexture);
    }

    void OnDisable()
    {
        Release();
    }

    /// <summary>Wipes all accumulated dents back to zero.</summary>
    public void ResetDents()
    {
        ClearRT(rtA);
        ClearRT(rtB);
    }

    bool EnsureInitialised()
    {
        if (initialised) return true;

        if (targetRenderer == null)
        {
            Debug.LogError($"{name}: targetRenderer is not assigned.", this);
            enabled = false;
            return false;
        }

        if (stampMaterial == null)
        {
            Debug.LogError($"{name}: stampMaterial is not assigned.", this);
            enabled = false;
            return false;
        }

        generator = targetRenderer.GetComponent<DentVertexUVGenerator>();
        if (generator == null)
        {
            Debug.LogError($"{name}: targetRenderer '{targetRenderer.name}' has no DentVertexUVGenerator. " +
                            "Add one so the dent system gets a unique vertex->texel mapping.", this);
            enabled = false;
            return false;
        }

        generator.Generate(); // no-op if Awake already ran

        if (!generator.IsGenerated || generator.PointMesh == null)
        {
            Debug.LogError($"{name}: DentVertexUVGenerator failed to produce a point mesh.", this);
            enabled = false;
            return false;
        }

        if (!SystemInfo.SupportsRenderTextureFormat(format))
        {
            Debug.LogWarning($"{name}: {format} unsupported on this device, falling back to ARGBFloat.", this);
            format = RenderTextureFormat.ARGBFloat;
        }

        int size = generator.TextureSize;
        rtA = CreateRT(size);
        rtB = CreateRT(size);
        aIsCurrent = true;

        cmd = new CommandBuffer { name = "Dent Stamp Pass" };

        initialised = true;
        return true;
    }

    RenderTexture CreateRT(int size)
    {
        var rt = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
        {
            name = $"DentMap_{size}",
            wrapMode = TextureWrapMode.Clamp,
            // Point is required: neighbouring texels belong to unrelated vertices,
            // so any filtering would blend garbage between them.
            filterMode = FilterMode.Point,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        ClearRT(rt);
        return rt;
    }

    static void ClearRT(RenderTexture rt)
    {
        if (rt == null) return;
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear); // zero displacement
        RenderTexture.active = prevActive;
    }

    void LateUpdate()
    {
        if (!EnsureInitialised()) return;

        RenderTexture src = aIsCurrent ? rtA : rtB;
        RenderTexture dst = aIsCurrent ? rtB : rtA;

        BuildDentArrays();
        BuildIslandArray();

        bool flipY = overrideFlipY ? flipYValue : SystemInfo.graphicsUVStartsAtTop;

        stampMaterial.SetVectorArray(DentPosID, dentPos);
        stampMaterial.SetVectorArray(DentAxisID, dentAxis);
        stampMaterial.SetVectorArray(DentRightID, dentRight);
        stampMaterial.SetVectorArray(DentParamsID, dentParams);
        stampMaterial.SetInt(DentCountID, ActiveDentCount);
        stampMaterial.SetVectorArray(IslandPushID, islandPush);
        stampMaterial.SetInt(IslandCountID, Mathf.Min(generator.IslandCount, MAX_ISLANDS));
        stampMaterial.SetTexture(PrevTexID, src);
        stampMaterial.SetFloat(DecayID, Mathf.Pow(1f - decayPerSecond, Time.deltaTime));
        stampMaterial.SetFloat(FlipYID, flipY ? 1f : 0f);
        stampMaterial.SetFloat(DebugStampAllID, debugStampAll ? 1f : 0f);

        cmd.Clear();
        cmd.SetRenderTarget(dst);
        // DrawMesh (not DrawRenderer) so we can substitute the points-topology mesh.
        // The matrix must be supplied explicitly here, and is also what makes
        // TransformWorldToObjectDir correct inside the stamp shader.
        cmd.DrawMesh(generator.PointMesh, targetRenderer.transform.localToWorldMatrix, stampMaterial, 0, 0);
        Graphics.ExecuteCommandBuffer(cmd);

        aIsCurrent = !aIsCurrent;

        Shader.SetGlobalTexture(globalTextureName, dst);
    }

    void BuildDentArrays()
    {
        int count = ActiveDentCount;

        for (int i = 0; i < count; i++)
        {
            var s = sources[i];
            Vector3 p = s.transform.position;
            Vector3 axis = s.transform.forward;  // +Z is the press direction
            Vector3 right = s.transform.right;   // orients the Square cross-section

            dentPos[i]    = new Vector4(p.x, p.y, p.z, (float)s.shape);
            dentAxis[i]   = new Vector4(axis.x, axis.y, axis.z, Mathf.Max(s.depth, 0.0001f));
            dentRight[i]  = new Vector4(right.x, right.y, right.z, Mathf.Clamp01(s.flattenScale));
            dentParams[i] = new Vector4(s.SafeInnerRadius, s.SafeOuterRadius, s.strength, 0f);
        }

        for (int i = count; i < MAX_DENTS; i++)
        {
            dentPos[i]    = Vector4.zero;
            dentAxis[i]   = Vector4.zero;
            dentRight[i]  = Vector4.zero;
            dentParams[i] = Vector4.zero;
        }
    }

    /// <summary>
    /// Evaluates the press once per mesh island so the stamp shader can move whole
    /// disconnected parts rigidly.
    ///
    /// Each island is sampled at several points and the STRONGEST push wins. A single
    /// centroid is not enough: on a toroid the centroid sits in the hole, outside the
    /// mesh, and would report no penetration while the ring itself is fully pressed.
    /// </summary>
    void BuildIslandArray()
    {
        int count = Mathf.Min(generator.IslandCount, MAX_ISLANDS);
        Transform tr = targetRenderer.transform;

        for (int i = 0; i < count; i++)
        {
            Vector3[] samples = generator.IslandSamples[i];

            Vector3 bestPushWS = Vector3.zero;
            float bestMagSq = 0f;

            for (int s = 0; s < samples.Length; s++)
            {
                Vector3 pushWS = EvaluateDentWorld(tr.TransformPoint(samples[s]));
                float magSq = pushWS.sqrMagnitude;
                if (magSq > bestMagSq)
                {
                    bestMagSq = magSq;
                    bestPushWS = pushWS;
                }
            }

            Vector3 pushOS = tr.InverseTransformDirection(bestPushWS);

            // Rigidity ramps with island size: small detail parts move as a block, large
            // ones bend to the stamp, and everything between blends rather than snapping
            // from one behaviour to the other.
            float radius = generator.IslandRadii[i];
            float sizeT = Mathf.InverseLerp(rigidBelowRadius,
                                            Mathf.Max(flexibleAboveRadius, rigidBelowRadius + 1e-4f),
                                            radius);
            float rigidity = islandRigidity * (1f - Mathf.SmoothStep(0f, 1f, sizeT));

            islandPush[i] = new Vector4(pushOS.x, pushOS.y, pushOS.z, rigidity);
        }

        for (int i = count; i < MAX_ISLANDS; i++)
            islandPush[i] = Vector4.zero;
    }

    // --------------------------------------------------------------------
    // CPU mirror of the press maths in DentStamp.hlsl.
    // Keep these two in step: if the shape logic changes there, change it here.
    // --------------------------------------------------------------------

    Vector3 EvaluateDentWorld(Vector3 worldPos)
    {
        Vector3 bestDisp = Vector3.zero;
        float bestMagSq = 0f;

        int count = ActiveDentCount;
        for (int i = 0; i < count; i++)
        {
            var s = sources[i];
            Vector3 axis = s.transform.forward;
            Vector3 right = s.transform.right;
            Vector3 toPoint = worldPos - s.transform.position;

            float inner = s.SafeInnerRadius;
            float outer = s.SafeOuterRadius;
            float axial = Vector3.Dot(toPoint, axis);

            // Square uses a Chebyshev cross-section; the other two are round.
            float lat;
            if (s.shape == DentShape.Square)
            {
                Vector3 up = Vector3.Cross(axis, right);
                lat = Mathf.Max(Mathf.Abs(Vector3.Dot(toPoint, right)),
                                Mathf.Abs(Vector3.Dot(toPoint, up)));
            }
            else
            {
                lat = Vector3.ProjectOnPlane(toPoint, axis).magnitude;
            }

            // Capsule is simply the punch with no flat face at all.
            float innerEff = s.shape == DentShape.Capsule ? 0f : inner;

            float surfaceAxial = DentSurfaceAxial(lat, innerEff, outer);

            float penetration = Mathf.Clamp(surfaceAxial - axial, 0f, Mathf.Max(s.depth, 0.0001f));
            float push = penetration * (1f - Mathf.Clamp01(s.flattenScale)) * s.strength;

            Vector3 disp = axis * push;
            float magSq = disp.sqrMagnitude;
            if (magSq > bestMagSq)
            {
                bestMagSq = magSq;
                bestDisp = disp;
            }
        }

        return bestDisp;
    }

    /// <summary>Mirror of DentSurfaceAxial in DentStamp.hlsl.</summary>
    static float DentSurfaceAxial(float lat, float inner, float outer)
    {
        if (lat >= outer) return -1e9f;
        if (lat <= inner) return 0f;

        float r = Mathf.Max(outer - inner, 1e-5f);
        float d = lat - inner;
        return -(r - Mathf.Sqrt(Mathf.Max(r * r - d * d, 0f)));
    }

    /// <summary>
    /// Displacement for a point on the mesh, in OBJECT space, matching what the stamp
    /// shader computes for a vertex - including the island rigidity blend.
    ///
    /// Used by DentColliderRig so physics and visuals agree. Note this reflects the
    /// CURRENT stamps only: the accumulated/decayed history lives in the dent texture,
    /// which the CPU has no view of. Callers that need history must accumulate it
    /// themselves using the same max-with-decay rule.
    /// </summary>
    public Vector3 EvaluateDisplacementOS(Vector3 localPos, int islandId)
    {
        if (targetRenderer == null) return Vector3.zero;

        Transform tr = targetRenderer.transform;
        Vector3 perVertexOS = tr.InverseTransformDirection(EvaluateDentWorld(tr.TransformPoint(localPos)));

        if (islandId < 0 || islandId >= MAX_ISLANDS) return perVertexOS;

        Vector4 island = islandPush[islandId];
        return Vector3.Lerp(perVertexOS,
                            new Vector3(island.x, island.y, island.z),
                            Mathf.Clamp01(island.w));
    }

    /// <summary>Same decay factor the stamp shader uses this frame.</summary>
    public float CurrentDecay => Mathf.Pow(1f - decayPerSecond, Time.deltaTime);

    void Release()
    {
        // Point the global at a known-zero texture BEFORE destroying the RTs, otherwise
        // the character material keeps sampling a released/garbage buffer and appears
        // fully deformed after exiting play mode.
        Shader.SetGlobalTexture(globalTextureName, Texture2D.blackTexture);

        DestroyRT(ref rtA);
        DestroyRT(ref rtB);

        if (cmd != null) { cmd.Release(); cmd = null; }
        initialised = false;
    }

    static void DestroyRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        // Release() only frees GPU memory; the object itself would leak otherwise.
        if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
        rt = null;
    }
}
