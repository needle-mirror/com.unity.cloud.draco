// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Cloud.Draco
{
    [BurstCompile]
    struct ReleaseDracoMeshJob : IJob
    {
        public NativeReference<DracoResources> resources;

        public void Execute()
        {
            var decoded = resources.Value;
            DracoInstance.ReleaseDracoMesh(ref decoded);
            resources.Value = new DracoResources();
        }
    }
}
