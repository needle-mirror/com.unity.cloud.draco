// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace Draco
{
    [BurstCompile]
    unsafe struct ReleaseDracoMeshJob : IJob
    {
        public NativeReference<DracoResources> resources;

        public void Execute()
        {
            if (resources.Value.mesh != null)
            {
                var tmp = resources.Value.mesh;
                DracoInstance.ReleaseDracoMesh(&tmp);
            }

            resources.Value = new DracoResources();
        }
    }
}
