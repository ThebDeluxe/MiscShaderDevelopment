using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DentColliderRig))]
public class DentColliderRigEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var rig = (DentColliderRig)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bake", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox(
            rig.SphereCount > 0
                ? $"{rig.SphereCount} sphere(s) baked. Select the object to preview them as gizmos."
                : "No spheres baked yet. Bake, then tune the settings above and re-bake until the " +
                  "packing looks right.",
            rig.SphereCount > 0 ? MessageType.Info : MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Bake Collider Spheres", GUILayout.Height(28)))
            {
                Undo.RecordObject(rig, "Bake Collider Spheres");
                rig.Bake();
                EditorUtility.SetDirty(rig);
            }

            using (new EditorGUI.DisabledScope(rig.SphereCount == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Height(28), GUILayout.Width(80)))
                {
                    Undo.RecordObject(rig, "Clear Collider Bake");
                    rig.ClearBake();
                    EditorUtility.SetDirty(rig);
                }
            }
        }

        if (Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Re-baking during play mode will not rebuild the live colliders - they are " +
                "created on Start. Exit play mode to re-bake properly.",
                MessageType.None);
        }
    }
}
