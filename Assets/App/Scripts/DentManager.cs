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

    static readonly int DentPosID       = Shader.PropertyToID("_DentPos");
    static readonly int DentAxisID      = Shader.PropertyToID("_DentAxis");
    static readonly int DentParamsID    = Shader.PropertyToID("_DentParams");
    static readonly int DentCountID     = Shader.PropertyToID("_DentCount");
    static readonly int PrevTexID       = Shader.PropertyToID("_PrevDentMap");
    static readonly int DecayID         = Shader.PropertyToID("_Decay");
    static readonly int FlipYID         = Shader.PropertyToID("_FlipY");
    static readonly int DebugStampAllID = Shader.PropertyToID("_DebugStampAll");

    static readonly List<DentSource> sources = new List<DentSource>();

    // xyz = world position,  w = shape id (0 sphere, 1 cylinder)
    readonly Vector4[] dentPos    = new Vector4[MAX_DENTS];
    // xyz = world push axis, w = axial reach
    readonly Vector4[] dentAxis   = new Vector4[MAX_DENTS];
    // x = inner radius, y = outer radius, z = intensity, w = unused
    readonly Vector4[] dentParams = new Vector4[MAX_DENTS];

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

        bool flipY = overrideFlipY ? flipYValue : SystemInfo.graphicsUVStartsAtTop;

        stampMaterial.SetVectorArray(DentPosID, dentPos);
        stampMaterial.SetVectorArray(DentAxisID, dentAxis);
        stampMaterial.SetVectorArray(DentParamsID, dentParams);
        stampMaterial.SetInt(DentCountID, ActiveDentCount);
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
            Vector3 axis = s.transform.forward; // +Z is the push direction

            dentPos[i]    = new Vector4(p.x, p.y, p.z, (float)s.shape);
            dentAxis[i]   = new Vector4(axis.x, axis.y, axis.z, Mathf.Max(s.axialReach, 0.0001f));
            dentParams[i] = new Vector4(s.SafeInnerRadius, s.SafeOuterRadius, s.intensity, 0f);
        }

        for (int i = count; i < MAX_DENTS; i++)
        {
            dentPos[i]    = Vector4.zero;
            dentAxis[i]   = Vector4.zero;
            dentParams[i] = Vector4.zero;
        }
    }

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
