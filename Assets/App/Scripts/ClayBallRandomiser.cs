using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Picks one child MeshRenderer to be the active variant and hides the rest.
/// Runs on Awake in play mode, and stays in sync in the editor.
/// Duplicating the object in the editor selects a new variant automatically.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ClayBallRandomiser : MonoBehaviour
{
    public enum HideMode
    {
        DeactivateGameObject,
        DisableRenderer
    }

    [Tooltip("How the unselected variants are hidden. Deactivating removes colliders and other components too.")]
    [SerializeField] HideMode _hideMode = HideMode.DeactivateGameObject;

    [Tooltip("Pick a fresh variant every time this spawns at runtime. Turn off to keep the variant chosen in the editor.")]
    [SerializeField] bool _rerollOnRuntimeAwake = true;

    [Tooltip("Stops the editor re-rolling this object. The selection survives duplication.")]
    [SerializeField] bool _lockSelection;

    [Tooltip("Index of the active child. Editable if you want to force a specific variant.")]
    [SerializeField] int _selectedIndex = -1;

    [SerializeField, HideInInspector] string _identity;
    [SerializeField, HideInInspector] int _rerollSalt;

    static readonly List<MeshRenderer> s_Variants = new List<MeshRenderer>();

    public int SelectedIndex => _selectedIndex;
    public int VariantCount => CollectVariants(s_Variants);

    void Awake()
    {
        if (Application.isPlaying)
        {
            if (_rerollOnRuntimeAwake || _selectedIndex < 0) SelectRandom();
            else Apply();
        }
#if UNITY_EDITOR
        else
        {
            DeferEditorSync();
        }
#endif
    }

    /// <summary>
    /// Picks a new variant at random and applies it. Safe to call from a spawner
    /// after Instantiate if you need to re-roll manually.
    /// </summary>
    public void SelectRandom()
    {
        int count = CollectVariants(s_Variants);
        if (count == 0)
        {
            _selectedIndex = -1;
            return;
        }

        _selectedIndex = Random.Range(0, count);
        Apply();
    }

    /// <summary>
    /// Forces a specific variant index.
    /// </summary>
    public void Select(int index)
    {
        _selectedIndex = index;
        Apply();
    }

    void Apply()
    {
        int count = CollectVariants(s_Variants);
        if (count == 0) return;

        int chosen = Mathf.Clamp(_selectedIndex, 0, count - 1);

        for (int i = 0; i < count; i++)
        {
            MeshRenderer r = s_Variants[i];
            if (r == null) continue;

            bool active = i == chosen;

            if (_hideMode == HideMode.DeactivateGameObject)
            {
                if (r.gameObject.activeSelf != active) r.gameObject.SetActive(active);
                if (active && !r.enabled) r.enabled = true;
            }
            else
            {
                if (r.enabled != active) r.enabled = active;
                if (active && !r.gameObject.activeSelf) r.gameObject.SetActive(true);
            }
        }
    }

    /// <summary>
    /// Gathers child MeshRenderers, excluding any renderer on this GameObject itself.
    /// Order is depth-first and stable, so indices stay valid unless children are reordered.
    /// </summary>
    int CollectVariants(List<MeshRenderer> results)
    {
        results.Clear();
        GetComponentsInChildren(true, results);

        for (int i = results.Count - 1; i >= 0; i--)
        {
            if (results[i].transform == transform) results.RemoveAt(i);
        }

        return results.Count;
    }

    /// <summary>
    /// FNV-1a over the identity string. Deterministic across sessions and platforms,
    /// unlike string.GetHashCode which carries no such guarantee.
    /// </summary>
    int PickDeterministic(string id, int salt, int count)
    {
        if (count <= 0) return -1;
        if (string.IsNullOrEmpty(id)) id = name;

        uint h = 2166136261u;
        for (int i = 0; i < id.Length; i++)
        {
            h ^= id[i];
            h *= 16777619u;
        }

        h ^= (uint)salt;
        h *= 16777619u;

        return (int)(h % (uint)count);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        DeferEditorSync();
    }

    void DeferEditorSync()
    {
        if (Application.isPlaying) return;
        // GlobalObjectId is not reliable during OnValidate for a freshly created
        // object, and scene mutation from OnValidate is unsupported. Defer a frame.
        EditorApplication.delayCall += EditorSync;
    }

    void EditorSync()
    {
        if (this == null) return;
        if (Application.isPlaying) return;
        if (PrefabUtility.IsPartOfPrefabAsset(this)) return;

        string id = GlobalObjectId.GetGlobalObjectIdSlow(this).ToString();
        if (string.IsNullOrEmpty(id)) return;

        bool isNewObject = id != _identity;
        int count = CollectVariants(s_Variants);

        if (isNewObject || _selectedIndex < 0)
        {
            _identity = id;

            if (!_lockSelection || _selectedIndex < 0)
                _selectedIndex = PickDeterministic(id, _rerollSalt, count);

            Apply();
            MarkDirty();
        }
        else
        {
            Apply();
        }
    }

    [ContextMenu("Reroll Variant")]
    void RerollVariant()
    {
        int count = CollectVariants(s_Variants);
        if (count == 0) return;

        Undo.RecordObject(this, "Reroll Clay Ball Variant");

        int previous = _selectedIndex;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            _rerollSalt++;
            _selectedIndex = PickDeterministic(_identity, _rerollSalt, count);
            if (count == 1 || _selectedIndex != previous) break;
        }

        Apply();
        MarkDirty();
    }

    void MarkDirty()
    {
        EditorUtility.SetDirty(this);
        if (gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }
#endif
}