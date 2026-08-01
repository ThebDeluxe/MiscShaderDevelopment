using UnityEngine;

/// <summary>
/// Draws the live dent map in the corner of the Game view, plus the numbers that
/// matter when diagnosing the stamp pass.
///
/// Note the map now holds a signed object-space vector in RGB, so the preview will
/// show colour rather than greyscale, and negative components clip to black.
/// That is expected - it is data, not a picture.
/// </summary>
public class DentDebugView : MonoBehaviour
{
    public DentManager dentManager;
    public int displaySize = 256;
    public bool showInfo = true;

    void OnGUI()
    {
        if (dentManager == null) return;

        var map = dentManager.CurrentDentMap;
        if (map == null) return;

        GUI.DrawTexture(new Rect(10, 10, displaySize, displaySize), map, ScaleMode.ScaleToFit, false);

        if (!showInfo) return;

        var gen = dentManager.targetRenderer != null
            ? dentManager.targetRenderer.GetComponent<DentVertexUVGenerator>()
            : null;

        string info =
            $"Dent map: {map.width}x{map.height} ({map.format})\n" +
            $"Verts: {(gen != null ? gen.VertexCount : 0)}\n" +
            $"UV channel: TEXCOORD{(gen != null ? gen.targetUVChannel : -1)}\n" +
            $"Active sources: {dentManager.ActiveDentCount}\n" +
            $"Flip Y: {(dentManager.overrideFlipY ? dentManager.flipYValue : SystemInfo.graphicsUVStartsAtTop)}" +
            $"{(dentManager.overrideFlipY ? " (override)" : " (auto)")}\n" +
            $"Debug Stamp All: {dentManager.debugStampAll}";

        GUI.Label(new Rect(10, 20 + displaySize, 340, 120), info);
    }
}
