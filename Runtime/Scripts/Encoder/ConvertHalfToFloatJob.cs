// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Encode
{
    /// <summary>
    /// Converts strided Float16 vectors to Float32 vectors with a (potentially larger) output stride.
    /// </summary>
    [BurstCompile]
    unsafe struct ConvertHalfToFloatJob : IJobFor
    {
        [NativeDisableUnsafePtrRestriction]
        public byte* input;
        public int inputStride;

        [NativeDisableUnsafePtrRestriction]
        public byte* output;
        public int outputStride;

        public int dimension;

        public void Execute(int index)
        {
            var src = (ushort*)(input + index * inputStride);
            var dst = (float*)(output + index * outputStride);
            for (var c = 0; c < dimension; c++)
            {
                dst[c] = math.f16tof32(src[c]);
            }
        }
    }
}
