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
    unsafe struct GetDracoDataInterleavedJob : IJob
    {
        [ReadOnly]
        public NativeReference<int> result;

        [ReadOnly]
        public NativeReference<DracoResources> resources;

        [ReadOnly, NativeDisableUnsafePtrRestriction]
        public NativeAttribute* attribute;

        [ReadOnly]
        public int stride;

        [ReadOnly]
        public bool flip;

        [ReadOnly]
        public int componentStride;

        public Mesh.MeshData mesh;

        [ReadOnly]
        public int streamIndex;

        [ReadOnly]
        public int offset;

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
            var dst = mesh.GetVertexData<byte>(streamIndex);
            var dstPtr = ((byte*)dst.GetUnsafePtr()) + offset;
            for (var v = 0; v < dracoMesh->numVertices; v++)
            {
                UnsafeUtility.MemCpy(dstPtr + (stride * v), ((byte*)data->data) + (elementSize * v), elementSize);
            }
            DracoInstance.ReleaseDracoData(&data);
        }
    }
}
