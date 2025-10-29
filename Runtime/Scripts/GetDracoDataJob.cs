// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Draco
{
    [BurstCompile]
    unsafe struct GetDracoDataJob : IJob
    {
        [ReadOnly]
        public NativeReference<int> result;

        [ReadOnly]
        public NativeReference<DracoResources> resources;

        [ReadOnly, NativeDisableUnsafePtrRestriction]
        public NativeAttribute* attribute;

        [ReadOnly]
        public bool flip;

        [ReadOnly]
        public int componentStride;

        public Mesh.MeshData mesh;
        [ReadOnly]
        public int streamIndex;

        [ReadOnly]
        public int baseVertex;

        public void Execute()
        {
            if (result.Value < 0)
            {
                return;
            }
            var dracoMesh = resources.Value.mesh;
            NativeData* data = null;
            if (!DracoInstance.GetAttributeData(
                    dracoMesh,
                    attribute,
                    &data,
                    flip,
                    componentStride
                ))
            {
                return;
            }
            var elementSize = DracoInstance.DataTypeSize(data->dataType) * componentStride;
            var size = elementSize * dracoMesh->numVertices;
            var dst = mesh.GetVertexData<byte>(streamIndex)
                .GetSubArray(baseVertex * elementSize, dracoMesh->numVertices * elementSize);
            var dstPtr = dst.GetUnsafePtr();
            UnsafeUtility.MemCpy(dstPtr, (void*)data->data, size);
            DracoInstance.ReleaseDracoData(&data);
        }
    }
}
