// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Per attribute connectivity data maintained while encoding an edgebreaker mesh.
    /// Mirrors the nested <c>AttributeData</c> struct of <c>MeshEdgebreakerEncoderImpl</c>.
    /// </summary>
    unsafe struct EdgebreakerEncoderAttributeData : IDisposable
    {
        /// <summary>Index of the attribute within the geometry.</summary>
        public int attributeIndex;
        /// <summary>Attribute specific connectivity.</summary>
        public MeshAttributeCornerTable connectivityData;
        /// <summary>True while the attribute specific connectivity is used.</summary>
        public bool isConnectivityUsed;
        /// <summary>Encoding order bookkeeping.</summary>
        public MeshAttributeIndicesEncodingData encodingData;
        /// <summary>Traversal method used for this attribute.</summary>
        public MeshTraversalMethod traversalMethod;

        /// <inheritdoc />
        public void Dispose()
        {
            connectivityData.Dispose();
            encodingData.Dispose();
        }
    }

    /// <summary>
    /// Encodes the connectivity of a triangular mesh with the edgebreaker method.
    /// Mirrors <c>draco::MeshEdgebreakerEncoder</c> and
    /// <c>draco::MeshEdgebreakerEncoderImpl</c>.
    /// </summary>
    unsafe struct MeshEdgebreakerEncoder : IDisposable
    {
        [NativeDisableUnsafePtrRestriction]
        DracoEncoderState* m_Encoder;

        CornerTable m_CornerTable;
        EdgebreakerTraversalEncoder m_TraversalEncoder;

        MeshAttributeIndicesEncodingData m_PosEncodingData;
        MeshTraversalMethod m_PosTraversalMethod;

        UnsafeList<EdgebreakerEncoderAttributeData> m_AttributeData;
        UnsafeList<int> m_AttributeEncoderToDataIdMap;

        UnsafeList<bool> m_VisitedFaces;
        UnsafeList<bool> m_VisitedVertexIds;
        UnsafeList<int> m_VertexTraversalLength;
        UnsafeList<int> m_ProcessedConnectivityCorners;
        UnsafeList<bool> m_VisitedHoles;
        UnsafeList<int> m_VertexHoleId;
        UnsafeList<int> m_CornerTraversalStack;
        UnsafeList<TopologySplitEventData> m_TopologySplitEventData;
        // Maps a face onto the split symbol encoded on it, or -1.
        UnsafeList<int> m_FaceToSplitSymbolMap;

        int m_LastEncodedSymbolId;
        uint m_NumSplitSymbols;
        bool m_UseSingleConnectivity;
        EdgebreakerConnectivityMethod m_ConnectivityMethod;

        /// <summary>The connectivity that is being encoded.</summary>
        public CornerTable* CornerTablePtr =>
            (CornerTable*)UnsafeUtility.AddressOf(ref m_CornerTable);

        /// <summary>
        /// Prepares the encoder and picks the edgebreaker variant.
        /// Mirrors <c>MeshEdgebreakerEncoder::InitializeEncoder</c> and
        /// <c>MeshEdgebreakerEncoderImpl::Init</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <returns>False when the mesh cannot be encoded.</returns>
        public bool Initialize(DracoEncoderState* encoder)
        {
            m_Encoder = encoder;
            m_AttributeData = new UnsafeList<EdgebreakerEncoderAttributeData>(
                4, Allocator.Persistent);
            m_AttributeEncoderToDataIdMap = new UnsafeList<int>(4, Allocator.Persistent);
            m_PosEncodingData = new MeshAttributeIndicesEncodingData();
            m_PosTraversalMethod = MeshTraversalMethod.DepthFirst;

            var options = encoder->options;
            // Attributes are split along their seams only at high speed settings.
            m_UseSingleConnectivity = options.Speed >= 6;

            // Tiny meshes compress better with the basic edgebreaker, because the overhead of
            // the predictive variant outweighs its benefit.
            var isTinyMesh = encoder->geometry.NumFaces < 1000;
            m_ConnectivityMethod = options.Speed >= 5 || isTinyMesh
                ? EdgebreakerConnectivityMethod.Standard
                : EdgebreakerConnectivityMethod.Valence;
            encoder->buffer.Encode((byte)m_ConnectivityMethod);
            return true;
        }

        /// <summary>
        /// Returns the attribute specific connectivity of an attribute, if it is used.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>Pointer to the attribute corner table, or null.</returns>
        public MeshAttributeCornerTable* GetAttributeCornerTable(int attributeId)
        {
            for (var i = 0; i < m_AttributeData.Length; ++i)
            {
                if (m_AttributeData.ElementAt(i).attributeIndex != attributeId)
                {
                    continue;
                }
                if (m_AttributeData.ElementAt(i).isConnectivityUsed)
                {
                    return (MeshAttributeCornerTable*)UnsafeUtility.AddressOf(
                        ref m_AttributeData.ElementAt(i).connectivityData);
                }
                return null;
            }
            return null;
        }

        /// <summary>
        /// Returns the encoding order bookkeeping of an attribute.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>Pointer to the encoding data.</returns>
        public MeshAttributeIndicesEncodingData* GetAttributeEncodingData(int attributeId)
        {
            for (var i = 0; i < m_AttributeData.Length; ++i)
            {
                if (m_AttributeData.ElementAt(i).attributeIndex == attributeId)
                {
                    return (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(
                        ref m_AttributeData.ElementAt(i).encodingData);
                }
            }
            return (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(
                ref m_PosEncodingData);
        }

        /// <summary>
        /// True when a face was already processed by the connectivity traversal.
        /// </summary>
        /// <param name="face">Face index.</param>
        /// <returns>True if the face was visited.</returns>
        public bool IsFaceEncoded(int face)
        {
            return m_VisitedFaces[face];
        }

        /// <summary>
        /// Creates the attributes encoder for one attribute, choosing the traversal that
        /// matches its connectivity.
        /// Mirrors <c>MeshEdgebreakerEncoderImpl::GenerateAttributesEncoder</c>.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>False on unsupported configurations.</returns>
        public bool GenerateAttributesEncoder(int attributeId)
        {
            // Either one encoder per attribute or a single encoder for all of them.
            if (m_UseSingleConnectivity && m_Encoder->NumAttributesEncoders > 0)
            {
                m_Encoder->GetAttributesEncoder(0)->AddAttributeId(attributeId);
                return true;
            }

            var attribute = m_Encoder->geometry.Attribute(attributeId);
            var attributeDataId = DracoConstants.invalidIndex;
            for (var i = 0; i < m_AttributeData.Length; ++i)
            {
                if (m_AttributeData.ElementAt(i).attributeIndex == attributeId)
                {
                    attributeDataId = i;
                    break;
                }
            }

            var traversalMethod = MeshTraversalMethod.DepthFirst;
            PointsSequencer sequencer;

            var perVertex = m_UseSingleConnectivity
                || attribute->attributeType == AttributeType.Position
                || (attributeDataId >= 0
                    && m_AttributeData.ElementAt(attributeDataId)
                        .connectivityData.NoInteriorSeams);

            if (perVertex)
            {
                // A per-vertex attribute can use the basic corner table.
                MeshAttributeIndicesEncodingData* encodingData;
                if (m_UseSingleConnectivity
                    || attribute->attributeType == AttributeType.Position)
                {
                    encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(
                        ref m_PosEncodingData);
                }
                else
                {
                    ref var data = ref m_AttributeData.ElementAt(attributeDataId);
                    encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(
                        ref data.encodingData);
                    // Use the vertex count of the base connectivity.
                    encodingData->InitVertexMap(m_CornerTable.NumVertices);
                    // The position connectivity is used, so the attribute specific one is not.
                    data.isConnectivityUsed = false;
                }

                if (m_Encoder->options.Speed == 0
                    && attribute->attributeType == AttributeType.Position)
                {
                    traversalMethod = MeshTraversalMethod.PredictionDegree;
                    if (m_UseSingleConnectivity && m_Encoder->geometry.NumAttributes > 1)
                    {
                        // The prediction degree traversal is not used when several attributes
                        // share the same connectivity.
                        traversalMethod = MeshTraversalMethod.DepthFirst;
                    }
                }

                var traverser = traversalMethod == MeshTraversalMethod.PredictionDegree
                    ? TraverserType.MaxPredictionDegree
                    : TraverserType.DepthFirst;
                sequencer = PointsSequencer.CreateMeshTraversalForEncoding(
                    traverser,
                    CornerTableView.FromCornerTable(CornerTablePtr),
                    m_Encoder->geometry.faces.Ptr,
                    m_Encoder->geometry.NumFaces,
                    m_Encoder->geometry.numPoints,
                    encodingData);
            }
            else
            {
                // A per-corner attribute needs its own connectivity.
                ref var data = ref m_AttributeData.ElementAt(attributeDataId);
                var encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(
                    ref data.encodingData);
                var attributeTable = (MeshAttributeCornerTable*)UnsafeUtility.AddressOf(
                    ref data.connectivityData);
                encodingData->InitVertexMap(data.connectivityData.NumVertices);

                sequencer = PointsSequencer.CreateMeshTraversalForEncoding(
                    TraverserType.DepthFirst,
                    CornerTableView.FromAttributeCornerTable(attributeTable),
                    m_Encoder->geometry.faces.Ptr,
                    m_Encoder->geometry.NumFaces,
                    m_Encoder->geometry.numPoints,
                    encodingData);
            }

            // Match the corner order the decoder is going to use.
            sequencer.SetCornerOrder(
                (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_ProcessedConnectivityCorners));

            if (attributeDataId == DracoConstants.invalidIndex)
            {
                m_PosTraversalMethod = traversalMethod;
            }
            else
            {
                m_AttributeData.ElementAt(attributeDataId).traversalMethod = traversalMethod;
            }

            // The decoder uses this mapping to select the attribute decoder and connectivity.
            m_AttributeEncoderToDataIdMap.Add(attributeDataId);
            m_Encoder->AddAttributesEncoder(
                AttributesEncoder.CreateSequential(sequencer, attributeId));
            return true;
        }

        /// <summary>
        /// Writes the data the decoder needs to recreate an attributes encoder.
        /// Mirrors <c>MeshEdgebreakerEncoderImpl::EncodeAttributesEncoderIdentifier</c>.
        /// </summary>
        /// <param name="attributesEncoderId">Attributes encoder index.</param>
        /// <returns>True on success.</returns>
        public bool EncodeAttributesEncoderIdentifier(int attributesEncoderId)
        {
            var attributeDataId = m_AttributeEncoderToDataIdMap[attributesEncoderId];
            m_Encoder->buffer.Encode((sbyte)attributeDataId);

            var isPerVertex = true;
            MeshTraversalMethod traversalMethod;
            if (attributeDataId >= 0)
            {
                ref var data = ref m_AttributeData.ElementAt(attributeDataId);
                traversalMethod = data.traversalMethod;
                // Attributes always start out as per-corner data, so only the absence of
                // interior seams makes them per-vertex.
                isPerVertex = data.connectivityData.NoInteriorSeams;
            }
            else
            {
                traversalMethod = m_PosTraversalMethod;
            }
            m_Encoder->buffer.Encode(
                isPerVertex ? (byte)MeshAttributeElementType.Vertex
                    : (byte)MeshAttributeElementType.Corner);
            m_Encoder->buffer.Encode((byte)traversalMethod);
            return true;
        }

        /// <summary>
        /// Encodes the mesh connectivity.
        /// Mirrors <c>MeshEdgebreakerEncoderImpl::EncodeConnectivity</c>.
        /// </summary>
        /// <returns>False when the mesh cannot be encoded.</returns>
        public bool EncodeConnectivity()
        {
            ref var geometry = ref m_Encoder->geometry;
            var numInputFaces = geometry.NumFaces;

            // The connectivity is derived from the position indices, because they define which
            // edges can be connected, unless all attributes share one connectivity in which
            // case the mesh is split along the attribute seams.
            if (m_UseSingleConnectivity)
            {
                if (!m_CornerTable.Init(geometry.faces.Ptr, numInputFaces))
                {
                    return false;
                }
            }
            else
            {
                var positionId = geometry.GetNamedAttributeId(AttributeType.Position);
                if (positionId == DracoConstants.invalidIndex)
                {
                    return false;
                }
                var position = geometry.Attribute(positionId);
                var mappedFaces = new UnsafeList<int3>(numInputFaces, Allocator.Temp);
                mappedFaces.Resize(numInputFaces, NativeArrayOptions.UninitializedMemory);
                var source = (int*)geometry.faces.Ptr;
                var destination = (int*)mappedFaces.Ptr;
                for (var i = 0; i < numInputFaces * 3; ++i)
                {
                    destination[i] = position->MappedIndex(source[i]);
                }
                var success = m_CornerTable.Init(mappedFaces.Ptr, numInputFaces);
                mappedFaces.Dispose();
                if (!success)
                {
                    return false;
                }
            }
            if (m_CornerTable.NumFaces == m_CornerTable.NumDegeneratedFaces)
            {
                return false; // All triangles are degenerate.
            }

            var traversalEncoder = (EdgebreakerTraversalEncoder*)UnsafeUtility.AddressOf(
                ref m_TraversalEncoder);
            traversalEncoder->Init(
                m_ConnectivityMethod,
                (MeshEdgebreakerEncoder*)UnsafeUtility.AddressOf(ref this),
                CornerTablePtr,
                numInputFaces);

            // The number of encoded vertices can differ from the input, because vertices on
            // degenerate faces and isolated vertices are ignored.
            var numVerticesToBeEncoded =
                m_CornerTable.NumVertices - m_CornerTable.NumIsolatedVertices;
            m_Encoder->buffer.EncodeVarintU32((uint)numVerticesToBeEncoded);

            var numFaces = m_CornerTable.NumFaces - m_CornerTable.NumDegeneratedFaces;
            m_Encoder->buffer.EncodeVarintU32((uint)numFaces);

            AllocateFlags(ref m_VisitedFaces, numInputFaces);
            m_PosEncodingData.InitForEncoding(
                m_CornerTable.NumVertices, m_CornerTable.NumFaces * 3);
            AllocateFlags(ref m_VisitedVertexIds, m_CornerTable.NumVertices);
            EnsureList(ref m_VertexTraversalLength);
            m_LastEncodedSymbolId = -1;
            m_NumSplitSymbols = 0;
            EnsureList(ref m_TopologySplitEventData);
            AllocateFilled(ref m_FaceToSplitSymbolMap, numInputFaces, DracoConstants.invalidIndex);
            EnsureList(ref m_VisitedHoles);
            AllocateFilled(ref m_VertexHoleId, m_CornerTable.NumVertices, DracoConstants.invalidIndex);
            EnsureList(ref m_ProcessedConnectivityCorners);
            m_ProcessedConnectivityCorners.SetCapacity(m_CornerTable.NumFaces);
            EnsureList(ref m_CornerTraversalStack);

            if (!FindHoles())
            {
                return false;
            }
            if (!InitAttributeData())
            {
                return false;
            }

            var numAttributeData = m_AttributeData.Length;
            m_Encoder->buffer.Encode((byte)numAttributeData);
            traversalEncoder->SetNumAttributeData(numAttributeData);

            var numCorners = m_CornerTable.NumCorners;
            traversalEncoder->Start();

            var initFaceConnectivityCorners = new UnsafeList<int>(8, Allocator.Temp);
            // Traverse the surface, starting from every unvisited corner.
            for (var cId = 0; cId < numCorners; ++cId)
            {
                var cornerIndex = cId;
                var faceId = CornerTable.Face(cornerIndex);
                if (m_VisitedFaces[faceId])
                {
                    continue; // Face was already processed.
                }
                if (m_CornerTable.IsDegenerated(faceId))
                {
                    continue; // Ignore degenerate faces.
                }

                var interiorConfig = FindInitFaceConfiguration(faceId, out var startCorner);
                traversalEncoder->EncodeStartFaceConfiguration(interiorConfig);

                if (interiorConfig)
                {
                    // Pick the root vertex of the face.
                    cornerIndex = startCorner;
                    var vertId = m_CornerTable.Vertex(cornerIndex);
                    var nextVertId = m_CornerTable.Vertex(CornerTable.Next(cornerIndex));
                    var prevVertId = m_CornerTable.Vertex(CornerTable.Previous(cornerIndex));

                    m_VisitedVertexIds[vertId] = true;
                    m_VisitedVertexIds[nextVertId] = true;
                    m_VisitedVertexIds[prevVertId] = true;
                    // A new traversal starts with one vertex.
                    m_VertexTraversalLength.Add(1);

                    m_VisitedFaces[faceId] = true;
                    // Start at the face opposite to the next corner, so that the first encoded
                    // corner is the tip corner of a regular edgebreaker traversal and the
                    // initial face can be treated as a C face.
                    initFaceConnectivityCorners.Add(CornerTable.Next(cornerIndex));
                    var oppId = m_CornerTable.Opposite(CornerTable.Next(cornerIndex));
                    var oppFaceId = CornerTable.Face(oppId);
                    if (oppFaceId != DracoConstants.invalidIndex && !m_VisitedFaces[oppFaceId])
                    {
                        if (!EncodeConnectivityFromCorner(oppId))
                        {
                            initFaceConnectivityCorners.Dispose();
                            return false;
                        }
                    }
                }
                else
                {
                    // The traversal starts on a boundary rather than on a face. Encode the hole
                    // opposite to the start corner first.
                    EncodeHole(CornerTable.Next(startCorner), true);
                    // Then process the face containing the start corner.
                    if (!EncodeConnectivityFromCorner(startCorner))
                    {
                        initFaceConnectivityCorners.Dispose();
                        return false;
                    }
                }
            }
            // Reverse the connectivity corners to match the decoding order.
            ReverseList(ref m_ProcessedConnectivityCorners);
            // The decoder processes the init face corners after the regular ones.
            for (var i = 0; i < initFaceConnectivityCorners.Length; ++i)
            {
                m_ProcessedConnectivityCorners.Add(initFaceConnectivityCorners[i]);
            }
            initFaceConnectivityCorners.Dispose();

            // Encode the connectivity of all non-position attributes.
            if (m_AttributeData.Length > 0)
            {
                // Use the corner order the decoder is going to use.
                for (var i = 0; i < m_VisitedFaces.Length; ++i)
                {
                    m_VisitedFaces[i] = false;
                }
                for (var i = 0; i < m_ProcessedConnectivityCorners.Length; ++i)
                {
                    EncodeAttributeConnectivitiesOnFace(m_ProcessedConnectivityCorners[i]);
                }
            }
            traversalEncoder->Done();

            m_Encoder->buffer.EncodeVarintU32((uint)traversalEncoder->NumEncodedSymbols);
            m_Encoder->buffer.EncodeVarintU32(m_NumSplitSymbols);

            if (!EncodeSplitData())
            {
                return false;
            }
            var traversalBuffer = traversalEncoder->Buffer;
            m_Encoder->buffer.Encode(traversalBuffer.Data, traversalBuffer.Length);
            return true;
        }

        bool EncodeSplitData()
        {
            var numEvents = m_TopologySplitEventData.Length;
            m_Encoder->buffer.EncodeVarintU32((uint)numEvents);
            if (numEvents <= 0)
            {
                return true;
            }
            // Split symbols use delta and varint coding, split edges use direct bit coding.
            var lastSourceSymbolId = 0u;
            for (var i = 0; i < numEvents; ++i)
            {
                var eventData = m_TopologySplitEventData[i];
                // Source symbol ids are stored in increasing order, so the delta is positive.
                m_Encoder->buffer.EncodeVarintU32(eventData.sourceSymbolId - lastSourceSymbolId);
                // The split symbol id is always smaller than the source symbol id.
                m_Encoder->buffer.EncodeVarintU32(
                    eventData.sourceSymbolId - eventData.splitSymbolId);
                lastSourceSymbolId = eventData.sourceSymbolId;
            }
            m_Encoder->buffer.StartBitEncoding(numEvents, false);
            for (var i = 0; i < numEvents; ++i)
            {
                m_Encoder->buffer.EncodeLeastSignificantBits32(
                    1, m_TopologySplitEventData[i].sourceEdge);
            }
            m_Encoder->buffer.EndBitEncoding();
            return true;
        }

        /// <summary>
        /// Finds the configuration a traversal should start a face with.
        /// Mirrors <c>MeshEdgebreakerEncoderImpl::FindInitFaceConfiguration</c>.
        /// </summary>
        bool FindInitFaceConfiguration(int faceId, out int outCorner)
        {
            var cornerIndex = 3 * faceId;
            for (var i = 0; i < 3; ++i)
            {
                if (m_CornerTable.Opposite(cornerIndex) == DracoConstants.invalidCornerIndex)
                {
                    // A boundary edge means an exterior configuration.
                    outCorner = cornerIndex;
                    return false;
                }
                if (m_VertexHoleId[m_CornerTable.Vertex(cornerIndex)] != DracoConstants.invalidIndex)
                {
                    // A boundary vertex. Find the first boundary edge attached to it and return
                    // the corner opposite to that edge.
                    var rightCorner = cornerIndex;
                    while (rightCorner != DracoConstants.invalidCornerIndex)
                    {
                        cornerIndex = rightCorner;
                        rightCorner = m_CornerTable.SwingRight(rightCorner);
                    }
                    // The corner now lies on a boundary edge and its previous corner is the
                    // opposite corner of that edge.
                    outCorner = CornerTable.Previous(cornerIndex);
                    return false;
                }
                cornerIndex = CornerTable.Next(cornerIndex);
            }
            // An interior configuration, so return the first corner.
            outCorner = cornerIndex;
            return true;
        }

        bool EncodeConnectivityFromCorner(int cornerId)
        {
            var traversalEncoder = (EdgebreakerTraversalEncoder*)UnsafeUtility.AddressOf(
                ref m_TraversalEncoder);
            m_CornerTraversalStack.Clear();
            m_CornerTraversalStack.Add(cornerId);
            var numFaces = m_Encoder->geometry.NumFaces;
            while (m_CornerTraversalStack.Length > 0)
            {
                cornerId = m_CornerTraversalStack[m_CornerTraversalStack.Length - 1];
                // The face may have been visited in the meantime.
                if (cornerId == DracoConstants.invalidCornerIndex
                    || m_VisitedFaces[CornerTable.Face(cornerId)])
                {
                    m_CornerTraversalStack.RemoveAt(m_CornerTraversalStack.Length - 1);
                    continue;
                }
                var numVisitedFaces = 0;
                while (numVisitedFaces < numFaces)
                {
                    ++numVisitedFaces;
                    ++m_LastEncodedSymbolId;

                    var faceId = CornerTable.Face(cornerId);
                    m_VisitedFaces[faceId] = true;
                    m_ProcessedConnectivityCorners.Add(cornerId);
                    traversalEncoder->NewCornerReached(cornerId);
                    var vertId = m_CornerTable.Vertex(cornerId);
                    var onBoundary = m_VertexHoleId[vertId] != DracoConstants.invalidIndex;
                    if (!m_VisitedVertexIds[vertId])
                    {
                        // A new vertex was reached, so its position difference has to be stored
                        // relative to the next, previous and opposite vertices.
                        m_VisitedVertexIds[vertId] = true;
                        if (!onBoundary)
                        {
                            // A vertex on a boundary belongs to an unvisited hole and is encoded
                            // with a split symbol later.
                            traversalEncoder->EncodeSymbol(EdgebreakerTopology.c);
                            cornerId = m_CornerTable.GetRightCorner(cornerId);
                            continue;
                        }
                    }
                    // The vertex was already visited or lies on a boundary, so check whether any
                    // of its neighbouring faces can be visited.
                    var rightCornerId = m_CornerTable.GetRightCorner(cornerId);
                    var leftCornerId = m_CornerTable.GetLeftCorner(cornerId);
                    var rightFaceId = CornerTable.Face(rightCornerId);
                    var leftFaceId = CornerTable.Face(leftCornerId);
                    if (IsRightFaceVisited(cornerId))
                    {
                        if (rightFaceId != DracoConstants.invalidIndex)
                        {
                            CheckAndStoreTopologySplitEvent(
                                m_LastEncodedSymbolId, EdgeFaceName.RightFaceEdge, rightFaceId);
                        }
                        if (IsLeftFaceVisited(cornerId))
                        {
                            // Both neighbours are visited, so the traversal ends here.
                            if (leftFaceId != DracoConstants.invalidIndex)
                            {
                                CheckAndStoreTopologySplitEvent(
                                    m_LastEncodedSymbolId, EdgeFaceName.LeftFaceEdge, leftFaceId);
                            }
                            traversalEncoder->EncodeSymbol(EdgebreakerTopology.e);
                            m_CornerTraversalStack.RemoveAt(m_CornerTraversalStack.Length - 1);
                            break;
                        }
                        traversalEncoder->EncodeSymbol(EdgebreakerTopology.r);
                        cornerId = leftCornerId;
                    }
                    else
                    {
                        if (IsLeftFaceVisited(cornerId))
                        {
                            if (leftFaceId != DracoConstants.invalidIndex)
                            {
                                CheckAndStoreTopologySplitEvent(
                                    m_LastEncodedSymbolId, EdgeFaceName.LeftFaceEdge, leftFaceId);
                            }
                            traversalEncoder->EncodeSymbol(EdgebreakerTopology.l);
                            cornerId = rightCornerId;
                        }
                        else
                        {
                            traversalEncoder->EncodeSymbol(EdgebreakerTopology.s);
                            ++m_NumSplitSymbols;
                            // Both neighbours are unvisited, so both have to be visited.
                            if (onBoundary)
                            {
                                // The tip vertex lies on a hole boundary that may still be
                                // unvisited, in which case it has to be encoded.
                                var holeId = m_VertexHoleId[vertId];
                                if (!m_VisitedHoles[holeId])
                                {
                                    EncodeHole(cornerId, false);
                                }
                            }
                            m_FaceToSplitSymbolMap[faceId] = m_LastEncodedSymbolId;
                            // Split the traversal. The left face is processed second, so the top
                            // of the stack points at it and the right face is pushed on top.
                            m_CornerTraversalStack[m_CornerTraversalStack.Length - 1] =
                                leftCornerId;
                            m_CornerTraversalStack.Add(rightCornerId);
                            break;
                        }
                    }
                }
            }
            return true;
        }

        int EncodeHole(int startCornerId, bool encodeFirstVertex)
        {
            // The start corner lies on a hole, so first find the boundary edge going from that
            // vertex, which is the first edge in clockwise direction.
            var cornerId = CornerTable.Previous(startCornerId);
            while (m_CornerTable.Opposite(cornerId) != DracoConstants.invalidCornerIndex)
            {
                cornerId = m_CornerTable.Opposite(cornerId);
                cornerId = CornerTable.Next(cornerId);
            }
            var startVertexId = m_CornerTable.Vertex(startCornerId);

            var numEncodedHoleVertices = 0;
            if (encodeFirstVertex)
            {
                m_VisitedVertexIds[startVertexId] = true;
                ++numEncodedHoleVertices;
            }

            // The corner is now opposite to the boundary edge, so mark the hole as visited.
            m_VisitedHoles[m_VertexHoleId[startVertexId]] = true;
            var actVertexId = m_CornerTable.Vertex(CornerTable.Previous(cornerId));
            while (actVertexId != startVertexId)
            {
                m_VisitedVertexIds[actVertexId] = true;
                ++numEncodedHoleVertices;
                cornerId = CornerTable.Next(cornerId);
                // Look for the next attached open boundary edge.
                while (m_CornerTable.Opposite(cornerId) != DracoConstants.invalidCornerIndex)
                {
                    cornerId = m_CornerTable.Opposite(cornerId);
                    cornerId = CornerTable.Next(cornerId);
                }
                actVertexId = m_CornerTable.Vertex(CornerTable.Previous(cornerId));
            }
            return numEncodedHoleVertices;
        }

        bool IsRightFaceVisited(int cornerId)
        {
            var oppCornerId = m_CornerTable.Opposite(CornerTable.Next(cornerId));
            if (oppCornerId != DracoConstants.invalidCornerIndex)
            {
                return m_VisitedFaces[CornerTable.Face(oppCornerId)];
            }
            // A boundary counts as visited.
            return true;
        }

        bool IsLeftFaceVisited(int cornerId)
        {
            var oppCornerId = m_CornerTable.Opposite(CornerTable.Previous(cornerId));
            if (oppCornerId != DracoConstants.invalidCornerIndex)
            {
                return m_VisitedFaces[CornerTable.Face(oppCornerId)];
            }
            return true;
        }

        bool FindHoles()
        {
            var numCorners = m_CornerTable.NumCorners;
            // Detect all unvisited open boundaries.
            for (var i = 0; i < numCorners; ++i)
            {
                if (m_CornerTable.IsDegenerated(CornerTable.Face(i)))
                {
                    continue; // Skip corners of degenerate faces.
                }
                if (m_CornerTable.Opposite(i) != DracoConstants.invalidCornerIndex)
                {
                    continue;
                }
                // No opposite corner means the opposite edge is an open boundary.
                var boundaryVertId = m_CornerTable.Vertex(CornerTable.Next(i));
                if (m_VertexHoleId[boundaryVertId] != DracoConstants.invalidIndex)
                {
                    continue; // The boundary was already traversed.
                }
                // A new open boundary was found, so traverse it and mark its vertices.
                var boundaryId = m_VisitedHoles.Length;
                m_VisitedHoles.Add(false);

                var cornerId = i;
                while (m_VertexHoleId[boundaryVertId] == DracoConstants.invalidIndex)
                {
                    m_VertexHoleId[boundaryVertId] = boundaryId;
                    cornerId = CornerTable.Next(cornerId);
                    // Look for the next attached open boundary edge.
                    while (m_CornerTable.Opposite(cornerId) != DracoConstants.invalidCornerIndex)
                    {
                        cornerId = m_CornerTable.Opposite(cornerId);
                        cornerId = CornerTable.Next(cornerId);
                    }
                    boundaryVertId = m_CornerTable.Vertex(CornerTable.Next(cornerId));
                }
            }
            return true;
        }

        void CheckAndStoreTopologySplitEvent(
            int sourceSymbolId, EdgeFaceName sourceEdge, int neighborFaceId)
        {
            var symbolId = m_FaceToSplitSymbolMap[neighborFaceId];
            if (symbolId == DracoConstants.invalidIndex)
            {
                return; // Not a split symbol, so no topology split event can happen.
            }
            m_TopologySplitEventData.Add(new TopologySplitEventData
            {
                splitSymbolId = (uint)symbolId,
                sourceSymbolId = (uint)sourceSymbolId,
                sourceEdge = (uint)sourceEdge
            });
        }

        bool InitAttributeData()
        {
            if (m_UseSingleConnectivity)
            {
                return true; // All attributes share the same connectivity.
            }

            ref var geometry = ref m_Encoder->geometry;
            var numAttributes = geometry.NumAttributes;
            // The position attribute is encoded separately.
            m_AttributeData.Clear();
            if (numAttributes == 1)
            {
                return true;
            }
            for (var i = 0; i < numAttributes; ++i)
            {
                var attribute = geometry.Attribute(i);
                if (attribute->attributeType == AttributeType.Position)
                {
                    continue;
                }
                var data = new EdgebreakerEncoderAttributeData
                {
                    attributeIndex = i,
                    isConnectivityUsed = true,
                    traversalMethod = MeshTraversalMethod.DepthFirst
                };
                data.encodingData.InitForEncoding(0, m_CornerTable.NumCorners);
                m_AttributeData.Add(data);
            }
            // The connectivity has to be derived after the list stopped growing, because it
            // stores a pointer to the base corner table.
            for (var i = 0; i < m_AttributeData.Length; ++i)
            {
                ref var data = ref m_AttributeData.ElementAt(i);
                var attribute = geometry.Attribute(data.attributeIndex);
                var connectivity = (MeshAttributeCornerTable*)UnsafeUtility.AddressOf(
                    ref data.connectivityData);
                if (!connectivity->InitFromAttribute(
                        geometry.faces.Ptr, CornerTablePtr, attribute))
                {
                    return false;
                }
            }
            return true;
        }

        bool EncodeAttributeConnectivitiesOnFace(int corner)
        {
            var traversalEncoder = (EdgebreakerTraversalEncoder*)UnsafeUtility.AddressOf(
                ref m_TraversalEncoder);
            var corners = stackalloc int[3];
            corners[0] = corner;
            corners[1] = CornerTable.Next(corner);
            corners[2] = CornerTable.Previous(corner);

            var srcFaceId = CornerTable.Face(corner);
            m_VisitedFaces[srcFaceId] = true;
            for (var c = 0; c < 3; ++c)
            {
                var oppCorner = m_CornerTable.Opposite(corners[c]);
                if (oppCorner == DracoConstants.invalidCornerIndex)
                {
                    continue; // Attribute seams on boundary edges are not encoded.
                }
                var oppFaceId = CornerTable.Face(oppCorner);
                // Edges whose opposite face was already processed are skipped.
                if (m_VisitedFaces[oppFaceId])
                {
                    continue;
                }

                for (var i = 0; i < m_AttributeData.Length; ++i)
                {
                    var isSeam = m_AttributeData.ElementAt(i)
                        .connectivityData.IsCornerOppositeToSeamEdge(corners[c]);
                    traversalEncoder->EncodeAttributeSeam(i, isSeam);
                }
            }
            return true;
        }

        /// <summary>
        /// Counts the points the decoder is going to produce.
        /// Mirrors <c>MeshEdgebreakerEncoder::ComputeNumberOfEncodedPoints</c>.
        /// </summary>
        /// <returns>Number of encoded points.</returns>
        public int ComputeNumberOfEncodedPoints()
        {
            var numPoints = m_CornerTable.NumVertices - m_CornerTable.NumIsolatedVertices;
            ref var geometry = ref m_Encoder->geometry;
            if (geometry.NumAttributes <= 1)
            {
                return numPoints;
            }

            // Collect the corner tables of all non-position attributes.
            var attributeCornerTables = new UnsafeList<IntPtr>(4, Allocator.Temp);
            for (var i = 0; i < geometry.NumAttributes; ++i)
            {
                if (geometry.Attribute(i)->attributeType == AttributeType.Position)
                {
                    continue;
                }
                var table = GetAttributeCornerTable(i);
                // The attribute connectivity may be unused, in which case the attribute has the
                // same connectivity as the positions.
                if (table != null)
                {
                    attributeCornerTables.Add((IntPtr)table);
                }
            }

            // Add a point per interior attribute seam configuration, replicating what the
            // decoder is going to do.
            for (var vi = 0; vi < m_CornerTable.NumVertices; ++vi)
            {
                if (m_CornerTable.IsVertexIsolated(vi))
                {
                    continue;
                }
                // Walk around the vertex and count the attribute seams.
                var firstCornerIndex = m_CornerTable.LeftMostCorner(vi);
                var firstPointIndex = geometry.CornerToPointId(firstCornerIndex);

                var lastPointIndex = firstPointIndex;
                var lastCornerIndex = firstCornerIndex;
                var cornerIndex = m_CornerTable.SwingRight(firstCornerIndex);
                var numAttributeSeams = 0;
                while (cornerIndex != DracoConstants.invalidCornerIndex)
                {
                    var pointIndex = geometry.CornerToPointId(cornerIndex);
                    var seamFound = false;
                    if (pointIndex != lastPointIndex)
                    {
                        seamFound = true;
                        lastPointIndex = pointIndex;
                    }
                    else
                    {
                        // Matching point indices can still hide a seam caused by non-manifold
                        // connectivity of a non-position attribute.
                        for (var i = 0; i < attributeCornerTables.Length; ++i)
                        {
                            var table = (MeshAttributeCornerTable*)attributeCornerTables[i];
                            if (table->Vertex(cornerIndex) != table->Vertex(lastCornerIndex))
                            {
                                seamFound = true;
                                break;
                            }
                        }
                    }
                    if (seamFound)
                    {
                        ++numAttributeSeams;
                    }

                    if (cornerIndex == firstCornerIndex)
                    {
                        break;
                    }

                    lastCornerIndex = cornerIndex;
                    cornerIndex = m_CornerTable.SwingRight(cornerIndex);
                }

                if (!m_CornerTable.IsOnBoundary(vi) && numAttributeSeams > 0)
                {
                    // The whole ring was traversed, so one of the seams closes the loop.
                    numPoints += numAttributeSeams - 1;
                }
                else
                {
                    // Either the vertex is on a boundary, so the ring could not be traversed
                    // completely, or the traversal ended on a different point.
                    numPoints += numAttributeSeams;
                }
            }
            attributeCornerTables.Dispose();
            return numPoints;
        }

        /// <summary>
        /// Counts the faces the decoder is going to produce.
        /// Mirrors <c>MeshEdgebreakerEncoder::ComputeNumberOfEncodedFaces</c>.
        /// </summary>
        /// <returns>Number of encoded faces.</returns>
        public int ComputeNumberOfEncodedFaces()
        {
            return m_CornerTable.NumFaces - m_CornerTable.NumDegeneratedFaces;
        }

        static void AllocateFlags(ref UnsafeList<bool> list, int length)
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<bool>(length, Allocator.Persistent);
            }
            list.Resize(length, NativeArrayOptions.ClearMemory);
        }

        static void AllocateFilled(ref UnsafeList<int> list, int length, int value)
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<int>(length, Allocator.Persistent);
            }
            list.Resize(length, NativeArrayOptions.UninitializedMemory);
            var ptr = list.Ptr;
            for (var i = 0; i < length; ++i)
            {
                ptr[i] = value;
            }
        }

        static void EnsureList<T>(ref UnsafeList<T> list) where T : unmanaged
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<T>(16, Allocator.Persistent);
            }
            list.Clear();
        }

        static void ReverseList(ref UnsafeList<int> list)
        {
            var ptr = list.Ptr;
            var last = list.Length - 1;
            for (var i = 0; i < list.Length / 2; ++i)
            {
                var tmp = ptr[i];
                ptr[i] = ptr[last - i];
                ptr[last - i] = tmp;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_CornerTable.Dispose();
            m_TraversalEncoder.Dispose();
            m_PosEncodingData.Dispose();
            if (m_AttributeData.IsCreated)
            {
                for (var i = 0; i < m_AttributeData.Length; ++i)
                {
                    m_AttributeData.ElementAt(i).Dispose();
                }
                m_AttributeData.Dispose();
            }
            if (m_AttributeEncoderToDataIdMap.IsCreated)
            {
                m_AttributeEncoderToDataIdMap.Dispose();
            }
            if (m_VisitedFaces.IsCreated)
            {
                m_VisitedFaces.Dispose();
            }
            if (m_VisitedVertexIds.IsCreated)
            {
                m_VisitedVertexIds.Dispose();
            }
            if (m_VertexTraversalLength.IsCreated)
            {
                m_VertexTraversalLength.Dispose();
            }
            if (m_ProcessedConnectivityCorners.IsCreated)
            {
                m_ProcessedConnectivityCorners.Dispose();
            }
            if (m_VisitedHoles.IsCreated)
            {
                m_VisitedHoles.Dispose();
            }
            if (m_VertexHoleId.IsCreated)
            {
                m_VertexHoleId.Dispose();
            }
            if (m_CornerTraversalStack.IsCreated)
            {
                m_CornerTraversalStack.Dispose();
            }
            if (m_TopologySplitEventData.IsCreated)
            {
                m_TopologySplitEventData.Dispose();
            }
            if (m_FaceToSplitSymbolMap.IsCreated)
            {
                m_FaceToSplitSymbolMap.Dispose();
            }
        }
    }

    /// <summary>
    /// Edge of a visited triangle, relative to its tip vertex.
    /// Mirrors <c>draco::EdgeFaceName</c>.
    /// </summary>
    enum EdgeFaceName
    {
        LeftFaceEdge = 0,
        RightFaceEdge = 1
    }

    /// <summary>
    /// Whether an attribute has one value per vertex or per corner.
    /// Mirrors <c>draco::MeshAttributeElementType</c>.
    /// </summary>
    enum MeshAttributeElementType
    {
        Vertex = 0,
        Corner = 1,
        Face = 2
    }
}
