// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Draco
{
    [BurstCompile]
    unsafe struct GetDracoDataInterleavedBoundsJob : IJob
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

        public NativeArray<float3> bounds;

        public void Execute()
        {
            if (result.Value < 0)
            {
                return;
            }
            var dracoMesh = resources.Value.mesh;
            NativeData* data = null;
            if (!DracoInstance.GetAttributeData(dracoMesh, attribute, &data, flip, componentStride))
            {
                return;
            }
            var elementSize = DracoInstance.DataTypeSize(data->dataType) * componentStride;
            var dst = mesh.GetVertexData<byte>(streamIndex);
            var dstPtr = ((byte*)dst.GetUnsafePtr()) + offset;
            for (var v = 0; v < dracoMesh->numVertices; v++)
            {
                var value = *(float3*)((byte*)data->data + elementSize * v);
                bounds[0] = math.min(bounds[0], value);
                bounds[1] = math.max(bounds[1], value);
                *((float3*)(dstPtr + stride * v)) = value;
            }
            DracoInstance.ReleaseDracoData(&data);
        }
    }
}
