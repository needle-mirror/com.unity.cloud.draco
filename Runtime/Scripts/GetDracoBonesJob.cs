// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using AOT;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Draco
{
    [BurstCompile]
    unsafe struct GetDracoBonesJob : IJob
    {
        delegate int GetIndexValueDelegate(IntPtr baseAddress, int index);

        // Cached function pointers
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueInt8Method;
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueUInt8Method;
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueInt16Method;
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueUInt16Method;
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueInt32Method;
        static FunctionPointer<GetIndexValueDelegate> s_GetIndexValueUInt32Method;

        public GetDracoBonesJob(
            NativeReference<int> result,
            NativeReference<DracoResources> resources,
            NativeAttribute* indicesAttribute,
            NativeAttribute* weightsAttribute,
            NativeArray<byte> bonesPerVertex,
            NativeArray<BoneWeight1> boneWeights,
            VertexAttributeFormat indexFormat
            )
        {
            m_Result = result;
            m_Resources = resources;
            m_IndicesAttribute = indicesAttribute;
            m_WeightsAttribute = weightsAttribute;
            m_BonesPerVertex = bonesPerVertex;
            m_BoneWeights = boneWeights;
            m_IndexValueConverter = GetIndexValueConverter(indexFormat);
        }

        FunctionPointer<GetIndexValueDelegate> m_IndexValueConverter;

        [ReadOnly]
        NativeReference<int> m_Result;

        [ReadOnly]
        NativeReference<DracoResources> m_Resources;

        [ReadOnly, NativeDisableUnsafePtrRestriction]
        NativeAttribute* m_IndicesAttribute;

        [ReadOnly, NativeDisableUnsafePtrRestriction]
        NativeAttribute* m_WeightsAttribute;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        NativeArray<byte> m_BonesPerVertex;

        [WriteOnly, NativeDisableContainerSafetyRestriction]
        NativeArray<BoneWeight1> m_BoneWeights;

        public void Execute()
        {
            if (m_Result.Value < 0)
            {
                return;
            }
            var dracoMesh = m_Resources.Value.mesh;

            NativeData* indicesData = null;
            if (!DracoInstance.GetAttributeData(
                dracoMesh,
                m_IndicesAttribute,
                &indicesData,
                false,
                m_IndicesAttribute->numComponents
                ))
            {
                return;
            }
            var indexSize = DracoInstance.DataTypeSize(indicesData->dataType) * m_IndicesAttribute->numComponents;

            NativeData* weightsData = null;
            if (!DracoInstance.GetAttributeData(
                    dracoMesh,
                    m_WeightsAttribute,
                    &weightsData,
                    false,
                    m_WeightsAttribute->numComponents
                ))
            {
                DracoInstance.ReleaseDracoData(&indicesData);
                return;
            }
            var weightSize = DracoInstance.DataTypeSize(weightsData->dataType) * m_WeightsAttribute->numComponents;

            for (var v = 0; v < dracoMesh->numVertices; v++)
            {
                m_BonesPerVertex[v] = (byte)m_IndicesAttribute->numComponents;
                var indicesPtr = (IntPtr)(((byte*)indicesData->data) + (indexSize * v));
                var weightsPtr = (float*)(((byte*)weightsData->data) + (weightSize * v));
                for (var b = 0; b < m_IndicesAttribute->numComponents; b++)
                {
                    m_BoneWeights[v * m_IndicesAttribute->numComponents + b] = new BoneWeight1
                    {
                        boneIndex = m_IndexValueConverter.Invoke(indicesPtr, b),
                        weight = *(weightsPtr + b)
                    };
                }
            }
            DracoInstance.ReleaseDracoData(&indicesData);
            DracoInstance.ReleaseDracoData(&weightsData);
        }

        /// <summary>
        /// Returns Burst compatible function that converts a (bone) index
        /// of type `format` into an int
        /// </summary>
        /// <param name="format">Data type of bone index</param>
        /// <returns>Burst Function Pointer to correct conversion function</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        static FunctionPointer<GetIndexValueDelegate> GetIndexValueConverter(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.UInt8:
                    if (!s_GetIndexValueUInt8Method.IsCreated)
                    {
                        s_GetIndexValueUInt8Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueUInt8);
                    }
                    return s_GetIndexValueUInt8Method;
                case VertexAttributeFormat.SInt8:
                    if (!s_GetIndexValueInt8Method.IsCreated)
                    {
                        s_GetIndexValueInt8Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueInt8);
                    }
                    return s_GetIndexValueInt8Method;
                case VertexAttributeFormat.UInt16:
                    if (!s_GetIndexValueUInt16Method.IsCreated)
                    {
                        s_GetIndexValueUInt16Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueUInt16);
                    }
                    return s_GetIndexValueUInt16Method;
                case VertexAttributeFormat.SInt16:
                    if (!s_GetIndexValueInt16Method.IsCreated)
                    {
                        s_GetIndexValueInt16Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueInt16);
                    }
                    return s_GetIndexValueInt16Method;
                case VertexAttributeFormat.UInt32:
                    if (!s_GetIndexValueUInt32Method.IsCreated)
                    {
                        s_GetIndexValueUInt32Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueUInt32);
                    }
                    return s_GetIndexValueUInt32Method;
                case VertexAttributeFormat.SInt32:
                    if (!s_GetIndexValueInt32Method.IsCreated)
                    {
                        s_GetIndexValueInt32Method = BurstCompiler.CompileFunctionPointer<GetIndexValueDelegate>(GetIndexValueInt32);
                    }
                    return s_GetIndexValueInt32Method;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueUInt8(IntPtr baseAddress, int index)
        {
            return *((byte*)baseAddress + index);
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueInt8(IntPtr baseAddress, int index)
        {
            return *(((sbyte*)baseAddress) + index);
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueUInt16(IntPtr baseAddress, int index)
        {
            return *(((ushort*)baseAddress) + index);
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueInt16(IntPtr baseAddress, int index)
        {
            return *(((short*)baseAddress) + index);
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueUInt32(IntPtr baseAddress, int index)
        {
            return (int)*(((uint*)baseAddress) + index);
        }

        [BurstCompile]
        [MonoPInvokeCallback(typeof(GetIndexValueDelegate))]
        public static int GetIndexValueInt32(IntPtr baseAddress, int index)
        {
            return *(((int*)baseAddress) + index);
        }
    }
}
