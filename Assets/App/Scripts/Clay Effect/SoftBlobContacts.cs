using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Adds a cushioning push where an assembly overlaps a surface more than it should.
///
/// A supplement to real collision, NOT a replacement for it. Absorbed blobs keep solid
/// colliders, because a trigger has no friction and no support - the assembly would lose
/// its grip wherever a blob carried the ground contact, which reads as a slippery balloon.
/// The main softness comes from Rigidbody.maxDepenetrationVelocity on the controller; this
/// only tops it up.
///
/// The overlaps come from each blob's DentContactSource, which already measures exactly
/// this for the dent effect. Nothing new is queried.
/// </summary>
[DefaultExecutionOrder(-30)]
public class SoftBlobContacts : MonoBehaviour
{
    [Header("Assembly")]
    [Tooltip("Rigidbody the spring pushes. Falls back to this object's.")]
    public Rigidbody body;

    [Tooltip("Supplies the absorbed blobs. Falls back to this object's.")]
    public BlobMerger merger;

    [Tooltip("Also spring the character's own body out of surfaces, not just the blobs.\n\n" +
             "Off by default: the character's collider already stops it properly, so this " +
             "only adds cushioning for impacts that push deeper than the mesh normally " +
             "sinks.")]
    public bool includeOwnBody = false;

    [Tooltip("The character's own deformation driver. Falls back to the merger's.")]
    public DentContactSource ownContactSource;

    [Header("Spring")]
    [Tooltip("Push per metre of EXCESS overlap, as acceleration.\n\n" +
             "A supplement to the solver, so keep it modest - the colliders are still doing " +
             "the real work. Too high and it fights them and jitters.")]
    public float stiffness = 40f;

    [Tooltip("Resists the assembly moving further INTO a surface, which is what stops the " +
             "spring bouncing. Too low and it wobbles, too high and it feels sticky.")]
    public float damping = 12f;

    [Tooltip("Overlap that counts as fully compressed. Beyond this the push stops growing, " +
             "so a deep intersection cannot fire the assembly across the level.")]
    public float maxOverlap = 0.3f;

    [Tooltip("Overlaps shallower than this are ignored, so resting contact does not buzz.")]
    public float minOverlap = 0.01f;

    [Tooltip("Scales the character's own spring down, since its collider is still doing the " +
             "real work of stopping it.")]
    [Range(0f, 1f)] public float ownBodyScale = 0.35f;

    [Header("Debug")]
    public bool drawGizmos = true;

    const int MaxOverlapsPerBlob = 8;

    readonly DentContactSource.SurfaceOverlap[] overlapBuffer =
        new DentContactSource.SurfaceOverlap[MaxOverlapsPerBlob];

    readonly List<(Vector3 point, Vector3 push)> debugPushes = new List<(Vector3, Vector3)>(16);

    void Awake()
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (merger == null) merger = GetComponent<BlobMerger>();
        if (ownContactSource == null && merger != null) ownContactSource = merger.ownContactSource;
    }

    void FixedUpdate()
    {
        if (body == null || merger == null) return;

        debugPushes.Clear();

        if (includeOwnBody && ownContactSource != null && merger.controller != null)
        {
            // The character's mesh is MEANT to sink by the gap between its visible size and
            // its collider - that is what the dent effect flattens. Only overlap beyond
            // that is a real intrusion.
            float restOverlap = ownContactSource.visualRadius - merger.innerRadius;
            PushOutOf(ownContactSource, restOverlap, ownBodyScale);
        }

        var blobs = merger.MergedBlobs;

        for (int i = 0; i < blobs.Count; i++)
        {
            ClayBlob blob = blobs[i];
            if (blob == null || blob.contactSource == null) continue;
            if (blob.disableCollidersWhenMerged) continue;   // nothing of it left to cushion

            // A merged blob's collider is shrunk, so its mesh sits deeper than its physical
            // size - that gap is its rest depth, and only overlap beyond it is an intrusion.
            float restOverlap = blob.outerRadius - blob.innerRadius * blob.mergedColliderScale;
            PushOutOf(blob.contactSource, restOverlap, 1f);
        }
    }

    /// <summary>
    /// Springs the assembly out of whatever this source is overlapping, beyond the depth it
    /// is supposed to sit at.
    ///
    /// That subtraction is the whole trick. The dent system reports how far the VISIBLE mesh
    /// overlaps a surface, and it is built to overlap - the mesh is deliberately larger than
    /// the collider, and the difference is the sink the dent flattens. Treating that as
    /// penetration to escape means pushing constantly against the ground, which floats the
    /// character like a balloon and shoves it sideways off anything nearby.
    ///
    /// Sibling overlaps are already filtered out by the source: they share our Rigidbody,
    /// so pushing away from one is pushing away from ourselves.
    /// </summary>
    void PushOutOf(DentContactSource source, float restOverlap, float scale)
    {
        int count = source.GetSurfaceOverlaps(overlapBuffer);

        for (int i = 0; i < count; i++)
        {
            var overlap = overlapBuffer[i];

            float excess = overlap.Depth - Mathf.Max(restOverlap, 0f);
            if (excess < minOverlap) continue;

            // Capped, so a deep intersection - a blob spawned inside geometry, say - pushes
            // out firmly rather than launching the whole assembly.
            float depth = Mathf.Min(excess, maxOverlap);

            // Damping reads the velocity AT THE CONTACT, not the body centre: on a rolling
            // assembly those differ a lot, and using the centre would damp the wrong motion.
            Vector3 pointVelocity = body.GetPointVelocity(overlap.Point);
            float closing = Vector3.Dot(pointVelocity, overlap.Normal);

            float magnitude = (depth * stiffness - closing * damping) * scale;
            if (magnitude <= 0f) continue;   // already separating, leave it alone

            // Faded by the contact's own weight, so a surface easing in or out of detection
            // does not snap the force on and off.
            Vector3 push = overlap.Normal * (magnitude * overlap.Weight);

            // Applied at the contact rather than the centre, so pushing off a wall to one
            // side also imparts the spin it should.
            body.AddForceAtPosition(push, overlap.Point, ForceMode.Acceleration);

            if (drawGizmos) debugPushes.Add((overlap.Point, push));
        }
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Gizmos.color = new Color(0.4f, 1f, 0.6f, 0.9f);

        for (int i = 0; i < debugPushes.Count; i++)
        {
            var (point, push) = debugPushes[i];

            Gizmos.DrawSphere(point, 0.03f);
            Gizmos.DrawLine(point, point + push.normalized * Mathf.Min(push.magnitude * 0.01f, 0.5f));
        }
    }
}
