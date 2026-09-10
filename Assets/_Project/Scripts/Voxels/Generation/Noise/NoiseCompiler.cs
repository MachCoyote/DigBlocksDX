using System;
using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>The managed result of compiling one expression, before it is copied into native memory.</summary>
    internal sealed class CompiledNoise
    {
        public NoiseOp[] Ops;
        public SplineKnot[] Knots;
        public string[] Externals;
        public int SlotCount, ResultSlot;
        public bool UsesY;
    }

    /// <summary>
    /// Flattens an authored expression into a linear instruction list.
    /// <para>
    /// Two things happen here that matter at runtime. Identical subexpressions collapse into one
    /// instruction, so a warp field referenced by four samplers is computed once. And slots are
    /// recycled the moment their last consumer has run, so a long program still evaluates out of a
    /// small working set rather than one buffer per node.
    /// </para>
    /// </summary>
    internal static class NoiseCompiler
    {
        //scratch is SlotCount * batch floats, so this bounds one evaluation's working set. At the
        //1024-sample batch a chunk uses, this cap is a megabyte.
        private const int MaxSlots = 256;

        private struct Node
        {
            public NoiseExpr Expr;
            public int A, B, C, D, Table, Uses;
        }

        public static CompiledNoise Compile(NoiseExpr root, GenSeed seed)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var nodes = new List<Node>();
            var byReference = new Dictionary<NoiseExpr, int>(ReferenceComparer.Instance);
            var byStructure = new Dictionary<string, int>(StringComparer.Ordinal);
            var splines = new List<Spline>();
            var splineTable = new Dictionary<Spline, int>();
            var externals = new List<string>();
            var externalTable = new Dictionary<string, int>(StringComparer.Ordinal);

            int rootIndex = Visit(root, nodes, byReference, byStructure, splines, splineTable, externals, externalTable);
            return Emit(nodes, rootIndex, splines, externals, seed);
        }

        private static int Visit(NoiseExpr expression, List<Node> nodes, Dictionary<NoiseExpr, int> byReference,
            Dictionary<string, int> byStructure, List<Spline> splines, Dictionary<Spline, int> splineTable,
            List<string> externals, Dictionary<string, int> externalTable)
        {
            if (expression == null) return -1;
            if (byReference.TryGetValue(expression, out int seen)) return seen;

            int a = Visit(expression.A, nodes, byReference, byStructure, splines, splineTable, externals, externalTable);
            int b = Visit(expression.B, nodes, byReference, byStructure, splines, splineTable, externals, externalTable);
            int c = Visit(expression.C, nodes, byReference, byStructure, splines, splineTable, externals, externalTable);
            int d = Visit(expression.D, nodes, byReference, byStructure, splines, splineTable, externals, externalTable);

            int table = -1;
            if (expression.Code == NoiseOpCode.Spline)
            {
                if (!splineTable.TryGetValue(expression.Curve, out table))
                {
                    table = splines.Count;
                    splines.Add(expression.Curve);
                    splineTable[expression.Curve] = table;
                }
            }
            else if (expression.Code == NoiseOpCode.CallExternal)
            {
                if (!externalTable.TryGetValue(expression.ExternalId, out table))
                {
                    table = externals.Count;
                    externals.Add(expression.ExternalId);
                    externalTable[expression.ExternalId] = table;
                }
            }

            string key = StructuralKey(expression, a, b, c, d, table);
            if (!byStructure.TryGetValue(key, out int index))
            {
                //operands were appended first, so a node always follows everything it reads.
                index = nodes.Count;
                nodes.Add(new Node { Expr = expression, A = a, B = b, C = c, D = d, Table = table });
                byStructure[key] = index;
            }
            byReference[expression] = index;
            return index;
        }

        private static string StructuralKey(NoiseExpr expression, int a, int b, int c, int d, int table)
        {
            var builder = new StringBuilder(64);
            builder.Append((int)expression.Code).Append('|')
                .Append(a).Append('|').Append(b).Append('|').Append(c).Append('|').Append(d).Append('|')
                //bit patterns rather than formatted text, so two constants that print alike but differ
                //in the last bit stay distinct.
                .Append(math.asint(expression.P0)).Append('|').Append(math.asint(expression.P1)).Append('|')
                .Append(expression.Count).Append('|').Append(expression.Mode).Append('|')
                .Append(expression.Name).Append('|').Append(expression.ExternalId).Append('|').Append(table);
            return builder.ToString();
        }

        private static CompiledNoise Emit(List<Node> nodes, int rootIndex, List<Spline> splines,
            List<string> externals, GenSeed seed)
        {
            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                Use(nodes, node.A); Use(nodes, node.B); Use(nodes, node.C); Use(nodes, node.D);
            }
            Use(nodes, rootIndex);

            var knots = new List<SplineKnot>();
            var splineOffsets = new int[splines.Count];
            for (int index = 0; index < splines.Count; index++)
            {
                splineOffsets[index] = knots.Count;
                knots.AddRange(splines[index].Knots);
            }

            var ops = new NoiseOp[nodes.Count];
            var slotOf = new int[nodes.Count];
            var free = new Stack<int>();
            int slotCount = 0;
            bool usesY = false;

            for (int index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var expression = node.Expr;
                if (expression.Code == NoiseOpCode.CoordY) usesY = true;

                //the destination is taken before operands are released, so it never lands on a slot
                //this instruction still has to read from a previous one.
                int destination = free.Count > 0 ? free.Pop() : slotCount++;
                if (slotCount > MaxSlots)
                    throw new InvalidOperationException($"A noise program needs more than {MaxSlots} live values.");
                slotOf[index] = destination;

                ops[index] = new NoiseOp
                {
                    Code = expression.Code,
                    Mode = expression.Code == NoiseOpCode.Spline ? (byte)splines[node.Table].Interpolation : expression.Mode,
                    Count = (ushort)(expression.Code == NoiseOpCode.Spline ? splines[node.Table].Knots.Length : expression.Count),
                    Dst = destination,
                    A = Slot(slotOf, node.A), B = Slot(slotOf, node.B),
                    C = Slot(slotOf, node.C), D = Slot(slotOf, node.D),
                    P0 = expression.P0,
                    P1 = expression.P1,
                    Seed = expression.Name == null ? 0u : seed.Derive(expression.Name).Lattice,
                    Table = expression.Code == NoiseOpCode.Spline ? splineOffsets[node.Table] : node.Table
                };

                Release(nodes, slotOf, free, node.A);
                Release(nodes, slotOf, free, node.B);
                Release(nodes, slotOf, free, node.C);
                Release(nodes, slotOf, free, node.D);
            }

            return new CompiledNoise
            {
                Ops = ops,
                Knots = knots.ToArray(),
                Externals = externals.ToArray(),
                SlotCount = math.max(1, slotCount),
                ResultSlot = slotOf[rootIndex],
                UsesY = usesY
            };
        }

        private static int Slot(int[] slotOf, int node) => node < 0 ? -1 : slotOf[node];

        private static void Use(List<Node> nodes, int index)
        {
            if (index < 0) return;
            var node = nodes[index];
            node.Uses++;
            nodes[index] = node;
        }

        private static void Release(List<Node> nodes, int[] slotOf, Stack<int> free, int index)
        {
            if (index < 0) return;
            var node = nodes[index];
            node.Uses--;
            nodes[index] = node;
            if (node.Uses == 0) free.Push(slotOf[index]);
        }

        private sealed class ReferenceComparer : IEqualityComparer<NoiseExpr>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(NoiseExpr left, NoiseExpr right) => ReferenceEquals(left, right);
            public int GetHashCode(NoiseExpr value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
