using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class DentSource : MonoBehaviour
{
    public float radius = 0.2f;
    public float intensity = 1f;

    void OnEnable() => DentManager.Register(this);
    void OnDisable() => DentManager.Unregister(this);

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.5f, 0f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius);

#if UNITY_EDITOR
        Vector3 labelPos = transform.position + Vector3.right * radius;
        Handles.Label(labelPos, intensity.ToString("0.00"));
#endif
    }
}