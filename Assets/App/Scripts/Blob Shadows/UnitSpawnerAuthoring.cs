using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// Put this on a GameObject inside a SubScene, and assign a unit prefab.
public class UnitSpawnerAuthoring : MonoBehaviour
{
    public GameObject Prefab;
    public int Count = 1000;
    public float Radius = 30f;

    class Baker : Baker<UnitSpawnerAuthoring>
    {
        public override void Bake(UnitSpawnerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new UnitSpawner
            {
                // Converts the prefab asset into an entity prefab during baking.
                Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic),
                Count = authoring.Count,
                Radius = authoring.Radius
            });
        }
    }
}

public struct UnitSpawner : IComponentData
{
    public Entity Prefab;
    public int Count;
    public float Radius;
}

// Spawns all units once, scattered evenly in a disc, then removes the spawner.
[BurstCompile]
public partial struct UnitSpawnerSystem : ISystem
{
    public void OnCreate(ref SystemState state) => state.RequireForUpdate<UnitSpawner>();

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var spawnerEntity = SystemAPI.GetSingletonEntity<UnitSpawner>();
        var spawner = SystemAPI.GetSingleton<UnitSpawner>();
        var rng = Unity.Mathematics.Random.CreateFromIndex(1234);

        var units = state.EntityManager.Instantiate(spawner.Prefab, spawner.Count, Allocator.Temp);
        foreach (var unit in units)
        {
            float2 p = rng.NextFloat2Direction() * math.sqrt(rng.NextFloat()) * spawner.Radius;
            var t = state.EntityManager.GetComponentData<LocalTransform>(unit);
            t.Position = new float3(p.x, 0f, p.y);
            state.EntityManager.SetComponentData(unit, t);
        }

        state.EntityManager.DestroyEntity(spawnerEntity);
    }
}