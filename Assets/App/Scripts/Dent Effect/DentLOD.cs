using UnityEngine;

/// <summary>
/// Scales dent work down as a character gets smaller on screen.
///
/// Deliberately measures its own screen coverage rather than reading Unity's LOD state.
/// Neither LOD system gives a clean "current level" query - Mesh LOD has no API for it at
/// all - and a project may use either, both, or neither. Coverage is the quantity that
/// drives LOD selection anyway, so deriving it here works identically in every case and
/// leaves this decoupled from a moving target.
///
/// Two things get throttled, and resolution is deliberately not one of them: skipping
/// vertices would freeze their texels, because decay happens inside the per-point stamp
/// shader, so they would stop fading and come back stale. Updating less often keeps every
/// texel consistent, and the decay is computed from elapsed time so it self-corrects.
/// </summary>
[DefaultExecutionOrder(-60)]
public class DentLOD : MonoBehaviour
{
    [Header("Targets")]
    public DentManager dentManager;
    public DentContactSource contactSource;

    [Tooltip("Renderer whose screen size is measured. Falls back to the manager's target.")]
    public Renderer targetRenderer;

    [Tooltip("Camera to measure against. Leave empty to use the main camera.")]
    public Camera referenceCamera;

    [Header("Coverage Thresholds")]
    [Tooltip("Screen coverage at or above which the effect runs at full quality, as a " +
             "fraction of screen height.")]
    [Range(0.01f, 1f)] public float fullQualityCoverage = 0.25f;

    [Tooltip("Screen coverage at or below which the effect stops updating entirely.")]
    [Range(0f, 0.5f)] public float cutoffCoverage = 0.04f;

    [Tooltip("How much larger the character has to get to switch back on than it did to " +
             "switch off, as a fraction. Stops a character hovering at the boundary from " +
             "flicking in and out.")]
    [Range(0f, 1f)] public float hysteresis = 0.25f;

    [Header("Throttling")]
    [Tooltip("Frames between updates at the lowest quality. 1 disables throttling.")]
    [Range(1, 10)] public int maxUpdateInterval = 4;

    [Header("Fade")]
    [Tooltip("Fade the deformation out as quality drops, so it is already gone by the time " +
             "updates stop. A hard cutoff pops even with hysteresis; a fade never does.")]
    public bool fadeDepth = true;

    [Tooltip("Material property scaling the deformation on the character shader.")]
    public string depthProperty = "_Max_Dent_Depth";

    [Tooltip("Value of that property at full quality.")]
    public float fullDepth = 1f;

    [Tooltip("Seconds for the fade to follow a change in quality.")]
    public float fadeSmoothing = 0.2f;

    /// <summary>Fraction of screen height the character currently covers.</summary>
    public float Coverage { get; private set; }

    /// <summary>0 = dormant, 1 = full quality.</summary>
    public float Quality { get; private set; } = 1f;

    /// <summary>Frames currently skipped between updates.</summary>
    public int UpdateInterval { get; private set; } = 1;

    public bool Active { get; private set; } = true;

    int depthID;
    Material characterMaterial;
    float smoothedDepth;
    float fadeVelocity;
    int frameCounter;

    void Start()
    {
        if (dentManager == null) dentManager = GetComponent<DentManager>();
        if (targetRenderer == null && dentManager != null) targetRenderer = dentManager.targetRenderer;

        depthID = Shader.PropertyToID(depthProperty);

        if (targetRenderer != null)
        {
            // Same instance DentManager uses; first access creates it, later ones reuse it.
            characterMaterial = targetRenderer.material;
            if (!characterMaterial.HasProperty(depthID)) characterMaterial = null;
        }

        smoothedDepth = fullDepth;
    }

    void LateUpdate()
    {
        Camera cam = referenceCamera != null ? referenceCamera : Camera.main;
        if (cam == null || targetRenderer == null) return;

        Coverage = MeasureCoverage(cam);
        UpdateQuality();
        ApplyThrottle();
        ApplyFade();
    }

    /// <summary>
    /// Fraction of screen height the renderer's bounds span. This is the same shape of
    /// measure LOD systems use, so the numbers correlate with where levels switch.
    /// </summary>
    float MeasureCoverage(Camera cam)
    {
        Bounds bounds = targetRenderer.bounds;

        float distance = Vector3.Distance(cam.transform.position, bounds.center);
        if (distance < 0.01f) return 1f;

        float size = bounds.size.magnitude;

        if (cam.orthographic)
            return size / Mathf.Max(cam.orthographicSize * 2f, 0.01f);

        float frustumHeight = 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        return size / Mathf.Max(frustumHeight, 0.01f);
    }

    void UpdateQuality()
    {
        // Separate on and off thresholds, so returning takes more than just drifting back
        // over the line that switched it off.
        float onThreshold = cutoffCoverage * (1f + hysteresis);
        float offThreshold = cutoffCoverage;

        Active = Active ? Coverage > offThreshold : Coverage > onThreshold;

        Quality = Active
            ? Mathf.Clamp01(Mathf.InverseLerp(cutoffCoverage, fullQualityCoverage, Coverage))
            : 0f;
    }

    void ApplyThrottle()
    {
        UpdateInterval = Active
            ? Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(maxUpdateInterval, 1f, Quality)))
            : int.MaxValue;

        frameCounter++;

        bool runThisFrame = Active && (UpdateInterval <= 1 || frameCounter % UpdateInterval == 0);

        // Paused rather than disabled: disabling DentManager would run its OnDisable and
        // release the render textures, losing every accumulated dent.
        if (dentManager != null) dentManager.paused = !runThisFrame;
        if (contactSource != null) contactSource.paused = !runThisFrame;
    }

    void ApplyFade()
    {
        if (!fadeDepth || characterMaterial == null) return;

        float target = fullDepth * Quality;

        smoothedDepth = fadeSmoothing > 0f
            ? Mathf.SmoothDamp(smoothedDepth, target, ref fadeVelocity, fadeSmoothing)
            : target;

        characterMaterial.SetFloat(depthID, smoothedDepth);
    }

    void OnDisable()
    {
        // Leave the system running rather than stuck paused.
        if (dentManager != null) dentManager.paused = false;
        if (contactSource != null) contactSource.paused = false;

        if (characterMaterial != null) characterMaterial.SetFloat(depthID, fullDepth);
    }
}
