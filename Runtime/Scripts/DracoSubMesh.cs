// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Draco
{
    sealed class DracoSubMesh : IDisposable
    {
        public List<AttributeBase> attributes = new();
        public DracoAttribute boneWeightAttribute;
        public DracoAttribute boneIndexAttribute;
        public MeshTopology topology;
        public int indicesCount;
        public int baseVertex;
        public int vertexCount;
        public bool calculateNormals;
        public NativeArray<float3> positionMinMax;

        public int bonesPerVertex => boneIndexAttribute?.numComponents ?? 0;

        public void Init(bool calculateBounds, Allocator allocator)
        {
            positionMinMax = calculateBounds ? new NativeArray<float3>(2, allocator) : default;
        }

        public Bounds GetBounds()
        {
            var extents = (positionMinMax[1] - positionMinMax[0]) * 0.5f;
            var bounds = new Bounds { extents = extents, center = positionMinMax[0] + extents };
            return bounds;
        }

        public void DisposeAttributes()
        {
            if (attributes != null)
            {
                foreach (var attribute in attributes)
                {
                    attribute?.Dispose();
                }

                attributes = null;
            }
            boneWeightAttribute?.Dispose();
            boneIndexAttribute?.Dispose();
        }

        public void Dispose()
        {
            DisposeAttributes();
            if (positionMinMax.IsCreated)
                positionMinMax.Dispose();
        }
    }
}
