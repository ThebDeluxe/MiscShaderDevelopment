using UnityEngine;

/// <summary>
/// Points a DentManager at whichever mesh variant is actually active.
///
/// The dent system binds to one renderer: the manager stamps into a texture indexed by
/// that mesh's vertices, and the generator has to live on the same GameObject. That works
/// when the mesh is known up front, but not when a randomiser picks one of several
/// children at runtime - the wiring cannot be done in the inspector because the choice
/// does not exist yet.
///
/// So the components move to the chosen renderer instead of the other way round. Runs in
/// Start, which is guaranteed to be after every Awake, so the variant has already been
/// picked by then.
/// </summary>
[DefaultExecutionOrder(-70)]
public class DentVariantBinder : MonoBehaviour
{
    [Header("Targets")]
    [Tooltip("Manager to bind. Falls back to one on this GameObject.")]
    public DentManager dentManager;

    [Tooltip("Stamp material the manager should use. Assign the shared Stamp_Dent_Mat.")]
    public Material stampMaterial;

    [Header("Generator Settings")]
    [Tooltip("Copied onto the generator created on the chosen variant.")]
    [Range(1, 7)] public int targetUVChannel = 2;

    public float weldEpsilon = 0.0001f;

    [Range(1, 128)] public int samplesPerIsland = 32;

    [Header("Debug")]
    public bool logBinding = true;

    /// <summary>The renderer that ended up being used, once bound.</summary>
    public Renderer BoundRenderer { get; private set; }

    void Start()
    {
        if (dentManager == null) dentManager = GetComponent<DentManager>();
        if (dentManager == null)
        {
            Debug.LogError($"{name}: no DentManager to bind.", this);
            return;
        }

        Renderer chosen = FindActiveRenderer();
        if (chosen == null)
        {
            Debug.LogError($"{name}: no active MeshRenderer found among the children. The " +
                           "variant randomiser may not have run yet, or every variant is " +
                           "disabled.", this);
            return;
        }

        // The generator must sit on the renderer's own GameObject: it instantiates that
        // mesh, writes the index UVs into it, and builds the point mesh the stamp pass draws.
        if (!chosen.TryGetComponent(out DentVertexUVGenerator generator))
            generator = chosen.gameObject.AddComponent<DentVertexUVGenerator>();

        generator.targetUVChannel = targetUVChannel;
        generator.weldEpsilon = weldEpsilon;
        generator.samplesPerIsland = samplesPerIsland;
        generator.Generate();

        dentManager.targetRenderer = chosen;
        if (stampMaterial != null) dentManager.stampMaterial = stampMaterial;

        BoundRenderer = chosen;

        if (logBinding)
            Debug.Log($"{name}: dent bound to variant '{chosen.name}'.", this);
    }

    /// <summary>
    /// The one visible variant. Checks both hiding strategies the randomiser supports:
    /// deactivating the GameObject, or just disabling the renderer.
    /// </summary>
    Renderer FindActiveRenderer()
    {
        var renderers = GetComponentsInChildren<MeshRenderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null || r.transform == transform) continue;
            if (!r.gameObject.activeInHierarchy || !r.enabled) continue;

            return r;
        }

        return null;
    }
}
