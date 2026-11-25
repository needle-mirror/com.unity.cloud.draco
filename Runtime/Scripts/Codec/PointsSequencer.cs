// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Bookkeeping produced while traversing a mesh to determine attribute decoding order.
    /// Mirrors <c>draco::MeshAttributeIndicesEncodingData</c>.
    /// </summary>
    unsafe struct MeshAttributeIndicesEncodingData : IDisposable
    {
        /// <summary>Corner visited for each decoded attribute value, in decoding order.</summary>
        public UnsafeList<int> encodedAttributeValueIndexToCornerMap;
        /// <summary>Decoding order of the attribute value of each vertex, -1 when not decoded yet.</summary>
        public UnsafeList<int> vertexToEncodedAttributeValueIndexMap;
        /// <summary>Total number of decoded attribute values.</summary>
        public int numValues;

        /// <summary>
        /// Allocates the per vertex bookkeeping.
        /// </summary>
        /// <param name="numVertices">Number of vertices.</param>
        public void Init(int numVertices)
        {
            if (!encodedAttributeValueIndexToCornerMap.IsCreated)
            {
                encodedAttributeValueIndexToCornerMap = new UnsafeList<int>(numVertices, Allocator.Persistent);
            }
            if (!vertexToEncodedAttributeValueIndexMap.IsCreated)
            {
                vertexToEncodedAttributeValueIndexMap = new UnsafeList<int>(numVertices, Allocator.Persistent);
            }
            vertexToEncodedAttributeValueIndexMap.Resize(numVertices, NativeArrayOptions.ClearMemory);
            numValues = 0;
        }

        /// <summary>
        /// Resets the bookkeeping for an encoding run, marking every vertex as not encoded.
        /// </summary>
        /// <param name="numVertices">Number of vertices.</param>
        /// <param name="cornerMapCapacity">Number of corners to reserve storage for.</param>
        public void InitForEncoding(int numVertices, int cornerMapCapacity)
        {
            Init(numVertices);
            InitVertexMap(numVertices);
            encodedAttributeValueIndexToCornerMap.Clear();
            if (cornerMapCapacity > encodedAttributeValueIndexToCornerMap.Capacity)
            {
                encodedAttributeValueIndexToCornerMap.SetCapacity(cornerMapCapacity);
            }
        }

        /// <summary>
        /// Resizes the per vertex bookkeeping, marking every vertex as not encoded.
        /// </summary>
        /// <param name="numVertices">Number of vertices.</param>
        public void InitVertexMap(int numVertices)
        {
            if (!vertexToEncodedAttributeValueIndexMap.IsCreated)
            {
                vertexToEncodedAttributeValueIndexMap =
                    new UnsafeList<int>(numVertices, Allocator.Persistent);
            }
            vertexToEncodedAttributeValueIndexMap.Resize(
                numVertices, NativeArrayOptions.UninitializedMemory);
            var ptr = vertexToEncodedAttributeValueIndexMap.Ptr;
            for (var i = 0; i < numVertices; ++i)
            {
                ptr[i] = DracoConstants.invalidIndex;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (encodedAttributeValueIndexToCornerMap.IsCreated)
            {
                encodedAttributeValueIndexToCornerMap.Dispose();
            }
            if (vertexToEncodedAttributeValueIndexMap.IsCreated)
            {
                vertexToEncodedAttributeValueIndexMap.Dispose();
            }
        }
    }

    /// <summary>
    /// Strategy a <see cref="PointsSequencer"/> uses to produce the point order.
    /// </summary>
    enum SequencerType
    {
        /// <summary>Points are decoded in their natural order.</summary>
        Linear = 0,
        /// <summary>Points are decoded in mesh traversal order.</summary>
        MeshTraversal = 1
    }

    /// <summary>
    /// Traversal strategy used by a mesh traversal sequencer.
    /// </summary>
    enum TraverserType
    {
        DepthFirst = 0,
        MaxPredictionDegree = 1
    }

    /// <summary>
    /// Produces the order in which attribute values are decoded.
    /// Combines Draco's <c>PointsSequencer</c> implementations and mesh traversers into a
    /// single dispatchable value type.
    /// </summary>
    unsafe struct PointsSequencer : IDisposable
    {
        const int k_MaxPriority = 3;

        /// <summary>Sequencing strategy.</summary>
        public SequencerType type;
        /// <summary>Traversal strategy, only used by <see cref="SequencerType.MeshTraversal"/>.</summary>
        public TraverserType traverserType;

        int m_NumPoints;

        CornerTableView m_CornerTable;
        // Set when sequencing a decoded mesh, whose point count grows while attributes are
        // decoded and therefore has to be read lazily.
        [NativeDisableUnsafePtrRestriction]
        DecodedGeometry* m_Mesh;
        // Set when sequencing a mesh that is being encoded, whose geometry is already final.
        [NativeDisableUnsafePtrRestriction]
        int3* m_Faces;
        int m_NumFaces;
        [NativeDisableUnsafePtrRestriction]
        MeshAttributeIndicesEncodingData* m_EncodingData;

        // Optional corner order, used by the encoder to match the order the decoder will use.
        [NativeDisableUnsafePtrRestriction]
        UnsafeList<int>* m_CornerOrder;

        int NumFaces => m_Mesh != null ? m_Mesh->NumFaces : m_NumFaces;
        int NumPoints => m_Mesh != null ? m_Mesh->numPoints : m_NumPoints;
        int3* Faces => m_Mesh != null ? m_Mesh->Faces : m_Faces;

        // Traverser bookkeeping.
        UnsafeList<bool> m_IsFaceVisited;
        UnsafeList<bool> m_IsVertexVisited;
        UnsafeList<int> m_CornerTraversalStack;
        UnsafeList<int> m_PredictionDegree;
        UnsafeList<int> m_Stack0;
        UnsafeList<int> m_Stack1;
        UnsafeList<int> m_Stack2;
        int m_BestPriority;

        [NativeDisableUnsafePtrRestriction]
        UnsafeList<int>* m_OutPointIds;

        /// <summary>
        /// Creates a sequencer that emits points in their natural order.
        /// </summary>
        /// <param name="numPoints">Number of points.</param>
        /// <returns>An initialized sequencer.</returns>
        public static PointsSequencer CreateLinear(int numPoints)
        {
            return new PointsSequencer
            {
                type = SequencerType.Linear,
                m_NumPoints = numPoints
            };
        }

        /// <summary>
        /// Creates a sequencer that emits points in mesh traversal order.
        /// </summary>
        /// <param name="traverser">Traversal strategy.</param>
        /// <param name="cornerTable">Connectivity to traverse.</param>
        /// <param name="mesh">Decoded mesh providing corner to point mapping.</param>
        /// <param name="encodingData">Bookkeeping to fill during the traversal.</param>
        /// <returns>An initialized sequencer.</returns>
        public static PointsSequencer CreateMeshTraversal(
            TraverserType traverser,
            CornerTableView cornerTable,
            DecodedGeometry* mesh,
            MeshAttributeIndicesEncodingData* encodingData
            )
        {
            return new PointsSequencer
            {
                type = SequencerType.MeshTraversal,
                traverserType = traverser,
                m_CornerTable = cornerTable,
                m_Mesh = mesh,
                m_EncodingData = encodingData
            };
        }

        /// <summary>
        /// Creates a sequencer that emits points in mesh traversal order, for a mesh that is
        /// being encoded.
        /// </summary>
        /// <param name="traverser">Traversal strategy.</param>
        /// <param name="cornerTable">Connectivity to traverse.</param>
        /// <param name="faces">Triangle point indices, three per face.</param>
        /// <param name="numFaces">Number of faces.</param>
        /// <param name="numPoints">Number of points.</param>
        /// <param name="encodingData">Bookkeeping to fill during the traversal.</param>
        /// <returns>An initialized sequencer.</returns>
        public static PointsSequencer CreateMeshTraversalForEncoding(
            TraverserType traverser,
            CornerTableView cornerTable,
            int3* faces,
            int numFaces,
            int numPoints,
            MeshAttributeIndicesEncodingData* encodingData
            )
        {
            return new PointsSequencer
            {
                type = SequencerType.MeshTraversal,
                traverserType = traverser,
                m_CornerTable = cornerTable,
                m_Faces = faces,
                m_NumFaces = numFaces,
                m_NumPoints = numPoints,
                m_EncodingData = encodingData
            };
        }

        /// <summary>
        /// Restricts the traversal to the given corners, in the given order. Used by the
        /// encoder to reproduce the corner order the decoder is going to use.
        /// Mirrors <c>MeshTraversalSequencer::SetCornerOrder</c>.
        /// </summary>
        /// <param name="cornerOrder">Corners to start traversals from.</param>
        public void SetCornerOrder(UnsafeList<int>* cornerOrder)
        {
            m_CornerOrder = cornerOrder;
        }

        /// <summary>
        /// Fills <paramref name="outPointIds"/> with the decoding order of the points.
        /// </summary>
        /// <param name="outPointIds">Destination list.</param>
        /// <returns>False on malformed connectivity.</returns>
        public bool GenerateSequence(UnsafeList<int>* outPointIds)
        {
            m_OutPointIds = outPointIds;
            outPointIds->Clear();
            if (type == SequencerType.Linear)
            {
                if (m_NumPoints < 0)
                {
                    return false;
                }
                outPointIds->Resize(m_NumPoints, NativeArrayOptions.UninitializedMemory);
                var ptr = outPointIds->Ptr;
                for (var i = 0; i < m_NumPoints; ++i)
                {
                    ptr[i] = i;
                }
                return true;
            }

            return GenerateMeshTraversalSequence();
        }

        /// <summary>
        /// Assigns the point to attribute value index mapping produced by the sequence.
        /// </summary>
        /// <param name="attribute">Attribute to update.</param>
        /// <returns>False on malformed connectivity.</returns>
        public bool UpdatePointToAttributeIndexMapping(ref PointAttribute attribute)
        {
            if (type == SequencerType.Linear)
            {
                attribute.SetIdentityMapping();
                return true;
            }

            attribute.SetExplicitMapping(NumPoints);
            var numFaces = NumFaces;
            var numPoints = NumPoints;
            var faces = Faces;
            var vertexToValue = m_EncodingData->vertexToEncodedAttributeValueIndexMap.Ptr;
            for (var f = 0; f < numFaces; ++f)
            {
                var face = faces[f];
                for (var p = 0; p < 3; ++p)
                {
                    var pointId = face[p];
                    var vertId = m_CornerTable.Vertex(3 * f + p);
                    if (vertId < 0)
                    {
                        return false;
                    }
                    var attEntryId = vertexToValue[vertId];
                    if (pointId >= numPoints || attEntryId >= numPoints)
                    {
                        // There cannot be more attribute values than points.
                        return false;
                    }
                    attribute.SetPointMapEntry(pointId, attEntryId);
                }
            }
            return true;
        }

        bool GenerateMeshTraversalSequence()
        {
            var numFaces = m_CornerTable.NumFaces;
            var numVertices = m_CornerTable.NumVertices;

            AllocateFlags(ref m_IsFaceVisited, numFaces);
            AllocateFlags(ref m_IsVertexVisited, numVertices);

            if (traverserType == TraverserType.MaxPredictionDegree)
            {
                if (!m_PredictionDegree.IsCreated)
                {
                    m_PredictionDegree = new UnsafeList<int>(numVertices, Allocator.Persistent);
                }
                m_PredictionDegree.Resize(numVertices, NativeArrayOptions.ClearMemory);
                EnsureStack(ref m_Stack0);
                EnsureStack(ref m_Stack1);
                EnsureStack(ref m_Stack2);
                m_BestPriority = 0;
            }
            else
            {
                EnsureStack(ref m_CornerTraversalStack);
            }

            if (m_CornerOrder != null)
            {
                var order = m_CornerOrder->Ptr;
                var count = m_CornerOrder->Length;
                for (var i = 0; i < count; ++i)
                {
                    if (!TraverseFromCorner(order[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            for (var i = 0; i < numFaces; ++i)
            {
                if (!TraverseFromCorner(3 * i))
                {
                    return false;
                }
            }
            return true;
        }

        static void AllocateFlags(ref UnsafeList<bool> list, int length)
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<bool>(length, Allocator.Persistent);
            }
            list.Resize(length, NativeArrayOptions.ClearMemory);
        }

        static void EnsureStack(ref UnsafeList<int> list)
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<int>(32, Allocator.Persistent);
            }
            list.Clear();
        }

        bool IsFaceVisited(int faceId)
        {
            if (faceId < 0)
            {
                return true; // Invalid faces count as visited.
            }
            return m_IsFaceVisited[faceId];
        }

        bool IsFaceVisitedAtCorner(int cornerId)
        {
            if (cornerId < 0)
            {
                return true;
            }
            return m_IsFaceVisited[cornerId / 3];
        }

        void OnNewVertexVisited(int vertex, int corner)
        {
            var pointId = ((int*)Faces)[corner];
            m_OutPointIds->Add(pointId);
            m_EncodingData->encodedAttributeValueIndexToCornerMap.Add(corner);
            m_EncodingData->vertexToEncodedAttributeValueIndexMap[vertex] = m_EncodingData->numValues;
            m_EncodingData->numValues++;
        }

        bool TraverseFromCorner(int cornerId)
        {
            return traverserType == TraverserType.MaxPredictionDegree
                ? TraverseFromCornerMaxPredictionDegree(cornerId)
                : TraverseFromCornerDepthFirst(cornerId);
        }

        bool TraverseFromCornerDepthFirst(int cornerId)
        {
            if (IsFaceVisitedAtCorner(cornerId))
            {
                return true; // Already traversed.
            }

            m_CornerTraversalStack.Clear();
            m_CornerTraversalStack.Add(cornerId);

            // The remaining corners of the first face may not be processed yet.
            var nextVert = m_CornerTable.Vertex(CornerTable.Next(cornerId));
            var prevVert = m_CornerTable.Vertex(CornerTable.Previous(cornerId));
            if (nextVert < 0 || prevVert < 0)
            {
                return false;
            }
            if (!m_IsVertexVisited[nextVert])
            {
                m_IsVertexVisited[nextVert] = true;
                OnNewVertexVisited(nextVert, CornerTable.Next(cornerId));
            }
            if (!m_IsVertexVisited[prevVert])
            {
                m_IsVertexVisited[prevVert] = true;
                OnNewVertexVisited(prevVert, CornerTable.Previous(cornerId));
            }

            while (m_CornerTraversalStack.Length > 0)
            {
                cornerId = m_CornerTraversalStack[m_CornerTraversalStack.Length - 1];
                var faceId = cornerId / 3;
                if (cornerId < 0 || IsFaceVisited(faceId))
                {
                    m_CornerTraversalStack.RemoveAt(m_CornerTraversalStack.Length - 1);
                    continue;
                }
                while (true)
                {
                    m_IsFaceVisited[faceId] = true;
                    var vertId = m_CornerTable.Vertex(cornerId);
                    if (vertId < 0)
                    {
                        return false;
                    }
                    if (!m_IsVertexVisited[vertId])
                    {
                        var onBoundary = IsOnBoundary(vertId);
                        m_IsVertexVisited[vertId] = true;
                        OnNewVertexVisited(vertId, cornerId);
                        if (!onBoundary)
                        {
                            cornerId = GetRightCorner(cornerId);
                            faceId = cornerId / 3;
                            continue;
                        }
                    }

                    // The vertex was already visited or lies on a boundary. Determine
                    // whether one of the neighbouring faces can be visited.
                    var rightCornerId = GetRightCorner(cornerId);
                    var leftCornerId = GetLeftCorner(cornerId);
                    var rightFaceId = rightCornerId < 0 ? DracoConstants.invalidIndex : rightCornerId / 3;
                    var leftFaceId = leftCornerId < 0 ? DracoConstants.invalidIndex : leftCornerId / 3;
                    if (IsFaceVisited(rightFaceId))
                    {
                        if (IsFaceVisited(leftFaceId))
                        {
                            // Both neighbours were visited, the traversal ends here.
                            m_CornerTraversalStack.RemoveAt(m_CornerTraversalStack.Length - 1);
                            break;
                        }
                        cornerId = leftCornerId;
                        faceId = leftFaceId;
                    }
                    else
                    {
                        if (IsFaceVisited(leftFaceId))
                        {
                            cornerId = rightCornerId;
                            faceId = rightFaceId;
                        }
                        else
                        {
                            // Split the traversal: process the right face first and keep the
                            // left face on the stack for later.
                            m_CornerTraversalStack[m_CornerTraversalStack.Length - 1] = leftCornerId;
                            m_CornerTraversalStack.Add(rightCornerId);
                            break;
                        }
                    }
                }
            }
            return true;
        }

        bool IsOnBoundary(int vertex)
        {
            return m_CornerTable.IsAttributeTable
                ? m_CornerTable.attributeCornerTable->IsOnBoundary(vertex)
                : m_CornerTable.cornerTable->IsOnBoundary(vertex);
        }

        int GetRightCorner(int corner)
        {
            return m_CornerTable.IsAttributeTable
                ? m_CornerTable.attributeCornerTable->GetRightCorner(corner)
                : m_CornerTable.cornerTable->GetRightCorner(corner);
        }

        int GetLeftCorner(int corner)
        {
            return m_CornerTable.IsAttributeTable
                ? m_CornerTable.attributeCornerTable->GetLeftCorner(corner)
                : m_CornerTable.cornerTable->GetLeftCorner(corner);
        }

        UnsafeList<int>* GetStack(int priority)
        {
            switch (priority)
            {
                case 0:
                    return (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_Stack0);
                case 1:
                    return (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_Stack1);
                default:
                    return (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_Stack2);
            }
        }

        int PopNextCornerToTraverse()
        {
            for (var i = m_BestPriority; i < k_MaxPriority; ++i)
            {
                var stack = GetStack(i);
                if (stack->Length > 0)
                {
                    var result = (*stack)[stack->Length - 1];
                    stack->RemoveAt(stack->Length - 1);
                    m_BestPriority = i;
                    return result;
                }
            }
            return DracoConstants.invalidCornerIndex;
        }

        void AddCornerToTraversalStack(int corner, int priority)
        {
            GetStack(priority)->Add(corner);
            if (priority < m_BestPriority)
            {
                m_BestPriority = priority;
            }
        }

        int ComputePriority(int cornerId)
        {
            var vTip = m_CornerTable.Vertex(cornerId);
            // Visiting an already visited vertex has the highest priority.
            var priority = 0;
            if (vTip >= 0 && !m_IsVertexVisited[vTip])
            {
                var degree = ++m_PredictionDegree.ElementAt(vTip);
                priority = degree > 1 ? 1 : 2;
            }
            if (priority >= k_MaxPriority)
            {
                priority = k_MaxPriority - 1;
            }
            return priority;
        }

        bool TraverseFromCornerMaxPredictionDegree(int cornerId)
        {
            if (m_PredictionDegree.Length == 0)
            {
                return true;
            }

            m_Stack0.Add(cornerId);
            m_BestPriority = 0;

            // The remaining corners of the first face may not be processed yet.
            var nextVert = m_CornerTable.Vertex(CornerTable.Next(cornerId));
            var prevVert = m_CornerTable.Vertex(CornerTable.Previous(cornerId));
            if (nextVert >= 0 && !m_IsVertexVisited[nextVert])
            {
                m_IsVertexVisited[nextVert] = true;
                OnNewVertexVisited(nextVert, CornerTable.Next(cornerId));
            }
            if (prevVert >= 0 && !m_IsVertexVisited[prevVert])
            {
                m_IsVertexVisited[prevVert] = true;
                OnNewVertexVisited(prevVert, CornerTable.Previous(cornerId));
            }
            var tipVertex = m_CornerTable.Vertex(cornerId);
            if (tipVertex >= 0 && !m_IsVertexVisited[tipVertex])
            {
                m_IsVertexVisited[tipVertex] = true;
                OnNewVertexVisited(tipVertex, cornerId);
            }

            while ((cornerId = PopNextCornerToTraverse()) >= 0)
            {
                var faceId = cornerId / 3;
                if (IsFaceVisited(faceId))
                {
                    continue;
                }

                while (true)
                {
                    faceId = cornerId / 3;
                    m_IsFaceVisited[faceId] = true;

                    var vertId = m_CornerTable.Vertex(cornerId);
                    if (vertId < 0)
                    {
                        return false;
                    }
                    if (!m_IsVertexVisited[vertId])
                    {
                        m_IsVertexVisited[vertId] = true;
                        OnNewVertexVisited(vertId, cornerId);
                    }

                    var rightCornerId = GetRightCorner(cornerId);
                    var leftCornerId = GetLeftCorner(cornerId);
                    var rightFaceId = rightCornerId < 0 ? DracoConstants.invalidIndex : rightCornerId / 3;
                    var leftFaceId = leftCornerId < 0 ? DracoConstants.invalidIndex : leftCornerId / 3;
                    var isRightFaceVisited = IsFaceVisited(rightFaceId);
                    var isLeftFaceVisited = IsFaceVisited(leftFaceId);

                    if (!isLeftFaceVisited)
                    {
                        var priority = ComputePriority(leftCornerId);
                        if (isRightFaceVisited && priority <= m_BestPriority)
                        {
                            // The left face would be traversed next anyway.
                            cornerId = leftCornerId;
                            continue;
                        }
                        AddCornerToTraversalStack(leftCornerId, priority);
                    }
                    if (!isRightFaceVisited)
                    {
                        var priority = ComputePriority(rightCornerId);
                        if (priority <= m_BestPriority)
                        {
                            // The right face would be traversed next anyway.
                            cornerId = rightCornerId;
                            continue;
                        }
                        AddCornerToTraversalStack(rightCornerId, priority);
                    }

                    break;
                }
            }
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_IsFaceVisited.IsCreated)
            {
                m_IsFaceVisited.Dispose();
            }
            if (m_IsVertexVisited.IsCreated)
            {
                m_IsVertexVisited.Dispose();
            }
            if (m_CornerTraversalStack.IsCreated)
            {
                m_CornerTraversalStack.Dispose();
            }
            if (m_PredictionDegree.IsCreated)
            {
                m_PredictionDegree.Dispose();
            }
            if (m_Stack0.IsCreated)
            {
                m_Stack0.Dispose();
            }
            if (m_Stack1.IsCreated)
            {
                m_Stack1.Dispose();
            }
            if (m_Stack2.IsCreated)
            {
                m_Stack2.Dispose();
            }
        }
    }
}
