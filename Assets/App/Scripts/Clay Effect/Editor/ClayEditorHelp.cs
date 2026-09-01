using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector help for the clay components.
///
/// These exist so the setup explains itself. Several components only apply to one kind of
/// character, and a few quietly do nothing when a reference is missing - both are easy to
/// lose an afternoon to otherwise, because nothing errors, the effect simply does not
/// appear.
/// </summary>
public static class ClayEditorHelp
{
    /// <summary>Finds the controller for whatever object is being inspected.</summary>
    public static ClayCharacterController FindController(Component component)
    {
        if (component == null) return null;

        var controller = component.GetComponentInParent<ClayCharacterController>();
        if (controller != null) return controller;

        // The controller often sits above the body while these live on or below it, so the
        // shared root is the reliable place to look.
        return component.transform.root.GetComponentInChildren<ClayCharacterController>();
    }

    /// <summary>
    /// Warns when a component only applies to blobs and the character is not one.
    /// Returns true when the component is inert.
    /// </summary>
    public static bool BlobOnly(Component component, string what)
    {
        var controller = FindController(component);
        if (controller == null) return false;

        if (controller.Kind == ClayCharacterKind.Blob) return false;

        EditorGUILayout.HelpBox(
            $"Inactive: the character is set to Generic, and {what} only applies to a Blob.\n\n" +
            "It scales the whole mesh about a single point, which on a jointed character " +
            "squashes the head into the feet. The controller disables this component at " +
            "runtime.\n\n" +
            "Denting is unaffected and still works on any shape.",
            MessageType.Warning);

        return true;
    }

    /// <summary>Explains what a component is for, above its fields.</summary>
    public static void Purpose(string text)
    {
        EditorGUILayout.HelpBox(text, MessageType.Info);
        EditorGUILayout.Space(2);
    }

    /// <summary>Flags a reference that is required but empty.</summary>
    public static void RequireReference(Object value, string name, string consequence)
    {
        if (value != null) return;

        EditorGUILayout.HelpBox($"{name} is not assigned. {consequence}", MessageType.Warning);
    }
}

[CustomEditor(typeof(DentContactSource))]
public class DentContactSourceEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var source = (DentContactSource)target;

        ClayEditorHelp.Purpose(
            "Finds the surfaces the character is touching and turns them into dent stamps.\n\n" +
            "This is the input to the whole dent effect: it probes outward, works out how " +
            "far the visible mesh overlaps each surface, and drives a pooled DentSource per " +
            "contact. DentManager then stamps those into the dent map.\n\n" +
            "Works on any character shape, blob or humanoid.");

        if (source.probeOrigins.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Probing from a single centre.\n\n" +
                "Right for a blob. On a jointed character an arm sits behind the torso, so " +
                "no ray from the middle reaches it and it never dents - list a collider per " +
                "limb under Probe Origins to give each its own origin.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox(
                $"Probing from {source.probeOrigins.Count} origin(s).\n\n" +
                "Each is read for its world bounds every frame, so origins parented under " +
                "bones follow the animation. Contacts from all of them merge together and " +
                "compete for the same source slots.",
                MessageType.None);
        }

        if (source.heightFieldLayers.value == 0)
        {
            EditorGUILayout.HelpBox(
                "Height Field Layers is empty.\n\n" +
                "If a ClayHeightFieldSampler is describing the ground, set this to the same " +
                "layers - otherwise that ground is described twice, once as a height field " +
                "and once as plane stamps, and the two disagree wherever they meet.",
                MessageType.None);
        }

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(SquashStretch))]
public class SquashStretchEditor : Editor
{
    public override void OnInspectorGUI()
    {
        ClayEditorHelp.Purpose(
            "Volume-preserving squash and stretch, driven by a spring.\n\n" +
            "The controller drives it: a held crouch while charging a jump, an impulse on " +
            "launch, and another on landing scaled by impact speed. Stretching along an axis " +
            "thins the mesh across it, so the character neither gains nor loses mass.\n\n" +
            "Applies to both character kinds. It scales the whole mesh about one pivot, which " +
            "is what a landing should do to a clay character whether it is round or not.");

        var squash = (SquashStretch)target;
        var controller = ClayEditorHelp.FindController(squash);

        if (controller != null && controller.Kind != ClayCharacterKind.Blob
            && !squash.pivotFromBodyColliders)
        {
            EditorGUILayout.HelpBox(
                "Pivot From Body Colliders is off on a Generic character.\n\n" +
                "There is no generated sphere to measure, so the pivot falls back to a fixed " +
                "local point and the squash will not sit on the ground. The controller turns " +
                "this on at runtime, but it is clearer set here.",
                MessageType.Warning);
        }

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(ClayShapeMorph))]
public class ClayShapeMorphEditor : Editor
{
    public override void OnInspectorGUI()
    {
        if (ClayEditorHelp.BlobOnly((ClayShapeMorph)target, "shape morphing"))
        {
            DrawDefaultInspector();
            return;
        }

        ClayEditorHelp.Purpose(
            "Morphs the character between whole-body shapes - pancake, plank, cone and so on.\n\n" +
            "Each shape is a superellipsoid described by a few numbers rather than a sculpted " +
            "mesh, so it needs no morph targets and works on any topology. The deformation is " +
            "a spatial warp, which is what keeps detail like eyes and hats in place relative " +
            "to the body instead of teleporting them onto the new surface.\n\n" +
            "The axis is captured when the shape forms, so it holds the orientation it was " +
            "made at and turns with the character afterwards.\n\n" +
            "BLOB ONLY.");

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(ClayShapeColliders))]
public class ClayShapeCollidersEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var colliders = (ClayShapeColliders)target;

        if (ClayEditorHelp.BlobOnly(colliders, "generated shape colliders"))
        {
            EditorGUILayout.HelpBox(
                "A generic character's colliders are authored by hand, usually under bones. " +
                "Nothing here is generated for one.",
                MessageType.None);

            DrawDefaultInspector();
            return;
        }

        ClayEditorHelp.Purpose(
            "Builds the character's collider out of primitives matching its current shape, " +
            "and answers questions about where its surface is.\n\n" +
            "This is the authority the rest of the system asks: dent reach, pickup range and " +
            "contact springs all measure against these colliders rather than reconstructing " +
            "the shape themselves. Real geometry cannot disagree with itself.\n\n" +
            "It also takes over every other collider on the body, so a leftover sphere cannot " +
            "quietly hold a flat shape off the ground.\n\n" +
            "BLOB ONLY.");

        ClayEditorHelp.RequireReference(colliders.controller, "Controller",
            "The controller's own sphere then stays enabled alongside these, and being larger " +
            "it wins every contact - so nothing here appears to do anything.");

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(BlobMerger))]
public class BlobMergerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var merger = (BlobMerger)target;

        if (ClayEditorHelp.BlobOnly(merger, "absorbing blobs"))
        {
            DrawDefaultInspector();
            return;
        }

        ClayEditorHelp.Purpose(
            "Absorbs ClayBlob objects on contact, and throws them back out on left click.\n\n" +
            "An absorbed blob is reparented onto the character's body, so its colliders join " +
            "that body and its mass folds in. The assembly's pivot, rolling radius and ground " +
            "reach are all recomputed as it grows.\n\n" +
            "BLOB ONLY.");

        ClayEditorHelp.RequireReference(merger.shapeColliders, "Shape Colliders",
            "Pickup then measures against a plain sphere instead of the real shape, so blobs " +
            "attach at the wrong distance on anything flat.");

        ClayEditorHelp.RequireReference(merger.ownContactSource, "Own Contact Source",
            "Absorbed blobs are then never told about each other, so they will not deform " +
            "against the character or their siblings.");

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(ClayHeightFieldSampler))]
public class ClayHeightFieldSamplerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var sampler = (ClayHeightFieldSampler)target;

        ClayEditorHelp.Purpose(
            "Samples the ground beneath the character into a small height map for the dent " +
            "shader to press against.\n\n" +
            "Terrain genuinely is a height field, so sampling it as one is exact. Describing " +
            "the same ground with a handful of plane stamps is not: they disagree where they " +
            "meet, and which of them exist changes as the character moves, so the seams " +
            "travel and rolling over terrain looks messy.\n\n" +
            "It cannot represent a wall, an overhang or a ceiling - a height map is a " +
            "function of x and z only. Those keep the ordinary contact path, which is why " +
            "both run together.\n\n" +
            "Works on any character shape.");

        EditorGUILayout.HelpBox(
            "Keep the settings under Matching The Contact Path in step with " +
            "DentContactSource. If they drift, terrain will dent harder or fade slower than " +
            "every other surface for the same contact - which reads as a bug in the terrain " +
            "rather than as two numbers disagreeing.",
            MessageType.None);

        DrawDefaultInspector();
    }
}

[CustomEditor(typeof(ClayCharacterController))]
public class ClayCharacterControllerEditor : Editor
{
    // Fields that only mean anything for a rolling blob. Hidden rather than greyed out for a
    // Generic character: a dozen inert rolling settings is the sort of thing that gets tuned
    // for an hour before anyone notices they do nothing.
    //
    // Squash and stretch is NOT here. It applies to both - a Generic character just finds its
    // pivot from the authored colliders instead of a generated sphere.
    static readonly string[] BlobOnlyFields =
    {
        "rollTorque", "brakeTorque", "tractionLimit", "spinRadius", "slipAssist",
        "matchSpinToTravel", "restAngularThreshold", "restLinearThreshold",
        "innerRadius", "outerRadius", "manageColliders", "innerMaterial", "outerMaterial",
        "shape", "shapeMorph"
    };

    // The mirror image: only used when walking.
    static readonly string[] GenericOnlyFields =
    {
        "walkAcceleration", "walkBraking", "turnSpeed"
    };

    public override void OnInspectorGUI()
    {
        var controller = (ClayCharacterController)target;
        bool blob = controller.Kind == ClayCharacterKind.Blob;

        ClayEditorHelp.Purpose(
            "Movement, jumping and ground detection for a clay character.\n\n" +
            "Character Kind decides which of the clay features apply, and which settings " +
            "below are shown.");

        if (blob)
        {
            EditorGUILayout.HelpBox(
                "Blob mode.\n\n" +
                "Rolls with torque against friction. Squash and stretch, shape morphing and " +
                "blob absorption all apply, and the inner and outer colliders are generated " +
                "from the settings below.",
                MessageType.Info);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Generic mode.\n\n" +
                "Walks rather than rolls. SquashStretch, ClayShapeMorph, ClayShapeColliders " +
                "and BlobMerger are disabled at runtime, and their settings are hidden here.\n\n" +
                "COLLIDERS ARE NOT GENERATED. Author them yourself, anywhere at or below the " +
                "Position Body - a collider with no Rigidbody of its own belongs to the " +
                "nearest one above it, so colliders parented under bones join this body " +
                "automatically and follow the animation.\n\n" +
                "Then list those same colliders under Probe Origins on DentContactSource, or " +
                "everything probes from one centre and a limb behind the torso never dents.",
                MessageType.Info);
        }

        serializedObject.Update();

        var property = serializedObject.GetIterator();
        bool enterChildren = true;

        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;

            if (property.propertyPath == "m_Script") continue;

            bool hide = blob ? System.Array.IndexOf(GenericOnlyFields, property.name) >= 0
                             : System.Array.IndexOf(BlobOnlyFields, property.name) >= 0;

            if (hide) continue;

            EditorGUILayout.PropertyField(property, true);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
