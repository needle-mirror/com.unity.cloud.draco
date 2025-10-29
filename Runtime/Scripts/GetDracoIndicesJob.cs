// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace Draco
{
    [BurstCompile]
    unsafe struct GetDracoIndicesJob : IJob
    {
        [ReadOnly]
        public NativeReference<int> result;
        [ReadOnly]
        public NativeReference<DracoResources> resources;
        [ReadOnly]
        public bool flip;
        public Mesh.MeshData mesh;
        public int offset;
        public int length;

        public void Execute()
        {
            if (result.Value < 0)
            {
                return;
            }
            var dracoMesh = resources.Value.mesh;
            Assert.IsFalse(dracoMesh->isPointCloud);
            void* indicesPtr;

            DataType dataType;
            switch (mesh.indexFormat)
            {
                case IndexFormat.UInt16:
                {
                    var indices = mesh.GetIndexData<ushort>().GetSubArray(offset, length);
                    indicesPtr = indices.GetUnsafePtr();
                    dataType = DataType.UInt16;
                    break;
                }
                case IndexFormat.UInt32:
                {
                    var indices = mesh.GetIndexData<uint>().GetSubArray(offset, length);
                    indicesPtr = indices.GetUnsafePtr();
                    length = indices.Length;
                    dataType = DataType.UInt32;
                    break;
                }
                default:
                    result.Value = -1;
                    return;
            }
            DracoInstance.GetMeshIndices(dracoMesh, dataType, indicesPtr, length, flip);
        }
    }
}
