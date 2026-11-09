// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Cloud.Draco
{
    [BurstCompile]
    struct DecodeVerticesJob : IJob
    {
        public NativeReference<int> result;

        // Read-only on purpose: the decoded mesh is read from the main thread while later jobs are
        // still in flight, so this job must not be seen as a writer of the resource handle.
        [ReadOnly]
        public NativeReference<DracoResources> resources;

        // Frees the decoder state. The container itself is disposed once this job completed, so no
        // later job can observe the freed pointer.
        [ReadOnly]
        public NativeReference<DracoDecoderHandle> decoderHandle;

        public void Execute()
        {
            if (result.Value < 0)
            {
                return;
            }
            result.Value = DracoInstance.DecodeDracoMeshStep2(resources.Value, decoderHandle.Value);
        }
    }
}
