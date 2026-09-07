using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using UnityEngine.Rendering;
using Unity.Entities.Serialization;
using Unity.Scenes;

[CreateAfter(typeof(EntitiesGraphicsSystem))]
partial class BootstrapSystem : SystemBase
{
    protected override void OnCreate()
    {
        Entity prefabSinlgeton = this.CheckedStateRef.EntityManager.CreateEntity();
        this.CheckedStateRef.EntityManager.AddComponentData(prefabSinlgeton, new PrefabSingleton { 
            chunk = BuildChunkPrefab(ref this.CheckedStateRef)
        });

        //load physics subscene
        SubsceneCache sc = Resources.Load<SubsceneCache>("SubsceneCache");
        EntitySceneReference physicsScene = sc.gamePhysicsSubscene;
        SceneSystem.LoadSceneAsync(World.Unmanaged, physicsScene);
    }

    protected override void OnUpdate()
    {
        //add update code here
    }

    public Entity BuildChunkPrefab(ref SystemState state)
    {
        Entity chunkPrefab = state.EntityManager.CreateEntity();

        int chunkSize = GenerationSettings.chunkSize;
        int blockCount = chunkSize * chunkSize * chunkSize;

        //add chunk component
        state.EntityManager.AddComponentData(chunkPrefab, new Chunk { id = 0, selfEntity = chunkPrefab, posFromWOX = 0, posFromWOY = 0, posFromWOZ = 0, chunkSize = GenerationSettings.chunkSize });

        //add chunk unrendered tag
        state.EntityManager.AddComponentData(chunkPrefab, new ChunkUnrenderedTag { });

        //add chunk col not gen tag
        state.EntityManager.AddComponentData(chunkPrefab, new ChunkColNotGenTag { });

        //add localtransform component
        state.EntityManager.AddComponentData(chunkPrefab, new LocalTransform { Position = new float3(0, 0, 0), Rotation = quaternion.identity, Scale = 1 });

        //add localtoworld component
        state.EntityManager.AddComponentData(chunkPrefab, new LocalToWorld { Value = float4x4.identity });

        //add render stuff

        //rendermeshdescription
        var desc = new RenderMeshDescription(UnityEngine.Rendering.ShadowCastingMode.TwoSided, true, MotionVectorGenerationMode.Camera, 0, 4294967295, UnityEngine.Rendering.LightProbeUsage.BlendProbes, true);

        //rendermesharray
        EntitiesGraphicsSystem egs = state.World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
        ShaderCache sc = Resources.Load<ShaderCache>("ShaderCache");
        Shader litShader = sc.unlit;
        Material material = new Material(litShader);
        Texture2D blockTexture = Resources.Load<Texture2D>("Textures/blocks");
        material.mainTexture = blockTexture;
        BatchMaterialID materialID = egs.RegisterMaterial(material);
        var renderMeshArray = new RenderMeshArray(new Material[] { material }, new Mesh[] { new Mesh() });

        //rendermeshutility add components
        RenderMeshUtility.AddComponents(chunkPrefab, state.EntityManager, desc, renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

        //add chunkmeshdata component
        int texW = blockTexture.width;
        int texH = blockTexture.height;
        state.EntityManager.AddComponentData(chunkPrefab, new ChunkMeshData { vertices = new UnsafeList<float3>(1, Allocator.Persistent), triangles = new UnsafeList<int>(1, Allocator.Persistent), texHeight = texH, texWidth = texW, materialID = materialID });

        //add chunkmeshholder component
        state.EntityManager.AddComponentData(chunkPrefab, new ChunkMeshHolder { mesh = new Mesh.MeshData() });

        return chunkPrefab;
    }
}
