// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Connectivity of a triangular mesh expressed through corners and their opposites.
    /// Mirrors <c>draco::CornerTable</c>.
    /// </summary>
    unsafe partial struct CornerTable : IDisposable
    {
        UnsafeList<int> m_CornerToVertexMap;
        UnsafeList<int> m_OppositeCorners;
        UnsafeList<int> m_VertexCorners;
        UnsafeList<int> m_NonManifoldVertexParents;
        int m_NumDegeneratedFaces;
        int m_NumIsolatedVertices;

        /// <summary>Number of vertices.</summary>
        public int NumVertices => m_VertexCorners.Length;
        /// <summary>Number of corners, that is three times the face count.</summary>
        public int NumCorners => m_CornerToVertexMap.Length;
        /// <summary>Number of faces.</summary>
        public int NumFaces => m_CornerToVertexMap.Length / 3;
        /// <summary>Number of faces with duplicated vertex indices.</summary>
        public int NumDegeneratedFaces => m_NumDegeneratedFaces;
        /// <summary>Number of vertices not attached to any face.</summary>
        public int NumIsolatedVertices => m_NumIsolatedVertices;

        /// <summary>
        /// Allocates the table for the given number of faces and vertices, marking everything invalid.
        /// Mirrors <c>CornerTable::Reset</c>.
        /// </summary>
        /// <param name="numFaces">Number of faces.</param>
        /// <param name="numVertices">Number of vertices.</param>
        /// <returns>False if the requested size is invalid.</returns>
        public bool Reset(int numFaces, int numVertices)
        {
            if (numFaces < 0 || numVertices < 0)
            {
                return false;
            }
            if (numFaces > int.MaxValue / 3)
            {
                return false;
            }
            var numCorners = numFaces * 3;
            AllocateFilled(ref m_CornerToVertexMap, numCorners, DracoConstants.invalidIndex);
            AllocateFilled(ref m_OppositeCorners, numCorners, DracoConstants.invalidCornerIndex);
            // Vertices are appended one by one during the traversal, so only reserve here.
            if (!m_VertexCorners.IsCreated)
            {
                m_VertexCorners = new UnsafeList<int>(numVertices, Allocator.Persistent);
            }
            m_VertexCorners.Clear();
            m_VertexCorners.SetCapacity(numVertices);
            if (!m_NonManifoldVertexParents.IsCreated)
            {
                m_NonManifoldVertexParents = new UnsafeList<int>(0, Allocator.Persistent);
            }
            m_NonManifoldVertexParents.Clear();
            m_NumDegeneratedFaces = 0;
            m_NumIsolatedVertices = 0;
            return true;
        }

        static void AllocateFilled(ref UnsafeList<int> list, int length, int value)
        {
            if (!list.IsCreated)
            {
                list = new UnsafeList<int>(length, Allocator.Persistent);
            }
            list.Resize(length, NativeArrayOptions.UninitializedMemory);
            var ptr = list.Ptr;
            for (var i = 0; i < length; i++)
            {
                ptr[i] = value;
            }
        }

        /// <summary>
        /// Returns the corner opposite to <paramref name="corner"/>.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Opposite corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Opposite(int corner)
        {
            if (corner < 0)
            {
                return corner;
            }
            return m_OppositeCorners[corner];
        }

        /// <summary>
        /// Returns the next corner within the same face.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Next corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Next(int corner)
        {
            if (corner < 0)
            {
                return corner;
            }
            return LocalIndex(corner + 1) != 0 ? corner + 1 : corner - 2;
        }

        /// <summary>
        /// Returns the previous corner within the same face.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Previous corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Previous(int corner)
        {
            if (corner < 0)
            {
                return corner;
            }
            return LocalIndex(corner) != 0 ? corner - 1 : corner + 2;
        }

        /// <summary>
        /// Returns the vertex a corner maps to.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Vertex index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Vertex(int corner)
        {
            if (corner < 0)
            {
                return DracoConstants.invalidIndex;
            }
            return m_CornerToVertexMap[corner];
        }

        /// <summary>
        /// Returns the face a corner belongs to.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Face index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Face(int corner)
        {
            if (corner < 0)
            {
                return DracoConstants.invalidIndex;
            }
            return corner / 3;
        }

        /// <summary>
        /// Returns the first corner of a face.
        /// </summary>
        /// <param name="face">Face index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FirstCorner(int face)
        {
            if (face < 0)
            {
                return DracoConstants.invalidCornerIndex;
            }
            return face * 3;
        }

        /// <summary>
        /// Returns the position of a corner within its face.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Zero, one or two.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LocalIndex(int corner)
        {
            return corner % 3;
        }

        /// <summary>
        /// Returns the left-most corner of a vertex' one ring.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int LeftMostCorner(int vertex)
        {
            return m_VertexCorners[vertex];
        }

        /// <summary>
        /// Returns the corner on the adjacent face to the right sharing the same vertex.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingRight(int corner)
        {
            return Previous(Opposite(Previous(corner)));
        }

        /// <summary>
        /// Returns the corner on the adjacent face to the left sharing the same vertex.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingLeft(int corner)
        {
            return Next(Opposite(Next(corner)));
        }

        /// <summary>
        /// Returns the opposite corner on the left adjacent face.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLeftCorner(int corner)
        {
            if (corner < 0)
            {
                return DracoConstants.invalidCornerIndex;
            }
            return Opposite(Previous(corner));
        }

        /// <summary>
        /// Returns the opposite corner on the right adjacent face.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetRightCorner(int corner)
        {
            if (corner < 0)
            {
                return DracoConstants.invalidCornerIndex;
            }
            return Opposite(Next(corner));
        }

        /// <summary>
        /// Returns true when the vertex lies on a mesh boundary.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        /// <returns>True if the vertex is on a boundary.</returns>
        public bool IsOnBoundary(int vertex)
        {
            var corner = LeftMostCorner(vertex);
            return SwingLeft(corner) < 0;
        }

        /// <summary>
        /// Returns the number of faces attached to a vertex without validity checks.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        /// <returns>Valence.</returns>
        public int ConfidentValence(int vertex)
        {
            var valence = 0;
            var startCorner = LeftMostCorner(vertex);
            var corner = startCorner;
            var leftTraversal = true;
            while (corner >= 0)
            {
                ++valence;
                if (leftTraversal)
                {
                    corner = SwingLeft(corner);
                    if (corner < 0)
                    {
                        corner = SwingRight(startCorner);
                        leftTraversal = false;
                    }
                    else if (corner == startCorner)
                    {
                        break;
                    }
                }
                else
                {
                    corner = SwingRight(corner);
                }
            }
            return valence;
        }

        /// <summary>
        /// Records the opposite corner of a single corner.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <param name="oppositeCorner">Opposite corner index.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetOppositeCorner(int corner, int oppositeCorner)
        {
            m_OppositeCorners[corner] = oppositeCorner;
        }

        /// <summary>
        /// Maps a corner onto a vertex.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <param name="vertex">Vertex index.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MapCornerToVertex(int corner, int vertex)
        {
            m_CornerToVertexMap[corner] = vertex;
        }

        /// <summary>
        /// Appends a new isolated vertex.
        /// </summary>
        /// <returns>Index of the new vertex.</returns>
        public int AddNewVertex()
        {
            m_VertexCorners.Add(DracoConstants.invalidCornerIndex);
            return m_VertexCorners.Length - 1;
        }

        /// <summary>
        /// Records the left-most corner of a vertex.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        /// <param name="corner">Corner index.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetLeftMostCorner(int vertex, int corner)
        {
            if (vertex >= 0)
            {
                m_VertexCorners[vertex] = corner;
            }
        }

        /// <summary>
        /// Detaches a vertex from all faces.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        public void MakeVertexIsolated(int vertex)
        {
            m_VertexCorners[vertex] = DracoConstants.invalidCornerIndex;
        }

        /// <summary>
        /// Returns true when a vertex is not attached to any face.
        /// </summary>
        /// <param name="vertex">Vertex index.</param>
        /// <returns>True if the vertex is isolated.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsVertexIsolated(int vertex)
        {
            return LeftMostCorner(vertex) < 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_CornerToVertexMap.IsCreated)
            {
                m_CornerToVertexMap.Dispose();
            }
            if (m_OppositeCorners.IsCreated)
            {
                m_OppositeCorners.Dispose();
            }
            if (m_VertexCorners.IsCreated)
            {
                m_VertexCorners.Dispose();
            }
            if (m_NonManifoldVertexParents.IsCreated)
            {
                m_NonManifoldVertexParents.Dispose();
            }
        }
    }

    /// <summary>
    /// Connectivity of a mesh attribute expressed as a difference from the base corner
    /// table through attribute seam edges.
    /// Mirrors <c>draco::MeshAttributeCornerTable</c>.
    /// </summary>
    unsafe partial struct MeshAttributeCornerTable : IDisposable
    {
        UnsafeList<bool> m_IsEdgeOnSeam;
        UnsafeList<bool> m_IsVertexOnSeam;
        bool m_NoInteriorSeams;
        UnsafeList<int> m_CornerToVertexMap;
        UnsafeList<int> m_VertexToLeftMostCornerMap;
        UnsafeList<int> m_VertexToAttributeEntryIdMap;
        [NativeDisableUnsafePtrRestriction]
        CornerTable* m_CornerTable;

        /// <summary>True while no interior attribute seam has been added.</summary>
        public bool NoInteriorSeams => m_NoInteriorSeams;
        /// <summary>Number of attribute vertices.</summary>
        public int NumVertices => m_VertexToAttributeEntryIdMap.Length;
        /// <summary>Number of faces of the base connectivity.</summary>
        public int NumFaces => m_CornerTable->NumFaces;
        /// <summary>Number of corners of the base connectivity.</summary>
        public int NumCorners => m_CornerTable->NumCorners;

        /// <summary>
        /// Initializes an empty attribute corner table over the given base connectivity.
        /// </summary>
        /// <param name="table">Base corner table.</param>
        /// <returns>True on success.</returns>
        public bool InitEmpty(CornerTable* table)
        {
            m_CornerTable = table;
            m_NoInteriorSeams = true;

            var numCorners = table->NumCorners;
            m_IsEdgeOnSeam = new UnsafeList<bool>(numCorners, Allocator.Persistent);
            m_IsEdgeOnSeam.Resize(numCorners, NativeArrayOptions.ClearMemory);
            m_IsVertexOnSeam = new UnsafeList<bool>(table->NumVertices, Allocator.Persistent);
            m_IsVertexOnSeam.Resize(table->NumVertices, NativeArrayOptions.ClearMemory);

            m_CornerToVertexMap = new UnsafeList<int>(numCorners, Allocator.Persistent);
            m_CornerToVertexMap.Resize(numCorners, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numCorners; i++)
            {
                m_CornerToVertexMap[i] = DracoConstants.invalidIndex;
            }

            m_VertexToAttributeEntryIdMap = new UnsafeList<int>(table->NumVertices, Allocator.Persistent);
            m_VertexToLeftMostCornerMap = new UnsafeList<int>(table->NumVertices, Allocator.Persistent);
            return true;
        }

        /// <summary>
        /// Marks the edge opposite to <paramref name="oppositeCorner"/> as an attribute seam.
        /// </summary>
        /// <param name="oppositeCorner">Corner opposite to the seam edge.</param>
        public void AddSeamEdge(int oppositeCorner)
        {
            m_IsEdgeOnSeam[oppositeCorner] = true;
            // Mark seam vertices.
            var table = m_CornerTable;
            m_IsVertexOnSeam[table->Vertex(CornerTable.Next(oppositeCorner))] = true;
            m_IsVertexOnSeam[table->Vertex(CornerTable.Previous(oppositeCorner))] = true;

            var oppOpp = table->Opposite(oppositeCorner);
            if (oppOpp >= 0)
            {
                m_NoInteriorSeams = false;
                m_IsEdgeOnSeam[oppOpp] = true;
                m_IsVertexOnSeam[table->Vertex(CornerTable.Next(oppOpp))] = true;
                m_IsVertexOnSeam[table->Vertex(CornerTable.Previous(oppOpp))] = true;
            }
        }

        /// <summary>
        /// Rebuilds the attribute vertices from the current set of seam edges, using an
        /// identity mapping between attribute vertices and attribute value indices.
        /// Mirrors <c>MeshAttributeCornerTable::RecomputeVerticesInternal&lt;false&gt;</c>.
        /// </summary>
        /// <returns>False if the connectivity is inconsistent.</returns>
        public bool RecomputeVertices()
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
                m_VertexToAttributeEntryIdMap.Add(firstVertId);

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
                        m_VertexToAttributeEntryIdMap.Add(firstVertId);
                        m_VertexToLeftMostCornerMap.Add(actC);
                    }
                    m_CornerToVertexMap[actC] = firstVertId;
                    actC = table->SwingRight(actC);
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when the edge opposite to a corner is an attribute seam.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>True if the opposite edge is a seam.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCornerOppositeToSeamEdge(int corner)
        {
            return m_IsEdgeOnSeam[corner];
        }

        /// <summary>
        /// Returns the opposite corner, stopping at attribute seams.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Opposite corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Opposite(int corner)
        {
            if (corner < 0 || IsCornerOppositeToSeamEdge(corner))
            {
                return DracoConstants.invalidCornerIndex;
            }
            return m_CornerTable->Opposite(corner);
        }

        /// <summary>
        /// Returns true when a corner touches any attribute seam.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>True if the corner is on a seam.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCornerOnSeam(int corner)
        {
            return m_IsVertexOnSeam[m_CornerTable->Vertex(corner)];
        }

        /// <summary>
        /// Returns the left neighbouring corner, stopping at attribute seams.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetLeftCorner(int corner)
        {
            return Opposite(CornerTable.Previous(corner));
        }

        /// <summary>
        /// Returns the right neighbouring corner, stopping at attribute seams.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetRightCorner(int corner)
        {
            return Opposite(CornerTable.Next(corner));
        }

        /// <summary>
        /// Swings right around the vertex, stopping at attribute seams.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingRight(int corner)
        {
            return CornerTable.Previous(Opposite(CornerTable.Previous(corner)));
        }

        /// <summary>
        /// Swings left around the vertex, stopping at attribute seams.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingLeft(int corner)
        {
            return CornerTable.Next(Opposite(CornerTable.Next(corner)));
        }

        /// <summary>
        /// Returns the attribute vertex a corner maps to.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Attribute vertex index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Vertex(int corner)
        {
            return m_CornerToVertexMap[corner];
        }

        /// <summary>
        /// Returns the left-most corner of an attribute vertex.
        /// </summary>
        /// <param name="vertex">Attribute vertex index.</param>
        /// <returns>Corner index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int LeftMostCorner(int vertex)
        {
            return m_VertexToLeftMostCornerMap[vertex];
        }

        /// <summary>
        /// Returns true when an attribute vertex lies on a boundary or seam.
        /// </summary>
        /// <param name="vertex">Attribute vertex index.</param>
        /// <returns>True if the vertex is on a boundary.</returns>
        public bool IsOnBoundary(int vertex)
        {
            var corner = LeftMostCorner(vertex);
            if (corner < 0)
            {
                return true;
            }
            return SwingLeft(corner) < 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_IsEdgeOnSeam.IsCreated)
            {
                m_IsEdgeOnSeam.Dispose();
            }
            if (m_IsVertexOnSeam.IsCreated)
            {
                m_IsVertexOnSeam.Dispose();
            }
            if (m_CornerToVertexMap.IsCreated)
            {
                m_CornerToVertexMap.Dispose();
            }
            if (m_VertexToLeftMostCornerMap.IsCreated)
            {
                m_VertexToLeftMostCornerMap.Dispose();
            }
            if (m_VertexToAttributeEntryIdMap.IsCreated)
            {
                m_VertexToAttributeEntryIdMap.Dispose();
            }
        }
    }

    /// <summary>
    /// Uniform read-only access to either a plain <see cref="CornerTable"/> or a
    /// <see cref="MeshAttributeCornerTable"/>, so prediction schemes can be written once.
    /// </summary>
    unsafe struct CornerTableView
    {
        [NativeDisableUnsafePtrRestriction]
        public CornerTable* cornerTable;
        [NativeDisableUnsafePtrRestriction]
        public MeshAttributeCornerTable* attributeCornerTable;

        /// <summary>True when the view wraps an attribute corner table.</summary>
        public bool IsAttributeTable => attributeCornerTable != null;

        /// <summary>True when the view points at a table.</summary>
        public bool IsValid => cornerTable != null || attributeCornerTable != null;

        /// <summary>
        /// Wraps a plain corner table.
        /// </summary>
        /// <param name="table">Corner table.</param>
        /// <returns>A view over the table.</returns>
        public static CornerTableView FromCornerTable(CornerTable* table)
        {
            return new CornerTableView { cornerTable = table, attributeCornerTable = null };
        }

        /// <summary>
        /// Wraps an attribute corner table.
        /// </summary>
        /// <param name="table">Attribute corner table.</param>
        /// <returns>A view over the table.</returns>
        public static CornerTableView FromAttributeCornerTable(MeshAttributeCornerTable* table)
        {
            return new CornerTableView { cornerTable = null, attributeCornerTable = table };
        }

        /// <summary>Number of corners.</summary>
        public int NumCorners => attributeCornerTable != null
            ? attributeCornerTable->NumCorners
            : cornerTable->NumCorners;

        /// <summary>Number of vertices.</summary>
        public int NumVertices => attributeCornerTable != null
            ? attributeCornerTable->NumVertices
            : cornerTable->NumVertices;

        /// <summary>Number of faces.</summary>
        public int NumFaces => attributeCornerTable != null
            ? attributeCornerTable->NumFaces
            : cornerTable->NumFaces;

        /// <summary>
        /// Returns the vertex a corner maps to.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Vertex index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Vertex(int corner)
        {
            return attributeCornerTable != null
                ? attributeCornerTable->Vertex(corner)
                : cornerTable->Vertex(corner);
        }

        /// <summary>
        /// Returns the opposite corner.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Opposite(int corner)
        {
            return attributeCornerTable != null
                ? attributeCornerTable->Opposite(corner)
                : cornerTable->Opposite(corner);
        }

        /// <summary>
        /// Swings right around the vertex.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingRight(int corner)
        {
            return attributeCornerTable != null
                ? attributeCornerTable->SwingRight(corner)
                : cornerTable->SwingRight(corner);
        }

        /// <summary>
        /// Swings left around the vertex.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Corner index or -1.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int SwingLeft(int corner)
        {
            return attributeCornerTable != null
                ? attributeCornerTable->SwingLeft(corner)
                : cornerTable->SwingLeft(corner);
        }

    }
}
