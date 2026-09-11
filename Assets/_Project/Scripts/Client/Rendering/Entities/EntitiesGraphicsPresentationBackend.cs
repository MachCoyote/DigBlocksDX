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
    public sealed class EntitiesGraphicsPresentationBackend : IEntityPresentationBackend
    {
        private readonly Dictionary<string, RenderMeshArray> byModel = new Dictionary<string, RenderMeshArray>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly Color tint;
        private Material material;
        private bool disposed;

        public EntitiesGraphicsPresentationBackend(Color? tint = null) => this.tint = tint ?? new Color(0.85f, 0.35f, 0.25f);

        public void Attach(EntityManager manager, Entity entity, EntityModelDefinition model)
        {
            if (disposed || model == null) return;
            if (!byModel.TryGetValue(model.Key, out var meshArray))
            {
                var mesh = EntityModelMesh.Build(model);
                meshes.Add(mesh);
                //one array per model, shared by every entity of that type, so a hundred mobs of one
                //kind cost one mesh and one material rather than a hundred of each.
                meshArray = new RenderMeshArray(new[] { Material }, new[] { mesh });
                byModel.Add(model.Key, meshArray);
            }

            var description = new RenderMeshDescription(ShadowCastingMode.On, receiveShadows: true);
            RenderMeshUtility.AddComponents(entity, manager, description, meshArray,
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
        }

        //built in code rather than authored, because a debug mob should not require an asset to exist.
        //Real entity materials belong in an appearance asset once there is more than one of them.
        private Material Material
        {
            get
            {
                if (material != null) return material;
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { name = "DigBlocks entity (runtime)" };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
                else if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
                return material;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            byModel.Clear();
            foreach (var mesh in meshes) Object.Destroy(mesh);
            meshes.Clear();
            if (material != null) Object.Destroy(material);
            material = null;
        }
    }
}
