// SPDX-FileCopyrightText: 2023 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Scripting.APIUpdating;

namespace Unity.Cloud.Draco.Encode
{
    /// <summary>
    /// Provides Draco encoding capabilities.
    /// </summary>
    [MovedFrom(true, sourceNamespace: "Draco.Encode", sourceAssembly: "Draco.Encode")]
    public static class DracoEncoder
    {
        struct AttributeData
        {
            public int stream;
            public int offset;
        }

        struct PaddedAttribute
        {
            public NativeArray<byte> data;
            public VertexAttributeFormat format;
            public int dimension;
            public int stride;
        }

        const int k_SkinAttributeDimension = 4;
        const int k_PadJobBatchSize = 512;

        /// <summary>
        /// Applies Draco compression to a given mesh and returns the encoded result (one per sub-mesh)
        /// </summary>
        /// <param name="unityMesh">Input mesh</param>
        /// <returns>Encoded data (one per sub-mesh)</returns>
        public static async Task<EncodeResult[]> EncodeMesh(Mesh unityMesh)
        {
            return await EncodeMesh(unityMesh, QuantizationSettings.Default, SpeedSettings.Default);
        }

        /// <inheritdoc cref="EncodeMesh(UnityEngine.Mesh)"/>
        /// <param name="quantization">Quantization settings</param>
        /// <param name="speed">Encode/decode speed settings</param>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh unityMesh,
            QuantizationSettings quantization,
            SpeedSettings speed
        )
        {
            return await EncodeMesh(unityMesh, quantization, speed, EncodeOptions.None);
        }

        /// <inheritdoc cref="EncodeMesh(Mesh,QuantizationSettings,SpeedSettings)"/>
        /// <param name="options">Encode option flags</param>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh unityMesh,
            QuantizationSettings quantization,
            SpeedSettings speed,
            EncodeOptions options
        )
        {
#if !UNITY_EDITOR
            if (!unityMesh.isReadable)
            {
                Debug.LogError("Mesh is not readable");
                return null;
            }
#endif
            var dataArray = Mesh.AcquireReadOnlyMeshData(unityMesh);
            var data = dataArray[0];

            var result = await EncodeMesh(unityMesh, data, quantization, speed, options);

            dataArray.Dispose();
            return result;
        }

        /// <summary>
        /// Applies Draco compression to a given mesh/meshData and returns the encoded result (one per sub-mesh).
        /// The user is responsible for
        /// <see cref="UnityEngine.Mesh.AcquireReadOnlyMeshData(Mesh)">acquiring the readable MeshData</see>
        /// and disposing it.
        /// </summary>
        /// <param name="mesh">Input mesh</param>
        /// <param name="meshData">Previously acquired readable mesh data</param>
        /// <returns>Encoded data (one per sub-mesh)</returns>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(Mesh mesh, Mesh.MeshData meshData)
        {
            return await EncodeMesh(mesh, meshData, QuantizationSettings.Default, SpeedSettings.Default);
        }

        /// <inheritdoc cref="EncodeMesh(Mesh,Mesh.MeshData)"/>
        /// <param name="quantization">Quantization settings</param>
        /// <param name="speed">Encode/decode speed settings</param>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh mesh,
            Mesh.MeshData meshData,
            QuantizationSettings quantization,
            SpeedSettings speed
        )
        {
            return await EncodeMesh(mesh, meshData, quantization, speed, EncodeOptions.None);
        }

        /// <inheritdoc cref="EncodeMesh(Mesh,Mesh.MeshData,QuantizationSettings,SpeedSettings)"/>
        /// <param name="options">Encode option flags</param>
        // ReSharper disable once MemberCanBePrivate.Global
        public static async Task<EncodeResult[]> EncodeMesh(
            Mesh mesh,
            Mesh.MeshData meshData,
            QuantizationSettings quantization,
            SpeedSettings speed,
            EncodeOptions options
        )
        {
#if !UNITY_EDITOR
            if (!mesh.isReadable)
            {
                Debug.LogError("Mesh is not readable");
                return null;
            }
#endif

            if (!quantization.IsValid)
            {
                // Could be ill-configured or accidental use of QuantizationSettings's implicit default constructor.
                Debug.LogError($"Invalid {quantization}! Falling back to Default");
                quantization = QuantizationSettings.Default;
            }
            Profiler.BeginSample("EncodeMesh.Prepare");

            var result = new EncodeResult[meshData.subMeshCount];
            var vertexAttributes = mesh.GetVertexAttributes();

            var strides = new int[DracoMesh.maxStreamCount];
            var attributeDataDict = new Dictionary<VertexAttribute, AttributeData>();

            foreach (var attribute in vertexAttributes)
            {
                var attributeData = new AttributeData { offset = strides[attribute.stream], stream = attribute.stream };
                var size = attribute.dimension * attribute.format.GetByteSize();
                strides[attribute.stream] += size;
                attributeDataDict[attribute.attribute] = attributeData;
            }

            var streamCount = 1;
            for (var stream = 0; stream < strides.Length; stream++)
            {
                var stride = strides[stream];
                if (stride <= 0) continue;
                streamCount = stream + 1;
            }

            var vData = new NativeArray<byte>[streamCount];
            for (var stream = 0; stream < streamCount; stream++)
            {
                vData[stream] = meshData.GetVertexData<byte>(stream);
            }

            var vDataPtr = GetReadOnlyPointers(streamCount, vData);

            var paddedAttributes = (options & EncodeOptions.PadSkinAttributesToFour) != 0
                ? PadSkinAttributes(mesh, attributeDataDict, strides, vDataPtr)
                : null;
            Profiler.EndSample(); // EncodeMesh.Prepare

            try
            {
                for (var subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
                {

                    Profiler.BeginSample("EncodeMesh.SubMesh.Prepare");
                    var subMesh = mesh.GetSubMesh(subMeshIndex);

                    if (subMesh.topology != MeshTopology.Triangles && subMesh.topology != MeshTopology.Points)
                    {
                        Debug.LogError($"Mesh topology {subMesh.topology} is not supported");
                        return null;
                    }

                    var dracoEncoder = subMesh.topology == MeshTopology.Triangles
                        ? dracoEncoderCreate(mesh.vertexCount)
                        : dracoEncoderCreatePointCloud(mesh.vertexCount);

                    var attributeIds = new Dictionary<VertexAttribute, (uint identifier, int dimensions)>();

                    foreach (var attributeTuple in attributeDataDict)
                    {
                        var attribute = attributeTuple.Key;
                        var attrData = attributeTuple.Value;
                        VertexAttributeFormat format;
                        int dimension;
                        int stride;
                        IntPtr baseAddr;
                        if (paddedAttributes != null && paddedAttributes.TryGetValue(attribute, out var padded))
                        {
                            format = padded.format;
                            dimension = padded.dimension;
                            stride = padded.stride;
                            baseAddr = GetReadOnlyPointer(padded.data);
                        }
                        else
                        {
                            format = mesh.GetVertexAttributeFormat(attribute);
                            dimension = mesh.GetVertexAttributeDimension(attribute);
                            stride = strides[attrData.stream];
                            baseAddr = vDataPtr[attrData.stream] + attrData.offset;
                        }
                        var id = dracoEncoderSetAttribute(
                            dracoEncoder,
                            (int)GetAttributeType(attribute),
                            GetDataType(format),
                            dimension,
                            stride,
                            DracoInstance.ConvertSpace(attribute),
                            baseAddr
                            );
                        attributeIds[attribute] = (id, dimension);
                    }

                    if (subMesh.topology == MeshTopology.Triangles)
                    {
                        var indices = mesh.GetIndices(subMeshIndex);
                        DracoEncoderSetIndicesWrapper(dracoEncoder, indices);
                    }

                    // For both encoding and decoding (0 = slow and best compression; 10 = fast)
                    dracoEncoderSetCompressionSpeed(
                        dracoEncoder,
                        speed.encodingSpeed,
                        speed.decodingSpeed
                    );
                    dracoEncoderSetQuantizationBits(
                        dracoEncoder,
                        quantization.positionQuantization,
                        quantization.normalQuantization,
                        quantization.texCoordQuantization,
                        quantization.colorQuantization,
                        QuantizationSettings.genericQuantization
                    );

                    var encodeJob = new EncodeJob
                    {
                        dracoEncoder = dracoEncoder
                    };

                    Profiler.EndSample(); //EncodeMesh.SubMesh.Prepare

                    var jobHandle = encodeJob.Schedule();
                    while (!jobHandle.IsCompleted)
                    {
                        await Task.Yield();
                    }
                    jobHandle.Complete();

                    Profiler.BeginSample("EncodeMesh.SubMesh.Aftermath");

                    result[subMeshIndex] = new EncodeResult(
                        dracoEncoder,
                        dracoEncoderGetEncodedIndexCount(dracoEncoder),
                        dracoEncoderGetEncodedVertexCount(dracoEncoder),
                        attributeIds
                    );

                    Profiler.EndSample(); // EncodeMesh.SubMesh.Aftermath
                }
            }
            finally
            {
                DisposePaddedAttributes(paddedAttributes);
            }

            Profiler.BeginSample("EncodeMesh.Aftermath");
            for (var stream = 0; stream < streamCount; stream++)
            {
                vData[stream].Dispose();
            }

            Profiler.EndSample();
            return result;
        }

        /// <summary>
        /// Creates copies of bone weights and indices with fewer than four components,
        /// zero-padded to four components. Float16 values are converted to Float32,
        /// since the native encoder receives them as Float32.
        /// </summary>
        static unsafe Dictionary<VertexAttribute, PaddedAttribute> PadSkinAttributes(
            Mesh mesh,
            Dictionary<VertexAttribute, AttributeData> attributeDataDict,
            int[] strides,
            IntPtr[] vDataPtr
        )
        {
            Dictionary<VertexAttribute, PaddedAttribute> result = null;
            var vertexCount = mesh.vertexCount;
            try
            {
                foreach (var (attribute, attrData) in attributeDataDict)
                {
                    if (attribute != VertexAttribute.BlendWeight && attribute != VertexAttribute.BlendIndices)
                        continue;
                    var dimension = mesh.GetVertexAttributeDimension(attribute);
                    if (dimension >= k_SkinAttributeDimension)
                        continue;

                    var srcFormat = mesh.GetVertexAttributeFormat(attribute);
                    var dstFormat = srcFormat == VertexAttributeFormat.Float16
                        ? VertexAttributeFormat.Float32
                        : srcFormat;
                    var srcSize = dimension * srcFormat.GetByteSize();
                    var dstStride = k_SkinAttributeDimension * dstFormat.GetByteSize();
                    var data = new NativeArray<byte>(vertexCount * dstStride, Allocator.Persistent);
                    result ??= new Dictionary<VertexAttribute, PaddedAttribute>();
                    result[attribute] = new PaddedAttribute
                    {
                        data = data,
                        format = dstFormat,
                        dimension = k_SkinAttributeDimension,
                        stride = dstStride
                    };

                    var dst = (byte*)data.GetUnsafePtr();
                    var src = (byte*)(vDataPtr[attrData.stream] + attrData.offset);
                    var srcStride = strides[attrData.stream];
                    if (srcFormat == VertexAttributeFormat.Float16)
                    {
                        new ConvertHalfToFloatJob
                        {
                            input = src,
                            inputStride = srcStride,
                            output = dst,
                            outputStride = dstStride,
                            dimension = dimension
                        }.ScheduleParallel(vertexCount, k_PadJobBatchSize, default).Complete();
                    }
                    else
                    {
                        UnsafeUtility.MemCpyStride(dst, dstStride, src, srcStride, srcSize, vertexCount);
                    }
                }
            }
            catch
            {
                DisposePaddedAttributes(result);
                throw;
            }
            return result;
        }

        static void DisposePaddedAttributes(Dictionary<VertexAttribute, PaddedAttribute> paddedAttributes)
        {
            if (paddedAttributes == null) return;
            foreach (var padded in paddedAttributes.Values)
            {
                padded.data.Dispose();
            }
        }

        static unsafe IntPtr GetReadOnlyPointer(NativeArray<byte> data)
        {
            return (IntPtr)data.GetUnsafeReadOnlyPtr();
        }

        static unsafe IntPtr[] GetReadOnlyPointers(int count, NativeArray<byte>[] vData)
        {
            var result = new IntPtr[count];
            for (var stream = 0; stream < count; stream++)
            {
                result[stream] = (IntPtr)vData[stream].GetUnsafeReadOnlyPtr();
            }

            return result;
        }

        static unsafe void DracoEncoderSetIndicesWrapper(IntPtr encoder, int[] indices)
        {
            fixed (int* indicesData = indices)
            {
                DracoEncoderApi.SetIndices(encoder, (uint)indices.Length, (uint*)indicesData);
            }
        }

        static DataType GetDataType(VertexAttributeFormat format)
        {
            switch (format)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.Float16:
                    return DataType.Float32;
                case VertexAttributeFormat.UNorm8:
                case VertexAttributeFormat.UInt8:
                    return DataType.UInt8;
                case VertexAttributeFormat.SNorm8:
                case VertexAttributeFormat.SInt8:
                    return DataType.Int8;
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.UNorm16:
                    return DataType.UInt16;
                case VertexAttributeFormat.SInt16:
                case VertexAttributeFormat.SNorm16:
                    return DataType.Int16;
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32:
                    return DataType.Int32;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, null);
            }
        }

        static AttributeType GetAttributeType(VertexAttribute attribute)
        {
            switch (attribute)
            {
                case VertexAttribute.Position:
                    return AttributeType.Position;
                case VertexAttribute.Normal:
                    return AttributeType.Normal;
                case VertexAttribute.Color:
                    return AttributeType.Color;
                case VertexAttribute.TexCoord0:
                case VertexAttribute.TexCoord1:
                case VertexAttribute.TexCoord2:
                case VertexAttribute.TexCoord3:
                case VertexAttribute.TexCoord4:
                case VertexAttribute.TexCoord5:
                case VertexAttribute.TexCoord6:
                case VertexAttribute.TexCoord7:
                    return AttributeType.TextureCoordinate;
                case VertexAttribute.Tangent:
                case VertexAttribute.BlendWeight:
                case VertexAttribute.BlendIndices:
                    return AttributeType.Generic;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null);
            }
        }

        static IntPtr dracoEncoderCreate(int vertexCount)
            => DracoEncoderApi.EncoderCreate(vertexCount);

        static IntPtr dracoEncoderCreatePointCloud(int vertexCount)
            => DracoEncoderApi.EncoderCreatePointCloud(vertexCount);

        internal static void dracoEncoderRelease(IntPtr encoder)
            => DracoEncoderApi.EncoderRelease(encoder);

        static void dracoEncoderSetCompressionSpeed(
            IntPtr encoder, int encodingSpeed, int decodingSpeed)
            => DracoEncoderApi.SetCompressionSpeed(encoder, encodingSpeed, decodingSpeed);

        static void dracoEncoderSetQuantizationBits(
            IntPtr encoder, int position, int normal, int uv, int color, int generic)
            => DracoEncoderApi.SetQuantizationBits(encoder, position, normal, uv, color, generic);

        internal static bool dracoEncoderEncode(IntPtr encoder, bool preserveTriangleOrder)
            => DracoEncoderApi.Encode(encoder, preserveTriangleOrder);

        static uint dracoEncoderGetEncodedVertexCount(IntPtr encoder)
            => DracoEncoderApi.GetEncodedVertexCount(encoder);

        static uint dracoEncoderGetEncodedIndexCount(IntPtr encoder)
            => DracoEncoderApi.GetEncodedIndexCount(encoder);

        internal static unsafe void dracoEncoderGetEncodeBuffer(
            IntPtr encoder, out void* data, out ulong size)
            => DracoEncoderApi.GetEncodeBuffer(encoder, out data, out size);

        static uint dracoEncoderSetAttribute(
            IntPtr encoder,
            int attributeType,
            DataType dracoDataType,
            int componentCount,
            int stride,
            bool flip,
            IntPtr data)
            => DracoEncoderApi.SetAttribute(
                encoder, attributeType, dracoDataType, componentCount, stride, flip, data);
    }
}
