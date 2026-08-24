using UnityEngine;

/// <summary>
/// Samples the ground beneath a character into a small height map, for the dent shader to
/// press against.
///
/// WHY THIS EXISTS ALONGSIDE THE CONTACT PATH
/// Terrain genuinely IS a heightfield, so sampling it as one is exact. Describing the same
/// ground with a handful of plane contacts is not: each plane is a flat answer to a curved
/// question, adjacent ones disagree where they meet, and which of them exist changes as the
/// character moves - so the seams travel, which is what makes rolling over terrain look
/// messy.
///
/// A grid fixed relative to the character removes that. The samples sit in the same places
/// every frame, so nothing jumps, and the result is one continuous surface rather than
/// several stamps competing for the same vertices.
///
/// It cannot represent a wall, an overhang or a ceiling - a height map is a function of x
/// and z only. Those keep the ordinary contact path, which is why both run together.
/// </summary>
[DefaultExecutionOrder(-25)]
public class ClayHeightFieldSampler : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Manager whose stamp pass receives the field. Found on this object or its " +
             "parents if empty.")]
    public DentManager dentManager;

    [Tooltip("What the character's footprint is centred on. Falls back to this transform.")]
    public Transform followTarget;

    [Header("Sampling")]
    [Tooltip("Which layers count as ground. Terrain especially - anything vertical will not " +
             "be represented properly here, so leave walls to the contact path.")]
    public LayerMask groundMask = ~0;

    [Tooltip("Side length of the sampled area, in world units. Should comfortably cover the " +
             "character's footprint plus a margin for its bulge.")]
    public float areaSize = 3f;

    [Tooltip("Samples per side. Detail finer than the spacing between them is smoothed away, " +
             "which is usually right for terrain and wrong for a sharp rock.")]
    [Range(4, 32)] public int resolution = 12;

    [Tooltip("How far above and below the character the rays search.")]
    public float probeHeight = 4f;

    [Header("Press")]
    [Tooltip("How far a vertex below the ground is pushed back up. 1 puts it exactly on the " +
             "surface.")]
    [Range(0f, 1f)] public float pressStrength = 1f;

    [Tooltip("How far the press follows the surface NORMAL rather than straight up.\n\n" +
             "1 presses perpendicular to the slope, which is what a contact should do. 0 " +
             "pushes straight up, which is what a height map literally describes - ground " +
             "holding a vertex at its own height, with no sideways component.\n\n" +
             "Either way the distance is the perpendicular one, so the mesh lands on the " +
             "surface rather than overshooting and sliding down it.")]
    [Range(0f, 1f)] public float normalPress = 1f;

    [Tooltip("Deepest a vertex can be pressed, in world units.\n\n" +
             "A height map has no natural limit the way a stamp's depth does, so without " +
             "this a vertex well below the ground is pushed the whole way - and a rolling " +
             "character presses everything that passes through contact to its full extent, " +
             "which reads as the shape flattening out as it moves.\n\n" +
             "Match it to what the contact path uses on flat ground, which is about 1.5x the " +
             "gap between the mesh and its collider. Set it BELOW the actual sink and most of " +
             "the contact patch presses to the same depth, which reads as a punched hole " +
             "rather than a press.")]
    public float maxPressDepth = 0.3f;

    [Header("Bulge")]
    [Tooltip("How far displaced material piles up beside the contact. 0 disables it.")]
    public float bulgeAmount = 0.12f;

    [Tooltip("How far above the ground the bulge reaches.")]
    public float bulgeReach = 0.35f;

    [Header("Matching The Contact Path")]
    [Tooltip("Fade rate for terrain dents, as a multiplier on the manager's own.\n\n" +
             "Match DentContactSource's Decay Multiplier, or terrain dents will linger for a " +
             "different length of time than every other surface - which reads as terrain " +
             "decaying slowly rather than as a setting being out of step.")]
    [Range(0f, 5f)] public float decayMultiplier = 2f;

    [Tooltip("Scale retained along the press, matching the stamps' Flatten Scale. Above 0 " +
             "softens the press so terrain does not dent harder than a plane for the same " +
             "contact.")]
    [Range(0f, 1f)] public float flattenScale = 0.15f;

    [Tooltip("Only sample while the character is near ground of this type. Off means the " +
             "grid is rebuilt every frame regardless.")]
    public bool skipWhenAirborne = true;

    [Header("Debug")]
    public bool drawGizmos = false;

    static readonly int HeightFieldID = Shader.PropertyToID("_HeightField");
    static readonly int AreaID = Shader.PropertyToID("_HeightFieldArea");
    static readonly int ParamsID = Shader.PropertyToID("_HeightFieldParams");
    static readonly int NormalPressID = Shader.PropertyToID("_HeightFieldNormalPress");
    static readonly int CentreID = Shader.PropertyToID("_HeightFieldCentre");
    static readonly int BulgeID = Shader.PropertyToID("_HeightFieldBulge");

    Texture2D field;
    float[] heights;
    Color[] pixels;

    float minHeight, heightRange;
    Vector2 areaMin;
    bool hasGround;

    void Start()
    {
        if (dentManager == null) dentManager = GetComponentInParent<DentManager>();
        if (followTarget == null) followTarget = transform;

        if (dentManager == null)
        {
            Debug.LogError($"{name}: ClayHeightFieldSampler needs a DentManager.", this);
            enabled = false;
            return;
        }

        Allocate();
    }

    void Allocate()
    {
        int size = Mathf.Max(resolution, 4);

        // R-only and unfiltered on the CPU side; the shader samples it bilinearly, which is
        // what makes the surface smooth between samples rather than stepped.
        field = new Texture2D(size, size, TextureFormat.RFloat, false, true)
        {
            name = $"HeightField_{name}",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        heights = new float[size * size];
        pixels = new Color[size * size];
    }

    void LateUpdate()
    {
        if (field == null || dentManager.StampInstance == null) return;

        if (heights.Length != resolution * resolution) Allocate();

        Sample();
        Upload();
    }

    void Sample()
    {
        int size = resolution;
        float half = areaSize * 0.5f;

        Vector3 centre = followTarget.position;
        areaMin = new Vector2(centre.x - half, centre.z - half);

        float step = areaSize / (size - 1);

        minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        hasGround = false;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                var origin = new Vector3(areaMin.x + x * step,
                                         centre.y + probeHeight,
                                         areaMin.y + z * step);

                float h;

                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, probeHeight * 2f,
                                    groundMask, QueryTriggerInteraction.Ignore))
                {
                    h = hit.point.y;
                    hasGround = true;
                }
                else
                {
                    // No ground here - a hole, or past the edge. Dropped well below so the
                    // field never presses upward where there is nothing.
                    h = centre.y - probeHeight;
                }

                heights[z * size + x] = h;

                if (h < minHeight) minHeight = h;
                if (h > maxHeight) maxHeight = h;
            }
        }

        heightRange = Mathf.Max(maxHeight - minHeight, 0.001f);
    }

    void Upload()
    {
        var stamp = dentManager.StampInstance;

        if (!hasGround && skipWhenAirborne)
        {
            // Nothing underneath, so the field is switched off rather than uploaded - the
            // shader then leaves every vertex to the ordinary contact path.
            stamp.SetVector(ParamsID, new Vector4(0f, 1f, 0f, 0f));
            dentManager.externalPressActive = false;
            return;
        }

        // The manager has no DentSource to count for this, so without telling it directly
        // it would idle out and stop stamping while the ground is still pressing - which
        // freezes the last dent onto the mesh.
        dentManager.externalPressActive = true;

        for (int i = 0; i < heights.Length; i++)
        {
            // Normalised into 0..1 against the sampled range, so precision is spent on the
            // heights actually present rather than on the world's whole vertical extent.
            float t = (heights[i] - minHeight) / heightRange;
            pixels[i] = new Color(t, 0f, 0f, 1f);
        }

        field.SetPixels(pixels);
        field.Apply(false, false);

        stamp.SetTexture(HeightFieldID, field);
        stamp.SetVector(AreaID, new Vector4(areaMin.x, areaMin.y, areaSize, areaSize));
        stamp.SetVector(ParamsID, new Vector4(minHeight, heightRange, pressStrength, 1f));
        stamp.SetFloat(NormalPressID, Mathf.Clamp01(normalPress));

        // The bulge pushes outward from the character's own axis, since the ground the
        // material was pressed out of is directly beneath it.
        Vector3 centre = followTarget.position;
        stamp.SetVector(CentreID, new Vector4(centre.x, centre.y, centre.z,
                                              Mathf.Max(maxPressDepth, 0.001f)));
        stamp.SetVector(BulgeID, new Vector4(bulgeAmount, Mathf.Max(bulgeReach, 0.001f),
                                            Mathf.Max(decayMultiplier, 0f),
                                            Mathf.Clamp01(flattenScale)));
    }

    void OnDisable()
    {
        if (dentManager == null) return;

        dentManager.externalPressActive = false;

        if (dentManager.StampInstance != null)
            dentManager.StampInstance.SetVector(ParamsID, new Vector4(0f, 1f, 0f, 0f));
    }

    void OnDestroy()
    {
        if (field == null) return;

        if (Application.isPlaying) Destroy(field); else DestroyImmediate(field);
        field = null;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || heights == null || !Application.isPlaying) return;

        int size = resolution;
        float step = areaSize / (size - 1);

        Gizmos.color = new Color(0.4f, 1f, 0.5f, 0.8f);

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                var point = new Vector3(areaMin.x + x * step,
                                        heights[z * size + x],
                                        areaMin.y + z * step);

                Gizmos.DrawSphere(point, step * 0.12f);
            }
        }
    }
}
