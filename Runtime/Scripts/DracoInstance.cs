// SPDX-FileCopyrightText: 2023 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("Draco.Encode")]

namespace Draco
{
    [BurstCompile]
    class DracoInstance
    {

#if !UNITY_EDITOR && (UNITY_WEBGL || UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS)
        internal const string k_DracoUnityLib = "__Internal";
#elif UNITY_ANDROID || UNITY_STANDALONE || UNITY_WSA || UNITY_EDITOR || PLATFORM_LUMIN || PLATFORM_EMBEDDED_LINUX
        internal const string k_DracoUnityLib = "draco_unity";
#else
        // Unsupported platform
        internal const string k_DracoUnityLib = "UnsupportedPlatform";
#endif

        public unsafe int numVertices => m_Resources.Value.mesh->numVertices;
        public unsafe bool isPointCloud => m_Resources.Value.mesh->isPointCloud;
        public unsafe int numFaces => m_Resources.Value.mesh->numFaces;

        NativeReference<int> m_DecodeResult;
        NativeReference<DracoResources> m_Resources;

        unsafe DracoInstance(NativeReference<DracoResources> resources)
        {
            Assert.AreNotEqual(IntPtr.Zero, (IntPtr)resources.Value.mesh);
            Assert.AreNotEqual(IntPtr.Zero, (IntPtr)resources.Value.decoder);
            Assert.AreNotEqual(IntPtr.Zero, (IntPtr)resources.Value.buffer);
            m_Resources = resources;
        }

        public static async Task<DracoInstance> Init(NativeArray<byte>.ReadOnly encodedData
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            using var result = new NativeReference<int>(Allocator.Persistent);
            var resources = new NativeReference<DracoResources>(Allocator.Persistent);
            var decodeJob = new DecodeJob
            {
                encodedData = encodedData,
                result = result,
                resources = resources
            };

#if UNITY_EDITOR
            if (sync) {
                decodeJob.Run();
            }
            else
#endif
            {
                var jobHandle = decodeJob.Schedule();
                await WaitForJobHandle(jobHandle);
                jobHandle.Complete();
            }

            if (result.Value != 0)
            {
                resources.Dispose();
                return null;
            }

            return new DracoInstance(resources);
        }

        public JobHandle DecodeVertices()
        {
            m_DecodeResult = new NativeReference<int>(Allocator.Persistent);
            var decodeVerticesJob = new DecodeVerticesJob
            {
                result = m_DecodeResult,
                resources = m_Resources
            };
            return decodeVerticesJob.Schedule();
        }

        public unsafe bool TryGetAttributeByUniqueId(int uniqueId, out DracoAttributeInstance attributeInstance)
        {
            attributeInstance = new DracoAttributeInstance();
            if (uniqueId < 0) return false;

            NativeAttribute* attr;
            if (GetAttributeByUniqueId(m_Resources.Value.mesh, uniqueId, &attr))
            {
                attributeInstance = new DracoAttributeInstance(attr);
                return true;
            }

            return false;
        }

        public unsafe bool TryGetAttributeByType(AttributeType type, int index, out DracoAttributeInstance attributeInstance)
        {
            attributeInstance = new DracoAttributeInstance();

            NativeAttribute* attr;
            if (GetAttributeByType(m_Resources.Value.mesh, type, index, &attr))
            {
                attributeInstance = new DracoAttributeInstance(attr);
                return true;
            }

            return false;
        }

        public JobHandle DecodeIndices(
            Mesh.MeshData mesh,
            bool flip,
            int indexOffset,
            int indexLength,
            JobHandle dependsOn
            )
        {
            return new GetDracoIndicesJob
            {
                result = m_DecodeResult,
                resources = m_Resources,
                flip = flip,
                mesh = mesh,
                offset = indexOffset,
                length = indexLength
            }.Schedule(dependsOn);
        }

        public unsafe JobHandle GetDracoDataInterleaved(
            Mesh.MeshData mesh,
            DracoAttributeInstance attributeInstance,
            int stream,
            int elementByteOffset,
            int baseVertex,
            int stride,
            int componentStride,
            bool flip,
            NativeArray<float3> bounds,
            JobHandle dependsOn
            )
        {
            if (bounds.IsCreated)
            {
                return new GetDracoDataInterleavedBoundsJob
                {
                    result = m_DecodeResult,
                    resources = m_Resources,
                    attribute = attributeInstance.attribute,
                    stride = stride,
                    flip = flip,
                    componentStride = componentStride,
                    mesh = mesh,
                    streamIndex = stream,
                    offset = baseVertex * stride + elementByteOffset,
                    bounds = bounds
                }.Schedule(dependsOn);
            }

            return new GetDracoDataInterleavedJob
            {
                result = m_DecodeResult,
                resources = m_Resources,
                attribute = attributeInstance.attribute,
                stride = stride,
                flip = flip,
                componentStride = componentStride,
                mesh = mesh,
                streamIndex = stream,
                offset = baseVertex * stride + elementByteOffset,
            }.Schedule(dependsOn);
        }

        public unsafe JobHandle GetDracoData(
            Mesh.MeshData mesh,
            DracoAttributeInstance attributeInstance,
            int stream,
            int baseVertex,
            int componentStride,
            bool flip,
            NativeArray<float3> bounds,
            JobHandle dependsOn
            )
        {
            if (bounds.IsCreated)
            {
                return new GetDracoDataBoundsJob
                {
                    result = m_DecodeResult,
                    resources = m_Resources,
                    attribute = attributeInstance.attribute,
                    flip = flip,
                    componentStride = componentStride,
                    mesh = mesh,
                    streamIndex = stream,
                    baseVertex = baseVertex,
                    bounds = bounds,
                }.Schedule(dependsOn);
            }

            return new GetDracoDataJob
            {
                result = m_DecodeResult,
                resources = m_Resources,
                attribute = attributeInstance.attribute,
                flip = flip,
                componentStride = componentStride,
                mesh = mesh,
                streamIndex = stream,
                baseVertex = baseVertex,
            }.Schedule(dependsOn);
        }

        public unsafe JobHandle GetDracoBones(
            DracoAttributeInstance indicesAttributeInstance,
            DracoAttributeInstance weightsAttributeInstance,
            NativeArray<byte> bonesPerVertex,
            NativeArray<BoneWeight1> boneWeights,
            VertexAttributeFormat indexFormat,
            JobHandle dependsOn
            )
        {
            return new GetDracoBonesJob(
                m_DecodeResult,
                m_Resources,
                indicesAttributeInstance.attribute,
                weightsAttributeInstance.attribute,
                bonesPerVertex,
                boneWeights,
                indexFormat
                ).Schedule(dependsOn);
        }

        public JobHandle Release(JobHandle dependsOn)
        {
            return new ReleaseDracoMeshJob
            {
                resources = m_Resources
            }.Schedule(dependsOn);
        }

        public bool ErrorOccured()
        {
            return m_DecodeResult.Value < 0;
        }

        public void DisposeDracoMesh()
        {
            if (m_DecodeResult.IsCreated)
                m_DecodeResult.Dispose();
            if (m_Resources.IsCreated)
                m_Resources.Dispose();
        }

        internal static async Task WaitForJobHandle(JobHandle jobHandle)
        {
            while (!jobHandle.IsCompleted)
            {
                await Task.Yield();
            }
            jobHandle.Complete();
        }

        internal static int DataTypeSize(DataType dt)
        {
            switch (dt)
            {
                case DataType.Int8:
                case DataType.UInt8:
                    return 1;
                case DataType.Int16:
                case DataType.UInt16:
                    return 2;
                case DataType.Int32:
                case DataType.UInt32:
                    return 4;
                case DataType.Int64:
                case DataType.UInt64:
                    return 8;
                case DataType.Float32:
                    return 4;
                case DataType.Float64:
                    return 8;
                case DataType.Bool:
                    return 1;
                default:
                    return -1;
            }
        }

        internal static VertexAttributeFormat? GetVertexAttributeFormat(DataType inputType, bool normalized = false)
        {
            switch (inputType)
            {
                case DataType.Int8:
                    return normalized ? VertexAttributeFormat.SNorm8 : VertexAttributeFormat.SInt8;
                case DataType.UInt8:
                    return normalized ? VertexAttributeFormat.UNorm8 : VertexAttributeFormat.UInt8;
                case DataType.Int16:
                    return normalized ? VertexAttributeFormat.SNorm16 : VertexAttributeFormat.SInt16;
                case DataType.UInt16:
                    return normalized ? VertexAttributeFormat.UNorm16 : VertexAttributeFormat.UInt16;
                case DataType.Int32:
                    return VertexAttributeFormat.SInt32;
                case DataType.UInt32:
                    return VertexAttributeFormat.UInt32;
                case DataType.Float32:
                    return VertexAttributeFormat.Float32;
                // Not supported by Unity
                // TODO: convert to supported types
                // case DataType.DT_INT64:
                // case DataType.DT_UINT64:
                // case DataType.DT_FLOAT64:
                // case DataType.DT_BOOL:
                default:
                    return null;
            }
        }

        internal static VertexAttribute? GetVertexAttribute(AttributeType inputType, int index = 0)
        {
            switch (inputType)
            {
                case AttributeType.Position:
                    return VertexAttribute.Position;
                case AttributeType.Normal:
                    return VertexAttribute.Normal;
                case AttributeType.Color:
                    return VertexAttribute.Color;
                case AttributeType.TextureCoordinate:
                    Assert.IsTrue(index < 8);
                    return (VertexAttribute)((int)VertexAttribute.TexCoord0 + index);
                default:
                    return null;
            }
        }

        internal static bool ConvertSpace(VertexAttribute attr)
        {
            switch (attr)
            {
                case VertexAttribute.Position:
                case VertexAttribute.Normal:
                case VertexAttribute.Tangent:
                case VertexAttribute.TexCoord0:
                case VertexAttribute.TexCoord1:
                case VertexAttribute.TexCoord2:
                case VertexAttribute.TexCoord3:
                case VertexAttribute.TexCoord4:
                case VertexAttribute.TexCoord5:
                case VertexAttribute.TexCoord6:
                case VertexAttribute.TexCoord7:
                    return true;
                default:
                    return false;
            }
        }

        // The order must be consistent with C++ interface.

        /// <summary>
        /// Release data associated with DracoMesh.
        /// </summary>
        /// <param name="mesh">Draco mesh</param>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe void ReleaseDracoMesh(
            NativeMesh** mesh);

        /// <summary>
        /// Release data associated with DracoAttribute.
        /// </summary>
        /// <param name="attr">Draco attribute</param>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe void
            ReleaseDracoAttribute(NativeAttribute** attr);

        /// <summary>
        /// Release attribute data.
        /// </summary>
        /// <param name="data">Draco data</param>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe void ReleaseDracoData(
            NativeData** data);

        /// <summary>
        /// Initializes decoding of a compressed Draco mesh.
        /// Has to be continued by calling <see cref="DecodeDracoMeshStep2"/> (if no error occured).
        /// The returned mesh must be released with <see cref="ReleaseDracoMesh"/>.
        /// </summary>
        /// <param name="buffer">Pointer to compressed Draco input data</param>
        /// <param name="length">Length of input buffer</param>
        /// <param name="mesh">Resulting mesh pointer</param>
        /// <param name="decoder">Resulting decoder instance pointer</param>
        /// <param name="decoderBuffer">Resulting decoder buffer pointer</param>
        /// <returns>Draco error code</returns>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe int DecodeDracoMeshStep1(
            byte* buffer, int length, NativeMesh** mesh, void** decoder, void** decoderBuffer);


        /// <summary>
        /// Decodes compressed DracoMesh.
        /// Comes after calling <see cref="DecodeDracoMeshStep1"/>.
        /// Mesh must be released with <see cref="ReleaseDracoMesh"/>.
        /// </summary>
        /// <param name="mesh">Draco mesh instance pointer</param>
        /// <param name="decoder">Draco decoder instance pointer</param>
        /// <param name="decoderBuffer">Decoder buffer pointer</param>
        /// <returns>Draco error code</returns>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe int DecodeDracoMeshStep2(
            NativeMesh** mesh, void* decoder, void* decoderBuffer);

        /// <summary>
        /// Returns the DracoAttribute of type at index in mesh. On input, attribute
        /// must be null. E.g. If the mesh has two texture coordinates then
        /// GetAttributeByType(mesh, AttributeType.TEX_COORD, 1, &amp;attr); will return
        /// the second TEX_COORD attribute. The returned attr must be released with
        /// ReleaseDracoAttribute.
        /// </summary>
        /// <param name="mesh">Draco mesh</param>
        /// <param name="type">Attribute type</param>
        /// <param name="index">Per attribute type sub-index</param>
        /// <param name="attr">Resulting attribute pointer</param>
        /// <returns>True if the attribute was retrieved successfully. False otherwise.</returns>
        [DllImport(k_DracoUnityLib)]
        static extern unsafe bool GetAttributeByType(
            NativeMesh* mesh, AttributeType type, int index, NativeAttribute** attr);

        /// <summary>
        /// Returns the DracoAttribute with unique_id in mesh. On input, attribute
        /// must be null.The returned attr must be released with
        /// ReleaseDracoAttribute.
        /// </summary>
        /// <param name="mesh">Draco mesh</param>
        /// <param name="uniqueID">Unique ID</param>
        /// <param name="attr">Resulting attribute pointer</param>
        /// <returns>True if the attribute was retrieved successfully. False otherwise.</returns>
        [DllImport(k_DracoUnityLib)]
        static extern unsafe bool
            GetAttributeByUniqueId(NativeMesh* mesh, int uniqueID,
                NativeAttribute** attr);

        /// <summary>
        /// Returns an array of indices as well as the type of data in data_type. On
        /// input, indices must be null. The returned indices must be released with
        /// ReleaseDracoData.
        /// </summary>
        /// <param name="mesh">DracoMesh to extract indices from</param>
        /// <param name="dataType">Index data type (int or short) </param>
        /// <param name="indices">Destination index buffer</param>
        /// <param name="indicesCount">Number of indices (equals triangle count * 3)</param>
        /// <param name="flip">If true, triangle vertex order is reverted</param>
        /// <returns>True if extraction succeeded, false otherwise</returns>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe bool GetMeshIndices(
            NativeMesh* mesh,
            DataType dataType,
            void* indices,
            int indicesCount,
            bool flip
            );

        /// <summary>
        /// Returns an array of attribute data as well as the type of data in
        /// data_type. On input, data must be null. The returned data must be
        /// released with ReleaseDracoData.
        /// </summary>
        /// <param name="mesh">Draco mesh</param>
        /// <param name="attr">Attribute</param>
        /// <param name="data">Resulting data</param>
        /// <param name="flip">Determines whether a space conversion should be applied (flips one axis)</param>
        /// <param name="componentStride">Component stride</param>
        /// <returns>True if retrieving data was successful. False otherwise.</returns>
        [DllImport(k_DracoUnityLib)]
        internal static extern unsafe bool GetAttributeData(
            NativeMesh* mesh, NativeAttribute* attr, NativeData** data, bool flip, int componentStride);
    }
}
