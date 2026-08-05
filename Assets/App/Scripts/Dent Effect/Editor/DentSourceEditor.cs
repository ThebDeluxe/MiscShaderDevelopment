using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DentSource))]
[CanEditMultipleObjects]
public class DentSourceEditor : Editor
{
    // Session-persistent so the sections stay where you left them while working.
    static bool showShape = true;
    static bool showFlatten = true;
    static bool showBulge = true;
    static bool showDecay = true;

    SerializedProperty shape, innerRadius, outerRadius, depth;
    SerializedProperty flattenScale, strength, spreadAmount;
    SerializedProperty rimBulge, bulgeReach, bulgeNormalBias, bulgeClamp;
    SerializedProperty decayMultiplier;

    void OnEnable()
    {
        shape           = serializedObject.FindProperty(nameof(DentSource.shape));
        innerRadius     = serializedObject.FindProperty(nameof(DentSource.innerRadius));
        outerRadius     = serializedObject.FindProperty(nameof(DentSource.outerRadius));
        depth           = serializedObject.FindProperty(nameof(DentSource.depth));

        flattenScale    = serializedObject.FindProperty(nameof(DentSource.flattenScale));
        strength        = serializedObject.FindProperty(nameof(DentSource.strength));
        spreadAmount    = serializedObject.FindProperty(nameof(DentSource.spreadAmount));

        rimBulge        = serializedObject.FindProperty(nameof(DentSource.rimBulge));
        bulgeReach      = serializedObject.FindProperty(nameof(DentSource.bulgeReach));
        bulgeNormalBias = serializedObject.FindProperty(nameof(DentSource.bulgeNormalBias));
        bulgeClamp      = serializedObject.FindProperty(nameof(DentSource.bulgeClamp));
        decayMultiplier = serializedObject.FindProperty(nameof(DentSource.decayMultiplier));
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        bool isPlane = !shape.hasMultipleDifferentValues
                       && (DentShape)shape.enumValueIndex == DentShape.Plane;

        DrawShapeSection(isPlane);
        DrawFlatteningSection(isPlane);
        DrawBulgeSection(isPlane);
        DrawDecaySection();

        serializedObject.ApplyModifiedProperties();
    }

    void DrawDecaySection()
    {
        showDecay = EditorGUILayout.BeginFoldoutHeaderGroup(showDecay, "Decay");
        if (showDecay)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(decayMultiplier);
            EditorGUILayout.HelpBox(
                "Scales DentManager's decay rate for dents this stamp creates. The rate is " +
                "stored per texel, so it keeps applying after the stamp has moved away.",
                MessageType.None);
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    void DrawShapeSection(bool isPlane)
    {
        showShape = EditorGUILayout.BeginFoldoutHeaderGroup(showShape, "Shape");
        if (showShape)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(shape);

            if (isPlane)
            {
                EditorGUILayout.HelpBox(
                    "A hard surface the object rests on, not a punch. Everything below it " +
                    "conforms to it and the displaced volume splays sideways.\n\n" +
                    "Point +Z into the object it supports. The two sizes are independent " +
                    "rectangle extents, so the surface can be clamped to a real collider face.",
                    MessageType.None);

                EditorGUILayout.PropertyField(innerRadius, new GUIContent(
                    "Size X", "Half extent along the source's local +X."));
                EditorGUILayout.PropertyField(outerRadius, new GUIContent(
                    "Size Y", "Half extent along the source's local +Y."));
            }
            else
            {
                using (new EditorGUI.DisabledScope(IsCapsule()))
                    EditorGUILayout.PropertyField(innerRadius, new GUIContent(
                        "Inner Radius",
                        "Half-width of the flat contact face. Capsule has no flat face."));

                EditorGUILayout.PropertyField(outerRadius, new GUIContent("Outer Radius"));
            }

            EditorGUILayout.PropertyField(depth);
            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    void DrawFlatteningSection(bool isPlane)
    {
        showFlatten = EditorGUILayout.BeginFoldoutHeaderGroup(showFlatten, "Flattening");
        if (showFlatten)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(flattenScale);
            EditorGUILayout.PropertyField(strength);

            // Spread has no meaning for a Plane, whose displaced volume is handled by the
            // splay instead, so it is simply absent rather than shown and explained away.
            if (!isPlane) EditorGUILayout.PropertyField(spreadAmount);

            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    void DrawBulgeSection(bool isPlane)
    {
        showBulge = EditorGUILayout.BeginFoldoutHeaderGroup(showBulge, "Bulge");
        if (showBulge)
        {
            EditorGUI.indentLevel++;

            if (isPlane)
            {
                EditorGUILayout.HelpBox(
                    "Plane splay: the squashed volume moves OUTWARD, staying above the " +
                    "surface. Scales automatically with how deep the object is pressed in.",
                    MessageType.None);

                EditorGUILayout.PropertyField(rimBulge, new GUIContent(
                    "Splay Amount",
                    "How far the squashed volume spreads sideways."));

                EditorGUILayout.PropertyField(bulgeReach, new GUIContent(
                    "Splay Height",
                    "How far above the surface the splay reaches, as a multiple of the " +
                    "press depth."));

                EditorGUILayout.PropertyField(bulgeClamp, new GUIContent(
                    "Press Depth Clamp",
                    "Caps how deep a press can drive the splay, so a long protrusion " +
                    "dipping deep cannot inflate it. 0 = no clamp."));
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Punch bulge: material piles up in a ring around the contact, centred " +
                    "on the contact plane so it straddles both sides.",
                    MessageType.None);

                EditorGUILayout.PropertyField(rimBulge, new GUIContent("Rim Bulge"));
                EditorGUILayout.PropertyField(bulgeReach, new GUIContent(
                    "Bulge Reach",
                    "How far past the outer radius the ring reaches, as a multiple of it."));
                EditorGUILayout.PropertyField(bulgeNormalBias);
                EditorGUILayout.PropertyField(bulgeClamp, new GUIContent(
                    "Press Depth Clamp",
                    "Caps how deep a press can drive the bulge, so a long protrusion " +
                    "dipping deep cannot inflate it. 0 = no clamp."));
            }

            EditorGUI.indentLevel--;
        }
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    bool IsCapsule() =>
        !shape.hasMultipleDifferentValues
        && (DentShape)shape.enumValueIndex == DentShape.Capsule;
}
