// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Records a face that connects to an already traversed face, caused by handles or
    /// holes on the traversed surface.
    /// Mirrors <c>draco::TopologySplitEventData</c>.
    /// </summary>
    struct TopologySplitEventData
    {
        public uint splitSymbolId;
        public uint sourceSymbolId;
        /// <summary>Zero for the left face edge, one for the right face edge.</summary>
        public uint sourceEdge;
    }

    /// <summary>
    /// Per attribute connectivity data maintained while decoding an edgebreaker mesh.
    /// Mirrors the nested <c>AttributeData</c> struct of <c>MeshEdgebreakerDecoderImpl</c>.
    /// </summary>
    unsafe struct EdgebreakerAttributeData : IDisposable
    {
        /// <summary>Index of the attributes decoder that decoded this attribute.</summary>
        public AttributesDecoderId decoderId;
        /// <summary>Attribute specific connectivity.</summary>
        public MeshAttributeCornerTable connectivityData;
        /// <summary>False when the base corner table should be used instead.</summary>
        public bool isConnectivityUsed;
        /// <summary>Decoding order bookkeeping.</summary>
        public MeshAttributeIndicesEncodingData encodingData;
        /// <summary>Corners opposite to attribute seam edges.</summary>
        public UnsafeList<int> attributeSeamCorners;

        /// <inheritdoc />
        public void Dispose()
        {
            connectivityData.Dispose();
            encodingData.Dispose();
            if (attributeSeamCorners.IsCreated)
            {
                attributeSeamCorners.Dispose();
            }
        }
    }

    /// <summary>
    /// Reconstructs mesh connectivity encoded with Draco's edgebreaker method.
    /// Mirrors <c>draco::MeshEdgebreakerDecoderImpl</c>.
    /// </summary>
    unsafe struct EdgebreakerDecoder : IDisposable
    {
        const uint k_LeftFaceEdge = 0;
        const uint k_RightFaceEdge = 1;

        [NativeDisableUnsafePtrRestriction]
        DracoDecoderState* m_Decoder;

        /// <summary>Reconstructed base connectivity.</summary>
        public CornerTable cornerTable;

        UnsafeList<TopologySplitEventData> m_TopologySplitData;
        UnsafeList<bool> m_InitFaceConfigurations;
        UnsafeList<int> m_InitCorners;
        UnsafeList<bool> m_IsVertHole;
        int m_NumEncodedVertices;

        /// <summary>Decoding order bookkeeping of the position attribute.</summary>
        public MeshAttributeIndicesEncodingData posEncodingData;
        /// <summary>Index of the attributes decoder that uses the position bookkeeping.</summary>
        public int posDataDecoderId;
        /// <summary>Per attribute connectivity data.</summary>
        public UnsafeList<EdgebreakerAttributeData> attributeData;

        EdgebreakerTraversalDecoder m_TraversalDecoder;

        // Scratch storage reused across the traversal.
        UnsafeList<int> m_ActiveCornerStack;
        UnsafeList<int> m_TopologySplitActiveCorners;
        UnsafeList<int> m_InvalidVertices;

        /// <summary>
        /// Initializes the decoder.
        /// </summary>
        /// <param name="decoder">Owning decoder state.</param>
        /// <param name="connectivityMethod">Edgebreaker variant read from the bit-stream.</param>
        public void Init(DracoDecoderState* decoder, EdgebreakerConnectivityMethod connectivityMethod)
        {
            m_Decoder = decoder;
            posDataDecoderId = DracoConstants.invalidIndex;
            m_TraversalDecoder = new EdgebreakerTraversalDecoder { method = connectivityMethod };
        }

        /// <summary>
        /// Returns the attribute specific connectivity of an attribute, if any.
        /// </summary>
        /// <param name="attributeId">Attribute index within the geometry.</param>
        /// <returns>Pointer to the attribute corner table, or null.</returns>
        public MeshAttributeCornerTable* GetAttributeCornerTable(AttributeId attributeId)
        {
            for (var i = 0; i < attributeData.Length; ++i)
            {
                ref var data = ref attributeData.ElementAt(i);
                var decoderId = data.decoderId;
                if (!decoderId.IsValid || decoderId.value >= m_Decoder->NumAttributesDecoders)
                {
                    continue;
                }
                ref var attributesDecoder = ref m_Decoder->GetAttributesDecoder(decoderId);
                for (var j = 0; j < attributesDecoder.NumAttributes; ++j)
                {
                    if (attributesDecoder.GetAttributeId(new LocalAttributeId(j)) != attributeId)
                    {
                        continue;
                    }
                    if (data.isConnectivityUsed)
                    {
                        return (MeshAttributeCornerTable*)UnsafeUtility.AddressOf(ref data.connectivityData);
                    }
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns the decoding order bookkeeping of an attribute.
        /// </summary>
        /// <param name="attributeId">Attribute index within the geometry.</param>
        /// <returns>Pointer to the encoding data.</returns>
        public MeshAttributeIndicesEncodingData* GetAttributeEncodingData(AttributeId attributeId)
        {
            for (var i = 0; i < attributeData.Length; ++i)
            {
                ref var data = ref attributeData.ElementAt(i);
                var decoderId = data.decoderId;
                if (!decoderId.IsValid || decoderId.value >= m_Decoder->NumAttributesDecoders)
                {
                    continue;
                }
                ref var attributesDecoder = ref m_Decoder->GetAttributesDecoder(decoderId);
                for (var j = 0; j < attributesDecoder.NumAttributes; ++j)
                {
                    if (attributesDecoder.GetAttributeId(new LocalAttributeId(j)) == attributeId)
                    {
                        return (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(ref data.encodingData);
                    }
                }
            }
            return (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(ref posEncodingData);
        }

        /// <summary>
        /// Creates the attributes decoder for the given decoder index.
        /// </summary>
        /// <param name="attributesDecoderId">Index of the attributes decoder.</param>
        /// <returns>False on malformed input.</returns>
        public bool CreateAttributesDecoder(int attributesDecoderId)
        {
            var buffer = &m_Decoder->buffer;
            if (!buffer->Decode(out sbyte attributeDataId))
            {
                return false;
            }
            if (!buffer->Decode(out byte decoderType))
            {
                return false;
            }

            if (attributeDataId >= 0)
            {
                if (attributeDataId >= attributeData.Length)
                {
                    return false; // Unexpected attribute data.
                }
                // The attribute data must not be claimed by another attributes decoder.
                if (attributeData.ElementAt(attributeDataId).decoderId.IsValid)
                {
                    return false;
                }
                attributeData.ElementAt(attributeDataId).decoderId = new AttributesDecoderId(attributesDecoderId);
            }
            else
            {
                if (posDataDecoderId >= 0)
                {
                    return false; // Another decoder already uses the position data.
                }
                posDataDecoderId = attributesDecoderId;
            }

            if (!buffer->Decode(out byte traversalMethodEncoded))
            {
                return false;
            }
            if (traversalMethodEncoded >= (byte)MeshTraversalMethod.Count)
            {
                return false;
            }
            var traversalMethod = (MeshTraversalMethod)traversalMethodEncoded;

            PointsSequencer sequencer;
            var table = (CornerTable*)UnsafeUtility.AddressOf(ref cornerTable);

            // MESH_VERTEX_ATTRIBUTE is zero, see draco::MeshAttributeElementType.
            if (decoderType == 0)
            {
                MeshAttributeIndicesEncodingData* encodingData;
                if (attributeDataId < 0)
                {
                    encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(ref posEncodingData);
                }
                else
                {
                    ref var data = ref attributeData.ElementAt(attributeDataId);
                    encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(ref data.encodingData);
                    // The attribute connectivity is unused for per-vertex attributes.
                    data.isConnectivityUsed = false;
                }

                TraverserType traverser;
                switch (traversalMethod)
                {
                    case MeshTraversalMethod.PredictionDegree:
                        traverser = TraverserType.MaxPredictionDegree;
                        break;
                    case MeshTraversalMethod.DepthFirst:
                        traverser = TraverserType.DepthFirst;
                        break;
                    default:
                        return false;
                }
                sequencer = PointsSequencer.CreateMeshTraversal(
                    traverser, CornerTableView.FromCornerTable(table), m_Decoder->geometry, encodingData);
            }
            else
            {
                if (traversalMethod != MeshTraversalMethod.DepthFirst)
                {
                    return false;
                }
                if (attributeDataId < 0)
                {
                    return false; // Attribute data must be specified.
                }

                ref var data = ref attributeData.ElementAt(attributeDataId);
                var encodingData = (MeshAttributeIndicesEncodingData*)UnsafeUtility.AddressOf(ref data.encodingData);
                var attributeTable = (MeshAttributeCornerTable*)UnsafeUtility.AddressOf(ref data.connectivityData);
                sequencer = PointsSequencer.CreateMeshTraversal(
                    TraverserType.DepthFirst,
                    CornerTableView.FromAttributeCornerTable(attributeTable),
                    m_Decoder->geometry,
                    encodingData);
            }

            return m_Decoder->SetAttributesDecoder(attributesDecoderId, sequencer);
        }

        /// <summary>
        /// Decodes the mesh connectivity.
        /// </summary>
        /// <returns>False on malformed input.</returns>
        public bool DecodeConnectivity()
        {
            var buffer = &m_Decoder->buffer;
            var bitstreamVersion = m_Decoder->BitstreamVersion;

            if (!buffer->DecodeVarintU32(out var numEncodedVertices))
            {
                return false;
            }
            m_NumEncodedVertices = (int)numEncodedVertices;

            if (!buffer->DecodeVarintU32(out var numFaces))
            {
                return false;
            }
            if (numFaces > int.MaxValue / 3)
            {
                return false; // Draco cannot handle this many faces.
            }
            if ((uint)m_NumEncodedVertices > numFaces * 3)
            {
                return false; // There cannot be more vertices than three per face.
            }

            // Minimum number of edges assuming every edge is shared by two faces.
            var minNumFaceEdges = 3ul * numFaces / 2;
            // Maximum number of edges of a simple connected graph with this vertex count.
            var numEncodedVertices64 = (ulong)m_NumEncodedVertices;
            var maxNumVertexEdges = numEncodedVertices64 * (numEncodedVertices64 - 1) / 2;
            if (maxNumVertexEdges < minNumFaceEdges)
            {
                return false; // No manifold mesh can have these properties.
            }

            if (!buffer->Decode(out byte numAttributeData))
            {
                return false;
            }

            if (!buffer->DecodeVarintU32(out var numEncodedSymbols))
            {
                return false;
            }
            if (numFaces < numEncodedSymbols)
            {
                // The initial face of a component may not be encoded as a symbol, so there
                // can be more faces than symbols but never fewer.
                return false;
            }
            var maxEncodedFaces = numEncodedSymbols + numEncodedSymbols / 3;
            if (numFaces > maxEncodedFaces)
            {
                return false;
            }

            if (!buffer->DecodeVarintU32(out var numEncodedSplitSymbols))
            {
                return false;
            }
            if (numEncodedSplitSymbols > numEncodedSymbols)
            {
                return false; // Split symbols are a subset of all symbols.
            }

            m_TopologySplitData = new UnsafeList<TopologySplitEventData>(16, Allocator.Persistent);
            m_InitFaceConfigurations = new UnsafeList<bool>(16, Allocator.Persistent);
            m_InitCorners = new UnsafeList<int>(16, Allocator.Persistent);
            m_ActiveCornerStack = new UnsafeList<int>(32, Allocator.Persistent);
            m_InvalidVertices = new UnsafeList<int>(16, Allocator.Persistent);

            attributeData = new UnsafeList<EdgebreakerAttributeData>(numAttributeData, Allocator.Persistent);
            attributeData.Resize(numAttributeData, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < numAttributeData; ++i)
            {
                ref var data = ref attributeData.ElementAt(i);
                data.decoderId = AttributesDecoderId.Invalid;
                data.isConnectivityUsed = true;
                data.attributeSeamCorners = new UnsafeList<int>(16, Allocator.Persistent);
            }

            var totalVertices = m_NumEncodedVertices + (int)numEncodedSplitSymbols;
            if (!cornerTable.Reset((int)numFaces, totalVertices))
            {
                return false;
            }

            // Every vertex starts out marked as a hole (boundary). Only vertices decoded
            // with a C symbol and the initial face become interior. Split symbols can add
            // extra vertices, which is why the array is allocated larger.
            m_IsVertHole = new UnsafeList<bool>(totalVertices, Allocator.Persistent);
            m_IsVertHole.Resize(totalVertices, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < totalVertices; ++i)
            {
                m_IsVertHole[i] = true;
            }

            if (!DecodeTopologySplitEvents(buffer))
            {
                return false;
            }

            var table = (CornerTable*)UnsafeUtility.AddressOf(ref cornerTable);
            var traversalDecoder = (EdgebreakerTraversalDecoder*)UnsafeUtility.AddressOf(ref m_TraversalDecoder);
            traversalDecoder->Init(buffer, table);
            // One extra vertex is added for every split symbol.
            traversalDecoder->SetNumEncodedVertices(totalVertices);
            traversalDecoder->SetNumAttributeData(numAttributeData);

            var traversalEndBuffer = new DecoderBuffer();
            if (!traversalDecoder->Start(&traversalEndBuffer))
            {
                return false;
            }

            var numConnectivityVerts = DecodeConnectivitySymbols((int)numEncodedSymbols);
            if (numConnectivityVerts == -1)
            {
                return false;
            }

            // Move the main buffer to the end of the traversal data.
            buffer->Init(
                traversalEndBuffer.DataHead, traversalEndBuffer.RemainingSize, bitstreamVersion);

            if (attributeData.Length > 0)
            {
                for (var ci = 0; ci < cornerTable.NumCorners; ci += 3)
                {
                    if (!DecodeAttributeConnectivitiesOnFace(ci))
                    {
                        return false;
                    }
                }
            }
            traversalDecoder->Done();

            // Build the attribute connectivity from the decoded seams.
            for (var i = 0; i < attributeData.Length; ++i)
            {
                ref var data = ref attributeData.ElementAt(i);
                data.connectivityData.InitEmpty(table);
                for (var j = 0; j < data.attributeSeamCorners.Length; ++j)
                {
                    data.connectivityData.AddSeamEdge(data.attributeSeamCorners[j]);
                }
                if (!data.connectivityData.RecomputeVertices())
                {
                    return false;
                }
            }

            posEncodingData.Init(cornerTable.NumVertices);
            for (var i = 0; i < attributeData.Length; ++i)
            {
                ref var data = ref attributeData.ElementAt(i);
                // Preallocate using the larger of the two vertex counts because the
                // attribute decoder may use either connectivity.
                var attConnectivityVerts = data.connectivityData.NumVertices;
                if (attConnectivityVerts < cornerTable.NumVertices)
                {
                    attConnectivityVerts = cornerTable.NumVertices;
                }
                data.encodingData.Init(attConnectivityVerts);
            }

            return AssignPointsToCorners(numConnectivityVerts);
        }

        bool IsTopologySplit(int encoderSymbolId, out uint faceEdge, out int encoderSplitSymbolId)
        {
            faceEdge = 0;
            encoderSplitSymbolId = 0;
            if (m_TopologySplitData.Length == 0)
            {
                return false;
            }
            ref var last = ref m_TopologySplitData.ElementAt(m_TopologySplitData.Length - 1);
            if (last.sourceSymbolId > (uint)encoderSymbolId)
            {
                // The desired source symbol was already passed, which indicates corrupt data.
                encoderSplitSymbolId = -1;
                return true;
            }
            if (last.sourceSymbolId != (uint)encoderSymbolId)
            {
                return false;
            }
            faceEdge = last.sourceEdge;
            encoderSplitSymbolId = (int)last.splitSymbolId;
            m_TopologySplitData.RemoveAt(m_TopologySplitData.Length - 1);
            return true;
        }

        void SetOppositeCorners(int corner0, int corner1)
        {
            cornerTable.SetOppositeCorner(corner0, corner1);
            cornerTable.SetOppositeCorner(corner1, corner0);
        }

        int DecodeConnectivitySymbols(int numSymbols)
        {
            var traversalDecoder = (EdgebreakerTraversalDecoder*)UnsafeUtility.AddressOf(ref m_TraversalDecoder);
            m_ActiveCornerStack.Clear();

            // Active corners created by topology split events, addressed by split symbol id.
            m_TopologySplitActiveCorners = new UnsafeList<int>(numSymbols, Allocator.Persistent);
            m_TopologySplitActiveCorners.Resize(numSymbols, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numSymbols; ++i)
            {
                m_TopologySplitActiveCorners[i] = DracoConstants.invalidCornerIndex;
            }

            m_InvalidVertices.Clear();
            var removeInvalidVertices = attributeData.Length == 0;

            var maxNumVertices = m_IsVertHole.Length;
            var numFaces = 0;
            for (var symbolId = 0; symbolId < numSymbols; ++symbolId)
            {
                var face = numFaces++;
                var checkTopologySplit = false;
                var symbol = traversalDecoder->DecodeSymbol();

                if (symbol == EdgebreakerTopology.c)
                {
                    // Create a new face between two edges on the open boundary.
                    if (m_ActiveCornerStack.Length == 0)
                    {
                        return -1;
                    }
                    var cornerA = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];
                    var vertexX = cornerTable.Vertex(CornerTable.Next(cornerA));
                    var cornerB = CornerTable.Next(cornerTable.LeftMostCorner(vertexX));

                    if (cornerA == cornerB)
                    {
                        return -1; // All matched corners must differ.
                    }
                    if (cornerTable.Opposite(cornerA) >= 0 || cornerTable.Opposite(cornerB) >= 0)
                    {
                        return -1; // Corner already opposite to an existing face.
                    }

                    var corner = 3 * face;
                    SetOppositeCorners(cornerA, corner + 1);
                    SetOppositeCorners(cornerB, corner + 2);

                    var vertAPrev = cornerTable.Vertex(CornerTable.Previous(cornerA));
                    var vertBNext = cornerTable.Vertex(CornerTable.Next(cornerB));
                    if (vertexX == vertAPrev || vertexX == vertBNext)
                    {
                        return -1; // Degenerate face.
                    }
                    cornerTable.MapCornerToVertex(corner, vertexX);
                    cornerTable.MapCornerToVertex(corner + 1, vertBNext);
                    cornerTable.MapCornerToVertex(corner + 2, vertAPrev);
                    cornerTable.SetLeftMostCorner(vertAPrev, corner + 2);
                    m_IsVertHole[vertexX] = false;
                    m_ActiveCornerStack[m_ActiveCornerStack.Length - 1] = corner;
                }
                else if (symbol == EdgebreakerTopology.r || symbol == EdgebreakerTopology.l)
                {
                    // Extend the boundary edge opposite to the active corner.
                    if (m_ActiveCornerStack.Length == 0)
                    {
                        return -1;
                    }
                    var cornerA = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];
                    if (cornerTable.Opposite(cornerA) >= 0)
                    {
                        return -1;
                    }

                    var corner = 3 * face;
                    int oppCorner;
                    int cornerL;
                    int cornerR;
                    if (symbol == EdgebreakerTopology.r)
                    {
                        oppCorner = corner + 2;
                        cornerL = corner + 1;
                        cornerR = corner;
                    }
                    else
                    {
                        oppCorner = corner + 1;
                        cornerL = corner;
                        cornerR = corner + 2;
                    }
                    SetOppositeCorners(oppCorner, cornerA);
                    var newVertIndex = cornerTable.AddNewVertex();
                    if (cornerTable.NumVertices > maxNumVertices)
                    {
                        return -1;
                    }
                    cornerTable.MapCornerToVertex(oppCorner, newVertIndex);
                    cornerTable.SetLeftMostCorner(newVertIndex, oppCorner);

                    var vertexR = cornerTable.Vertex(CornerTable.Previous(cornerA));
                    cornerTable.MapCornerToVertex(cornerR, vertexR);
                    cornerTable.SetLeftMostCorner(vertexR, cornerR);
                    cornerTable.MapCornerToVertex(cornerL, cornerTable.Vertex(CornerTable.Next(cornerA)));
                    m_ActiveCornerStack[m_ActiveCornerStack.Length - 1] = corner;
                    checkTopologySplit = true;
                }
                else if (symbol == EdgebreakerTopology.s)
                {
                    // Merge the two last active edges from the stack.
                    if (m_ActiveCornerStack.Length == 0)
                    {
                        return -1;
                    }
                    var cornerB = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];
                    m_ActiveCornerStack.RemoveAt(m_ActiveCornerStack.Length - 1);

                    // Corner "a" is either a normal active edge or one created by a
                    // topology split event.
                    if (symbolId < m_TopologySplitActiveCorners.Length
                        && m_TopologySplitActiveCorners[symbolId] >= 0)
                    {
                        m_ActiveCornerStack.Add(m_TopologySplitActiveCorners[symbolId]);
                    }
                    if (m_ActiveCornerStack.Length == 0)
                    {
                        return -1;
                    }
                    var cornerA = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];

                    if (cornerA == cornerB)
                    {
                        return -1;
                    }
                    if (cornerTable.Opposite(cornerA) >= 0 || cornerTable.Opposite(cornerB) >= 0)
                    {
                        return -1;
                    }

                    var corner = 3 * face;
                    SetOppositeCorners(cornerA, corner + 2);
                    SetOppositeCorners(cornerB, corner + 1);

                    var vertexP = cornerTable.Vertex(CornerTable.Previous(cornerA));
                    cornerTable.MapCornerToVertex(corner, vertexP);
                    cornerTable.MapCornerToVertex(corner + 1, cornerTable.Vertex(CornerTable.Next(cornerA)));
                    var vertBPrev = cornerTable.Vertex(CornerTable.Previous(cornerB));
                    cornerTable.MapCornerToVertex(corner + 2, vertBPrev);
                    cornerTable.SetLeftMostCorner(vertBPrev, corner + 2);

                    var cornerN = CornerTable.Next(cornerB);
                    var vertexN = cornerTable.Vertex(cornerN);
                    traversalDecoder->MergeVertices(vertexP, vertexN);
                    cornerTable.SetLeftMostCorner(vertexP, cornerTable.LeftMostCorner(vertexN));

                    // Update the vertex of corner "n" and everything connected to it in the
                    // counter clockwise direction.
                    var firstCorner = cornerN;
                    while (cornerN >= 0)
                    {
                        cornerTable.MapCornerToVertex(cornerN, vertexP);
                        cornerN = cornerTable.SwingLeft(cornerN);
                        if (cornerN == firstCorner)
                        {
                            return -1; // Reaching the start again must not happen here.
                        }
                    }
                    cornerTable.MakeVertexIsolated(vertexN);
                    if (removeInvalidVertices)
                    {
                        m_InvalidVertices.Add(vertexN);
                    }
                    m_ActiveCornerStack[m_ActiveCornerStack.Length - 1] = corner;
                }
                else if (symbol == EdgebreakerTopology.e)
                {
                    var corner = 3 * face;
                    var firstVertIndex = cornerTable.AddNewVertex();
                    cornerTable.MapCornerToVertex(corner, firstVertIndex);
                    cornerTable.MapCornerToVertex(corner + 1, cornerTable.AddNewVertex());
                    cornerTable.MapCornerToVertex(corner + 2, cornerTable.AddNewVertex());

                    if (cornerTable.NumVertices > maxNumVertices)
                    {
                        return -1;
                    }

                    cornerTable.SetLeftMostCorner(firstVertIndex, corner);
                    cornerTable.SetLeftMostCorner(firstVertIndex + 1, corner + 1);
                    cornerTable.SetLeftMostCorner(firstVertIndex + 2, corner + 2);
                    m_ActiveCornerStack.Add(corner);
                    checkTopologySplit = true;
                }
                else
                {
                    return -1; // Unknown symbol.
                }

                traversalDecoder->NewActiveCornerReached(
                    m_ActiveCornerStack[m_ActiveCornerStack.Length - 1]);

                if (checkTopologySplit)
                {
                    // Only L, R and E symbols can be connected to an S face through a
                    // topology split event.
                    var encoderSymbolId = numSymbols - symbolId - 1;
                    while (IsTopologySplit(encoderSymbolId, out var splitEdge, out var encoderSplitSymbolId))
                    {
                        if (encoderSplitSymbolId < 0)
                        {
                            return -1;
                        }
                        var actTopCorner = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];
                        var newActiveCorner = splitEdge == k_RightFaceEdge
                            ? CornerTable.Next(actTopCorner)
                            : CornerTable.Previous(actTopCorner);
                        var decoderSplitSymbolId = numSymbols - encoderSplitSymbolId - 1;
                        if (decoderSplitSymbolId < 0 || decoderSplitSymbolId >= m_TopologySplitActiveCorners.Length)
                        {
                            return -1;
                        }
                        m_TopologySplitActiveCorners[decoderSplitSymbolId] = newActiveCorner;
                    }
                }
            }

            if (cornerTable.NumVertices > maxNumVertices)
            {
                return -1;
            }

            // Decode the start faces and connect them to the faces on the active stack.
            while (m_ActiveCornerStack.Length > 0)
            {
                var corner = m_ActiveCornerStack[m_ActiveCornerStack.Length - 1];
                m_ActiveCornerStack.RemoveAt(m_ActiveCornerStack.Length - 1);
                var interiorFace = traversalDecoder->DecodeStartFaceConfiguration();
                if (interiorFace)
                {
                    if (numFaces >= cornerTable.NumFaces)
                    {
                        return -1; // More faces than expected.
                    }

                    var cornerA = corner;
                    var vertN = cornerTable.Vertex(CornerTable.Next(cornerA));
                    var cornerB = CornerTable.Next(cornerTable.LeftMostCorner(vertN));
                    var vertX = cornerTable.Vertex(CornerTable.Next(cornerB));
                    var cornerC = CornerTable.Next(cornerTable.LeftMostCorner(vertX));

                    if (corner == cornerB || corner == cornerC || cornerB == cornerC)
                    {
                        return -1;
                    }
                    if (cornerTable.Opposite(corner) >= 0
                        || cornerTable.Opposite(cornerB) >= 0
                        || cornerTable.Opposite(cornerC) >= 0)
                    {
                        return -1;
                    }

                    var vertP = cornerTable.Vertex(CornerTable.Next(cornerC));

                    var newFace = numFaces++;
                    var newCorner = 3 * newFace;
                    SetOppositeCorners(newCorner, corner);
                    SetOppositeCorners(newCorner + 1, cornerB);
                    SetOppositeCorners(newCorner + 2, cornerC);

                    cornerTable.MapCornerToVertex(newCorner, vertX);
                    cornerTable.MapCornerToVertex(newCorner + 1, vertP);
                    cornerTable.MapCornerToVertex(newCorner + 2, vertN);

                    for (var ci = 0; ci < 3; ++ci)
                    {
                        m_IsVertHole[cornerTable.Vertex(newCorner + ci)] = false;
                    }

                    m_InitFaceConfigurations.Add(true);
                    m_InitCorners.Add(newCorner);
                }
                else
                {
                    // The traversal started from an open boundary, no new face is added.
                    m_InitFaceConfigurations.Add(false);
                    m_InitCorners.Add(corner);
                }
            }

            if (numFaces != cornerTable.NumFaces)
            {
                return -1;
            }

            var numVertices = cornerTable.NumVertices;
            // Remove vertices that were marked isolated so that all vertices in the range
            // zero to numVertices are valid.
            for (var i = 0; i < m_InvalidVertices.Length; ++i)
            {
                var invalidVert = m_InvalidVertices[i];
                var srcVert = numVertices - 1;
                while (cornerTable.LeftMostCorner(srcVert) < 0)
                {
                    srcVert = --numVertices - 1;
                }
                if (srcVert < invalidVert)
                {
                    continue; // Nothing to swap.
                }

                // Remap all corners of the source vertex onto the invalid vertex.
                var startCorner = cornerTable.LeftMostCorner(srcVert);
                var cid = startCorner;
                var leftTraversal = true;
                while (cid >= 0)
                {
                    if (cornerTable.Vertex(cid) != srcVert)
                    {
                        return -1; // Corrupted data.
                    }
                    cornerTable.MapCornerToVertex(cid, invalidVert);

                    if (leftTraversal)
                    {
                        cid = cornerTable.SwingLeft(cid);
                        if (cid < 0)
                        {
                            cid = cornerTable.SwingRight(startCorner);
                            leftTraversal = false;
                        }
                        else if (cid == startCorner)
                        {
                            cid = DracoConstants.invalidCornerIndex;
                        }
                    }
                    else
                    {
                        cid = cornerTable.SwingRight(cid);
                    }
                }
                cornerTable.SetLeftMostCorner(invalidVert, cornerTable.LeftMostCorner(srcVert));
                cornerTable.MakeVertexIsolated(srcVert);
                m_IsVertHole[invalidVert] = m_IsVertHole[srcVert];
                m_IsVertHole[srcVert] = false;
                numVertices--;
            }
            return numVertices;
        }

        bool DecodeTopologySplitEvents(DecoderBuffer* decoderBuffer)
        {
            if (!decoderBuffer->DecodeVarintU32(out var numTopologySplits))
            {
                return false;
            }
            if (numTopologySplits == 0)
            {
                return true;
            }
            if (numTopologySplits > (uint)cornerTable.NumFaces)
            {
                return false;
            }

            // Source and split symbol ids are delta and varint coded.
            uint lastSourceSymbolId = 0;
            for (uint i = 0; i < numTopologySplits; ++i)
            {
                if (!decoderBuffer->DecodeVarintU32(out var delta))
                {
                    return false;
                }
                var sourceSymbolId = delta + lastSourceSymbolId;
                if (!decoderBuffer->DecodeVarintU32(out delta))
                {
                    return false;
                }
                if (delta > sourceSymbolId)
                {
                    return false;
                }
                m_TopologySplitData.Add(new TopologySplitEventData
                {
                    splitSymbolId = sourceSymbolId - delta,
                    sourceSymbolId = sourceSymbolId,
                    sourceEdge = 0
                });
                lastSourceSymbolId = sourceSymbolId;
            }
            // The split edges follow as a direct bit sequence.
            decoderBuffer->StartBitDecoding(false, out _);
            for (uint i = 0; i < numTopologySplits; ++i)
            {
                decoderBuffer->DecodeLeastSignificantBits32(1, out var edgeData);
                m_TopologySplitData.ElementAt((int)i).sourceEdge = edgeData & 1;
            }
            decoderBuffer->EndBitDecoding();
            return true;
        }

        bool DecodeAttributeConnectivitiesOnFace(int corner)
        {
            var traversalDecoder = (EdgebreakerTraversalDecoder*)UnsafeUtility.AddressOf(ref m_TraversalDecoder);
            var corners = stackalloc int[3];
            corners[0] = corner;
            corners[1] = CornerTable.Next(corner);
            corners[2] = CornerTable.Previous(corner);

            var srcFaceId = CornerTable.Face(corner);
            for (var c = 0; c < 3; ++c)
            {
                var oppCorner = cornerTable.Opposite(corners[c]);
                if (oppCorner < 0)
                {
                    // Boundary edges are always attribute seams.
                    for (var i = 0; i < attributeData.Length; ++i)
                    {
                        attributeData.ElementAt(i).attributeSeamCorners.Add(corners[c]);
                    }
                    continue;
                }
                var oppFaceId = CornerTable.Face(oppCorner);
                // Skip edges whose opposite face was already processed.
                if (oppFaceId < srcFaceId)
                {
                    continue;
                }
                for (var i = 0; i < attributeData.Length; ++i)
                {
                    if (traversalDecoder->DecodeAttributeSeam(i))
                    {
                        attributeData.ElementAt(i).attributeSeamCorners.Add(corners[c]);
                    }
                }
            }
            return true;
        }

        bool AssignPointsToCorners(int numConnectivityVerts)
        {
            var geometry = m_Decoder->geometry;
            geometry->SetNumFaces(cornerTable.NumFaces);

            if (attributeData.Length == 0)
            {
                // Connectivity for positions only, vertex indices equal point indices.
                for (var f = 0; f < geometry->NumFaces; ++f)
                {
                    var startCorner = 3 * f;
                    geometry->SetFace(f, new int3(
                        cornerTable.Vertex(startCorner),
                        cornerTable.Vertex(startCorner + 1),
                        cornerTable.Vertex(startCorner + 2)));
                }
                geometry->numPoints = numConnectivityVerts;
                return true;
            }

            // Multiple attributes need to be deduplicated.
            var pointToCornerMap = new UnsafeList<int>(cornerTable.NumCorners, Allocator.Temp);
            var cornerToPointMap = new UnsafeList<int>(cornerTable.NumCorners, Allocator.Temp);
            cornerToPointMap.Resize(cornerTable.NumCorners, NativeArrayOptions.ClearMemory);

            var success = true;
            for (var v = 0; v < cornerTable.NumVertices; ++v)
            {
                var c = cornerTable.LeftMostCorner(v);
                if (c < 0)
                {
                    continue; // Isolated vertex.
                }
                var deduplicationFirstCorner = c;
                if (!m_IsVertHole[v])
                {
                    // Not on a boundary, so find the first seam of any attribute.
                    for (var i = 0; i < attributeData.Length; ++i)
                    {
                        ref var data = ref attributeData.ElementAt(i);
                        if (!data.connectivityData.IsCornerOnSeam(c))
                        {
                            continue; // No seam for this attribute.
                        }
                        var vertId = data.connectivityData.Vertex(c);
                        var actC = cornerTable.SwingRight(c);
                        var seamFound = false;
                        while (actC != c)
                        {
                            if (actC < 0)
                            {
                                success = false;
                                break;
                            }
                            if (data.connectivityData.Vertex(actC) != vertId)
                            {
                                deduplicationFirstCorner = actC;
                                seamFound = true;
                                break;
                            }
                            actC = cornerTable.SwingRight(actC);
                        }
                        if (!success || seamFound)
                        {
                            break;
                        }
                    }
                }
                if (!success)
                {
                    break;
                }

                // Walk the corners of this vertex clockwise and start a new point whenever
                // one of the attributes changes.
                c = deduplicationFirstCorner;
                cornerToPointMap[c] = pointToCornerMap.Length;
                pointToCornerMap.Add(c);
                var prevC = c;
                c = cornerTable.SwingRight(c);
                while (c >= 0 && c != deduplicationFirstCorner)
                {
                    var attributeSeam = false;
                    for (var i = 0; i < attributeData.Length; ++i)
                    {
                        ref var data = ref attributeData.ElementAt(i);
                        if (data.connectivityData.Vertex(c) != data.connectivityData.Vertex(prevC))
                        {
                            attributeSeam = true;
                            break;
                        }
                    }
                    if (attributeSeam)
                    {
                        cornerToPointMap[c] = pointToCornerMap.Length;
                        pointToCornerMap.Add(c);
                    }
                    else
                    {
                        cornerToPointMap[c] = cornerToPointMap[prevC];
                    }
                    prevC = c;
                    c = cornerTable.SwingRight(c);
                }
            }

            if (success)
            {
                for (var f = 0; f < geometry->NumFaces; ++f)
                {
                    geometry->SetFace(f, new int3(
                        cornerToPointMap[3 * f],
                        cornerToPointMap[3 * f + 1],
                        cornerToPointMap[3 * f + 2]));
                }
                geometry->numPoints = pointToCornerMap.Length;
            }

            pointToCornerMap.Dispose();
            cornerToPointMap.Dispose();
            return success;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            cornerTable.Dispose();
            if (m_TopologySplitData.IsCreated)
            {
                m_TopologySplitData.Dispose();
            }
            if (m_InitFaceConfigurations.IsCreated)
            {
                m_InitFaceConfigurations.Dispose();
            }
            if (m_InitCorners.IsCreated)
            {
                m_InitCorners.Dispose();
            }
            if (m_IsVertHole.IsCreated)
            {
                m_IsVertHole.Dispose();
            }
            if (m_ActiveCornerStack.IsCreated)
            {
                m_ActiveCornerStack.Dispose();
            }
            if (m_TopologySplitActiveCorners.IsCreated)
            {
                m_TopologySplitActiveCorners.Dispose();
            }
            if (m_InvalidVertices.IsCreated)
            {
                m_InvalidVertices.Dispose();
            }
            posEncodingData.Dispose();
            if (attributeData.IsCreated)
            {
                for (var i = 0; i < attributeData.Length; ++i)
                {
                    attributeData.ElementAt(i).Dispose();
                }
                attributeData.Dispose();
            }
            m_TraversalDecoder.Dispose();
        }
    }
}
