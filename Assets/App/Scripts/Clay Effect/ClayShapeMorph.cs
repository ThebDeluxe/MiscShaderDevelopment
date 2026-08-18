using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>How a shape's collider is built. Auto picks from the shape's own proportions.</summary>
public enum ClayColliderStyle
{
    Auto = 0,
    Sphere = 1,
    Capsule = 2,
    Box = 3,
    /// <summary>Capsule rim with a filled face. Suits discs and cylinders.</summary>
    DiscComposite = 4,
    /// <summary>Capsules along the edges with a box filling the faces.</summary>
    BoxCage = 5,
    /// <summary>Flat base with rings of spheres above. Suits cones and pyramids.</summary>
    TaperedComposite = 6
}

public enum ClayShape
{
    Sphere = 0,
    Pancake = 1,
    Noodle = 2,
    Cylinder = 3,
    Box = 4,
    Cone = 5,
    Pyramid = 6,
    Plank = 7
}

/// <summary>Which way a shape's long axis points when it forms.</summary>
public enum ClayShapeAxis
{
    /// <summary>Straight up. A pancake flattens against the ground.</summary>
    WorldUp = 0,
    /// <summary>The way the character is travelling. A noodle lies along the ground.</summary>
    Travel = 1,
    /// <summary>Whatever axis the caller passes in - an impact direction, say.</summary>
    Custom = 2
}

/// <summary>
/// Morphs the character between whole-body shapes using a spatial field in the vertex
/// shader, rather than per-vertex targets.
///
/// Every shape is one superquadric - the family running continuously from sphere through
/// capsule and cylinder to box - plus a taper for cones and pyramids. So a shape is a
/// handful of numbers rather than a sculpted mesh, and the parameters below say what they
/// actually do: how wide, how thick, how rounded the edges are.
///
/// The axis is captured in object space when the shape changes, so the deformation keeps
/// the orientation it formed at and turns with the character afterwards.
/// </summary>
[DefaultExecutionOrder(-65)]
public class ClayShapeMorph : MonoBehaviour
{
    /// <summary>
    /// One shape's dimensions. Named per use in the inspector below, since the same three
    /// numbers mean different things depending on the shape.
    /// </summary>
    [Serializable]
    public class ShapeDefinition
    {
        [Tooltip("Half extent across the axis, relative to the character's radius.")]
        public float width = 1.4f;

        [Tooltip("Half extent across the OTHER cross-axis. 0 matches Width, giving a " +
                 "circular or square cross-section; a smaller value flattens it into a " +
                 "plank or a blade.")]
        public float thickness = 0f;

        [Tooltip("Half extent along the axis, relative to the character's radius.")]
        public float length = 0.3f;

        [Tooltip("Cross-section shape. 1 is a circle, 0 is a square.")]
        [Range(0f, 1f)] public float crossRoundness = 1f;

        [Tooltip("Ends of the axis. 1 rounds them into a dome, 0 flattens them into faces. " +
                 "Values around 0.3 give a flat face with a rounded rim.")]
        [Range(0f, 1f)] public float endRoundness = 0.35f;

        [Tooltip("Narrowing toward the far end. 0 is straight-sided, 1 comes to a point.")]
        [Range(0f, 1f)] public float taper = 0f;

        [Tooltip("How vertices are placed on the shape.\n\n" +
                 "0 keeps each vertex's own direction, which covers flat faces evenly and " +
                 "suits SQUARE shapes - but on a flat shape it collapses the top cap into a " +
                 "small central disc.\n\n" +
                 "1 places them by angle, which keeps FLAT shapes spread - but bunches " +
                 "vertices at the corners of square ones, smearing the faces.")]
        [Range(0f, 1f)] public float spread = 1f;

        [Tooltip("Which way the long axis points when this shape forms.")]
        public ClayShapeAxis axis = ClayShapeAxis.WorldUp;

        [Tooltip("How large the PHYSICAL collider is compared to this shape's visible size.\n\n" +
                 "Below 1 by design: the gap is what the mesh sinks by, and that sink is " +
                 "what the dent effect flattens. Per shape, because a flat pancake and a " +
                 "chunky box want different amounts - and a shape approximated by a " +
                 "composite often wants a little more clearance than one that fits exactly.")]
        [Range(0.2f, 1f)] public float colliderScale = 0.75f;

        [Tooltip("How the collider is built. Auto picks from the shape's proportions, which " +
                 "is usually right but changes as those are tuned - set it explicitly if a " +
                 "shape keeps landing on the wrong one.")]
        public ClayColliderStyle colliderStyle = ClayColliderStyle.Auto;

        [Tooltip("Seconds to morph into this shape.")]
        public float duration = 0.35f;

        /// <summary>Thickness, falling back to Width for a symmetric cross-section.</summary>
        public float SafeThickness => thickness > 0.001f ? thickness : width;

        public ShapeDefinition(float width, float thickness, float length,
                               float crossRoundness, float endRoundness, float taper,
                               float spread, float colliderScale, ClayShapeAxis axis)
        {
            this.width = width;
            this.thickness = thickness;
            this.length = length;
            this.crossRoundness = crossRoundness;
            this.endRoundness = endRoundness;
            this.taper = taper;
            this.spread = spread;
            this.colliderScale = colliderScale;
            this.axis = axis;
            this.duration = 0.35f;
        }
    }

    [Header("Setup")]
    [Tooltip("Renderer whose material receives the shape. Found on this object if empty.")]
    public Renderer targetRenderer;

    [Tooltip("Dent manager whose stamp pass also needs the shape state. Found in parents if " +
             "empty.\n\n" +
             "The stamp measures each vertex against nearby surfaces, so it has to warp the " +
             "vertex the same way this does - otherwise dents are computed for the original " +
             "mesh and applied to a deformed one, and the surface sails straight through " +
             "whatever it should be flattening against.")]
    public DentManager dentManager;

    [Tooltip("Point the deformation is built around, in the renderer's local space.")]
    public Vector3 pivotLocal = Vector3.zero;

    [Tooltip("Radius of the character at rest. Everything below is relative to this, so a " +
             "width of 1 means 'as wide as it already is'.")]
    public float baseRadius = 0.7f;

    [Header("Shapes")]
    [Tooltip("The character at rest. Only its collider scale is used - the mesh is already " +
             "this shape, so nothing is deformed.")]
    public ShapeDefinition sphere = new ShapeDefinition(1f, 0f, 1f, 1f, 1f, 0f, 1f, 0.57f, ClayShapeAxis.WorldUp);

    [Tooltip("Wide and flat, with a rounded rim.")]
    public ShapeDefinition pancake = new ShapeDefinition(1.5f, 0f, 0.25f, 1f, 0.3f, 0f, 1f, 0.8f, ClayShapeAxis.WorldUp);

    [Tooltip("Long and thin, lying along the direction of travel.")]
    public ShapeDefinition noodle = new ShapeDefinition(0.45f, 0f, 2.2f, 1f, 1f, 0f, 0.5f, 0.75f, ClayShapeAxis.Travel)
    {
        // One capsule is already exactly this shape, so there is nothing a composite adds.
        colliderStyle = ClayColliderStyle.Capsule
    };

    [Tooltip("A less flat pancake - proper flat faces and a rounded edge.")]
    public ShapeDefinition cylinder = new ShapeDefinition(1f, 0f, 0.9f, 1f, 0.2f, 0f, 1f, 0.75f, ClayShapeAxis.WorldUp);

    [Tooltip("Rectangular prism. Square cross-section, flat ends.")]
    public ShapeDefinition box = new ShapeDefinition(0.85f, 0f, 0.85f, 0f, 0.05f, 0f, 0f, 0.75f, ClayShapeAxis.WorldUp);

    [Tooltip("Round base tapering to a point.")]
    public ShapeDefinition cone = new ShapeDefinition(1.2f, 0f, 1.1f, 1f, 0.1f, 0.95f, 0.5f, 0.7f, ClayShapeAxis.WorldUp);

    [Tooltip("Square base tapering to a point.")]
    public ShapeDefinition pyramid = new ShapeDefinition(1.1f, 0f, 1.1f, 0f, 0.05f, 0.95f, 0f, 0.7f, ClayShapeAxis.WorldUp);

    [Tooltip("Long, wide and thin - a wooden plank. Lies along the direction of travel.")]
    public ShapeDefinition plank = new ShapeDefinition(0.9f, 0.22f, 2f, 0f, 0.05f, 0f, 0f, 0.8f, ClayShapeAxis.Travel);

    [Header("Blending")]
    public AnimationCurve blendCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    const string AxisProperty = "_ShapeAxis";
    const string PivotProperty = "_ShapePivot";
    const string SizeProperty = "_ShapeSize";
    const string ParamsProperty = "_ShapeParams";
    const string SpreadProperty = "_ShapeSpread";
    const string AmountProperty = "_ShapeAmount";

    static readonly int AxisID = Shader.PropertyToID(AxisProperty);
    static readonly int PivotID = Shader.PropertyToID(PivotProperty);
    static readonly int SizeID = Shader.PropertyToID(SizeProperty);
    static readonly int ParamsID = Shader.PropertyToID(ParamsProperty);
    static readonly int SpreadID = Shader.PropertyToID(SpreadProperty);
    static readonly int AmountID = Shader.PropertyToID(AmountProperty);

    readonly List<Material> materials = new List<Material>();

    Material material;
    ClayShape current = ClayShape.Sphere;
    Coroutine blending;

    // Set when an inspector value changes, so tuning a shape shows up immediately instead
    // of only on the next shape change. Deferred to Update rather than pushed straight from
    // OnValidate, which can run at times when touching materials is not safe.
    bool settingsDirty;

    public ClayShape CurrentShape => current;

    /// <summary>The shape's long axis in world space, for anything matching itself to it.</summary>
    public Vector3 CurrentAxisWorld =>
        targetRenderer != null ? targetRenderer.transform.TransformDirection(currentAxisObject)
                               : currentAxisObject;

    /// <summary>
    /// The axis in the RENDERER's object space, which is where the shader holds it.
    ///
    /// Anything lining itself up with the shape has to start from this rather than from a
    /// world-space snapshot: the snapshot goes stale the moment the character turns, and
    /// any rotation between the renderer and the body puts the two permanently out of step.
    /// </summary>
    public Vector3 CurrentAxisObject => currentAxisObject;

    Vector3 currentAxisObject = Vector3.up;

    /// <summary>Dimensions for a shape, so colliders can be built from the same numbers.</summary>
    public ShapeDefinition GetDefinition(ClayShape shape) => DefinitionFor(shape);

    /// <summary>
    /// The point the shape is built around, in world space.
    ///
    /// Anything measuring against the shape has to start here rather than from its own
    /// transform: the renderer can sit somewhere else entirely - it gets re-centred as blobs
    /// merge - so measuring from the body's origin puts the whole silhouette off by that
    /// offset.
    /// </summary>
    public Vector3 PivotWorld =>
        targetRenderer != null ? targetRenderer.transform.TransformPoint(pivotLocal)
                               : transform.position;

    /// <summary>How far the deformed surface currently reaches at its widest.</summary>
    public float MaxRadius
    {
        get
        {
            if (current == ClayShape.Sphere) return baseRadius;

            var d = DefinitionFor(current);
            float largest = Mathf.Max(d.width, Mathf.Max(d.SafeThickness, d.length));

            return Mathf.Lerp(baseRadius, largest * baseRadius, blendAmount);
        }
    }

    /// <summary>
    /// Distance from the pivot to the deformed surface along an OBJECT space direction.
    ///
    /// A C# mirror of the warp in ClayShape.hlsl, so anything that needs to know where the
    /// character's surface actually is - contact detection especially - can ask rather than
    /// assuming it is still a sphere. On a pancake that assumption is wrong twice over: the
    /// rim reaches half again as far as the sphere did, while the flat faces sit at a
    /// quarter of it.
    ///
    /// Keep this in step with the shader. If the shape maths changes there, it changes here.
    /// </summary>
    public float SurfaceDistanceObject(Vector3 directionObject)
    {
        if (current == ClayShape.Sphere || blendAmount < 1e-4f) return baseRadius;

        Vector3 dir = directionObject.normalized;
        if (dir.sqrMagnitude < 1e-6f) return baseRadius;

        var d = DefinitionFor(current);

        // Into the shape's own frame, the same basis the shader builds.
        Vector3 w = currentAxisObject.normalized;
        Vector3 helper = Mathf.Abs(w.y) > 0.99f ? Vector3.right : Vector3.up;
        Vector3 u = Vector3.Cross(helper, w).normalized;
        Vector3 v = Vector3.Cross(w, u);

        var local = new Vector3(Vector3.Dot(dir, u), Vector3.Dot(dir, v), Vector3.Dot(dir, w));

        var extents = new Vector3(d.width, d.SafeThickness, d.length) * baseRadius;

        float crossExp = Mathf.Lerp(0.15f, 1f, Mathf.Clamp01(d.crossRoundness));
        float profileExp = Mathf.Lerp(0.15f, 1f, Mathf.Clamp01(d.endRoundness));

        float squaring = SurfaceFactor(local, crossExp, profileExp);

        // The warp: a surface point starts at baseRadius along this direction.
        var warped = new Vector3(local.x * extents.x, local.y * extents.y, local.z * extents.z)
                     * squaring;

        // Taper narrows the cross-section toward the far end.
        float taper = Mathf.Clamp01(d.taper);
        if (taper > 1e-4f)
        {
            float halfHeight = Mathf.Max(extents.z, 1e-4f);
            float alongAxis = Mathf.Clamp01((warped.z + halfHeight) / (2f * halfHeight));
            float scale = Mathf.Lerp(1f, Mathf.Max(1f - taper, 0f), alongAxis);

            warped.x *= scale;
            warped.y *= scale;
        }

        // Blended against the untouched sphere, matching the shader's final lerp.
        Vector3 blended = Vector3.Lerp(local * baseRadius, warped, blendAmount);

        return Mathf.Max(blended.magnitude, 1e-3f);
    }

    /// <summary>Mirror of ClaySurfaceDistance: the unit superellipsoid along a direction.</summary>
    static float SurfaceFactor(Vector3 dir, float crossExp, float profileExp)
    {
        float m = 2f / Mathf.Max(crossExp, 1e-3f);
        float n = 2f / Mathf.Max(profileExp, 1e-3f);

        Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));

        float across = Mathf.Pow(Mathf.Max(a.x, 1e-6f), m) + Mathf.Pow(Mathf.Max(a.y, 1e-6f), m);
        across = Mathf.Pow(across, n / m);

        float total = across + Mathf.Pow(Mathf.Max(a.z, 1e-6f), n);

        return 1f / Mathf.Max(Mathf.Pow(total, 1f / n), 1e-5f);
    }

    /// <summary>Distance to the surface along a WORLD space direction.</summary>
    public float SurfaceDistanceWorld(Vector3 directionWorld)
    {
        if (targetRenderer == null) return baseRadius;

        return SurfaceDistanceObject(targetRenderer.transform.InverseTransformDirection(directionWorld));
    }

    float blendAmount;

    /// <summary>Adds another renderer to be shaped alongside this one, e.g. an absorbed blob.</summary>
    public void AddRenderer(Renderer renderer)
    {
        if (renderer == null) return;
        AddMaterial(renderer.material);
    }

    /// <summary>
    /// Adds a material that needs the shape state, whether or not it draws the character.
    ///
    /// The dent stamp pass needs it too: it measures each vertex against nearby surfaces, so
    /// it has to warp the vertex the same way first. Measuring the original mesh instead
    /// computes dents for where a sphere's surface was and the character shader then applies
    /// them to a pancake, which lands the deformation nowhere near the contact.
    /// </summary>
    public void AddMaterial(Material instance)
    {
        if (instance == null || materials.Contains(instance)) return;
        if (!instance.HasProperty(AmountID)) return;

        materials.Add(instance);

        // Brought up to date immediately, since it has missed every push so far.
        if (current != ClayShape.Sphere) PushShape(DefinitionFor(current), Vector3.zero);
        else instance.SetFloat(AmountID, 0f);
    }

    public void RemoveRenderer(Renderer renderer)
    {
        if (renderer == null) return;

        for (int i = materials.Count - 1; i >= 0; i--)
        {
            if (materials[i] != renderer.sharedMaterial) continue;

            materials[i].SetFloat(AmountID, 0f);
            materials.RemoveAt(i);
        }
    }

    void Start()
    {
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();

        if (targetRenderer == null)
        {
            Debug.LogError($"{name}: ClayShapeMorph needs a Renderer.", this);
            enabled = false;
            return;
        }

        material = targetRenderer.material;

        if (!material.HasProperty(AmountID) || !material.HasProperty(SizeID))
        {
            Debug.LogError($"{name}: material '{material.name}' is missing " +
                           $"'{AmountProperty}' or '{SizeProperty}'. Add the shape properties " +
                           "in the shader graph and wire them into the vertex position " +
                           "through the ApplyClayShape custom function.", this);
            enabled = false;
            return;
        }

        materials.Add(material);
        SetAll(AmountID, 0f);

        if (dentManager == null) dentManager = GetComponentInParent<DentManager>();
    }

    void LateUpdate()
    {
        // The stamp material is created lazily by DentManager, so it cannot be picked up in
        // Start. Cheap to check, and it only ever succeeds once.
        if (dentManager != null && dentManager.StampInstance != null)
        {
            AddMaterial(dentManager.StampInstance);
            dentManager = null;
        }
    }

    void Update()
    {
        if (!settingsDirty) return;
        settingsDirty = false;

        // Nothing to refresh while the character is its plain self.
        if (current == ClayShape.Sphere || material == null) return;

        PushShape(DefinitionFor(current), Vector3.zero);
    }

    void OnValidate()
    {
        baseRadius = Mathf.Max(0.01f, baseRadius);

        // Every shape's length and width are half extents, so zero would collapse the mesh.
        ClampDefinition(sphere);
        ClampDefinition(pancake);
        ClampDefinition(noodle);
        ClampDefinition(cylinder);
        ClampDefinition(box);
        ClampDefinition(cone);
        ClampDefinition(pyramid);
        ClampDefinition(plank);

        settingsDirty = true;
    }

    static void ClampDefinition(ShapeDefinition definition)
    {
        if (definition == null) return;

        definition.width = Mathf.Max(0.01f, definition.width);
        definition.length = Mathf.Max(0.01f, definition.length);
        definition.duration = Mathf.Max(0.01f, definition.duration);
    }

    /// <summary>
    /// Sends a shape's dimensions to every material being driven.
    ///
    /// Separate from SetShape so the numbers can be re-sent while tuning without
    /// restarting the blend - the shape updates under the cursor rather than replaying its
    /// morph on every keystroke.
    /// </summary>
    void PushShape(ShapeDefinition definition, Vector3 customWorldAxis)
    {
        Vector3 worldAxis = ResolveAxis(definition, customWorldAxis);
        Vector3 axisOS = targetRenderer.transform.InverseTransformDirection(worldAxis).normalized;

        currentAxisObject = axisOS;

        SetAll(AxisID, axisOS);
        SetAll(SizeID, new Vector3(definition.width, definition.SafeThickness, definition.length));
        SetAll(ParamsID, new Vector4(definition.crossRoundness, definition.endRoundness,
                                     definition.taper, Mathf.Max(baseRadius, 0.01f)));
        SetAll(SpreadID, definition.spread);
        SetAll(PivotID, pivotLocal);
    }

    /// <summary>Morphs to a shape, using that shape's own axis setting.</summary>
    public void SetShape(ClayShape shape) => SetShape(shape, Vector3.zero);

    /// <summary>
    /// Morphs to a shape. The axis is converted to object space and then left alone, so the
    /// deformation keeps the orientation it formed at and turns with the character
    /// afterwards - pass an impact direction to have a hit flatten it the way it landed.
    /// </summary>
    public void SetShape(ClayShape shape, Vector3 customWorldAxis)
    {
        if (shape == current) return;

        ClayShape previous = current;

        // Updated NOW rather than when the blend finishes. Callers poll CurrentShape to
        // decide whether to request a change, so leaving it stale means the request repeats
        // every frame and restarts the blend from zero.
        current = shape;

        ShapeDefinition definition = DefinitionFor(shape == ClayShape.Sphere ? previous : shape);

        if (blending != null) StopCoroutine(blending);

        // Going from one sculpted shape to another leaves the blend amount at 1 the whole
        // way, so pushing the new dimensions straight in swaps them within a single frame -
        // the shape snaps rather than morphing. Relaxing back to the base first gives the
        // amount somewhere to travel, and reads as the clay letting go before it re-forms.
        bool shapeToShape = previous != ClayShape.Sphere && shape != ClayShape.Sphere;

        if (shapeToShape)
        {
            blending = StartCoroutine(BlendThroughBase(definition, customWorldAxis));
            return;
        }

        if (shape != ClayShape.Sphere) PushShape(definition, customWorldAxis);
        else SetAll(PivotID, pivotLocal);

        blending = StartCoroutine(BlendAndFinish(shape == ClayShape.Sphere ? 0f : 1f,
                                                 definition.duration));
    }

    IEnumerator BlendAndFinish(float to, float duration)
    {
        yield return Blend(to, duration);
        blending = null;
    }

    /// <summary>
    /// Relaxes to the base shape, swaps the dimensions there, then forms the new one.
    ///
    /// The swap happens while the blend amount is zero, so none of it is visible - which is
    /// the only way to change dimensions without them jumping, since the shader has no
    /// second set to interpolate against.
    /// </summary>
    IEnumerator BlendThroughBase(ShapeDefinition definition, Vector3 customWorldAxis)
    {
        // Half each way, so a shape-to-shape change takes about as long as any other.
        float half = Mathf.Max(definition.duration, 0.02f) * 0.5f;

        yield return Blend(0f, half);

        PushShape(definition, customWorldAxis);

        yield return Blend(1f, half);

        blending = null;
    }

    Vector3 ResolveAxis(ShapeDefinition definition, Vector3 customWorldAxis)
    {
        switch (definition.axis)
        {
            case ClayShapeAxis.Travel:
            {
                // Flattened onto the ground, so a noodle lies along it rather than rearing up.
                Vector3 velocity = Vector3.zero;

                var body = GetComponentInParent<Rigidbody>();
                if (body != null) velocity = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);

                if (velocity.sqrMagnitude > 0.01f) return velocity.normalized;

                // Standing still, so fall back to facing rather than collapsing to zero.
                Vector3 forward = Vector3.ProjectOnPlane(targetRenderer.transform.forward, Vector3.up);
                return forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            }

            case ClayShapeAxis.Custom:
                return customWorldAxis.sqrMagnitude > 1e-4f ? customWorldAxis.normalized : Vector3.up;

            default:
                return Vector3.up;
        }
    }

    ShapeDefinition DefinitionFor(ClayShape shape)
    {
        switch (shape)
        {
            case ClayShape.Pancake: return pancake;
            case ClayShape.Noodle: return noodle;
            case ClayShape.Cylinder: return cylinder;
            case ClayShape.Box: return box;
            case ClayShape.Cone: return cone;
            case ClayShape.Pyramid: return pyramid;
            case ClayShape.Plank: return plank;
            default: return sphere;
        }
    }

    IEnumerator Blend(float to, float duration)
    {
        float from = material.GetFloat(AmountID);
        float elapsed = 0f;
        duration = Mathf.Max(duration, 0.01f);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = blendCurve.Evaluate(Mathf.Clamp01(elapsed / duration));
            SetAll(AmountID, Mathf.LerpUnclamped(from, to, t));
            yield return null;
        }

        SetAll(AmountID, to);
    }

    void SetAll(int id, float value)
    {
        if (id == AmountID) blendAmount = value;

        for (int i = 0; i < materials.Count; i++)
            if (materials[i] != null) materials[i].SetFloat(id, value);
    }

    void SetAll(int id, Vector4 value)
    {
        for (int i = 0; i < materials.Count; i++)
            if (materials[i] != null) materials[i].SetVector(id, value);
    }

    void OnDisable()
    {
        SetAll(AmountID, 0f);
    }
}
