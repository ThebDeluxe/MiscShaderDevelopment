using Unity.Entities;
using UnityEngine;

// Add to any unit prefab that should cast a blob shadow.
public struct BlobShadowCaster : IComponentData
{
    public float Radius;   // world units
    public float Strength; // 0..1 darkness
}

public class BlobShadowCasterAuthoring : MonoBehaviour
{
    public float Radius = 0.5f;
    [Range(0f, 1f)] public float Strength = 0.6f;

    class Baker : Baker<BlobShadowCasterAuthoring>
    {
        public override void Bake(BlobShadowCasterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new BlobShadowCaster
            {
                Radius = authoring.Radius,
                Strength = authoring.Strength
            });
        }
    }
}
