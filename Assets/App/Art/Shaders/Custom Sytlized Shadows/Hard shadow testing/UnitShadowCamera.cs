using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(Camera))]
public class UnitShadowCamera : MonoBehaviour
{
    public Light sun;
    public Camera viewCamera;
    public LayerMask proxyLayer;
    public int resolution = 1024;
    public float coverage = 40f;      // world-space width of the map
    public float depthRange = 100f;   // distance along the light the map covers
    public float focusDistance = 15f; // how far ahead of the view camera to centre the map
    public float depthBias = 0.05f;   // world units

    Camera cam;
    RenderTexture rt;

    static readonly int MapId = Shader.PropertyToID("_UnitShadowMap");
    static readonly int ViewId = Shader.PropertyToID("_UnitShadowView");
    static readonly int ParamsId = Shader.PropertyToID("_UnitShadowParams");
    static readonly int BiasId = Shader.PropertyToID("_UnitShadowBias");

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        rt = new RenderTexture(resolution, resolution, 16, RenderTextureFormat.RFloat)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "UnitShadowMap"
        };
        rt.Create();

        cam.orthographic = true;
        cam.orthographicSize = coverage * 0.5f;
        cam.aspect = 1f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = depthRange;
        cam.cullingMask = proxyLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.white; // 1 = no occluder
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.targetTexture = rt;
        cam.depth = viewCamera.depth - 1;  // render before the main camera

        var data = cam.GetUniversalAdditionalCameraData();
        data.renderShadows = false;
        data.renderPostProcessing = false;
        data.requiresDepthTexture = false;
        data.requiresColorTexture = false;
        data.antialiasing = AntialiasingMode.None;

        Shader.SetGlobalTexture(MapId, rt);
    }

    void OnDisable()
    {
        if (cam) cam.targetTexture = null;
        if (rt) { rt.Release(); Destroy(rt); }
        Shader.SetGlobalTexture(MapId, Texture2D.whiteTexture); // receivers fall back to lit
    }

    void LateUpdate()
    {
        Quaternion rot = sun.transform.rotation;
        Vector3 lightDir = rot * Vector3.forward;
        Vector3 focus = viewCamera.transform.position + viewCamera.transform.forward * focusDistance;

        // Snap to the texel grid in light space to stop edge shimmer
        float texel = coverage / resolution;
        Vector3 ls = Quaternion.Inverse(rot) * focus;
        ls.x = Mathf.Round(ls.x / texel) * texel;
        ls.y = Mathf.Round(ls.y / texel) * texel;
        focus = rot * ls;

        transform.SetPositionAndRotation(focus - lightDir * (depthRange * 0.5f), rot);

        float near = cam.nearClipPlane;
        float far = cam.farClipPlane;
        Shader.SetGlobalMatrix(ViewId, cam.worldToCameraMatrix);
        Shader.SetGlobalVector(ParamsId, new Vector4(1f / coverage, near, 1f / (far - near), resolution));
        Shader.SetGlobalFloat(BiasId, depthBias / (far - near));
    }
}