using UnityEngine;

/// <summary>
/// Marks a lump of clay that can be absorbed into the player's assembly, and thrown back
/// out again.
///
/// Deliberately thin: the blob already carries its own DentManager, DentContactSource and
/// dent texture, because the system was made to handle several deforming objects at once.
/// Merging is therefore about physics ownership and telling the blobs about each other,
/// not about building anything new.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
public class ClayBlob : MonoBehaviour
{
    [Header("Colliders")]
    [Tooltip("Physical collision radius. Smaller than the visible mesh, so surfaces can sink " +
             "into it - which is what the dent effect flattens.")]
    public float innerRadius = 0.25f;

    [Tooltip("Radius of the visible mesh. Used as an interaction trigger and as the " +
             "detection sphere for contact-driven dents.")]
    public float outerRadius = 0.35f;

    [Tooltip("Build and maintain both sphere colliders, matching how the character does it. " +
             "Turn off to author them by hand.")]
    public bool manageColliders = true;

    [Tooltip("Physics material for the inner collider.")]
    public PhysicsMaterial innerMaterial;

    [Header("Merging")]
    [Tooltip("Give up this blob's colliders once absorbed, so the assembly collides as one " +
             "growing sphere instead.\n\n" +
             "Smoother, but it throws away the lumpy silhouette - the assembly stops " +
             "catching on things the way its shape suggests. Off by default: keeping the " +
             "colliders keeps both the lumps and, importantly, the friction that rolling " +
             "depends on.")]
    public bool disableCollidersWhenMerged = false;

    [Tooltip("How much of its physical size a blob keeps once absorbed.\n\n" +
             "Below 1 the collider shrinks while the mesh stays put, so the lump still " +
             "looks lumpy but rolls over its bumps more gently. The gap also gives the dent " +
             "effect more to flatten, so blobs squash harder against the ground than they " +
             "did loose.")]
    [Range(0.2f, 1f)] public float mergedColliderScale = 0.7f;

    [Tooltip("Deformation driver on this blob. Found automatically if left empty.")]
    public DentContactSource contactSource;

    /// <summary>Radius of the visible mesh, which is what other blobs interface against.</summary>
    public float visualRadius => outerRadius;

    public bool Merged { get; private set; }

    /// <summary>False for a moment after being thrown, so it is not instantly re-absorbed.</summary>
    public bool CanBePickedUp => !Merged && Time.time >= pickupLockUntil;

    public SphereCollider InnerCollider { get; private set; }
    public SphereCollider OuterCollider { get; private set; }

    Rigidbody ownBody;
    float originalMass = 1f;
    float pickupLockUntil;

    void Awake()
    {
        if (contactSource == null) contactSource = GetComponentInChildren<DentContactSource>();

        ownBody = GetComponent<Rigidbody>();
        if (ownBody != null) originalMass = ownBody.mass;

        if (manageColliders) SetUpColliders();

        if (contactSource != null) contactSource.visualRadius = outerRadius;
    }

    /// <summary>
    /// Builds the two spheres, same split as the character: a smaller physical one that
    /// collides, and a trigger at the visible size for interaction and dent detection.
    /// </summary>
    void SetUpColliders()
    {
        // The RequireComponent collider becomes the inner one, so nothing is orphaned.
        InnerCollider = GetComponent<SphereCollider>();
        InnerCollider.radius = innerRadius;
        InnerCollider.isTrigger = false;
        if (innerMaterial != null) InnerCollider.sharedMaterial = innerMaterial;

        Transform existing = transform.Find("Collider (Outer)");

        if (existing == null)
        {
            var go = new GameObject("Collider (Outer)");
            go.transform.SetParent(transform, false);
            existing = go.transform;
        }

        if (!existing.TryGetComponent(out SphereCollider outer))
            outer = existing.gameObject.AddComponent<SphereCollider>();

        OuterCollider = outer;
        OuterCollider.radius = outerRadius;
        OuterCollider.isTrigger = true;

        if (outerRadius < innerRadius)
            Debug.LogWarning($"{name}: Outer Radius is smaller than Inner Radius, so nothing " +
                             "can sink into this blob.", this);
    }

    void OnValidate()
    {
        innerRadius = Mathf.Max(0.01f, innerRadius);
        outerRadius = Mathf.Max(innerRadius, outerRadius);

        if (InnerCollider != null) InnerCollider.radius = innerRadius;
        if (OuterCollider != null) OuterCollider.radius = outerRadius;
    }

    /// <summary>
    /// Hands this blob over to an assembly.
    ///
    /// Reparenting is most of the work: a collider belongs to its nearest Rigidbody
    /// ancestor, so once the blob sits under the player's body its colliders become part
    /// of that body, stop colliding with their new siblings, and are folded into the
    /// combined mass and inertia automatically.
    /// </summary>
    public void AttachTo(Transform parent)
    {
        if (Merged) return;
        Merged = true;

        if (ownBody != null)
        {
            Destroy(ownBody);
            ownBody = null;
        }

        // Colliders stay SOLID by default. A trigger would remove friction and support at
        // exactly the moment the blob is touching down, so the assembly loses its grip
        // wherever a blob carries the contact - softness has to come from how the solver
        // resolves a real collision, not from removing it.
        if (disableCollidersWhenMerged)
        {
            if (InnerCollider != null) InnerCollider.enabled = false;
            if (OuterCollider != null) OuterCollider.enabled = false;
        }
        else if (InnerCollider != null)
        {
            // Shrinking the collider while the mesh stays put keeps the lumpy look while
            // easing how hard the lump is to roll over.
            InnerCollider.radius = innerRadius * mergedColliderScale;
        }

        // Keep the world pose so the blob does not jump on pickup.
        transform.SetParent(parent, true);
    }

    /// <summary>
    /// Cuts the blob loose and gives it its own body back.
    ///
    /// The pickup lock matters: without it the merger would find the blob still overlapping
    /// on the very next frame and swallow it again before it had travelled anywhere.
    /// </summary>
    public Rigidbody Detach(float pickupLockSeconds)
    {
        Merged = false;
        transform.SetParent(null, true);

        // Its own object again, so it collides for itself at full size.
        if (InnerCollider != null)
        {
            InnerCollider.enabled = true;
            InnerCollider.isTrigger = false;
            InnerCollider.radius = innerRadius;
        }

        if (OuterCollider != null) OuterCollider.enabled = true;

        ownBody = gameObject.AddComponent<Rigidbody>();
        ownBody.mass = originalMass;
        ownBody.interpolation = RigidbodyInterpolation.Interpolate;

        // Thrown fast enough to tunnel through thin geometry otherwise.
        ownBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        pickupLockUntil = Time.time + pickupLockSeconds;

        return ownBody;
    }
}
