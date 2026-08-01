using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the dent map: every frame it stamps all active DentSources into a
/// double-buffered RenderTexture, then publishes the result as a global texture
/// for the character shader to read in its vertex stage.
///
/// The stamp pass draws the mesh as POINTS into texel space (see DentVertexUVGenerator),
/// so each vertex writes exactly one texel. This is camera independent and does not
/// depend on the authored UV0 layout at all.
///
/// Play mode only, deliberately: creating mesh/RT instances in edit mode leaks assets.
/// </summary>
public class DentManager : MonoBehaviour
{
    [Header("Target")]
    public Renderer targetRenderer;
    public Material stampMaterial;
    public RenderTextureFormat format = RenderTextureFormat.R8;

    [Header("Decay")]
    [Tooltip("Fraction of the dent value lost per second. 0 = dents are permanent.")]
    [Range(0f, 1f)] public float decayPerSecond = 0.15f;

    [Header("Output")]
    public string globalTextureName = "_CustomRT_Dents";

    [Header("Debug")]
    [Tooltip("Stamp 1.0 into EVERY vertex texel, ignoring dent sources.\n" +
             "If the mapping is correct the whole mesh should displace uniformly by Max Dent Depth.\n" +
             "If only parts move, the vertex->texel mapping is wrong (try toggling Override Flip Y).")]
    public bool debugStampAll = false;

    [Tooltip("Leave off to auto-detect from SystemInfo.graphicsUVStartsAtTop.\n" +
             "Turn on and change Flip Y Value if the dents look scrambled across unrelated vertices.")]
    public bool overrideFlipY = false;
    public bool flipYValue = true;

    const int MAX_DENTS = 32;

    static readonly int DentDataID      = Shader.PropertyToID("_DentData");
    static readonly int DentIntensityID = Shader.PropertyToID("_DentIntensity");
    static readonly int DentCountID     = Shader.PropertyToID("_DentCount");
    static readonly int PrevTexID       = Shader.PropertyToID("_PrevDentMap");
    static readonly int DecayID         = Shader.PropertyToID("_Decay");
    static readonly int FlipYID         = Shader.PropertyToID("_FlipY");
    static readonly int DebugStampAllID = Shader.PropertyToID("_DebugStampAll");

    static readonly List<DentSource> sources = new List<DentSource>();

    readonly Vector4[] dentData      = new Vector4[MAX_DENTS];
    readonly float[]   dentIntensity = new float[MAX_DENTS];

    DentVertexUVGenerator generator;
    RenderTexture rtA, rtB;
    bool aIsCurrent;
    CommandBuffer cmd;
    bool initialised;

    public RenderTexture CurrentDentMap => aIsCurrent ? rtA : rtB;
    public int TextureSize => generator != null ? generator.TextureSize : 0;

    public static void Register(DentSource s)
    {
        if (!sources.Contains(s)) sources.Add(s);
    }

    public static void Unregister(DentSource s) => sources.Remove(s);

    void OnDisable()
    {
        Release();
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

        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = prevActive;

        return rt;
    }

    void LateUpdate()
    {
        if (!EnsureInitialised()) return;

        RenderTexture src = aIsCurrent ? rtA : rtB;
        RenderTexture dst = aIsCurrent ? rtB : rtA;

        BuildDentArrays();

        bool flipY = overrideFlipY ? flipYValue : SystemInfo.graphicsUVStartsAtTop;

        stampMaterial.SetVectorArray(DentDataID, dentData);
        stampMaterial.SetFloatArray(DentIntensityID, dentIntensity);
        stampMaterial.SetInt(DentCountID, Mathf.Min(sources.Count, MAX_DENTS));
        stampMaterial.SetTexture(PrevTexID, src);
        stampMaterial.SetFloat(DecayID, Mathf.Pow(1f - decayPerSecond, Time.deltaTime));
        stampMaterial.SetFloat(FlipYID, flipY ? 1f : 0f);
        stampMaterial.SetFloat(DebugStampAllID, debugStampAll ? 1f : 0f);

        cmd.Clear();
        cmd.SetRenderTarget(dst);
        // DrawMesh (not DrawRenderer) so we can substitute the points-topology mesh.
        // The matrix must be supplied explicitly here.
        cmd.DrawMesh(generator.PointMesh, targetRenderer.transform.localToWorldMatrix, stampMaterial, 0, 0);
        Graphics.ExecuteCommandBuffer(cmd);

        aIsCurrent = !aIsCurrent;

        Shader.SetGlobalTexture(globalTextureName, dst);
    }

    void BuildDentArrays()
    {
        int count = Mathf.Min(sources.Count, MAX_DENTS);
        for (int i = 0; i < count; i++)
        {
            var s = sources[i];
            Vector3 p = s.transform.position;
            // Guard against a zero radius producing a divide-by-zero in the shader.
            dentData[i] = new Vector4(p.x, p.y, p.z, Mathf.Max(s.radius, 0.0001f));
            dentIntensity[i] = s.intensity;
        }
        for (int i = count; i < MAX_DENTS; i++)
        {
            dentData[i] = Vector4.zero;
            dentIntensity[i] = 0f;
        }
    }

    void Release()
    {
        if (rtA != null) { rtA.Release(); rtA = null; }
        if (rtB != null) { rtB.Release(); rtB = null; }
        if (cmd != null) { cmd.Release(); cmd = null; }
        initialised = false;
    }
}
