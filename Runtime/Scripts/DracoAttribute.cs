// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Draco
{
    sealed class DracoAttribute : AttributeBase
    {
        public DracoAttributeInstance DracoAttributeInstance;

        readonly bool m_ConvertSpace;

        public DracoAttribute(
            DracoAttributeInstance dracoAttributeInstance,
            VertexAttribute attribute,
            VertexAttributeFormat format,
            bool convertSpace
            ) : base(attribute, format)
        {
            this.DracoAttributeInstance = dracoAttributeInstance;
            m_ConvertSpace = convertSpace;
        }

        /// <summary>
        /// Unity specifies that attribute data size must be divisible by 4.
        /// This value may contain an additional pad to meet this requirement.
        /// </summary>
        public override int numComponents
        {
            get
            {
                var dataTypeSize = DracoInstance.DataTypeSize(DracoAttributeInstance.Value.dataType);
                var dracoElemSize = dataTypeSize * DracoAttributeInstance.Value.numComponents;

                if (dracoElemSize % 4 == 0)
                {
                    return DracoAttributeInstance.Value.numComponents;
                }

                // Pad such that element size is divisible by 4.
                var padBytes = 4 - dracoElemSize % 4;
                var padComponents = padBytes / dataTypeSize;
                return DracoAttributeInstance.Value.numComponents + padComponents;
            }
        }

        public JobHandle GetDracoData(
            Mesh.MeshData mesh,
            int baseVertex,
            DracoInstance dracoInstance,
            int stream,
            int offset,
            int stride,
            NativeArray<float3> bounds,
            JobHandle dependsOn
            )
        {
            if (stride > 0)
            {
                return dracoInstance.GetDracoDataInterleaved(
                    mesh,
                    DracoAttributeInstance,
                    stream,
                    offset,
                    baseVertex,
                    stride,
                    numComponents,
                    m_ConvertSpace,
                    bounds,
                    dependsOn
                );
            }

            return dracoInstance.GetDracoData(
                mesh,
                DracoAttributeInstance,
                stream,
                baseVertex,
                numComponents,
                m_ConvertSpace,
                bounds,
                dependsOn
            );
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DracoAttributeInstance.Dispose();
            }
        }
    }
}
