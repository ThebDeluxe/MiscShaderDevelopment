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
    [Tooltip("Radius of the visible mesh. Should match the blob's DentContactSource.")]
    public float visualRadius = 0.5f;

    [Tooltip("Deformation driver on this blob. Found automatically if left empty.")]
    public DentContactSource contactSource;

    public bool Merged { get; private set; }

    /// <summary>False for a moment after being thrown, so it is not instantly re-absorbed.</summary>
    public bool CanBePickedUp => !Merged && Time.time >= pickupLockUntil;

    Rigidbody ownBody;
    float originalMass = 1f;
    float pickupLockUntil;

    void Awake()
    {
        if (contactSource == null) contactSource = GetComponentInChildren<DentContactSource>();

        ownBody = GetComponent<Rigidbody>();
        if (ownBody != null) originalMass = ownBody.mass;

        if (contactSource != null) visualRadius = contactSource.visualRadius;
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

        ownBody = gameObject.AddComponent<Rigidbody>();
        ownBody.mass = originalMass;
        ownBody.interpolation = RigidbodyInterpolation.Interpolate;

        // Thrown fast enough to tunnel through thin geometry otherwise.
        ownBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        pickupLockUntil = Time.time + pickupLockSeconds;

        return ownBody;
    }
}
