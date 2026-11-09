// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Encodes the edgebreaker traversal symbols, start face configurations and attribute
    /// seams. Combines <c>draco::MeshEdgebreakerTraversalEncoder</c> and
    /// <c>draco::MeshEdgebreakerTraversalValenceEncoder</c>.
    /// </summary>
    unsafe struct EdgebreakerTraversalEncoder : IDisposable
    {
        const int k_MinValence = 2;
        const int k_MaxValence = 7;

        /// <summary>Which edgebreaker variant to produce.</summary>
        public EdgebreakerConnectivityMethod method;

        EncoderBuffer m_TraversalBuffer;
        RAnsBitEncoder m_StartFaceEncoder;
        UnsafeList<RAnsBitEncoder> m_AttributeConnectivityEncoders;
        int m_NumAttributeData;
        int m_NumFaces;

        // Standard encoder state. Symbols are collected and written in reverse order to
        // speed up decoding.
        UnsafeList<uint> m_Symbols;

        // Valence encoder state.
        [NativeDisableUnsafePtrRestriction]
        CornerTable* m_CornerTable;
        [NativeDisableUnsafePtrRestriction]
        MeshEdgebreakerEncoder* m_Impl;
        UnsafeList<int> m_CornerToVertexMap;
        UnsafeList<int> m_VertexValences;
        int m_PrevSymbol;
        int m_LastCorner;
        int m_NumSymbols;
        // One symbol list per valence context.
        UnsafeList<UnsafeList<uint>> m_ContextSymbols;

        /// <summary>The buffer holding the encoded traversal data.</summary>
        public EncoderBuffer Buffer => m_TraversalBuffer;

        /// <summary>Number of encoded symbols.</summary>
        public int NumEncodedSymbols =>
            method == EdgebreakerConnectivityMethod.Valence ? m_NumSymbols : m_Symbols.Length;

        /// <summary>
        /// Initializes the traversal encoder.
        /// </summary>
        /// <param name="connectivityMethod">Edgebreaker variant to produce.</param>
        /// <param name="impl">Connectivity encoder driving the traversal.</param>
        /// <param name="cornerTable">Connectivity that is being encoded.</param>
        /// <param name="numFaces">Number of faces of the input mesh.</param>
        public void Init(
            EdgebreakerConnectivityMethod connectivityMethod,
            MeshEdgebreakerEncoder* impl,
            CornerTable* cornerTable,
            int numFaces
            )
        {
            method = connectivityMethod;
            m_Impl = impl;
            m_CornerTable = cornerTable;
            m_NumFaces = numFaces;
            m_TraversalBuffer = new EncoderBuffer();
            m_TraversalBuffer.Init(1024);
            m_Symbols = new UnsafeList<uint>(numFaces, Allocator.Persistent);
            m_PrevSymbol = -1;
            m_LastCorner = DracoConstants.invalidCornerIndex;
            m_NumSymbols = 0;

            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                return;
            }

            var numVertices = cornerTable->NumVertices;
            m_VertexValences = new UnsafeList<int>(numVertices, Allocator.Persistent);
            m_VertexValences.Resize(numVertices, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numVertices; ++i)
            {
                m_VertexValences[i] = cornerTable->ConfidentValence(i);
            }

            // The corner to vertex map is replicated because split symbols add new vertices
            // while the connectivity is encoded.
            var numCorners = cornerTable->NumCorners;
            m_CornerToVertexMap = new UnsafeList<int>(numCorners, Allocator.Persistent);
            m_CornerToVertexMap.Resize(numCorners, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numCorners; ++i)
            {
                m_CornerToVertexMap[i] = cornerTable->Vertex(i);
            }

            var numUniqueValences = k_MaxValence - k_MinValence + 1;
            m_ContextSymbols = new UnsafeList<UnsafeList<uint>>(
                numUniqueValences, Allocator.Persistent);
            for (var i = 0; i < numUniqueValences; ++i)
            {
                m_ContextSymbols.Add(new UnsafeList<uint>(16, Allocator.Persistent));
            }
        }

        /// <summary>
        /// Sets the number of non-position attributes whose connectivity has to be encoded.
        /// </summary>
        /// <param name="numData">Number of attribute data entries.</param>
        public void SetNumAttributeData(int numData)
        {
            m_NumAttributeData = numData;
        }

        /// <summary>
        /// Prepares the arithmetic coders. Called before the traversal starts.
        /// </summary>
        public void Start()
        {
            var startFaceEncoder = (RAnsBitEncoder*)UnsafeUtility.AddressOf(ref m_StartFaceEncoder);
            startFaceEncoder->StartEncoding();
            if (m_NumAttributeData <= 0)
            {
                return;
            }
            m_AttributeConnectivityEncoders = new UnsafeList<RAnsBitEncoder>(
                m_NumAttributeData, Allocator.Persistent);
            m_AttributeConnectivityEncoders.Resize(
                m_NumAttributeData, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < m_NumAttributeData; ++i)
            {
                m_AttributeConnectivityEncoders.ElementAt(i).StartEncoding();
            }
        }

        /// <summary>
        /// Records the configuration of a face a traversal starts from.
        /// </summary>
        /// <param name="interior">True when the start face has no boundary.</param>
        public void EncodeStartFaceConfiguration(bool interior)
        {
            var startFaceEncoder = (RAnsBitEncoder*)UnsafeUtility.AddressOf(ref m_StartFaceEncoder);
            startFaceEncoder->EncodeBit(interior);
        }

        /// <summary>
        /// Records that the traversal reached a new corner.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        public void NewCornerReached(int corner)
        {
            m_LastCorner = corner;
        }

        /// <summary>
        /// Records an attribute seam between two connected faces.
        /// </summary>
        /// <param name="attribute">Attribute data index.</param>
        /// <param name="isSeam">True when the edge is an attribute seam.</param>
        public void EncodeAttributeSeam(int attribute, bool isSeam)
        {
            m_AttributeConnectivityEncoders.ElementAt(attribute).EncodeBit(isSeam);
        }

        /// <summary>
        /// Records a traversal symbol.
        /// </summary>
        /// <param name="symbol">Topology bit pattern of the symbol.</param>
        public void EncodeSymbol(uint symbol)
        {
            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                // The symbol is stored and encoded once the traversal is complete.
                m_Symbols.Add(symbol);
                return;
            }
            EncodeSymbolValence(symbol);
        }

        void EncodeSymbolValence(uint symbol)
        {
            ++m_NumSymbols;
            // Valences are updated for the part of the mesh that is not encoded yet, because
            // the decoder processes the connectivity in reverse. Adding a symbol either
            // reduces the valences or leaves them unchanged.
            var next = CornerTable.Next(m_LastCorner);
            var prev = CornerTable.Previous(m_LastCorner);
            var valences = m_VertexValences.Ptr;
            var cornerToVertex = m_CornerToVertexMap.Ptr;

            // Valence at the tip corner of the active edge, which the reverse decoding uses to
            // predict the next symbol.
            var activeValence = valences[cornerToVertex[next]];

            switch (symbol)
            {
                case EdgebreakerTopology.c:
                case EdgebreakerTopology.s:
                    valences[cornerToVertex[next]] -= 1;
                    valences[cornerToVertex[prev]] -= 1;
                    if (symbol == EdgebreakerTopology.s)
                    {
                        SplitVertexForValence(next, prev);
                        // The lists may have been reallocated by the split.
                        valences = m_VertexValences.Ptr;
                        cornerToVertex = m_CornerToVertexMap.Ptr;
                    }
                    break;
                case EdgebreakerTopology.r:
                    valences[cornerToVertex[m_LastCorner]] -= 1;
                    valences[cornerToVertex[next]] -= 1;
                    valences[cornerToVertex[prev]] -= 2;
                    break;
                case EdgebreakerTopology.l:
                    valences[cornerToVertex[m_LastCorner]] -= 1;
                    valences[cornerToVertex[next]] -= 2;
                    valences[cornerToVertex[prev]] -= 1;
                    break;
                case EdgebreakerTopology.e:
                    valences[cornerToVertex[m_LastCorner]] -= 2;
                    valences[cornerToVertex[next]] -= 2;
                    valences[cornerToVertex[prev]] -= 2;
                    break;
            }

            if (m_PrevSymbol != -1)
            {
                int clampedValence;
                if (activeValence < k_MinValence)
                {
                    clampedValence = k_MinValence;
                }
                else if (activeValence > k_MaxValence)
                {
                    clampedValence = k_MaxValence;
                }
                else
                {
                    clampedValence = activeValence;
                }

                var context = clampedValence - k_MinValence;
                var list = m_ContextSymbols[context];
                list.Add(EdgebreakerTopology.TopologyToSymbol((uint)m_PrevSymbol));
                m_ContextSymbols[context] = list;
            }

            m_PrevSymbol = (int)symbol;
        }

        /// <summary>
        /// Splits the vertex of a split symbol into two, attaching the corners on either side
        /// to the respective vertex. The decoder merges them again once it processes the split
        /// symbol, which it does after the surrounding faces.
        /// </summary>
        void SplitVertexForValence(int next, int prev)
        {
            // Count the faces on the left side and update the valence of the left vertex.
            var numLeftFaces = 0;
            var actC = m_CornerTable->Opposite(prev);
            while (actC != DracoConstants.invalidCornerIndex)
            {
                if (m_Impl->IsFaceEncoded(CornerTable.Face(actC)))
                {
                    break; // The first visited face ends the fan.
                }
                ++numLeftFaces;
                actC = m_CornerTable->Opposite(CornerTable.Next(actC));
            }
            m_VertexValences[m_CornerToVertexMap[m_LastCorner]] = numLeftFaces + 1;

            // Create a new vertex for the right side and attach its faces to it.
            var newVertexId = m_VertexValences.Length;
            var numRightFaces = 0;

            actC = m_CornerTable->Opposite(next);
            while (actC != DracoConstants.invalidCornerIndex)
            {
                if (m_Impl->IsFaceEncoded(CornerTable.Face(actC)))
                {
                    break;
                }
                ++numRightFaces;
                m_CornerToVertexMap[CornerTable.Next(actC)] = newVertexId;
                actC = m_CornerTable->Opposite(CornerTable.Previous(actC));
            }
            m_VertexValences.Add(numRightFaces + 1);
        }

        /// <summary>
        /// Finalizes the traversal and writes all collected data.
        /// </summary>
        public void Done()
        {
            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                EncodeTraversalSymbols();
                EncodeStartFaces();
                EncodeAttributeSeams();
                return;
            }

            EncodeStartFaces();
            EncodeAttributeSeams();

            // The per context symbol lists.
            for (var i = 0; i < m_ContextSymbols.Length; ++i)
            {
                var list = m_ContextSymbols[i];
                m_TraversalBuffer.EncodeVarintU32((uint)list.Length);
                if (list.Length > 0)
                {
                    SymbolEncoding.EncodeSymbols(
                        list.Ptr, list.Length, 1, ref m_TraversalBuffer);
                }
            }
        }

        void EncodeTraversalSymbols()
        {
            // Each face needs at most three bits.
            m_TraversalBuffer.StartBitEncoding((long)m_NumFaces * 3, true);
            for (var i = m_Symbols.Length - 1; i >= 0; --i)
            {
                var symbol = m_Symbols[i];
                m_TraversalBuffer.EncodeLeastSignificantBits32(
                    EdgebreakerTopology.BitPatternLength(symbol), symbol);
            }
            m_TraversalBuffer.EndBitEncoding();
        }

        void EncodeStartFaces()
        {
            var startFaceEncoder = (RAnsBitEncoder*)UnsafeUtility.AddressOf(ref m_StartFaceEncoder);
            startFaceEncoder->EndEncoding(ref m_TraversalBuffer);
        }

        void EncodeAttributeSeams()
        {
            if (!m_AttributeConnectivityEncoders.IsCreated)
            {
                return;
            }
            for (var i = 0; i < m_NumAttributeData; ++i)
            {
                m_AttributeConnectivityEncoders.ElementAt(i).EndEncoding(ref m_TraversalBuffer);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_StartFaceEncoder.Dispose();
            if (m_AttributeConnectivityEncoders.IsCreated)
            {
                for (var i = 0; i < m_AttributeConnectivityEncoders.Length; ++i)
                {
                    m_AttributeConnectivityEncoders.ElementAt(i).Dispose();
                }
                m_AttributeConnectivityEncoders.Dispose();
            }
            if (m_Symbols.IsCreated)
            {
                m_Symbols.Dispose();
            }
            if (m_CornerToVertexMap.IsCreated)
            {
                m_CornerToVertexMap.Dispose();
            }
            if (m_VertexValences.IsCreated)
            {
                m_VertexValences.Dispose();
            }
            if (m_ContextSymbols.IsCreated)
            {
                for (var i = 0; i < m_ContextSymbols.Length; ++i)
                {
                    var list = m_ContextSymbols[i];
                    if (list.IsCreated)
                    {
                        list.Dispose();
                    }
                }
                m_ContextSymbols.Dispose();
            }
            if (m_TraversalBuffer.IsCreated)
            {
                m_TraversalBuffer.Dispose();
            }
        }
    }
}
