// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Burst;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Draco
{
    [BurstCompile]
    struct GeneratePointCloudIndicesJob : IJob
    {
        public Mesh.MeshData mesh;

        public void Execute()
        {
            switch (mesh.indexFormat)
            {
                case IndexFormat.UInt16:
                {
                    var indices = mesh.GetIndexData<ushort>();
                    for (var i = 0; i < indices.Length; i++)
                    {
                        indices[i] = (ushort)i;
                    }
                    break;
                }
                case IndexFormat.UInt32:
                {
                    var indices = mesh.GetIndexData<uint>();
                    for (var i = 0; i < indices.Length; i++)
                    {
                        indices[i] = (uint)i;
                    }
                    break;
                }
            }
        }
    }
}
