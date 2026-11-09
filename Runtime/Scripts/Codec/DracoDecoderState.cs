// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Decodes a Draco bit-stream into a <see cref="DecodedGeometry"/>.
    /// Combines <c>draco::PointCloudDecoder</c>, <c>draco::MeshDecoder</c>,
    /// <c>draco::MeshSequentialDecoder</c> and <c>draco::MeshEdgebreakerDecoder</c>.
    /// </summary>
    unsafe struct DracoDecoderState : IDisposable
    {
        /// <summary>Geometry type read from the header.</summary>
        public EncodedGeometryType geometryType;
        /// <summary>Encoding method read from the header.</summary>
        public byte encoderMethod;
        /// <summary>Input buffer.</summary>
        public DecoderBuffer buffer;
        /// <summary>Geometry that is being filled in.</summary>
        [NativeDisableUnsafePtrRestriction]
        public DecodedGeometry* geometry;

        byte m_VersionMajor;
        byte m_VersionMinor;

        UnsafeList<AttributesDecoder> m_AttributesDecoders;
        UnsafeList<int> m_AttributeToDecoderMap;

        [NativeDisableUnsafePtrRestriction]
        EdgebreakerDecoder* m_Edgebreaker;

        /// <summary>Draco bit-stream version of the input.</summary>
        public ushort BitstreamVersion => DracoConstants.BitstreamVersion(m_VersionMajor, m_VersionMinor);

        /// <summary>Number of attributes decoders.</summary>
        public int NumAttributesDecoders => m_AttributesDecoders.Length;

        /// <summary>
        /// Returns one of the attributes decoders.
        /// </summary>
        /// <param name="index">Decoder index.</param>
        /// <returns>Reference to the decoder.</returns>
        public ref AttributesDecoder GetAttributesDecoder(AttributesDecoderId index)
        {
            return ref m_AttributesDecoders.ElementAt(index.value);
        }

        /// <summary>
        /// Parses the Draco header.
        /// Mirrors <c>PointCloudDecoder::DecodeHeader</c>.
        /// </summary>
        /// <param name="sourceBuffer">Source buffer.</param>
        /// <param name="header">Parsed header.</param>
        /// <returns>False when the data is not a Draco stream.</returns>
        public static bool DecodeHeader(DecoderBuffer* sourceBuffer, out DracoHeader header)
        {
            header = default;
            var magic = stackalloc byte[5];
            if (!sourceBuffer->Decode(magic, 5))
            {
                return false;
            }
            if (magic[0] != 'D' || magic[1] != 'R' || magic[2] != 'A' || magic[3] != 'C' || magic[4] != 'O')
            {
                return false;
            }
            if (!sourceBuffer->Decode(out header.versionMajor)
                || !sourceBuffer->Decode(out header.versionMinor)
                || !sourceBuffer->Decode(out header.encoderType)
                || !sourceBuffer->Decode(out header.encoderMethod)
                || !sourceBuffer->Decode(out header.flags))
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Reads the header of a stream without consuming the source buffer.
        /// Mirrors <c>Decoder::GetEncodedGeometryType</c>.
        /// </summary>
        /// <param name="data">Encoded data.</param>
        /// <param name="length">Length of the data in bytes.</param>
        /// <param name="type">Geometry type of the stream.</param>
        /// <returns>False when the data is not a Draco stream.</returns>
        public static bool GetEncodedGeometryType(byte* data, long length, out EncodedGeometryType type)
        {
            type = EncodedGeometryType.Invalid;
            var tempBuffer = new DecoderBuffer();
            tempBuffer.Init(data, length);
            if (!DecodeHeader(&tempBuffer, out var header))
            {
                return false;
            }
            if (header.encoderType >= (byte)EncodedGeometryType.Count)
            {
                return false;
            }
            type = (EncodedGeometryType)header.encoderType;
            return true;
        }

        /// <summary>
        /// Decodes the header, the connectivity and the attribute descriptors.
        /// Mirrors <c>PointCloudDecoder::DecodeStep1</c>.
        /// </summary>
        /// <param name="data">Encoded data.</param>
        /// <param name="length">Length of the data in bytes.</param>
        /// <param name="outGeometry">Geometry to fill in.</param>
        /// <returns>True on success.</returns>
        public bool DecodeStep1(byte* data, long length, DecodedGeometry* outGeometry)
        {
            geometry = outGeometry;
            buffer = new DecoderBuffer();
            buffer.Init(data, length);

            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            if (!DecodeHeader(bufferPtr, out var header))
            {
                return false;
            }
            if (header.encoderType != (byte)geometryType)
            {
                return false; // Incompatible decoder for the input geometry.
            }
            m_VersionMajor = header.versionMajor;
            m_VersionMinor = header.versionMinor;
            encoderMethod = header.encoderMethod;

            // Only the current bitstream versions are supported. Older versions would need
            // the reference decoder's backwards compatibility mode, which the native plug-in
            // this code replaces was built without.
            var supportedVersion = geometryType == EncodedGeometryType.PointCloud
                ? DracoConstants.BitstreamVersion(
                    DracoConstants.pointCloudBitstreamVersionMajor,
                    DracoConstants.pointCloudBitstreamVersionMinor)
                : DracoConstants.BitstreamVersion(
                    DracoConstants.meshBitstreamVersionMajor,
                    DracoConstants.meshBitstreamVersionMinor);
            if (BitstreamVersion != supportedVersion)
            {
                return false;
            }
            buffer.BitstreamVersion = BitstreamVersion;

            if ((header.flags & DracoConstants.metadataFlagMask) != 0)
            {
                if (!SkipMetadata(bufferPtr))
                {
                    return false;
                }
            }

            if (!InitializeDecoder())
            {
                return false;
            }
            if (!DecodeGeometryData())
            {
                return false;
            }
            return DecodePointAttributesStep1();
        }

        /// <summary>
        /// Decodes the attribute values.
        /// Mirrors <c>PointCloudDecoder::DecodeStep2</c>.
        /// </summary>
        /// <returns>True on success.</returns>
        public bool DecodeStep2()
        {
            // Build the attribute to decoder mapping.
            for (var i = 0; i < NumAttributesDecoders; ++i)
            {
                ref var attributesDecoder = ref m_AttributesDecoders.ElementAt(i);
                var numAttributes = attributesDecoder.NumAttributes;
                for (var j = 0; j < numAttributes; ++j)
                {
                    var attributeId = attributesDecoder.GetAttributeId(new LocalAttributeId(j));
                    if (attributeId.value >= m_AttributeToDecoderMap.Length)
                    {
                        m_AttributeToDecoderMap.Resize(attributeId.value + 1, NativeArrayOptions.ClearMemory);
                    }
                    m_AttributeToDecoderMap[attributeId.value] = i;
                }
            }

            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            for (var i = 0; i < NumAttributesDecoders; ++i)
            {
                if (!m_AttributesDecoders.ElementAt(i).DecodeAttributes(bufferPtr))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the portable representation of an attribute.
        /// Mirrors <c>PointCloudDecoder::GetPortableAttribute</c>.
        /// </summary>
        /// <param name="attributeId">Attribute index within the geometry.</param>
        /// <returns>Pointer to the portable attribute, or null.</returns>
        public PointAttribute* GetPortableAttribute(AttributeId attributeId)
        {
            if (!attributeId.IsValid || attributeId.value >= geometry->NumAttributes)
            {
                return null;
            }
            if (attributeId.value >= m_AttributeToDecoderMap.Length)
            {
                return null;
            }
            var decoderId = new AttributesDecoderId(m_AttributeToDecoderMap[attributeId.value]);
            if (!decoderId.IsValid || decoderId.value >= NumAttributesDecoders)
            {
                return null;
            }
            return m_AttributesDecoders.ElementAt(decoderId.value).GetPortableAttribute(attributeId);
        }

        /// <summary>
        /// Gathers the connectivity data a mesh prediction scheme needs.
        /// Mirrors <c>CreateMeshPredictionScheme</c>.
        /// </summary>
        /// <param name="attributeId">Attribute the prediction scheme decodes.</param>
        /// <param name="method">Prediction method from the bit-stream.</param>
        /// <returns>Connectivity data, or an uninitialized value when unavailable.</returns>
        public MeshPredictionSchemeData GetMeshPredictionSchemeData(
            AttributeId attributeId, PredictionSchemeMethod method)
        {
            var result = new MeshPredictionSchemeData();
            if (geometryType != EncodedGeometryType.TriangularMesh || m_Edgebreaker == null)
            {
                return result;
            }
            switch (method)
            {
                case PredictionSchemeMethod.MeshParallelogram:
                case PredictionSchemeMethod.MeshMultiParallelogram:
                case PredictionSchemeMethod.MeshConstrainedMultiParallelogram:
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                case PredictionSchemeMethod.MeshGeometricNormal:
                case PredictionSchemeMethod.MeshTexCoordsDeprecated:
                    break;
                default:
                    return result;
            }

            var encodingData = m_Edgebreaker->GetAttributeEncodingData(attributeId);
            if (encodingData == null)
            {
                return result;
            }

            var attributeCornerTable = m_Edgebreaker->GetAttributeCornerTable(attributeId);
            result.cornerTable = attributeCornerTable != null
                ? CornerTableView.FromAttributeCornerTable(attributeCornerTable)
                : CornerTableView.FromCornerTable(
                    (CornerTable*)UnsafeUtility.AddressOf(ref m_Edgebreaker->cornerTable));
            result.dataToCornerMap = (UnsafeList<int>*)UnsafeUtility.AddressOf(
                ref encodingData->encodedAttributeValueIndexToCornerMap);
            result.vertexToDataMap = (UnsafeList<int>*)UnsafeUtility.AddressOf(
                ref encodingData->vertexToEncodedAttributeValueIndexMap);
            return result;
        }

        /// <summary>
        /// Registers an attributes decoder with the given sequencer.
        /// </summary>
        /// <param name="attributesDecoderId">Decoder index.</param>
        /// <param name="sequencer">Sequencer producing the decoding order.</param>
        /// <returns>False when the index is invalid.</returns>
        public bool SetAttributesDecoder(int attributesDecoderId, PointsSequencer sequencer)
        {
            if (attributesDecoderId < 0 || attributesDecoderId >= m_AttributesDecoders.Length)
            {
                return false;
            }
            var self = (DracoDecoderState*)UnsafeUtility.AddressOf(ref this);
            m_AttributesDecoders.ElementAt(attributesDecoderId).Init(self, sequencer);
            return true;
        }

        /// <summary>
        /// Registers an attributes decoder for kd-tree encoded attributes.
        /// </summary>
        /// <param name="attributesDecoderId">Decoder index.</param>
        /// <returns>False when the index is invalid.</returns>
        bool SetKdTreeAttributesDecoder(int attributesDecoderId)
        {
            if (attributesDecoderId < 0 || attributesDecoderId >= m_AttributesDecoders.Length)
            {
                return false;
            }
            var self = (DracoDecoderState*)UnsafeUtility.AddressOf(ref this);
            m_AttributesDecoders.ElementAt(attributesDecoderId).InitKdTree(self);
            return true;
        }

        bool InitializeDecoder()
        {
            if (geometryType != EncodedGeometryType.TriangularMesh)
            {
                return true;
            }
            if (encoderMethod == (byte)MeshEncoderMethod.Sequential)
            {
                return true;
            }
            if (encoderMethod != (byte)MeshEncoderMethod.Edgebreaker)
            {
                return false;
            }

            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            if (!bufferPtr->Decode(out byte traversalDecoderType))
            {
                return false;
            }
            switch ((EdgebreakerConnectivityMethod)traversalDecoderType)
            {
                case EdgebreakerConnectivityMethod.Standard:
                case EdgebreakerConnectivityMethod.Valence:
                    break;
                default:
                    return false; // The predictive variant is not supported.
            }

            m_Edgebreaker = Unmanaged.New<EdgebreakerDecoder>();
            var self = (DracoDecoderState*)UnsafeUtility.AddressOf(ref this);
            m_Edgebreaker->Init(self, (EdgebreakerConnectivityMethod)traversalDecoderType);
            return true;
        }

        bool DecodeGeometryData()
        {
            if (geometryType != EncodedGeometryType.TriangularMesh)
            {
                // Point clouds store their point count here, for both encoding methods.
                // Mirrors PointCloudSequentialDecoder/PointCloudKdTreeDecoder::DecodeGeometryData.
                var pointCloudBuffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
                if (!pointCloudBuffer->Decode(out int numPoints) || numPoints < 0)
                {
                    return false;
                }
                geometry->numPoints = numPoints;
                return true;
            }
            if (m_Edgebreaker != null)
            {
                return m_Edgebreaker->DecodeConnectivity();
            }
            return DecodeSequentialConnectivity();
        }

        bool DecodeSequentialConnectivity()
        {
            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            if (!bufferPtr->DecodeVarintU32(out var numFaces) || !bufferPtr->DecodeVarintU32(out var numPoints))
            {
                return false;
            }

            if (numPoints > int.MaxValue)
            {
                // Point indices are stored as signed integers.
                return false;
            }
            // Compressed sequential encoding supports at most (2^32 - 1) / 3 indices.
            if (numFaces > 0xffffffff / 3)
            {
                return false;
            }
            if (numFaces > bufferPtr->RemainingSize / 3)
            {
                // Face indices would not fit into the remaining buffer.
                return false;
            }

            if (!bufferPtr->Decode(out byte connectivityMethod))
            {
                return false;
            }
            if (connectivityMethod == 0)
            {
                if (!DecodeAndDecompressIndices(numFaces, numPoints))
                {
                    return false;
                }
            }
            else if (numPoints < 256)
            {
                for (uint i = 0; i < numFaces; ++i)
                {
                    var face = new int3();
                    for (var j = 0; j < 3; ++j)
                    {
                        if (!bufferPtr->Decode(out byte value) || value >= numPoints)
                        {
                            return false;
                        }
                        face[j] = value;
                    }
                    geometry->AddFace(face);
                }
            }
            else if (numPoints < 1 << 16)
            {
                for (uint i = 0; i < numFaces; ++i)
                {
                    var face = new int3();
                    for (var j = 0; j < 3; ++j)
                    {
                        if (!bufferPtr->Decode(out ushort value) || value >= numPoints)
                        {
                            return false;
                        }
                        face[j] = value;
                    }
                    geometry->AddFace(face);
                }
            }
            else if (numPoints < 1 << 21)
            {
                for (uint i = 0; i < numFaces; ++i)
                {
                    var face = new int3();
                    for (var j = 0; j < 3; ++j)
                    {
                        if (!bufferPtr->DecodeVarintU32(out var value) || value >= numPoints)
                        {
                            return false;
                        }
                        face[j] = (int)value;
                    }
                    geometry->AddFace(face);
                }
            }
            else
            {
                for (uint i = 0; i < numFaces; ++i)
                {
                    var face = new int3();
                    for (var j = 0; j < 3; ++j)
                    {
                        if (!bufferPtr->Decode(out uint value) || value >= numPoints)
                        {
                            return false;
                        }
                        face[j] = (int)value;
                    }
                    geometry->AddFace(face);
                }
            }

            geometry->numPoints = (int)numPoints;
            return true;
        }

        bool DecodeAndDecompressIndices(uint numFaces, uint numPoints)
        {
            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            var numIndices = (int)(numFaces * 3);
            var indicesBuffer = new UnsafeList<uint>(numIndices, Allocator.Temp);
            indicesBuffer.Resize(numIndices, NativeArrayOptions.UninitializedMemory);

            var success = SymbolDecoding.DecodeSymbols(
                (uint)numIndices, 1, bufferPtr, indicesBuffer.Ptr);
            if (success)
            {
                // Reconstruct the indices from their differences.
                var lastIndexValue = 0;
                var vertexIndex = 0;
                for (uint i = 0; i < numFaces; ++i)
                {
                    var face = new int3();
                    for (var j = 0; j < 3; ++j)
                    {
                        var encodedValue = indicesBuffer[vertexIndex++];
                        var indexDiff = (int)(encodedValue >> 1);
                        if ((encodedValue & 1) != 0)
                        {
                            if (indexDiff > lastIndexValue)
                            {
                                success = false; // Would produce a negative index.
                                break;
                            }
                            indexDiff = -indexDiff;
                        }
                        else if (indexDiff > int.MaxValue - lastIndexValue)
                        {
                            success = false; // Would overflow.
                            break;
                        }
                        var indexValue = indexDiff + lastIndexValue;
                        if ((uint)indexValue >= numPoints)
                        {
                            success = false; // Index out of range.
                            break;
                        }
                        face[j] = indexValue;
                        lastIndexValue = indexValue;
                    }
                    if (!success)
                    {
                        break;
                    }
                    geometry->AddFace(face);
                }
            }
            indicesBuffer.Dispose();
            return success;
        }

        bool DecodePointAttributesStep1()
        {
            var bufferPtr = (DecoderBuffer*)UnsafeUtility.AddressOf(ref buffer);
            if (!bufferPtr->Decode(out byte numAttributesDecoders))
            {
                return false;
            }

            m_AttributesDecoders = new UnsafeList<AttributesDecoder>(
                numAttributesDecoders, Allocator.Persistent);
            m_AttributesDecoders.Resize(numAttributesDecoders, NativeArrayOptions.ClearMemory);
            m_AttributeToDecoderMap = new UnsafeList<int>(numAttributesDecoders, Allocator.Persistent);

            for (var i = 0; i < numAttributesDecoders; ++i)
            {
                if (!CreateAttributesDecoder(i))
                {
                    return false;
                }
            }

            for (var i = 0; i < numAttributesDecoders; ++i)
            {
                if (!m_AttributesDecoders.ElementAt(i).DecodeAttributesDecoderData(bufferPtr))
                {
                    return false;
                }
            }
            return true;
        }

        bool CreateAttributesDecoder(int attributesDecoderId)
        {
            if (m_Edgebreaker != null)
            {
                return m_Edgebreaker->CreateAttributesDecoder(attributesDecoderId);
            }
            if (geometryType == EncodedGeometryType.PointCloud
                && encoderMethod == (byte)PointCloudEncodingMethod.KdTree)
            {
                return SetKdTreeAttributesDecoder(attributesDecoderId);
            }
            // Point clouds and sequentially encoded meshes decode attributes in the
            // natural point order.
            return SetAttributesDecoder(
                attributesDecoderId, PointsSequencer.CreateLinear(geometry->numPoints));
        }

        static bool SkipMetadata(DecoderBuffer* sourceBuffer)
        {
            if (!sourceBuffer->DecodeVarintU32(out var numAttributeMetadata))
            {
                return false;
            }
            for (uint i = 0; i < numAttributeMetadata; ++i)
            {
                if (!sourceBuffer->DecodeVarintU32(out _))
                {
                    return false;
                }
                if (!SkipMetadataElement(sourceBuffer))
                {
                    return false;
                }
            }
            return SkipMetadataElement(sourceBuffer);
        }

        static bool SkipMetadataElement(DecoderBuffer* sourceBuffer)
        {
            // Draco limits nesting to avoid unbounded recursion. The traversal order has
            // to match the reference decoder because sub metadata names are interleaved
            // with the entries of their parent.
            const int maxSubMetadataLevel = 1000;
            // x marks whether the element has a parent, y is the nesting level.
            var stack = new UnsafeList<int2>(8, Allocator.Temp);
            stack.Add(new int2(0, 0));
            var success = true;

            while (stack.Length > 0)
            {
                var current = stack[stack.Length - 1];
                stack.RemoveAt(stack.Length - 1);

                if (current.x != 0)
                {
                    if (current.y > maxSubMetadataLevel || !SkipName(sourceBuffer))
                    {
                        success = false;
                        break;
                    }
                }

                if (!sourceBuffer->DecodeVarintU32(out var numEntries))
                {
                    success = false;
                    break;
                }
                for (uint i = 0; i < numEntries; ++i)
                {
                    if (!SkipName(sourceBuffer)
                        || !sourceBuffer->DecodeVarintU32(out var dataSize)
                        || dataSize == 0
                        || dataSize > sourceBuffer->RemainingSize)
                    {
                        success = false;
                        break;
                    }
                    sourceBuffer->Advance(dataSize);
                }
                if (!success)
                {
                    break;
                }

                if (!sourceBuffer->DecodeVarintU32(out var numSubMetadata)
                    || numSubMetadata > sourceBuffer->RemainingSize)
                {
                    success = false;
                    break;
                }
                var childLevel = current.x != 0 ? current.y + 1 : current.y;
                for (uint i = 0; i < numSubMetadata; ++i)
                {
                    stack.Add(new int2(1, childLevel));
                }
            }

            stack.Dispose();
            return success;
        }

        static bool SkipName(DecoderBuffer* sourceBuffer)
        {
            if (!sourceBuffer->Decode(out byte nameLength))
            {
                return false;
            }
            if (nameLength > sourceBuffer->RemainingSize)
            {
                return false;
            }
            sourceBuffer->Advance(nameLength);
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_AttributesDecoders.IsCreated)
            {
                for (var i = 0; i < m_AttributesDecoders.Length; ++i)
                {
                    m_AttributesDecoders.ElementAt(i).Dispose();
                }
                m_AttributesDecoders.Dispose();
            }
            if (m_AttributeToDecoderMap.IsCreated)
            {
                m_AttributeToDecoderMap.Dispose();
            }
            if (m_Edgebreaker != null)
            {
                m_Edgebreaker->Dispose();
                Unmanaged.Free(m_Edgebreaker);
                m_Edgebreaker = null;
            }
        }
    }
}
