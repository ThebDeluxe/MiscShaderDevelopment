using UnityEngine;

/// <summary>
/// Marks a lump of clay that can be absorbed into the player's assembly.
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

    Rigidbody ownBody;

    void Awake()
    {
        if (contactSource == null) contactSource = GetComponentInChildren<DentContactSource>();
        ownBody = GetComponent<Rigidbody>();

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
}
