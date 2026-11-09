// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    unsafe partial struct CornerTable
    {
        /// <summary>
        /// Builds the connectivity from a list of triangles.
        /// Mirrors <c>CornerTable::Init</c>.
        /// </summary>
        /// <param name="faces">Triangle vertex indices, three per face.</param>
        /// <param name="numFaces">Number of faces.</param>
        /// <returns>False if the connectivity could not be established.</returns>
        public bool Init(int3* faces, int numFaces)
        {
            var numCorners = numFaces * 3;
            if (!m_CornerToVertexMap.IsCreated)
            {
                m_CornerToVertexMap = new UnsafeList<int>(numCorners, Allocator.Persistent);
            }
            m_CornerToVertexMap.Resize(numCorners, NativeArrayOptions.UninitializedMemory);
            for (var f = 0; f < numFaces; ++f)
            {
                var face = faces[f];
                m_CornerToVertexMap[f * 3] = face.x;
                m_CornerToVertexMap[f * 3 + 1] = face.y;
                m_CornerToVertexMap[f * 3 + 2] = face.z;
            }
            m_NumDegeneratedFaces = 0;
            m_NumIsolatedVertices = 0;

            if (!ComputeOppositeCorners(out var numVertices))
            {
                return false;
            }
            if (!BreakNonManifoldEdges())
            {
                return false;
            }
            return ComputeVertexCorners(numVertices);
        }

        /// <summary>
        /// Returns true when a face has duplicated vertex indices.
        /// Mirrors <c>CornerTable::IsDegenerated</c>.
        /// </summary>
        /// <param name="face">Face index.</param>
        /// <returns>True if the face is degenerate or invalid.</returns>
        public bool IsDegenerated(int face)
        {
            if (face < 0)
            {
                return true;
            }
            var firstCorner = FirstCorner(face);
            var v0 = Vertex(firstCorner);
            var v1 = Vertex(Next(firstCorner));
            var v2 = Vertex(Previous(firstCorner));
            return v0 == v1 || v0 == v2 || v1 == v2;
        }

        /// <summary>
        /// Half edge slot used while matching opposite corners.
        /// </summary>
        struct VertexEdgePair
        {
            public int sinkVertex;
            public int edgeCorner;
        }

        /// <summary>
        /// Matches half edges to find the opposite corner of every corner.
        /// Mirrors <c>CornerTable::ComputeOppositeCorners</c>.
        /// </summary>
        bool ComputeOppositeCorners(out int numVertices)
        {
            numVertices = 0;
            var numCorners = NumCorners;
            AllocateFilled(ref m_OppositeCorners, numCorners, DracoConstants.invalidCornerIndex);

            // Number of outgoing half edges per vertex. There is exactly one per corner.
            var numCornersOnVertices = new UnsafeList<int>(numCorners, Allocator.Temp);
            for (var c = 0; c < numCorners; ++c)
            {
                var v1 = Vertex(c);
                if (v1 >= numCornersOnVertices.Length)
                {
                    var oldLength = numCornersOnVertices.Length;
                    numCornersOnVertices.Resize(v1 + 1, NativeArrayOptions.UninitializedMemory);
                    for (var i = oldLength; i <= v1; ++i)
                    {
                        numCornersOnVertices[i] = 0;
                    }
                }
                numCornersOnVertices[v1]++;
            }

            // Every vertex gets a non-overlapping range of half edge slots. Unused slots are
            // marked with an invalid sink vertex. One extra slot guards the read-ahead below.
            var vertexEdges = new UnsafeList<VertexEdgePair>(numCorners + 1, Allocator.Temp);
            vertexEdges.Resize(numCorners + 1, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < vertexEdges.Length; ++i)
            {
                vertexEdges[i] = new VertexEdgePair
                {
                    sinkVertex = DracoConstants.invalidIndex,
                    edgeCorner = DracoConstants.invalidCornerIndex
                };
            }

            var vertexOffset = new UnsafeList<int>(numCornersOnVertices.Length, Allocator.Temp);
            vertexOffset.Resize(numCornersOnVertices.Length, NativeArrayOptions.UninitializedMemory);
            var offset = 0;
            for (var i = 0; i < numCornersOnVertices.Length; ++i)
            {
                vertexOffset[i] = offset;
                offset += numCornersOnVertices[i];
            }

            // Process all half edges, either inserting them or connecting them with their sibling.
            for (var c = 0; c < numCorners; ++c)
            {
                var tipV = Vertex(c);
                var sourceV = Vertex(Next(c));
                var sinkV = Vertex(Previous(c));

                var faceIndex = Face(c);
                if (c == FirstCorner(faceIndex))
                {
                    // Ignore degenerate faces.
                    if (tipV == sourceV || tipV == sinkV || sourceV == sinkV)
                    {
                        ++m_NumDegeneratedFaces;
                        c += 2; // Skip the remaining two corners of the same face.
                        continue;
                    }
                }

                var oppositeCorner = DracoConstants.invalidCornerIndex;
                var numCornersOnVert = numCornersOnVertices[sinkV];
                offset = vertexOffset[sinkV];
                for (var i = 0; i < numCornersOnVert; ++i, ++offset)
                {
                    var otherV = vertexEdges[offset].sinkVertex;
                    if (otherV == DracoConstants.invalidIndex)
                    {
                        break; // No matching half edge on the sink vertex.
                    }
                    if (otherV != sourceV)
                    {
                        continue;
                    }
                    if (tipV == Vertex(vertexEdges[offset].edgeCorner))
                    {
                        continue; // Don't connect mirrored faces.
                    }
                    oppositeCorner = vertexEdges[offset].edgeCorner;
                    // Remove the half edge from the sink vertex by shifting the remaining
                    // entries one slot down.
                    for (var j = i + 1; j < numCornersOnVert; ++j, ++offset)
                    {
                        vertexEdges[offset] = vertexEdges[offset + 1];
                        if (vertexEdges[offset].sinkVertex == DracoConstants.invalidIndex)
                        {
                            break;
                        }
                    }
                    var last = vertexEdges[offset];
                    last.sinkVertex = DracoConstants.invalidIndex;
                    vertexEdges[offset] = last;
                    break;
                }
                if (oppositeCorner == DracoConstants.invalidCornerIndex)
                {
                    // Insert the new half edge on the source vertex.
                    var numCornersOnSourceVert = numCornersOnVertices[sourceV];
                    offset = vertexOffset[sourceV];
                    for (var i = 0; i < numCornersOnSourceVert; ++i, ++offset)
                    {
                        if (vertexEdges[offset].sinkVertex != DracoConstants.invalidIndex)
                        {
                            continue;
                        }
                        vertexEdges[offset] = new VertexEdgePair
                        {
                            sinkVertex = sinkV,
                            edgeCorner = c
                        };
                        break;
                    }
                }
                else
                {
                    m_OppositeCorners[c] = oppositeCorner;
                    m_OppositeCorners[oppositeCorner] = c;
                }
            }

            numVertices = numCornersOnVertices.Length;
            numCornersOnVertices.Dispose();
            vertexEdges.Dispose();
            vertexOffset.Dispose();
            return true;
        }

        /// <summary>
        /// Disconnects faces that share a non-manifold edge caused by a fold in the one ring
        /// around a vertex. New vertices for the resulting patches are created later in
        /// <see cref="ComputeVertexCorners"/>.
        /// Mirrors <c>CornerTable::BreakNonManifoldEdges</c>.
        /// </summary>
        bool BreakNonManifoldEdges()
        {
            var numCorners = NumCorners;
            var visitedCorners = new UnsafeList<bool>(numCorners, Allocator.Temp);
            visitedCorners.Resize(numCorners, NativeArrayOptions.ClearMemory);
            // Pairs of sink vertex and the corner defining the edge on the face.
            var sinkVertices = new UnsafeList<int2>(8, Allocator.Temp);

            bool connectivityUpdated;
            do
            {
                connectivityUpdated = false;
                for (var c = 0; c < numCorners; ++c)
                {
                    if (visitedCorners[c])
                    {
                        continue;
                    }
                    sinkVertices.Clear();

                    // Swing all the way left to find the left-most corner of the vertex.
                    var firstC = c;
                    var currentC = c;
                    int nextC;
                    while ((nextC = SwingLeft(currentC)) != firstC
                        && nextC != DracoConstants.invalidCornerIndex
                        && !visitedCorners[nextC])
                    {
                        currentC = nextC;
                    }

                    firstC = currentC;

                    // Swing right from the first corner and check that all edges are unique.
                    do
                    {
                        visitedCorners[currentC] = true;
                        // Every edge around the shared pivot vertex is uniquely identified by
                        // its sink vertex.
                        var sinkC = Next(currentC);
                        var sinkV = m_CornerToVertexMap[sinkC];

                        // Corner that defines the edge on the face.
                        var edgeCorner = Previous(currentC);
                        var vertexConnectivityUpdated = false;
                        for (var s = 0; s < sinkVertices.Length; ++s)
                        {
                            if (sinkVertices[s].x != sinkV)
                            {
                                continue;
                            }
                            // Sink vertex was already processed, which may indicate a
                            // non-manifold edge that has to be broken.
                            var otherEdgeCorner = sinkVertices[s].y;
                            var oppEdgeCorner = Opposite(edgeCorner);

                            if (oppEdgeCorner == otherEdgeCorner)
                            {
                                continue; // Closing the loop, connectivity stays valid.
                            }

                            var oppOtherEdgeCorner = Opposite(otherEdgeCorner);
                            if (oppEdgeCorner != DracoConstants.invalidCornerIndex)
                            {
                                SetOppositeCorner(oppEdgeCorner, DracoConstants.invalidCornerIndex);
                            }
                            if (oppOtherEdgeCorner != DracoConstants.invalidCornerIndex)
                            {
                                SetOppositeCorner(oppOtherEdgeCorner, DracoConstants.invalidCornerIndex);
                            }
                            SetOppositeCorner(edgeCorner, DracoConstants.invalidCornerIndex);
                            SetOppositeCorner(otherEdgeCorner, DracoConstants.invalidCornerIndex);

                            vertexConnectivityUpdated = true;
                            break;
                        }
                        if (vertexConnectivityUpdated)
                        {
                            // The connectivity changed, so not all corners of this vertex have
                            // been processed and they need to be revisited.
                            connectivityUpdated = true;
                            break;
                        }
                        sinkVertices.Add(new int2(m_CornerToVertexMap[Previous(currentC)], sinkC));

                        currentC = SwingRight(currentC);
                    } while (currentC != firstC && currentC != DracoConstants.invalidCornerIndex);
                }
            } while (connectivityUpdated);

            visitedCorners.Dispose();
            sinkVertices.Dispose();
            return true;
        }

        /// <summary>
        /// Assigns the left-most corner to every vertex, creating new vertices for
        /// non-manifold configurations.
        /// Mirrors <c>CornerTable::ComputeVertexCorners</c>.
        /// </summary>
        bool ComputeVertexCorners(int numVertices)
        {
            AllocateFilled(ref m_VertexCorners, numVertices, DracoConstants.invalidCornerIndex);
            if (!m_NonManifoldVertexParents.IsCreated)
            {
                m_NonManifoldVertexParents = new UnsafeList<int>(0, Allocator.Persistent);
            }
            m_NonManifoldVertexParents.Clear();

            var numCorners = NumCorners;
            var visitedVertices = new UnsafeList<bool>(numVertices, Allocator.Temp);
            visitedVertices.Resize(numVertices, NativeArrayOptions.ClearMemory);
            var visitedCorners = new UnsafeList<bool>(numCorners, Allocator.Temp);
            visitedCorners.Resize(numCorners, NativeArrayOptions.ClearMemory);

            for (var f = 0; f < NumFaces; ++f)
            {
                var firstFaceCorner = FirstCorner(f);
                if (IsDegenerated(f))
                {
                    continue;
                }

                for (var k = 0; k < 3; ++k)
                {
                    var c = firstFaceCorner + k;
                    if (visitedCorners[c])
                    {
                        continue;
                    }
                    var v = m_CornerToVertexMap[c];
                    // One vertex maps to many corners, but only the corner that has a boundary
                    // on its left is stored for boundary vertices. All other corners are then
                    // reachable through SwingRight. For interior vertices the choice is
                    // arbitrary.
                    var isNonManifoldVertex = false;
                    if (visitedVertices[v])
                    {
                        // An unvisited corner of an already visited vertex means the vertex is
                        // non-manifold, so a new vertex is created for it.
                        m_VertexCorners.Add(DracoConstants.invalidCornerIndex);
                        m_NonManifoldVertexParents.Add(v);
                        visitedVertices.Add(false);
                        v = numVertices++;
                        isNonManifoldVertex = true;
                    }
                    visitedVertices[v] = true;

                    // Swing all the way left, marking all corners on the way.
                    var actC = c;
                    while (actC != DracoConstants.invalidCornerIndex)
                    {
                        visitedCorners[actC] = true;
                        // The vertex ends up pointing at the left-most corner.
                        m_VertexCorners[v] = actC;
                        if (isNonManifoldVertex)
                        {
                            m_CornerToVertexMap[actC] = v;
                        }
                        actC = SwingLeft(actC);
                        if (actC == c)
                        {
                            break; // Full circle reached.
                        }
                    }
                    if (actC == DracoConstants.invalidCornerIndex)
                    {
                        // An open boundary was reached, so swing right from the initial corner
                        // to mark the corners in the opposite direction.
                        actC = SwingRight(c);
                        while (actC != DracoConstants.invalidCornerIndex)
                        {
                            visitedCorners[actC] = true;
                            if (isNonManifoldVertex)
                            {
                                m_CornerToVertexMap[actC] = v;
                            }
                            actC = SwingRight(actC);
                        }
                    }
                }
            }

            m_NumIsolatedVertices = 0;
            for (var i = 0; i < visitedVertices.Length; ++i)
            {
                if (!visitedVertices[i])
                {
                    ++m_NumIsolatedVertices;
                }
            }

            visitedVertices.Dispose();
            visitedCorners.Dispose();
            return true;
        }
    }

    unsafe partial struct MeshAttributeCornerTable
    {
        /// <summary>
        /// Derives the attribute connectivity from an attribute by detecting the edges across
        /// which the attribute values differ.
        /// Mirrors <c>MeshAttributeCornerTable::InitFromAttribute</c>.
        /// </summary>
        /// <param name="faces">Triangle point indices of the mesh, three per face.</param>
        /// <param name="table">Base corner table.</param>
        /// <param name="attribute">Attribute to derive the seams from.</param>
        /// <returns>False if the connectivity is inconsistent.</returns>
        public bool InitFromAttribute(int3* faces, CornerTable* table, PointAttribute* attribute)
        {
            if (!InitEmpty(table))
            {
                return false;
            }

            // Attribute seams need special handling, so find all of them first.
            for (var c = 0; c < table->NumCorners; ++c)
            {
                var f = CornerTable.Face(c);
                if (table->IsDegenerated(f))
                {
                    continue; // Ignore corners on degenerated faces.
                }
                var oppCorner = table->Opposite(c);
                if (oppCorner == DracoConstants.invalidCornerIndex)
                {
                    // Boundary edges are seam edges.
                    m_IsEdgeOnSeam[c] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Next(c))] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Previous(c))] = true;
                    continue;
                }
                if (oppCorner < c)
                {
                    continue; // Opposite corner was already processed.
                }

                var actC = c;
                var actSiblingC = oppCorner;
                for (var i = 0; i < 2; ++i)
                {
                    // The two corners attached to the same vertex but divided by the seam edge.
                    actC = CornerTable.Next(actC);
                    actSiblingC = CornerTable.Previous(actSiblingC);
                    var pointId = CornerToPointId(faces, actC);
                    var siblingPointId = CornerToPointId(faces, actSiblingC);
                    if (attribute->MappedIndex(pointId) == attribute->MappedIndex(siblingPointId))
                    {
                        continue;
                    }
                    m_NoInteriorSeams = false;
                    m_IsEdgeOnSeam[c] = true;
                    m_IsEdgeOnSeam[oppCorner] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Next(c))] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Previous(c))] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Next(oppCorner))] = true;
                    m_IsVertexOnSeam[table->Vertex(CornerTable.Previous(oppCorner))] = true;
                    break;
                }
            }

            return RecomputeVertices(faces, attribute);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int CornerToPointId(int3* faces, int corner)
        {
            return ((int*)faces)[corner];
        }

        /// <summary>
        /// Rebuilds the attribute vertices from the current set of seam edges, mapping each
        /// attribute vertex onto the attribute value index of its corner.
        /// Mirrors <c>MeshAttributeCornerTable::RecomputeVerticesInternal&lt;true&gt;</c>.
        /// </summary>
        /// <param name="faces">Triangle point indices of the mesh, three per face.</param>
        /// <param name="attribute">Attribute the connectivity was derived from.</param>
        /// <returns>False if the connectivity is inconsistent.</returns>
        public bool RecomputeVertices(int3* faces, PointAttribute* attribute)
        {
            var table = m_CornerTable;
            m_VertexToAttributeEntryIdMap.Clear();
            m_VertexToLeftMostCornerMap.Clear();

            var numNewVertices = 0;
            for (var v = 0; v < table->NumVertices; ++v)
            {
                var c = table->LeftMostCorner(v);
                if (c < 0)
                {
                    continue; // Isolated vertex.
                }
                var firstVertId = numNewVertices++;
                m_VertexToAttributeEntryIdMap.Add(
                    attribute->MappedIndex(CornerToPointId(faces, c)));

                var firstC = c;
                // On a seam vertex, walk counter-clockwise to the first corner that defines
                // an attribute seam.
                if (m_IsVertexOnSeam[v])
                {
                    var swung = SwingLeft(firstC);
                    while (swung >= 0)
                    {
                        firstC = swung;
                        swung = SwingLeft(swung);
                        if (swung == c)
                        {
                            // Swinging left from |c| must never return to |c|.
                            return false;
                        }
                    }
                }
                m_CornerToVertexMap[firstC] = firstVertId;
                m_VertexToLeftMostCornerMap.Add(firstC);

                var actC = table->SwingRight(firstC);
                while (actC >= 0 && actC != firstC)
                {
                    if (IsCornerOppositeToSeamEdge(CornerTable.Next(actC)))
                    {
                        firstVertId = numNewVertices++;
                        m_VertexToAttributeEntryIdMap.Add(
                            attribute->MappedIndex(CornerToPointId(faces, actC)));
                        m_VertexToLeftMostCornerMap.Add(actC);
                    }
                    m_CornerToVertexMap[actC] = firstVertId;
                    actC = table->SwingRight(actC);
                }
            }

            return true;
        }
    }
}
