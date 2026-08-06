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
    }

    void OnGUI()
    {
        if (dentManager == null) return;

        var map = dentManager.CurrentDentMap;
        if (map == null) return;

        var rect = new Rect(10, 10, displaySize, displaySize);

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
            $"Verts: {(gen != null ? gen.VertexCount : 0)}\n" +
            $"UV channel: TEXCOORD{(gen != null ? gen.targetUVChannel : -1)}\n" +
            $"Active sources: {dentManager.ActiveDentCount}\n" +
            $"Flip Y: {(dentManager.overrideFlipY ? dentManager.flipYValue : SystemInfo.graphicsUVStartsAtTop)}" +
            $"{(dentManager.overrideFlipY ? " (override)" : " (auto)")}\n" +
            $"Debug Stamp All: {dentManager.debugStampAll}";

        GUI.Label(new Rect(10, 20 + displaySize, 340, 140), info);
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
