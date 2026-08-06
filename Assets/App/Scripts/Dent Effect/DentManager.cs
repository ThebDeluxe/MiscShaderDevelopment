using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Drives the dent map for ONE renderer: every frame it stamps the dent sources that
/// actually reach it into a double-buffered RenderTexture, then assigns the result to
/// that renderer's own material.
///
/// Multiple dented objects are supported. Each manager owns its own render textures,
/// its own instance of the stamp material, and writes to its own material instance -
/// so nothing is shared and nothing collides. Sources are filtered per manager by
/// bounds, so punching one character does not dent the others.
///
/// The map stores an OBJECT SPACE DISPLACEMENT VECTOR in RGB (A unused), so it needs a
/// signed floating point format - ARGBHalf. An 8-bit format cannot store signed
/// directions, and its quantisation also stalls the decay.
///
/// The stamp pass draws the mesh as POINTS into texel space (see DentVertexUVGenerator),
/// so each vertex writes exactly one texel. Camera independent, and independent of the
/// authored UV0 layout.
///
/// Play mode only, deliberately: creating mesh/RT/material instances in edit mode leaks.
/// </summary>
public class DentManager : MonoBehaviour
{
    [Header("Target")]
    public Renderer targetRenderer;

    [Tooltip("Source asset for the stamp pass. Each manager instantiates its own copy, so " +
             "several dented objects can share this asset without fighting over it.")]
    public Material stampMaterial;

    [Tooltip("Must be a signed, floating point format. ARGBHalf is the sane default; " +
             "RGBA8 cannot store negative direction components.")]
    public RenderTextureFormat format = RenderTextureFormat.ARGBHalf;

    [Header("Decay")]
    [Tooltip("Fraction of the dent magnitude lost per second. 0 = dents are permanent.")]
    [Range(0f, 1f)] public float decayPerSecond = 0.15f;

    [Tooltip("Extra decay proportional to how deep a dent is.\n\n" +
             "Plain decay is proportional, so a deep dent and a shallow one shrink by the " +
             "same fraction each frame and the deep one stays visible far longer. Raise " +
             "this to make deeper dents fade faster so they even out.\n\n" +
             "0 = classic proportional decay.")]
    [Range(0f, 8f)] public float decayDepthBias = 0f;

    [Header("Source Filtering")]
    [Tooltip("Only stamp sources whose reach overlaps this renderer's bounds. Keeps several " +
             "dented objects independent, and skips work for distant sources.")]
    public bool filterSourcesByBounds = true;

    [Tooltip("Extra slack on the bounds test, in world units.")]
    public float boundsPadding = 0.05f;

    [Header("Island Rigidity")]
    [Tooltip("How much disconnected mesh parts move as a rigid block instead of being\n" +
             "pressed per-vertex. 1 keeps a part's shape and its offset from whatever it\n" +
             "sits on; 0 is the old per-vertex behaviour.")]
    [Range(0f, 1f)] public float islandRigidity = 1f;

    [Tooltip("Islands with a local-space radius at or below this are FULLY rigid: the whole " +
             "piece translates by one push value and does not bend at all.")]
    public float rigidBelowRadius = 0.1f;

    [Tooltip("Islands at or above this local-space radius are pressed fully per-vertex, so " +
             "they bend to the stamp. Between the two radii rigidity ramps smoothly.")]
    public float flexibleAboveRadius = 0.35f;

    [Header("Debug")]
    [Tooltip("Push EVERY vertex along object-space up, ignoring dent sources.\n" +
             "If the mapping is correct the whole mesh should displace uniformly.")]
    public bool debugStampAll = false;

    [Tooltip("Leave off to auto-detect from SystemInfo.graphicsUVStartsAtTop.\n" +
             "Turn on and change Flip Y Value if dents look scrambled across unrelated vertices.")]
    public bool overrideFlipY = false;
    public bool flipYValue = true;

    const int MAX_DENTS = 16;     // must match DENT_MAX in DentStamp.hlsl
    const int MAX_ISLANDS = 32;   // must match ISLAND_MAX in DentStamp.hlsl

    /// <summary>
    /// Texture property on the character material that receives the dent map. Must be a
    /// Per Material scope property in the shader graph - a Global scope one cannot be set
    /// per object, so several dented characters would overwrite each other.
    /// </summary>
    const string DentTextureProperty = "_CustomRT_Dents";

    static readonly int DentPosID       = Shader.PropertyToID("_DentPos");
    static readonly int DentAxisID      = Shader.PropertyToID("_DentAxis");
    static readonly int DentRightID     = Shader.PropertyToID("_DentRight");
    static readonly int DentParamsID    = Shader.PropertyToID("_DentParams");
    static readonly int DentBulgeID     = Shader.PropertyToID("_DentBulge");
    static readonly int DentDecayID     = Shader.PropertyToID("_DentDecay");
    static readonly int DentCountID     = Shader.PropertyToID("_DentCount");
    static readonly int PrevTexID       = Shader.PropertyToID("_PrevDentMap");
    static readonly int DecayID         = Shader.PropertyToID("_Decay");
    static readonly int DecayDepthBiasID = Shader.PropertyToID("_DecayDepthBias");
    static readonly int FlipYID         = Shader.PropertyToID("_FlipY");
    static readonly int DebugStampAllID = Shader.PropertyToID("_DebugStampAll");
    static readonly int IslandPushID    = Shader.PropertyToID("_IslandPush");
    static readonly int IslandCountID   = Shader.PropertyToID("_IslandCount");
    static readonly int DentTextureID   = Shader.PropertyToID(DentTextureProperty);

    /// <summary>Every enabled source in the scene. Each manager filters this down itself.</summary>
    static readonly List<DentSource> allSources = new List<DentSource>();

    /// <summary>The subset actually reaching this renderer, rebuilt each frame.</summary>
    readonly List<DentSource> active = new List<DentSource>(MAX_DENTS);

    // xyz = world position of contact point, w = shape id (0 capsule, 1 cylinder, 2 square)
    readonly Vector4[] dentPos    = new Vector4[MAX_DENTS];
    // xyz = world press axis (+Z),            w = depth
    readonly Vector4[] dentAxis   = new Vector4[MAX_DENTS];
    // xyz = world right (+X, orients Square), w = flatten scale
    readonly Vector4[] dentRight  = new Vector4[MAX_DENTS];
    // x = inner radius, y = outer radius, z = strength, w = spread amount
    readonly Vector4[] dentParams = new Vector4[MAX_DENTS];
    // x = rim bulge amount, y = bulge reach, z = normal bias, w = press depth
    readonly Vector4[] dentBulge  = new Vector4[MAX_DENTS];
    // x = decay multiplier for dents this stamp creates
    readonly Vector4[] dentDecay  = new Vector4[MAX_DENTS];
    /// <summary>Deepest penetration each active source achieves anywhere on the mesh.</summary>
    readonly float[] pressDepth = new float[MAX_DENTS];
    // xyz = OBJECT space rigid push for this island, w = rigidity 0..1
    readonly Vector4[] islandPush = new Vector4[MAX_ISLANDS];

    DentVertexUVGenerator generator;
    Material stampInstance;      // our own copy, so managers never fight over uniforms
    Material characterMaterial;  // renderer's instance, receives the dent map
    RenderTexture rtA, rtB;
    bool aIsCurrent;
    CommandBuffer cmd;
    bool initialised;

    public RenderTexture CurrentDentMap => aIsCurrent ? rtA : rtB;
    public int TextureSize => generator != null ? generator.TextureSize : 0;
    public int ActiveDentCount => active.Count;

    public static void Register(DentSource s)
    {
        if (!allSources.Contains(s)) allSources.Add(s);
    }

    public static void Unregister(DentSource s) => allSources.Remove(s);

    void OnDisable()
    {
        Release();
    }

    bool EnsureInitialised()
    {
        if (initialised) return true;

        if (targetRenderer == null)
        {
            Debug.LogError($"{name}: targetRenderer is not assigned.", this);
            enabled = false;
            return false;
        }

        if (stampMaterial == null)
        {
            Debug.LogError($"{name}: stampMaterial is not assigned.", this);
            enabled = false;
            return false;
        }

        generator = targetRenderer.GetComponent<DentVertexUVGenerator>();
        if (generator == null)
        {
            Debug.LogError($"{name}: targetRenderer '{targetRenderer.name}' has no DentVertexUVGenerator. " +
                            "Add one so the dent system gets a unique vertex->texel mapping.", this);
            enabled = false;
            return false;
        }

        generator.Generate(); // no-op if Awake already ran

        if (!generator.IsGenerated || generator.PointMesh == null)
        {
            Debug.LogError($"{name}: DentVertexUVGenerator failed to produce a point mesh.", this);
            enabled = false;
            return false;
        }

        if (!SystemInfo.SupportsRenderTextureFormat(format))
        {
            Debug.LogWarning($"{name}: {format} unsupported on this device, falling back to ARGBFloat.", this);
            format = RenderTextureFormat.ARGBFloat;
        }

        // Our own stamp material, so two managers cannot overwrite each other's uniforms
        // between setting them and the draw actually executing.
        stampInstance = new Material(stampMaterial) { name = stampMaterial.name + " (Instance)" };

        // Renderer's own material instance. Instances keep SRP Batcher compatibility,
        // unlike MaterialPropertyBlock overrides.
        characterMaterial = targetRenderer.material;

        if (!characterMaterial.HasProperty(DentTextureID))
        {
            Debug.LogError($"{name}: material '{characterMaterial.name}' has no texture property " +
                           $"'{DentTextureProperty}'. In the shader graph, set that property's Scope " +
                           "to 'Per Material' - a Global scope property cannot be set per object.", this);
            enabled = false;
            return false;
        }

        int size = generator.TextureSize;
        rtA = CreateRT(size);
        rtB = CreateRT(size);
        aIsCurrent = true;

        cmd = new CommandBuffer { name = $"Dent Stamp Pass ({targetRenderer.name})" };

        initialised = true;
        return true;
    }

    RenderTexture CreateRT(int size)
    {
        var rt = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
        {
            name = $"DentMap_{name}_{size}",
            wrapMode = TextureWrapMode.Clamp,
            // Point is required: neighbouring texels belong to unrelated vertices,
            // so any filtering would blend garbage between them.
            filterMode = FilterMode.Point,
            useMipMap = false,
            autoGenerateMips = false
        };
        rt.Create();
        ClearRT(rt);
        return rt;
    }

    static void ClearRT(RenderTexture rt)
    {
        if (rt == null) return;
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, Color.clear); // zero displacement
        RenderTexture.active = prevActive;
    }

    /// <summary>Wipes all accumulated dents back to zero.</summary>
    public void ResetDents()
    {
        ClearRT(rtA);
        ClearRT(rtB);
    }

    void LateUpdate()
    {
        if (!EnsureInitialised()) return;

        CollectActiveSources();
        ComputePressDepths();

        RenderTexture src = aIsCurrent ? rtA : rtB;
        RenderTexture dst = aIsCurrent ? rtB : rtA;

        BuildDentArrays();
        BuildIslandArray();

        bool flipY = overrideFlipY ? flipYValue : SystemInfo.graphicsUVStartsAtTop;

        stampInstance.SetVectorArray(DentPosID, dentPos);
        stampInstance.SetVectorArray(DentAxisID, dentAxis);
        stampInstance.SetVectorArray(DentRightID, dentRight);
        stampInstance.SetVectorArray(DentParamsID, dentParams);
        stampInstance.SetVectorArray(DentBulgeID, dentBulge);
        stampInstance.SetVectorArray(DentDecayID, dentDecay);
        stampInstance.SetInt(DentCountID, ActiveDentCount);
        stampInstance.SetVectorArray(IslandPushID, islandPush);
        stampInstance.SetInt(IslandCountID, Mathf.Min(generator.IslandCount, MAX_ISLANDS));
        stampInstance.SetTexture(PrevTexID, src);
        stampInstance.SetFloat(DecayID, CurrentDecay);
        stampInstance.SetFloat(DecayDepthBiasID, Mathf.Max(decayDepthBias, 0f));
        stampInstance.SetFloat(FlipYID, flipY ? 1f : 0f);
        stampInstance.SetFloat(DebugStampAllID, debugStampAll ? 1f : 0f);

        cmd.Clear();
        cmd.SetRenderTarget(dst);
        // DrawMesh (not DrawRenderer) so we can substitute the points-topology mesh.
        // The matrix must be supplied explicitly here, and is also what makes
        // TransformWorldToObjectDir correct inside the stamp shader.
        cmd.DrawMesh(generator.PointMesh, targetRenderer.transform.localToWorldMatrix, stampInstance, 0, 0);
        Graphics.ExecuteCommandBuffer(cmd);

        aIsCurrent = !aIsCurrent;

        characterMaterial.SetTexture(DentTextureID, dst);
    }

    /// <summary>
    /// Narrows the scene-wide source list to the ones that can actually reach this
    /// renderer. Without this every dented object would receive every dent in the scene.
    /// </summary>
    void CollectActiveSources()
    {
        active.Clear();

        Bounds bounds = targetRenderer.bounds;

        for (int i = 0; i < allSources.Count && active.Count < MAX_DENTS; i++)
        {
            var s = allSources[i];
            if (s == null) continue;

            if (filterSourcesByBounds)
            {
                // The stamp reaches 'outer' sideways and 'depth' backwards from its face,
                // so a sphere of that radius around the face is a safe conservative test.
                float reach = s.LateralReach + Mathf.Max(s.depth, 0f) + boundsPadding;
                if (bounds.SqrDistance(s.transform.position) > reach * reach) continue;
            }

            active.Add(s);
        }
    }

    void BuildDentArrays()
    {
        int count = ActiveDentCount;

        for (int i = 0; i < count; i++)
        {
            var s = active[i];
            Vector3 p = s.transform.position;
            Vector3 axis = s.transform.forward;  // +Z is the press direction
            Vector3 right = s.transform.right;   // orients the Square cross-section

            dentPos[i]    = new Vector4(p.x, p.y, p.z, (float)s.shape);
            dentAxis[i]   = new Vector4(axis.x, axis.y, axis.z, Mathf.Max(s.depth, 0.0001f));
            dentRight[i]  = new Vector4(right.x, right.y, right.z, Mathf.Clamp01(s.flattenScale));
            // Plane has no rim fillet and no spread, so its params slots carry its second
            // rectangle extent and its edge softness instead.
            float paramW = s.shape == DentShape.Plane ? s.planeEdgeSoftness : s.EffectiveSpread;
            dentParams[i] = new Vector4(s.SafeInnerRadius, s.SafeOuterRadius, s.strength, paramW);
            dentBulge[i]  = new Vector4(s.rimBulge, Mathf.Max(s.bulgeReach, 1f),
                                        Mathf.Clamp01(s.bulgeNormalBias), DriverFor(i));
            // Plane repurposes the spare decay slots to place its rectangle relative to
            // the source. Punch shapes use one of them to size their bulge ring.
            dentDecay[i] = s.shape == DentShape.Plane
                ? new Vector4(Mathf.Max(s.decayMultiplier, 0f), s.planeOffset.x, s.planeOffset.y, 0f)
                : new Vector4(Mathf.Max(s.decayMultiplier, 0f), Mathf.Max(s.bulgeRadius, 0f),
                              Mathf.Clamp01(s.bulgeOutward), 0f);

            // Fed back purely so the Plane gizmo can draw its real splay height.
            s.lastPressDepth = DriverFor(i);
        }

        for (int i = count; i < MAX_DENTS; i++)
        {
            dentPos[i]    = Vector4.zero;
            dentAxis[i]   = Vector4.zero;
            dentRight[i]  = Vector4.zero;
            dentParams[i] = Vector4.zero;
            dentBulge[i]  = Vector4.zero;
            dentDecay[i]  = Vector4.zero;
        }
    }

    /// <summary>
    /// Evaluates the press once per mesh island so the stamp shader can move whole
    /// disconnected parts rigidly.
    ///
    /// Each island is sampled at several points and the STRONGEST push wins. A single
    /// centroid is not enough: on a toroid the centroid sits in the hole, outside the
    /// mesh, and would report no penetration while the ring itself is fully pressed.
    /// </summary>
    void BuildIslandArray()
    {
        int count = Mathf.Min(generator.IslandCount, MAX_ISLANDS);
        Transform tr = targetRenderer.transform;

        for (int i = 0; i < count; i++)
        {
            Vector3[] samples = generator.IslandSamples[i];

            Vector3 bestPushWS = Vector3.zero;
            float bestMagSq = 0f;

            for (int s = 0; s < samples.Length; s++)
            {
                // Island pushes drive whole rigid parts, where the rim bulge is not
                // meaningful, so a zero normal is passed deliberately.
                Vector3 pushWS = EvaluateDentWorld(tr.TransformPoint(samples[s]), Vector3.zero, out _);
                float magSq = pushWS.sqrMagnitude;
                if (magSq > bestMagSq)
                {
                    bestMagSq = magSq;
                    bestPushWS = pushWS;
                }
            }

            Vector3 pushOS = tr.InverseTransformDirection(bestPushWS);

            // Rigidity ramps with island size: small detail parts move as a block, large
            // ones bend to the stamp, and everything between blends rather than snapping
            // from one behaviour to the other.
            float radius = generator.IslandRadii[i];
            float sizeT = Mathf.InverseLerp(rigidBelowRadius,
                                            Mathf.Max(flexibleAboveRadius, rigidBelowRadius + 1e-4f),
                                            radius);
            float rigidity = islandRigidity * (1f - Mathf.SmoothStep(0f, 1f, sizeT));

            islandPush[i] = new Vector4(pushOS.x, pushOS.y, pushOS.z, rigidity);
        }

        for (int i = count; i < MAX_ISLANDS; i++)
            islandPush[i] = Vector4.zero;
    }

    // --------------------------------------------------------------------
    // CPU mirror of the press maths in DentStamp.hlsl.
    // Keep these two in step: if the shape logic changes there, change it here.
    // --------------------------------------------------------------------

    /// <summary>
    /// How deeply each active source is pressed into the mesh, measured across the island
    /// sample points.
    ///
    /// The bulge needs this: material displaced by a press has to go somewhere, and no
    /// single vertex can know how squashed the object is - a vertex sitting above the
    /// contact has no penetration of its own to go on.
    /// </summary>
    void ComputePressDepths()
    {
        Transform tr = targetRenderer.transform;

        for (int i = 0; i < active.Count; i++) pressDepth[i] = 0f;

        if (generator == null || generator.IslandSamples == null) return;

        int islands = Mathf.Min(generator.IslandCount, MAX_ISLANDS);

        for (int island = 0; island < islands; island++)
        {
            Vector3[] samples = generator.IslandSamples[island];
            for (int s = 0; s < samples.Length; s++)
            {
                Vector3 world = tr.TransformPoint(samples[s]);
                for (int i = 0; i < active.Count; i++)
                {
                    float pen = Penetration(active[i], world);
                    if (pen > pressDepth[i]) pressDepth[i] = pen;
                }
            }
        }
    }

    /// <summary>
    /// Bulge driver for a source: how deep it is pressed in, capped by its own clamp.
    /// Without the clamp a single long protrusion dipping deep inflates the bulge across
    /// the whole object.
    /// </summary>
    float DriverFor(int sourceIndex)
    {
        float clamp = active[sourceIndex].bulgeClamp;
        float depth = pressDepth[sourceIndex];
        return clamp > 0f ? Mathf.Min(depth, clamp) : depth;
    }

    /// <summary>How far a world point sits inside a source's contact surface.</summary>
    static float Penetration(DentSource s, Vector3 worldPos)
    {
        Vector3 axis = s.transform.forward;
        Vector3 right = s.transform.right;
        Vector3 toPoint = worldPos - s.transform.position;

        float outer = s.SafeOuterRadius;
        float axial = Vector3.Dot(toPoint, axis);

        float lat;
        if (s.shape == DentShape.Square || s.shape == DentShape.Plane)
        {
            Vector3 up = Vector3.Cross(axis, right);
            lat = Mathf.Max(Mathf.Abs(Vector3.Dot(toPoint, right)),
                            Mathf.Abs(Vector3.Dot(toPoint, up)));
        }
        else
        {
            lat = Vector3.ProjectOnPlane(toPoint, axis).magnitude;
        }

        float surfaceAxial;
        if (s.shape == DentShape.Plane)
        {
            surfaceAxial = lat <= outer ? 0f : -1e9f;
        }
        else
        {
            surfaceAxial = DentSurfaceAxial(lat, s.SafeInnerRadius, outer);
        }

        return Mathf.Clamp(surfaceAxial - axial, 0f, Mathf.Max(s.depth, 0.0001f));
    }

    Vector3 EvaluateDentWorld(Vector3 worldPos, Vector3 worldNormal, out float decayMul)
    {
        // Press is accumulated, bulge is not - see the combination notes further down.
        Vector3 pressAccum = Vector3.zero;
        Vector3 bestExtras = Vector3.zero;
        float bestExtrasMagSq = 0f;
        float deepestPress = 0f;
        decayMul = 1f;

        for (int i = 0; i < active.Count; i++)
        {
            var s = active[i];
            Vector3 axis = s.transform.forward;
            Vector3 right = s.transform.right;
            Vector3 toPoint = worldPos - s.transform.position;

            float inner = s.SafeInnerRadius;
            float outer = s.SafeOuterRadius;
            float axial = Vector3.Dot(toPoint, axis);

            // Component across the axis: the round cross-section distance, and the
            // outward direction the spread pushes along.
            Vector3 radialV = toPoint - axial * axis;
            float radial = radialV.magnitude;
            Vector3 outward = radial > 1e-5f ? radialV / radial : Vector3.zero;

            // Square and Plane use a Chebyshev cross-section; the other two are round.
            float lat;
            if (s.shape == DentShape.Square || s.shape == DentShape.Plane)
            {
                Vector3 up = Vector3.Cross(axis, right);
                lat = Mathf.Max(Mathf.Abs(Vector3.Dot(toPoint, right)),
                                Mathf.Abs(Vector3.Dot(toPoint, up)));
            }
            else
            {
                lat = radial;
            }

            // Capsule has no flat face; Plane is flat right to its edge.
            float innerEff = inner;

            // A Plane is a rectangle with independent half sizes, clamped to the collider
            // face that produced it. Without that it runs past a ledge and keeps flattening
            // the part of the mesh hanging over the drop.
            float surfaceAxial;
            float planeEdge = 1f;

            if (s.shape == DentShape.Plane)
            {
                Vector3 planeUp = Vector3.Cross(axis, right);

                float lx = Mathf.Abs(Vector3.Dot(toPoint, right) - s.planeOffset.x);
                float ly = Mathf.Abs(Vector3.Dot(toPoint, planeUp) - s.planeOffset.y);

                float halfX = inner;
                float halfY = outer;

                surfaceAxial = (lx <= halfX && ly <= halfY) ? 0f : -1e9f;

                float soft = Mathf.Max(Mathf.Clamp01(s.planeEdgeSoftness), 0.001f);
                float sx = 1f - SmoothStep01(halfX * (1f - soft), halfX, lx);
                float sy = 1f - SmoothStep01(halfY * (1f - soft), halfY, ly);
                planeEdge = sx * sy;
            }
            else
            {
                surfaceAxial = DentSurfaceAxial(lat, innerEff, outer);
            }

            float penetration = Mathf.Clamp(surfaceAxial - axial, 0f, Mathf.Max(s.depth, 0.0001f));
            float push = penetration * (1f - Mathf.Clamp01(s.flattenScale)) * planeEdge;

            // Sideways spread, inside the contact, peaking around the inner radius.
            float peak = Mathf.Max(innerEff, outer * 0.15f);
            float rampIn = SmoothStep01(0f, peak, lat);
            float rampOut = 1f - SmoothStep01(peak, outer, lat);
            float bulge = push * s.EffectiveSpread * rampIn * rampOut;

            // Rim bulge / splay.
            float rim;
            Vector3 rimDir;

            if (s.shape == DentShape.Plane)
            {
                // Resting on a hard surface: the squashed volume splays outward just above
                // the plane and never crosses it.
                float driver = DriverFor(i);
                float height = Mathf.Max(driver * Mathf.Max(s.bulgeReach, 1f), 1e-5f);
                float above = axial > 0f ? 1f - SmoothStep01(0f, height, axial) : 0f;

                rim = driver * s.rimBulge * above * planeEdge;
                rimDir = outward;
            }
            else
            {
                // A punch: the pile straddles the contact plane rather than sitting only
                // on the approach side.
                float driver = DriverFor(i);
                float ringRadius = s.bulgeRadius > 1e-5f ? s.bulgeRadius : outer;
                float reach = Mathf.Max(s.bulgeReach, 1f);
                float axialProf = 1f - SmoothStep01(0f, Mathf.Max(driver, 1e-5f), Mathf.Abs(axial));
                float rimIn = SmoothStep01(innerEff, ringRadius, lat);
                float rimOut = 1f - SmoothStep01(ringRadius, ringRadius * reach, lat);

                rim = driver * s.rimBulge * rimIn * rimOut * axialProf * (1f - Mathf.Clamp01(s.flattenScale));

                Vector3 punchDir = Vector3.Lerp(-axis, worldNormal, Mathf.Clamp01(s.bulgeNormalBias));
                rimDir = Vector3.Lerp(punchDir, outward, Mathf.Clamp01(s.bulgeOutward));
                if (rimDir.sqrMagnitude > 1e-8f) rimDir.Normalize();
            }

            // The PRESS is a constraint: "do not be inside this stamp". In a corner two of
            // them must BOTH be satisfied, so taking whichever is strongest would push a
            // vertex out of the wall while leaving it under the floor, and no fold appears.
            //
            // Plain summing is wrong too - two parallel stamps would stack into a
            // double-deep dent. Adding only the part not already covered along this axis
            // gives both behaviours from one rule.
            float wanted = push * s.strength;
            float already = Vector3.Dot(pressAccum, axis);
            float extra = Mathf.Max(wanted - already, 0f);

            pressAccum += axis * extra;

            // The BULGE is not a constraint, just displaced material, so strongest wins.
            Vector3 extras = (outward * bulge + rimDir * rim) * s.strength;
            float extrasMagSq = extras.sqrMagnitude;
            if (extrasMagSq > bestExtrasMagSq)
            {
                bestExtrasMagSq = extrasMagSq;
                bestExtras = extras;
            }

            if (extra > deepestPress)
            {
                deepestPress = extra;
                decayMul = Mathf.Max(s.decayMultiplier, 0f);
            }
        }

        return pressAccum + bestExtras;
    }

    /// <summary>Mirror of DentSurfaceAxial in DentStamp.hlsl.</summary>
    static float DentSurfaceAxial(float lat, float inner, float outer)
    {
        if (lat >= outer) return -1e9f;
        if (lat <= inner) return 0f;

        float r = Mathf.Max(outer - inner, 1e-5f);
        float d = lat - inner;
        return -(r - Mathf.Sqrt(Mathf.Max(r * r - d * d, 0f)));
    }

    /// <summary>HLSL-style smoothstep. Unity's Mathf.SmoothStep does something different.</summary>
    static float SmoothStep01(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-6f));
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Displacement for a point on the mesh, in OBJECT space, matching what the stamp
    /// shader computes for a vertex - including the island rigidity blend.
    ///
    /// Used by DentColliderRig so physics and visuals agree. Note this reflects the
    /// CURRENT stamps only: the accumulated/decayed history lives in the dent texture,
    /// which the CPU has no view of. Callers that need history must accumulate it
    /// themselves using the same max-with-decay rule.
    /// </summary>
    public Vector3 EvaluateDisplacementOS(Vector3 localPos, Vector3 localNormal, int islandId,
                                          out float decayMul)
    {
        decayMul = 1f;
        if (targetRenderer == null) return Vector3.zero;

        Transform tr = targetRenderer.transform;
        Vector3 worldNormal = tr.TransformDirection(localNormal).normalized;
        Vector3 perVertexOS = tr.InverseTransformDirection(
            EvaluateDentWorld(tr.TransformPoint(localPos), worldNormal, out decayMul));

        if (islandId < 0 || islandId >= MAX_ISLANDS) return perVertexOS;

        Vector4 island = islandPush[islandId];
        return Vector3.Lerp(perVertexOS,
                            new Vector3(island.x, island.y, island.z),
                            Mathf.Clamp01(island.w));
    }

    /// <summary>Same base decay factor the stamp shader uses this frame.</summary>
    public float CurrentDecay => Mathf.Pow(1f - decayPerSecond, Time.deltaTime);

    /// <summary>
    /// Effective decay for one accumulated value, matching the stamp shader: the source's
    /// own multiplier, plus extra proportional to how deep the dent is.
    /// </summary>
    public float DecayFor(float storedMultiplier, float magnitude)
    {
        float rate = Mathf.Max(storedMultiplier, 0f)
                     * (1f + Mathf.Max(decayDepthBias, 0f) * magnitude);
        return Mathf.Pow(CurrentDecay, rate);
    }

    void Release()
    {
        // Leave the character material on a known-zero texture, otherwise it keeps
        // sampling a released buffer and appears fully deformed after exiting play mode.
        if (characterMaterial != null && characterMaterial.HasProperty(DentTextureID))
            characterMaterial.SetTexture(DentTextureID, Texture2D.blackTexture);

        DestroyRT(ref rtA);
        DestroyRT(ref rtB);

        if (stampInstance != null)
        {
            if (Application.isPlaying) Destroy(stampInstance); else DestroyImmediate(stampInstance);
            stampInstance = null;
        }

        if (cmd != null) { cmd.Release(); cmd = null; }
        initialised = false;
    }

    static void DestroyRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        // Release() only frees GPU memory; the object itself would leak otherwise.
        if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
        rt = null;
    }
}
