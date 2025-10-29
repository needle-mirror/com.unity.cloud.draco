// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Draco
{
    [BurstCompile]
    unsafe struct DecodeVerticesJob : IJob
    {
        public NativeReference<int> result;

        [ReadOnly]
        public NativeReference<DracoResources> resources;

        public void Execute()
        {
            if (result.Value < 0)
            {
                return;
            }
            var dracoMeshPtr = resources.Value.mesh;
            var dracoMeshPtrPtr = &dracoMeshPtr;
            var decoder = resources.Value.decoder;
            var buffer = resources.Value.buffer;
            result.Value = DracoInstance.DecodeDracoMeshStep2(dracoMeshPtrPtr, decoder, buffer);
        }
    }
}
