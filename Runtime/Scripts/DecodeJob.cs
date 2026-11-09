// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Cloud.Draco
{
    [BurstCompile]
    struct DecodeJob : IJob
    {
        public NativeArray<byte>.ReadOnly encodedData;

        public NativeReference<int> result;

        [WriteOnly]
        public NativeReference<DracoResources> resources;

        [WriteOnly]
        public NativeReference<DracoDecoderHandle> decoderHandle;

        public void Execute()
        {
            var decodeResult = DracoInstance.DecodeDracoMeshStep1(encodedData, out var decoded, out var decoder);
            result.Value = decodeResult;
            if (decodeResult < 0)
            {
                return;
            }
            resources.Value = decoded;
            decoderHandle.Value = decoder;
            result.Value = 0;
        }
    }
}
