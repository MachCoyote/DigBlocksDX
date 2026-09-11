using System;
using DigBlocks.Simulation.Definitions;
using Unity.Entities;
using UnityEngine;

namespace DigBlocks.Client.Rendering
{
    /// <summary>
    /// Marks an entity whose view has been attached, so the attach pass is a one-off per entity
    /// rather than something that runs against every ghost every frame.
    /// </summary>
    public struct EntityViewAttached : IComponentData { }

    /// <summary>
    /// Everything the client does to make a simulated entity visible, behind one seam.
    /// </summary>
    /// <remarks>
    /// Presentation is deliberately attached after spawn rather than baked into the ghost prefab.
    /// The prefab then stays simulation-only and identical in both worlds, which is the riskiest
    /// property of code-built ghosts, and nothing about rendering can perturb the collection hash.
    /// It also means replacing the renderer is one new implementation of this interface: no
    /// simulation, loading, relevancy or netcode code names a rendering type.
    /// <para>
    /// The cost is one structural change per spawned entity. Baking would have cost the same, and it
    /// happens on spawn rather than per frame, so at mob counts it is free. Entity spawn rates in the
    /// thousands per frame, such as dense item drops or particles, would want a batched attach.
    /// </para>
    /// </remarks>
    public interface IEntityPresentationBackend : IDisposable
    {
        /// <summary>Gives an entity whatever it needs to be drawn as the given model.</summary>
        void Attach(EntityManager manager, Entity entity, EntityModelDefinition model);
    }

    /// <summary>
    /// Turns a box-list entity model into a mesh.
    /// </summary>
    /// <remarks>
    /// Models are authored as boxes rather than imported geometry because that is what a blocky game
    /// wants and it keeps a dedicated server free of mesh assets. Each box contributes six quads with
    /// outward normals; named parts survive as submesh-free geometry for now, and become separately
    /// transformable when animation needs them.
    /// </remarks>
    public static class EntityModelMesh
    {
        private static readonly int[] QuadIndices = { 0, 1, 2, 0, 2, 3 };

        public static Mesh Build(EntityModelDefinition model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            int boxes = model.Boxes.Count;
            var vertices = new Vector3[boxes * 24];
            var normals = new Vector3[boxes * 24];
            var uvs = new Vector2[boxes * 24];
            var indices = new int[boxes * 36];

            int vertex = 0, index = 0;
            for (int b = 0; b < boxes; b++)
            {
                var box = model.Boxes[b];
                //model units are sixteenths of a block, matching the block model convention.
                Vector3 min = (Vector3)(box.Pivot + box.Origin) / EntityModelDefinition.UnitsPerBlock;
                Vector3 size = (Vector3)box.Size / EntityModelDefinition.UnitsPerBlock;
                Vector3 max = min + size;

                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.back,
                    new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(min.x, max.y, min.z));
                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.forward,
                    new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(max.x, max.y, max.z));
                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.left,
                    new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z));
                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.right,
                    new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, max.y, min.z));
                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.up,
                    new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z));
                AddFace(vertices, normals, uvs, indices, ref vertex, ref index, Vector3.down,
                    new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, min.y, min.z), new Vector3(min.x, min.y, min.z));
            }

            var mesh = new Mesh { name = model.Key };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddFace(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices,
            ref int vertex, ref int index, Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = vertex;
            vertices[vertex] = a; uvs[vertex] = new Vector2(0f, 0f); normals[vertex++] = normal;
            vertices[vertex] = b; uvs[vertex] = new Vector2(1f, 0f); normals[vertex++] = normal;
            vertices[vertex] = c; uvs[vertex] = new Vector2(1f, 1f); normals[vertex++] = normal;
            vertices[vertex] = d; uvs[vertex] = new Vector2(0f, 1f); normals[vertex++] = normal;
            for (int i = 0; i < QuadIndices.Length; i++) indices[index++] = start + QuadIndices[i];
        }
    }
}
