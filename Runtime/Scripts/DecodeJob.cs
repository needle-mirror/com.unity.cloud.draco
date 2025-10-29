// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Draco
{
    [BurstCompile]
    unsafe struct DecodeJob : IJob
    {
        public NativeArray<byte>.ReadOnly encodedData;

        public NativeReference<int> result;

        [WriteOnly]
        public NativeReference<DracoResources> resources;

        public void Execute()
        {
            NativeMesh* dracoMeshPtr;
            void* decoder;
            void* buffer;
            var decodeResult = DracoInstance.DecodeDracoMeshStep1(
                (byte*)encodedData.GetUnsafeReadOnlyPtr(), encodedData.Length,
                &dracoMeshPtr, &decoder, &buffer);
            result.Value = decodeResult;
            if (decodeResult < 0)
            {
                return;
            }
            resources.Value = new DracoResources(dracoMeshPtr, decoder, buffer);
            result.Value = 0;
        }
    }
}
