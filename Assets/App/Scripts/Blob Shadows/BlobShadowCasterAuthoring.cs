using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ECS blob shadow caster. Use on prefabs or objects inside a SubScene.
public struct BlobShadowCaster : IComponentData
{
    public float3 GroundOffset; // feet position relative to the pivot (local space)
    public float  Height;       // shadow caster height in metres
    public float  Radius;       // shadow width (world units)
    public float  Strength;     // 0..1 darkness
}

public class BlobShadowCasterAuthoring : MonoBehaviour
{
    [Tooltip("Where the feet are, relative to this object's pivot.")]
    public Vector3 GroundOffset = Vector3.zero;
    [Tooltip("How tall the shadow caster is, in metres.")]
    public float Height = 1.8f;
    public float Radius = 0.5f;
    [Range(0f, 1f)] public float Strength = 0.6f;

    void OnDrawGizmosSelected() =>
        BlobShadowGizmos.Draw(transform.TransformPoint(GroundOffset), Height, Radius);

    class Baker : Baker<BlobShadowCasterAuthoring>
    {
        public override void Bake(BlobShadowCasterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new BlobShadowCaster
            {
                GroundOffset = authoring.GroundOffset,
                Height       = authoring.Height,
                Radius       = authoring.Radius,
                Strength     = authoring.Strength
            });
        }
    }
}
