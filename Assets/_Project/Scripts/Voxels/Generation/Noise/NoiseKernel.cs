using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DigBlocks.Voxels.Generation
{
    /// <summary>A compiled program in the form the kernel reads: raw pointers and counts, nothing managed.</summary>
    public unsafe struct NoiseProgramData
    {
        public NoiseOp* Ops;
        public SplineKnot* Knots;
        public IntPtr* Externals;
        public int OpCount, KnotCount, ExternalCount, SlotCount, ResultSlot;
    }

    /// <summary>
    /// One evaluation's inputs and workspace. <see cref="Slots"/> holds
    /// <c>SlotCount * Count</c> floats, and <see cref="Y"/> may be null for a program that never
    /// samples it.
    /// </summary>
    public unsafe struct NoiseBatch
    {
        public float* X, Y, Z, Slots, Result;
        public int Count;
    }

    /// <summary>
    /// Runs a compiled noise program over a batch of sample points.
    /// <para>
    /// One instruction runs across the whole batch before the next begins. That makes the switch cost
    /// per sample negligible and leaves the arithmetic in shapes Burst can vectorize. It is also why
    /// operand aliasing is safe: every op reads and writes the same index in one pass and never
    /// revisits it, so the compiler is free to hand a result the slot an operand just vacated.
    /// </para>
    /// <para>
    /// This is the only implementation. The managed path calls it directly and the generation path
    /// calls it through a Burst function pointer, so the two cannot drift apart in behaviour, only in
    /// codegen, which is exactly what the agreement test checks.
    /// </para>
    /// </summary>
    [BurstCompile]
    public static unsafe class NoiseKernel
    {
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        [AOT.MonoPInvokeCallback(typeof(NoiseEvaluate))]
        public static void Evaluate(NoiseProgramData* program, NoiseBatch* batch)
        {
            int count = batch->Count;
            if (count <= 0) return;
            float* slots = batch->Slots;
            float* coordX = batch->X;
            float* coordY = batch->Y;
            float* coordZ = batch->Z;

            for (int index = 0; index < program->OpCount; index++)
            {
                NoiseOp op = program->Ops[index];
                float* destination = slots + (long)op.Dst * count;
                float* a = op.A >= 0 ? slots + (long)op.A * count : null;
                float* b = op.B >= 0 ? slots + (long)op.B * count : null;
                float* c = op.C >= 0 ? slots + (long)op.C * count : null;
                float* d = op.D >= 0 ? slots + (long)op.D * count : null;

                switch (op.Code)
                {
                    case NoiseOpCode.Const:
                        for (int i = 0; i < count; i++) destination[i] = op.P0;
                        break;
                    case NoiseOpCode.CoordX:
                        for (int i = 0; i < count; i++) destination[i] = coordX[i];
                        break;
                    case NoiseOpCode.CoordY:
                        for (int i = 0; i < count; i++) destination[i] = coordY[i];
                        break;
                    case NoiseOpCode.CoordZ:
                        for (int i = 0; i < count; i++) destination[i] = coordZ[i];
                        break;

                    case NoiseOpCode.Add:
                        for (int i = 0; i < count; i++) destination[i] = a[i] + b[i];
                        break;
                    case NoiseOpCode.Sub:
                        for (int i = 0; i < count; i++) destination[i] = a[i] - b[i];
                        break;
                    case NoiseOpCode.Mul:
                        for (int i = 0; i < count; i++) destination[i] = a[i] * b[i];
                        break;
                    case NoiseOpCode.Div:
                        for (int i = 0; i < count; i++) destination[i] = a[i] / b[i];
                        break;
                    case NoiseOpCode.Min:
                        for (int i = 0; i < count; i++) destination[i] = math.min(a[i], b[i]);
                        break;
                    case NoiseOpCode.Max:
                        for (int i = 0; i < count; i++) destination[i] = math.max(a[i], b[i]);
                        break;

                    case NoiseOpCode.Abs:
                        for (int i = 0; i < count; i++) destination[i] = math.abs(a[i]);
                        break;
                    case NoiseOpCode.Neg:
                        for (int i = 0; i < count; i++) destination[i] = -a[i];
                        break;
                    case NoiseOpCode.Floor:
                        for (int i = 0; i < count; i++) destination[i] = math.floor(a[i]);
                        break;
                    case NoiseOpCode.Sqrt:
                        for (int i = 0; i < count; i++) destination[i] = math.sqrt(a[i]);
                        break;
                    case NoiseOpCode.Clamp:
                        for (int i = 0; i < count; i++) destination[i] = math.min(math.max(a[i], op.P0), op.P1);
                        break;
                    case NoiseOpCode.Lerp:
                        for (int i = 0; i < count; i++) destination[i] = a[i] + c[i] * (b[i] - a[i]);
                        break;
                    case NoiseOpCode.Select:
                        for (int i = 0; i < count; i++) destination[i] = a[i] < b[i] ? c[i] : d[i];
                        break;

                    case NoiseOpCode.Perlin2:
                        for (int i = 0; i < count; i++)
                            destination[i] = GenNoise.Fbm2(op.Seed, a[i], c[i], op.Count, op.P0, op.P1, (FbmMode)op.Mode);
                        break;
                    case NoiseOpCode.Perlin3:
                        for (int i = 0; i < count; i++)
                            destination[i] = GenNoise.Fbm3(op.Seed, a[i], b[i], c[i], op.Count, op.P0, op.P1, (FbmMode)op.Mode);
                        break;
                    case NoiseOpCode.Value2:
                        for (int i = 0; i < count; i++)
                            destination[i] = GenNoise.FbmValue2(op.Seed, a[i], c[i], op.Count, op.P0, op.P1, (FbmMode)op.Mode);
                        break;
                    case NoiseOpCode.Value3:
                        for (int i = 0; i < count; i++)
                            destination[i] = GenNoise.FbmValue3(op.Seed, a[i], b[i], c[i], op.Count, op.P0, op.P1, (FbmMode)op.Mode);
                        break;

                    case NoiseOpCode.Spline:
                    {
                        SplineKnot* knots = program->Knots + op.Table;
                        var interpolation = (SplineInterpolation)op.Mode;
                        for (int i = 0; i < count; i++)
                            destination[i] = SampleSpline(knots, op.Count, interpolation, a[i]);
                        break;
                    }
                    case NoiseOpCode.Terrace:
                    {
                        float steps = op.P0, smoothing = op.P1;
                        for (int i = 0; i < count; i++)
                        {
                            float value = a[i];
                            float quantized = math.floor(value * steps) / steps;
                            destination[i] = quantized + smoothing * (value - quantized);
                        }
                        break;
                    }
                    case NoiseOpCode.CallExternal:
                    {
                        var external = new FunctionPointer<NoiseExternal>(program->Externals[op.Table]);
                        //a two-dimensional program has no Y plane, so an external sampling one there
                        //sees a consistent zero rather than reading past the batch.
                        if (coordY != null)
                            for (int i = 0; i < count; i++)
                                destination[i] = external.Invoke(coordX[i], coordY[i], coordZ[i], op.Seed, a[i], b[i]);
                        else
                            for (int i = 0; i < count; i++)
                                destination[i] = external.Invoke(coordX[i], 0f, coordZ[i], op.Seed, a[i], b[i]);
                        break;
                    }
                }
            }

            UnsafeUtility.MemCpy(batch->Result, slots + (long)program->ResultSlot * count, (long)count * sizeof(float));
        }

        private static float SampleSpline(SplineKnot* knots, int count, SplineInterpolation interpolation, float input)
        {
            //outside the authored domain a spline holds its end value, so terrain cannot run away where
            //the noise briefly exceeds the range the author had in mind.
            if (input <= knots[0].X) return knots[0].Y;
            int last = count - 1;
            if (input >= knots[last].X) return knots[last].Y;

            int segment = 0;
            while (segment < last - 1 && input >= knots[segment + 1].X) segment++;

            float x0 = knots[segment].X, y0 = knots[segment].Y;
            float x1 = knots[segment + 1].X, y1 = knots[segment + 1].Y;
            float width = x1 - x0;
            float t = (input - x0) / width;
            if (interpolation == SplineInterpolation.Linear) return y0 + t * (y1 - y0);

            float t2 = t * t, t3 = t2 * t;
            float basis00 = 2f * t3 - 3f * t2 + 1f;
            float basis10 = t3 - 2f * t2 + t;
            float basis01 = -2f * t3 + 3f * t2;
            float basis11 = t3 - t2;
            return basis00 * y0 + basis10 * width * knots[segment].Tangent
                 + basis01 * y1 + basis11 * width * knots[segment + 1].Tangent;
        }
    }

    /// <summary>The Burst-callable form of <see cref="NoiseKernel.Evaluate"/>.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void NoiseEvaluate(NoiseProgramData* program, NoiseBatch* batch);
}
