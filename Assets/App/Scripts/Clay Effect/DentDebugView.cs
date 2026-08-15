using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Draws the live dent map in the corner of the Game view, plus the numbers that matter
/// when diagnosing the stamp pass.
///
/// The map holds a signed object-space vector in RGB and the per-texel decay multiplier in
/// A, so the combined view is colour rather than greyscale and negative components clip to
/// black. Press 1-5 to isolate a channel when that gets hard to read.
/// </summary>
public class DentDebugView : MonoBehaviour
{
    public enum Channel { All = 0, Red = 1, Green = 2, Blue = 3, Alpha = 4 }

    public DentManager dentManager;

    [Tooltip("Optional. Shows the screen coverage and quality driving the throttle.")]
    public DentLOD dentLOD;

    [Tooltip("Longest edge of the preview, in pixels. The other edge follows the texture's " +
             "own aspect, which changes with vertex count.")]
    public int displaySize = 256;

    public bool showInfo = true;

    [Tooltip("Which channel is displayed. 1 = all, 2 = R, 3 = G, 4 = B, 5 = A.")]
    public Channel channel = Channel.All;

    [Tooltip("Allow the number keys to switch channels at runtime.")]
    public bool keyboardShortcuts = true;

    [Tooltip("Show magnitude rather than raw value. The map stores a signed displacement " +
             "vector, so without this half the data sits below zero and clips to black.")]
    public bool showAbsolute = true;

    [Tooltip("Show per-phase timings read from the profiler markers, in the build itself.\n\n" +
             "Needs a Development Build - markers compile out of release builds. Useful " +
             "where attaching the profiler is awkward, WebGL especially.")]
    public bool showTimings = true;

    static readonly string[] TimedMarkers =
    {
        "Dent.CollectSources",
        "Dent.CacheSamples",
        "Dent.PressDepths",
        "Dent.BuildDentArrays",
        "Dent.BuildIslands",
        "Dent.UploadAndDraw",
        "DentContact.Probe",
        "DentContact.Track",
        "DentContact.Apply"
    };

    ProfilerRecorder[] recorders;
    readonly StringBuilder timingText = new StringBuilder(256);

    static readonly int MainTexID = Shader.PropertyToID("_MainTex");
    static readonly int ChannelID = Shader.PropertyToID("_Channel");
    static readonly int AbsoluteID = Shader.PropertyToID("_Absolute");

    Material material;

    void Update()
    {
        if (!keyboardShortcuts) return;

        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (keyboard.digit1Key.wasPressedThisFrame) channel = Channel.All;
        else if (keyboard.digit2Key.wasPressedThisFrame) channel = Channel.Red;
        else if (keyboard.digit3Key.wasPressedThisFrame) channel = Channel.Green;
        else if (keyboard.digit4Key.wasPressedThisFrame) channel = Channel.Blue;
        else if (keyboard.digit5Key.wasPressedThisFrame) channel = Channel.Alpha;

        // Unity cannot report which Mesh LOD is being drawn, so the way to see them is to
        // force one and look. -1 hands control back to automatic selection.
        if (keyboard.leftBracketKey.wasPressedThisFrame) StepForcedLod(-1);
        else if (keyboard.rightBracketKey.wasPressedThisFrame) StepForcedLod(1);

        // Selection bias nudges automatic selection toward more or less detail, which is
        // the better way to check the levels actually switch under normal conditions.
        if (keyboard.commaKey.wasPressedThisFrame) StepBias(-0.25f);
        else if (keyboard.periodKey.wasPressedThisFrame) StepBias(0.25f);
    }

    void StepBias(float delta)
    {
        var renderer = dentManager != null ? dentManager.targetRenderer : null;
        if (renderer == null) return;

        WriteSelectionBias(renderer, ReadSelectionBias(renderer) + delta);
    }

    void StepForcedLod(int direction)
    {
        if (dentManager == null || dentManager.targetRenderer == null) return;

        var gen = dentManager.targetRenderer.GetComponent<DentVertexUVGenerator>();
        int levels = gen != null ? gen.MeshLodCount : 1;
        if (levels <= 1) return;

        int current = ReadForcedLod(dentManager.targetRenderer);

        // -1 (auto) sits below level 0, so stepping walks auto, 0, 1, ... and wraps.
        int next = current + direction;
        if (next < -1) next = levels - 1;
        if (next >= levels) next = -1;

        WriteForcedLod(dentManager.targetRenderer, next);
    }

    void OnGUI()
    {
        if (dentManager == null) return;

        var map = dentManager.CurrentDentMap;
        if (map == null) return;

        // Fit the texture's own aspect inside a displaySize box. The map is sized to the
        // vertex count, so it is rarely square and never a fixed shape - drawing it into a
        // hardcoded square stretches it and makes the layout impossible to read.
        float aspect = map.height > 0 ? map.width / (float)map.height : 1f;

        float drawWidth = aspect >= 1f ? displaySize : displaySize * aspect;
        float drawHeight = aspect >= 1f ? displaySize / aspect : displaySize;

        var rect = new Rect(10, 10, drawWidth, drawHeight);

        // Graphics.DrawTexture is the only way to put a material behind an IMGUI blit, and
        // it is only valid during Repaint.
        if (Event.current.type == EventType.Repaint)
        {
            if (EnsureMaterial())
            {
                material.SetTexture(MainTexID, map);
                material.SetFloat(ChannelID, (float)channel);
                material.SetFloat(AbsoluteID, showAbsolute ? 1f : 0f);
                Graphics.DrawTexture(rect, map, material);
            }
            else
            {
                // Shader missing: still show something rather than nothing.
                GUI.DrawTexture(rect, map, ScaleMode.ScaleToFit, false);
            }
        }

        if (!showInfo) return;

        var gen = dentManager.targetRenderer != null
            ? dentManager.targetRenderer.GetComponent<DentVertexUVGenerator>()
            : null;

        string info =
            $"Dent map: {map.width}x{map.height} ({map.format})\n" +
            $"Channel: {ChannelLabel()}   [1-5]{(showAbsolute ? "  abs" : "")}\n" +
            $"Verts: {(gen != null ? gen.VertexCount : 0)}" +
            $"{(gen != null ? $"  ({gen.VertexCount / (float)(map.width * map.height):P0} of texels used)" : "")}\n" +
            $"Mesh LOD: {LodLabel(gen)}\n" +
            $"{LodDetailLabel()}" +
            $"UV channel: TEXCOORD{(gen != null ? gen.targetUVChannel : -1)}\n" +
            $"Active sources: {dentManager.ActiveDentCount}\n" +
            $"Flip Y: {(dentManager.overrideFlipY ? dentManager.flipYValue : SystemInfo.graphicsUVStartsAtTop)}" +
            $"{(dentManager.overrideFlipY ? " (override)" : " (auto)")}\n" +
            $"Debug Stamp All: {dentManager.debugStampAll}" +
            (showTimings ? TimingText() : string.Empty);

        GUI.Label(new Rect(10, 20 + drawHeight, 380, 340), info);
    }

    /// <summary>
    /// Mesh LOD state.
    ///
    /// Unity gives no way to query which Mesh LOD index is currently being rendered, so
    /// this reports the level count and any forced override rather than pretending to know
    /// the active one. The dent mapping is LOD invariant regardless - every level shares
    /// one vertex buffer, so a vertex reads the same texel at any level.
    /// </summary>
    string LodLabel(DentVertexUVGenerator gen)
    {
        if (gen == null) return "unknown";
        if (gen.MeshLodCount <= 1) return "none (single level)";

        int forced = ReadForcedLod(dentManager.targetRenderer);

        if (!forcedLodAvailable)
            return $"{gen.MeshLodCount} levels - forcing unavailable (no 'forceMeshLod' on Renderer). " +
                   "Use the Mesh Renderer inspector's LOD bar.";

        string active = forced >= 0 ? $"FORCED to {forced}" : "auto";
        string lost = gen.InstanceMeshLodCount < gen.MeshLodCount
            ? $"  WARNING: instance kept only {gen.InstanceMeshLodCount}"
            : string.Empty;

        float bias = ReadSelectionBias(dentManager.targetRenderer);

        return $"{gen.MeshLodCount} levels, {active}   [ ] step,  , . bias {bias:0.00}{lost}";
    }

    /// <summary>
    /// Screen coverage and the quality it produces.
    ///
    /// Unity exposes no way to read the active Mesh LOD, so coverage is shown instead - it
    /// is the quantity LOD selection is based on, so it moves in step with the switches
    /// even though the level itself cannot be queried.
    /// </summary>
    string LodDetailLabel()
    {
        if (dentLOD == null) return string.Empty;

        return $"Coverage: {dentLOD.Coverage:P1}   Quality: {dentLOD.Quality:0.00}   " +
               $"{(dentLOD.Active ? $"every {dentLOD.UpdateInterval} frame(s)" : "DORMANT")}\n";
    }

    static PropertyInfo forcedLodProperty;
    static PropertyInfo selectionBiasProperty;
    static bool lodPropertiesResolved;

    /// <summary>Whether this Unity version exposes a way to force a Mesh LOD level.</summary>
    static bool forcedLodAvailable => forcedLodProperty != null;

    static void ResolveLodProperties(Renderer renderer)
    {
        if (lodPropertiesResolved) return;
        lodPropertiesResolved = true;

        forcedLodProperty = typeof(Renderer).GetProperty("forceMeshLod",
                                                         BindingFlags.Public | BindingFlags.Instance);
        selectionBiasProperty = typeof(Renderer).GetProperty("meshLodSelectionBias",
                                                             BindingFlags.Public | BindingFlags.Instance);

        if (forcedLodProperty == null)
            Debug.LogWarning("DentDebugView: no 'forceMeshLod' on Renderer. Mesh LOD forcing is " +
                             "unavailable; use the Mesh Renderer inspector's Mesh LOD section.");
    }

    /// <summary>
    /// Reads forceMeshLod. It is declared as Int16, so unboxing it straight to int throws -
    /// which, caught, looks exactly like the property not existing. Convert handles the
    /// widening without caring about the declared type.
    /// </summary>
    static int ReadForcedLod(Renderer renderer)
    {
        if (renderer == null) return -1;

        ResolveLodProperties(renderer);
        if (forcedLodProperty == null) return -1;

        try { return System.Convert.ToInt32(forcedLodProperty.GetValue(renderer)); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"DentDebugView: could not read forceMeshLod - {e.Message}");
            return -1;
        }
    }

    static void WriteForcedLod(Renderer renderer, int level)
    {
        if (renderer == null) return;

        ResolveLodProperties(renderer);
        if (forcedLodProperty == null || !forcedLodProperty.CanWrite) return;

        try
        {
            // Int16 on the native side, so narrow before setting.
            forcedLodProperty.SetValue(renderer, System.Convert.ToInt16(level));
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"DentDebugView: could not set forceMeshLod - {e.Message}");
        }
    }

    static float ReadSelectionBias(Renderer renderer)
    {
        if (renderer == null) return 0f;

        ResolveLodProperties(renderer);
        if (selectionBiasProperty == null) return 0f;

        try { return System.Convert.ToSingle(selectionBiasProperty.GetValue(renderer)); }
        catch { return 0f; }
    }

    static void WriteSelectionBias(Renderer renderer, float bias)
    {
        if (renderer == null) return;

        ResolveLodProperties(renderer);
        if (selectionBiasProperty == null || !selectionBiasProperty.CanWrite) return;

        try { selectionBiasProperty.SetValue(renderer, bias); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"DentDebugView: could not set meshLodSelectionBias - {e.Message}");
        }
    }

    string ChannelLabel() => channel switch
    {
        Channel.Red => "R (displacement X)",
        Channel.Green => "G (displacement Y)",
        Channel.Blue => "B (displacement Z)",
        Channel.Alpha => "A (decay multiplier)",
        _ => "RGB (all)"
    };

    bool EnsureMaterial()
    {
        if (material != null) return true;

        var shader = Shader.Find("Hidden/DentDebugChannel");
        if (shader == null) return false;

        material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        return true;
    }

    void OnEnable()
    {
        if (!showTimings) return;

        recorders = new ProfilerRecorder[TimedMarkers.Length];
        for (int i = 0; i < TimedMarkers.Length; i++)
            recorders[i] = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, TimedMarkers[i]);
    }

    void OnDisable()
    {
        if (recorders != null)
        {
            for (int i = 0; i < recorders.Length; i++)
                if (recorders[i].Valid) recorders[i].Dispose();

            recorders = null;
        }

        if (material == null) return;

        if (Application.isPlaying) Destroy(material); else DestroyImmediate(material);
        material = null;
    }

    /// <summary>
    /// Per-phase timings, read in the player rather than over a profiler connection.
    ///
    /// Marker values are nanoseconds. Only meaningful in a Development Build, since
    /// ProfilerMarker compiles out of release builds and the recorders go invalid.
    /// </summary>
    string TimingText()
    {
        if (recorders == null) return string.Empty;

        timingText.Clear();
        timingText.Append('\n');

        double total = 0;

        for (int i = 0; i < recorders.Length; i++)
        {
            if (!recorders[i].Valid) continue;

            double ms = recorders[i].LastValue * 1e-6;
            total += ms;

            timingText.Append(TimedMarkers[i]).Append(": ").Append(ms.ToString("0.000")).Append(" ms\n");
        }

        if (timingText.Length <= 1) return "\n(no marker data - needs a Development Build)\n";

        timingText.Append("TOTAL: ").Append(total.ToString("0.000")).Append(" ms\n");
        return timingText.ToString();
    }
}
