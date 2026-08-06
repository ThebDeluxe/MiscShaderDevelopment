using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Absorbs nearby clay blobs into the player, and keeps the resulting assembly coherent.
///
/// Three jobs, none of which need new deformation machinery:
///
///  - Ownership. Reparenting a blob under the rolling object makes its colliders part of
///    the player's Rigidbody, so they stop colliding with each other and fold into the
///    combined mass automatically.
///
///  - Pivot. A two-blob assembly does not rotate about the original character's centre, so
///    the rolling pivot and the Rigidbody's centre of mass are both moved to the combined
///    centre. Children are restored to their world positions afterwards, otherwise moving
///    the pivot would drag everything with it.
///
///  - Interfaces. Two clay balls pressed together share a FLAT disc, not a spherical dent.
///    Each blob is told about its siblings so their contact sources emit a Plane at the
///    radical plane between the pair rather than the curved surface they would otherwise
///    see.
/// </summary>
[DefaultExecutionOrder(-40)]
public class BlobMerger : MonoBehaviour
{
    [Header("Assembly")]
    [Tooltip("Rigidbody the assembly belongs to. Falls back to this object's.")]
    public Rigidbody body;

    [Tooltip("Object blobs are parented to, so they roll with the assembly.")]
    public Transform rollingObject;

    [Tooltip("The player's own deformation driver, so it learns about its new siblings.")]
    public DentContactSource ownContactSource;

    [Tooltip("Controller told about the assembly's effective rolling radius.")]
    public BallCharacterController controller;

    [Header("Pickup")]
    [Tooltip("Layers searched for blobs.")]
    public LayerMask blobMask = ~0;

    [Tooltip("Extra slack on the touch test, so a blob is absorbed on contact rather than " +
             "needing to overlap.")]
    public float pickupPadding = 0.05f;

    [Header("Debug")]
    public bool drawGizmos = true;

    readonly List<ClayBlob> merged = new List<ClayBlob>();
    readonly Collider[] overlaps = new Collider[16];
    readonly List<DentContactSource> siblingBuffer = new List<DentContactSource>();

    /// <summary>Blobs currently part of the assembly.</summary>
    public int MergedCount => merged.Count;

    /// <summary>Radius of a sphere with the assembly's combined volume. Drives rolling.</summary>
    public float EffectiveRadius { get; private set; }

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (ownContactSource == null) ownContactSource = GetComponentInChildren<DentContactSource>();
        if (controller == null) controller = GetComponent<BallCharacterController>();

        EffectiveRadius = ownContactSource != null ? ownContactSource.visualRadius : 0.5f;
    }

    void FixedUpdate()
    {
        if (ownContactSource == null || rollingObject == null) return;

        LookForBlobs();
    }

    void LookForBlobs()
    {
        Vector3 centre = AssemblyCentre();
        float reach = EffectiveRadius + LargestBlobRadius() + pickupPadding;

        int count = Physics.OverlapSphereNonAlloc(centre, reach, overlaps, blobMask,
                                                  QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            if (overlaps[i] == null) continue;

            var blob = overlaps[i].GetComponentInParent<ClayBlob>();
            if (blob == null || blob.Merged) continue;

            // Touching is measured surface to surface, against whichever part of the
            // assembly is nearest rather than its centre.
            if (!IsTouching(blob)) continue;

            Absorb(blob);
        }
    }

    bool IsTouching(ClayBlob blob)
    {
        float nearest = Vector3.Distance(blob.transform.position, OwnCentre())
                        - blob.visualRadius - ownContactSource.visualRadius;

        for (int i = 0; i < merged.Count; i++)
        {
            float gap = Vector3.Distance(blob.transform.position, merged[i].transform.position)
                        - blob.visualRadius - merged[i].visualRadius;

            if (gap < nearest) nearest = gap;
        }

        return nearest <= pickupPadding;
    }

    void Absorb(ClayBlob blob)
    {
        blob.AttachTo(rollingObject);
        merged.Add(blob);

        RecentrePivot();
        RefreshSiblings();
        RefreshRollingRadius();
    }

    /// <summary>
    /// Moves the rolling pivot and the Rigidbody's centre of mass onto the assembly's
    /// combined centre, so it turns about its real balance point rather than the original
    /// character's origin.
    /// </summary>
    void RecentrePivot()
    {
        Vector3 comWorld = AssemblyCentre();

        // Children have to be pinned in world space across the move, or shifting the pivot
        // would carry the whole assembly with it.
        int childCount = rollingObject.childCount;
        var children = new Transform[childCount];
        var worldPoses = new (Vector3 pos, Quaternion rot)[childCount];

        for (int i = 0; i < childCount; i++)
        {
            children[i] = rollingObject.GetChild(i);
            worldPoses[i] = (children[i].position, children[i].rotation);
        }

        rollingObject.position = comWorld;

        for (int i = 0; i < childCount; i++)
            children[i].SetPositionAndRotation(worldPoses[i].pos, worldPoses[i].rot);

        if (body != null)
            body.centerOfMass = body.transform.InverseTransformPoint(comWorld);
    }

    void RefreshSiblings()
    {
        siblingBuffer.Clear();

        if (ownContactSource != null) siblingBuffer.Add(ownContactSource);

        for (int i = 0; i < merged.Count; i++)
            if (merged[i].contactSource != null) siblingBuffer.Add(merged[i].contactSource);

        // Everyone gets everyone else, so each pair produces a flat interface from both
        // sides rather than one blob denting the other spherically.
        for (int i = 0; i < siblingBuffer.Count; i++)
            siblingBuffer[i].SetSiblings(siblingBuffer, siblingBuffer[i]);
    }

    /// <summary>
    /// Effective radius of the assembly, from combined volume rather than bounds - a wide
    /// assembly should roll as though it were the size its mass suggests, not its extent.
    /// </summary>
    void RefreshRollingRadius()
    {
        float volume = Cube(ownContactSource != null ? ownContactSource.visualRadius : 0.5f);

        for (int i = 0; i < merged.Count; i++)
            volume += Cube(merged[i].visualRadius);

        EffectiveRadius = Mathf.Pow(volume, 1f / 3f);

        if (controller != null) controller.RollingRadius = EffectiveRadius;
    }

    static float Cube(float v) => v * v * v;

    Vector3 OwnCentre() =>
        ownContactSource != null
            ? ownContactSource.transform.TransformPoint(ownContactSource.centreOffset)
            : transform.position;

    Vector3 AssemblyCentre()
    {
        // Volume weighted, so a big blob pulls the pivot more than a small one.
        float ownWeight = Cube(ownContactSource != null ? ownContactSource.visualRadius : 0.5f);

        Vector3 sum = OwnCentre() * ownWeight;
        float total = ownWeight;

        for (int i = 0; i < merged.Count; i++)
        {
            float weight = Cube(merged[i].visualRadius);
            sum += merged[i].transform.position * weight;
            total += weight;
        }

        return total > 0f ? sum / total : transform.position;
    }

    float LargestBlobRadius()
    {
        float largest = 0.5f;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].visualRadius > largest) largest = merged[i].visualRadius;

        return largest;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos || !Application.isPlaying) return;

        Gizmos.color = new Color(1f, 0.5f, 0.9f, 0.9f);
        Gizmos.DrawWireSphere(AssemblyCentre(), 0.08f);

        Gizmos.color = new Color(1f, 0.5f, 0.9f, 0.3f);
        Gizmos.DrawWireSphere(AssemblyCentre(), EffectiveRadius);
    }
}
