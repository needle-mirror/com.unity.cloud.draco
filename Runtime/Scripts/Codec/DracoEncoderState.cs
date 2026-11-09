// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Encodes a geometry into a Draco bit-stream.
    /// Combines <c>draco::PointCloudEncoder</c>, <c>draco::MeshEncoder</c>,
    /// <c>draco::MeshEdgebreakerEncoder</c> and <c>draco::PointCloudKdTreeEncoder</c>.
    /// </summary>
    unsafe struct DracoEncoderState : IDisposable
    {
        /// <summary>Geometry to encode.</summary>
        public EncoderGeometry geometry;
        /// <summary>Encoder settings.</summary>
        public EncoderOptions options;
        /// <summary>Buffer receiving the encoded data.</summary>
        public EncoderBuffer buffer;
        /// <summary>Number of points the decoder is going to produce.</summary>
        public int numEncodedPoints;
        /// <summary>Number of faces the decoder is going to produce.</summary>
        public int numEncodedFaces;

        UnsafeList<AttributesEncoder> m_AttributesEncoders;
        UnsafeList<int> m_AttributeToEncoderMap;
        UnsafeList<int> m_AttributesEncoderIdsOrder;

        [NativeDisableUnsafePtrRestriction]
        MeshEdgebreakerEncoder* m_Edgebreaker;

        /// <summary>Number of attributes encoders.</summary>
        public int NumAttributesEncoders =>
            m_AttributesEncoders.IsCreated ? m_AttributesEncoders.Length : 0;

        /// <summary>
        /// Returns one of the attributes encoders.
        /// </summary>
        /// <param name="index">Encoder index.</param>
        /// <returns>Pointer to the encoder.</returns>
        public AttributesEncoder* GetAttributesEncoder(int index)
        {
            return m_AttributesEncoders.Ptr + index;
        }

        /// <summary>
        /// Appends a new attributes encoder.
        /// </summary>
        /// <param name="encoder">Encoder to add.</param>
        /// <returns>Index of the new encoder.</returns>
        public int AddAttributesEncoder(AttributesEncoder encoder)
        {
            m_AttributesEncoders.Add(encoder);
            return m_AttributesEncoders.Length - 1;
        }

        /// <summary>
        /// Allocates the encoder for the given geometry.
        /// </summary>
        /// <param name="pointCloud">True to encode a point cloud without connectivity.</param>
        /// <param name="pointCount">Number of points.</param>
        public void Init(bool pointCloud, int pointCount)
        {
            geometry = new EncoderGeometry();
            geometry.Init(pointCloud, pointCount);
            options = EncoderOptions.Default;
            buffer = new EncoderBuffer();
            buffer.Init(4096);
            m_AttributesEncoders = new UnsafeList<AttributesEncoder>(4, Allocator.Persistent);
            m_AttributeToEncoderMap = new UnsafeList<int>(4, Allocator.Persistent);
            m_AttributesEncoderIdsOrder = new UnsafeList<int>(4, Allocator.Persistent);
        }

        /// <summary>
        /// Encodes the geometry.
        /// Mirrors <c>PointCloudEncoder::Encode</c>.
        /// </summary>
        /// <returns>False when the geometry cannot be encoded.</returns>
        public bool Encode()
        {
            // Discard anything left over from a previous run.
            DisposeAttributesEncoders();
            m_AttributesEncoders = new UnsafeList<AttributesEncoder>(4, Allocator.Persistent);
            m_AttributeToEncoderMap = new UnsafeList<int>(4, Allocator.Persistent);
            m_AttributesEncoderIdsOrder = new UnsafeList<int>(4, Allocator.Persistent);
            buffer.Clear();

            if (!EncodeHeader())
            {
                return false;
            }
            if (!InitializeEncoder())
            {
                return false;
            }
            if (!EncodeGeometryData())
            {
                return false;
            }
            if (!EncodePointAttributes())
            {
                return false;
            }
            ComputeNumberOfEncodedPoints();
            return true;
        }

        bool EncodeHeader()
        {
            // The header follows the version one specification.
            buffer.Encode((byte)'D');
            buffer.Encode((byte)'R');
            buffer.Encode((byte)'A');
            buffer.Encode((byte)'C');
            buffer.Encode((byte)'O');
            var encoderType = geometry.isPointCloud
                ? (byte)EncodedGeometryType.PointCloud
                : (byte)EncodedGeometryType.TriangularMesh;
            var versionMajor = geometry.isPointCloud
                ? DracoConstants.pointCloudBitstreamVersionMajor
                : DracoConstants.meshBitstreamVersionMajor;
            var versionMinor = geometry.isPointCloud
                ? DracoConstants.pointCloudBitstreamVersionMinor
                : DracoConstants.meshBitstreamVersionMinor;
            buffer.Encode(versionMajor);
            buffer.Encode(versionMinor);
            buffer.Encode(encoderType);
            // Both the edgebreaker and the kd-tree method use identifier one.
            buffer.Encode(geometry.isPointCloud
                ? (byte)PointCloudEncodingMethod.KdTree
                : (byte)MeshEncoderMethod.Edgebreaker);
            // Flags. The first bit is reserved for metadata, which is never written.
            buffer.Encode((ushort)0);
            return true;
        }

        bool InitializeEncoder()
        {
            if (geometry.isPointCloud)
            {
                return true;
            }
            // Start from a clean connectivity encoder so that a second encode run does not
            // build on the state of the first one.
            if (m_Edgebreaker != null)
            {
                m_Edgebreaker->Dispose();
                Unmanaged.Free(m_Edgebreaker);
            }
            m_Edgebreaker = Unmanaged.New<MeshEdgebreakerEncoder>();
            return m_Edgebreaker->Initialize(
                (DracoEncoderState*)UnsafeUtility.AddressOf(ref this));
        }

        bool EncodeGeometryData()
        {
            if (geometry.isPointCloud)
            {
                buffer.Encode(geometry.numPoints);
                return true;
            }
            if (!m_Edgebreaker->EncodeConnectivity())
            {
                return false;
            }
            numEncodedFaces = m_Edgebreaker->ComputeNumberOfEncodedFaces();
            return true;
        }

        bool EncodePointAttributes()
        {
            if (!GenerateAttributesEncoders())
            {
                return false;
            }

            buffer.Encode((byte)m_AttributesEncoders.Length);

            // Initialize all encoders. This sets up attribute dependencies without encoding.
            var self = (DracoEncoderState*)UnsafeUtility.AddressOf(ref this);
            for (var i = 0; i < m_AttributesEncoders.Length; ++i)
            {
                if (!m_AttributesEncoders.ElementAt(i).Init(self))
                {
                    return false;
                }
            }

            if (!RearrangeAttributesEncoders())
            {
                return false;
            }

            // Data the decoder needs to create the matching attribute decoders.
            if (!geometry.isPointCloud)
            {
                for (var i = 0; i < m_AttributesEncoderIdsOrder.Length; ++i)
                {
                    if (!m_Edgebreaker->EncodeAttributesEncoderIdentifier(
                            m_AttributesEncoderIdsOrder[i]))
                    {
                        return false;
                    }
                }
            }

            for (var i = 0; i < m_AttributesEncoderIdsOrder.Length; ++i)
            {
                var encoderId = m_AttributesEncoderIdsOrder[i];
                if (!m_AttributesEncoders.ElementAt(encoderId).EncodeAttributesEncoderData(
                        self, ref buffer))
                {
                    return false;
                }
            }

            for (var i = 0; i < m_AttributesEncoderIdsOrder.Length; ++i)
            {
                var encoderId = m_AttributesEncoderIdsOrder[i];
                if (!m_AttributesEncoders.ElementAt(encoderId).EncodeAttributes(self, ref buffer))
                {
                    return false;
                }
            }
            return true;
        }

        bool GenerateAttributesEncoders()
        {
            for (var i = 0; i < geometry.NumAttributes; ++i)
            {
                if (!GenerateAttributesEncoder(i))
                {
                    return false;
                }
            }
            m_AttributeToEncoderMap.Resize(
                geometry.NumAttributes, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < m_AttributesEncoders.Length; ++i)
            {
                ref var encoder = ref m_AttributesEncoders.ElementAt(i);
                for (var j = 0; j < encoder.NumAttributes; ++j)
                {
                    m_AttributeToEncoderMap[encoder.GetAttributeId(j)] = i;
                }
            }
            return true;
        }

        bool GenerateAttributesEncoder(int attributeId)
        {
            if (!geometry.isPointCloud)
            {
                return m_Edgebreaker->GenerateAttributesEncoder(attributeId);
            }
            // The kd-tree encoder handles all attributes at once.
            if (m_AttributesEncoders.Length == 0)
            {
                AddAttributesEncoder(AttributesEncoder.CreateKdTree(attributeId));
                return true;
            }
            GetAttributesEncoder(0)->AddAttributeId(attributeId);
            return true;
        }

        /// <summary>
        /// Marks an attribute as a parent of another attribute, which forces its portable
        /// values to be available for prediction.
        /// Mirrors <c>PointCloudEncoder::MarkParentAttribute</c>.
        /// </summary>
        /// <param name="parentAttributeId">Attribute index.</param>
        /// <returns>False for an unknown attribute.</returns>
        public bool MarkParentAttribute(int parentAttributeId)
        {
            if (parentAttributeId < 0 || parentAttributeId >= geometry.NumAttributes)
            {
                return false;
            }
            var encoderId = m_AttributeToEncoderMap[parentAttributeId];
            return m_AttributesEncoders.ElementAt(encoderId).MarkParentAttribute(
                parentAttributeId);
        }

        /// <summary>
        /// Returns the losslessly encoded values of an attribute, which are safe to predict
        /// from.
        /// Mirrors <c>PointCloudEncoder::GetPortableAttribute</c>.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>Pointer to the portable attribute, or null.</returns>
        public PointAttribute* GetPortableAttribute(int attributeId)
        {
            if (attributeId < 0 || attributeId >= geometry.NumAttributes)
            {
                return null;
            }
            var encoderId = m_AttributeToEncoderMap[attributeId];
            return m_AttributesEncoders.ElementAt(encoderId).GetPortableAttribute(attributeId);
        }

        /// <summary>
        /// Collects the connectivity data a mesh prediction scheme needs.
        /// Mirrors <c>CreateMeshPredictionScheme</c>.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <param name="meshData">The collected connectivity data.</param>
        /// <returns>False when no connectivity data is available.</returns>
        public bool TryGetMeshPredictionSchemeData(
            int attributeId, out MeshPredictionSchemeData meshData)
        {
            meshData = default;
            if (geometry.isPointCloud || m_Edgebreaker == null)
            {
                return false;
            }
            var cornerTable = m_Edgebreaker->CornerTablePtr;
            var encodingData = m_Edgebreaker->GetAttributeEncodingData(attributeId);
            if (cornerTable == null || encodingData == null)
            {
                return false;
            }
            var attributeCornerTable = m_Edgebreaker->GetAttributeCornerTable(attributeId);
            meshData = new MeshPredictionSchemeData
            {
                cornerTable = attributeCornerTable != null
                    ? CornerTableView.FromAttributeCornerTable(attributeCornerTable)
                    : CornerTableView.FromCornerTable(cornerTable),
                dataToCornerMap = (UnsafeList<int>*)UnsafeUtility.AddressOf(
                    ref encodingData->encodedAttributeValueIndexToCornerMap),
                vertexToDataMap = (UnsafeList<int>*)UnsafeUtility.AddressOf(
                    ref encodingData->vertexToEncodedAttributeValueIndexMap)
            };
            return true;
        }

        /// <summary>
        /// Orders the attributes encoders and their attributes so that parent attributes are
        /// encoded before the attributes that depend on them.
        /// Mirrors <c>PointCloudEncoder::RearrangeAttributesEncoders</c>.
        /// </summary>
        bool RearrangeAttributesEncoders()
        {
            // Instead of traversing the dependency graph, the encoders are processed in
            // multiple passes where an encoder is postponed until its parents are done. This
            // also detects cycles.
            var numEncoders = m_AttributesEncoders.Length;
            m_AttributesEncoderIdsOrder.Resize(numEncoders, NativeArrayOptions.ClearMemory);
            var isEncoderProcessed = new UnsafeList<bool>(numEncoders, Allocator.Temp);
            isEncoderProcessed.Resize(numEncoders, NativeArrayOptions.ClearMemory);
            var numProcessedEncoders = 0;
            while (numProcessedEncoders < numEncoders)
            {
                var encoderProcessed = false;
                for (var i = 0; i < numEncoders; ++i)
                {
                    if (isEncoderProcessed[i])
                    {
                        continue;
                    }
                    // Check whether all parent encoders are done.
                    var canBeProcessed = true;
                    ref var encoder = ref m_AttributesEncoders.ElementAt(i);
                    for (var p = 0; p < encoder.NumAttributes; ++p)
                    {
                        var attributeId = encoder.GetAttributeId(p);
                        for (var ap = 0; ap < encoder.NumParentAttributes(attributeId); ++ap)
                        {
                            var parentAttributeId = encoder.GetParentAttributeId(attributeId, ap);
                            var parentEncoderId = m_AttributeToEncoderMap[parentAttributeId];
                            if (parentAttributeId != i && !isEncoderProcessed[parentEncoderId])
                            {
                                canBeProcessed = false;
                                break;
                            }
                        }
                    }
                    if (!canBeProcessed)
                    {
                        continue; // Retry in the next pass.
                    }
                    m_AttributesEncoderIdsOrder[numProcessedEncoders++] = i;
                    isEncoderProcessed[i] = true;
                    encoderProcessed = true;
                }
                if (!encoderProcessed && numProcessedEncoders < numEncoders)
                {
                    isEncoderProcessed.Dispose();
                    return false;
                }
            }

            // Reorder the attributes within every encoder to satisfy their dependencies.
            var attributeEncodingOrder = new UnsafeList<int>(4, Allocator.Temp);
            var isAttributeProcessed = new UnsafeList<bool>(
                geometry.NumAttributes, Allocator.Temp);
            isAttributeProcessed.Resize(geometry.NumAttributes, NativeArrayOptions.ClearMemory);
            var success = true;
            for (var order = 0; order < numEncoders; ++order)
            {
                var encoderId = m_AttributesEncoderIdsOrder[order];
                ref var encoder = ref m_AttributesEncoders.ElementAt(encoderId);
                var numEncoderAttributes = encoder.NumAttributes;
                if (numEncoderAttributes < 2)
                {
                    continue; // A single attribute has nothing to resolve.
                }
                var numProcessedAttributes = 0;
                attributeEncodingOrder.Resize(
                    numEncoderAttributes, NativeArrayOptions.ClearMemory);
                while (numProcessedAttributes < numEncoderAttributes)
                {
                    var attributeProcessed = false;
                    for (var i = 0; i < numEncoderAttributes; ++i)
                    {
                        var attributeId = encoder.GetAttributeId(i);
                        if (isAttributeProcessed[i])
                        {
                            continue;
                        }
                        var canBeProcessed = true;
                        for (var p = 0; p < encoder.NumParentAttributes(attributeId); ++p)
                        {
                            var parentAttributeId = encoder.GetParentAttributeId(attributeId, p);
                            if (!isAttributeProcessed[parentAttributeId])
                            {
                                canBeProcessed = false;
                                break;
                            }
                        }
                        if (!canBeProcessed)
                        {
                            continue;
                        }
                        attributeEncodingOrder[numProcessedAttributes++] = i;
                        isAttributeProcessed[i] = true;
                        attributeProcessed = true;
                    }
                    if (!attributeProcessed && numProcessedAttributes < numEncoderAttributes)
                    {
                        success = false;
                        break;
                    }
                }
                if (!success)
                {
                    break;
                }
                encoder.SetAttributeIds(ref attributeEncodingOrder);
            }

            isEncoderProcessed.Dispose();
            attributeEncodingOrder.Dispose();
            isAttributeProcessed.Dispose();
            return success;
        }

        void ComputeNumberOfEncodedPoints()
        {
            if (geometry.isPointCloud)
            {
                numEncodedPoints = geometry.numPoints;
                return;
            }
            numEncodedPoints = m_Edgebreaker->ComputeNumberOfEncodedPoints();
        }

        void DisposeAttributesEncoders()
        {
            if (m_AttributesEncoders.IsCreated)
            {
                for (var i = 0; i < m_AttributesEncoders.Length; ++i)
                {
                    m_AttributesEncoders.ElementAt(i).Dispose();
                }
                m_AttributesEncoders.Dispose();
            }
            if (m_AttributeToEncoderMap.IsCreated)
            {
                m_AttributeToEncoderMap.Dispose();
            }
            if (m_AttributesEncoderIdsOrder.IsCreated)
            {
                m_AttributesEncoderIdsOrder.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            DisposeAttributesEncoders();
            if (m_Edgebreaker != null)
            {
                m_Edgebreaker->Dispose();
                Unmanaged.Free(m_Edgebreaker);
                m_Edgebreaker = null;
            }
            geometry.Dispose();
            if (buffer.IsCreated)
            {
                buffer.Dispose();
            }
        }
    }
}
