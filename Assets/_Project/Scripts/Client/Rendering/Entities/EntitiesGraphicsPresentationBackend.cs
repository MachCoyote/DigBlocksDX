using System;
using System.Collections.Generic;
using DigBlocks.Simulation.Definitions;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace DigBlocks.Client.Rendering
{
    /// <summary>
    /// Draws entities with Entities Graphics. The only implementation of
    /// <see cref="IEntityPresentationBackend"/>; replacing it is what a renderer change costs.
    /// </summary>
    /// <remarks>
    /// Meshes and materials are registered with <see cref="EntitiesGraphicsSystem"/> and referenced
    /// by their batch ids, rather than through a cached <see cref="RenderMeshArray"/>.
    /// <para>
    /// A <c>RenderMeshArray</c> is backed by a reference-counted host object that the Entities
    /// runtime destroys as soon as the last entity using it leaves the world. Caching one across a
    /// moment when no entity of that type exists therefore leaves a live-looking wrapper around a
    /// destroyed host, and the next attach dereferences it. Entities do leave: a mob that moves out
    /// of a peer's simulation distance is destroyed, and one that comes back is a new entity. Batch
    /// ids have no such lifetime, and last exactly as long as this backend does.
    /// </para>
    /// </remarks>
    public sealed class EntitiesGraphicsPresentationBackend : IEntityPresentationBackend
    {
        private readonly Dictionary<string, Registration> byModel = new Dictionary<string, Registration>();
        private readonly Color tint;
        private EntitiesGraphicsSystem graphics;
        private Material material;
        private BatchMaterialID materialId;
        private bool disposed;

        public EntitiesGraphicsPresentationBackend(Color? tint = null) => this.tint = tint ?? new Color(0.85f, 0.35f, 0.25f);

        private readonly struct Registration
        {
            public readonly Mesh Mesh;
            public readonly BatchMeshID MeshId;
            public Registration(Mesh mesh, BatchMeshID meshId) { Mesh = mesh; MeshId = meshId; }
        }

        public void Attach(EntityManager manager, Entity entity, EntityModelDefinition model)
        {
            if (disposed || model == null) return;
            graphics ??= manager.World.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            if (graphics == null) return;

            if (!byModel.TryGetValue(model.Key, out var registration))
            {
                //one mesh per model, shared by every entity of that type, so a hundred mobs of one
                //kind cost one mesh and one material rather than a hundred of each.
                var mesh = EntityModelMesh.Build(model);
                registration = new Registration(mesh, graphics.RegisterMesh(mesh));
                byModel.Add(model.Key, registration);
            }

            var description = new RenderMeshDescription(ShadowCastingMode.On, receiveShadows: true);
            RenderMeshUtility.AddComponents(entity, manager, description,
                new MaterialMeshInfo(MaterialId, registration.MeshId));
        }

        //built in code rather than authored, because a debug mob should not require an asset to exist.
        //Real entity materials belong in an appearance asset once there is more than one of them.
        private BatchMaterialID MaterialId
        {
            get
            {
                if (material != null) return materialId;
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = "DigBlocks entity (runtime)" };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                else if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
                materialId = graphics.RegisterMaterial(material);
                return materialId;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            //the graphics system may already be gone if the world is being torn down, in which case
            //its registrations went with it and only the Unity objects are ours to clean up.
            bool graphicsAlive = graphics != null && graphics.World is { IsCreated: true };

            foreach (var registration in byModel.Values)
            {
                if (graphicsAlive) graphics.UnregisterMesh(registration.MeshId);
                if (registration.Mesh != null) UnityEngine.Object.Destroy(registration.Mesh);
            }
            byModel.Clear();

            if (material != null)
            {
                if (graphicsAlive) graphics.UnregisterMaterial(materialId);
                UnityEngine.Object.Destroy(material);
                material = null;
            }
            graphics = null;
        }
    }
}
