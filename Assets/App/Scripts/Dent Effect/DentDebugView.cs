using System.Reflection;
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
            $"Debug Stamp All: {dentManager.debugStampAll}";

        GUI.Label(new Rect(10, 20 + drawHeight, 380, 160), info);
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

        return $"{gen.MeshLodCount} levels, {active}   [ ] to step{lost}";
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
    static bool forcedLodResolved;

    /// <summary>Whether this Unity version exposes a way to force a Mesh LOD level.</summary>
    static bool forcedLodAvailable => forcedLodProperty != null;

    static int ReadForcedLod(Renderer renderer)
    {
        if (renderer == null) return -1;

        if (!forcedLodResolved)
        {
            forcedLodResolved = true;

            // The property has moved around between versions, so try the type it is
            // documented on as well as the base. Reporting failure beats failing silently:
            // a swallowed miss looks identical to a working API that does nothing.
            forcedLodProperty =
                renderer.GetType().GetProperty("forceMeshLod",
                                               BindingFlags.Public | BindingFlags.Instance)
                ?? typeof(Renderer).GetProperty("forceMeshLod",
                                                BindingFlags.Public | BindingFlags.Instance);

            if (forcedLodProperty == null)
                Debug.LogWarning("DentDebugView: no 'forceMeshLod' property found on " +
                                 $"{renderer.GetType().Name}. Mesh LOD forcing is unavailable; " +
                                 "use the Mesh Renderer inspector's Mesh LOD selection bar instead.");
        }

        if (forcedLodProperty == null) return -1;

        try { return (int)forcedLodProperty.GetValue(renderer); }
        catch { return -1; }
    }

    static void WriteForcedLod(Renderer renderer, int level)
    {
        if (renderer == null) return;

        ReadForcedLod(renderer);   // resolves the property and warns once if missing

        if (forcedLodProperty == null || !forcedLodProperty.CanWrite) return;

        try { forcedLodProperty.SetValue(renderer, level); }
        catch (System.Exception e)
        {
            Debug.LogWarning($"DentDebugView: could not set forceMeshLod - {e.Message}");
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

    void OnDisable()
    {
        if (material == null) return;

        if (Application.isPlaying) Destroy(material); else DestroyImmediate(material);
        material = null;
    }
}
