// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Edgebreaker topology symbols as stored in the bit-stream.
    /// Mirrors <c>draco::EdgebreakerTopologyBitPattern</c>.
    /// </summary>
    static class EdgebreakerTopology
    {
        public const uint c = 0x0;
        public const uint s = 0x1;
        public const uint l = 0x3;
        public const uint r = 0x5;
        public const uint e = 0x7;
        public const uint initFace = 0x8;
        public const uint invalid = 0x9;

        /// <summary>
        /// Maps a zero based symbol id onto its topology bit pattern.
        /// Mirrors <c>edge_breaker_symbol_to_topology_id</c>.
        /// </summary>
        /// <param name="symbolId">Symbol id in the range zero to four.</param>
        /// <returns>The topology bit pattern.</returns>
        public static uint SymbolToTopology(uint symbolId)
        {
            switch (symbolId)
            {
                case 0: return c;
                case 1: return s;
                case 2: return l;
                case 3: return r;
                default: return e;
            }
        }

        /// <summary>
        /// Maps a topology bit pattern onto its zero based symbol id.
        /// Mirrors <c>edge_breaker_topology_to_symbol_id</c>.
        /// </summary>
        /// <param name="topology">Topology bit pattern.</param>
        /// <returns>Symbol id in the range zero to four.</returns>
        public static uint TopologyToSymbol(uint topology)
        {
            switch (topology)
            {
                case c: return 0;
                case s: return 1;
                case l: return 2;
                case r: return 3;
                default: return 4;
            }
        }

        /// <summary>
        /// Number of bits a topology bit pattern occupies in the bit-stream.
        /// Mirrors <c>edge_breaker_topology_bit_pattern_length</c>.
        /// </summary>
        /// <param name="topology">Topology bit pattern.</param>
        /// <returns>Number of bits.</returns>
        public static int BitPatternLength(uint topology)
        {
            return topology == c ? 1 : 3;
        }
    }

    /// <summary>
    /// Decodes the edgebreaker traversal symbols, start face configurations and attribute
    /// seams. Combines <c>draco::MeshEdgebreakerTraversalDecoder</c> and
    /// <c>draco::MeshEdgebreakerTraversalValenceDecoder</c>.
    /// </summary>
    unsafe struct EdgebreakerTraversalDecoder : IDisposable
    {
        /// <summary>Which edgebreaker variant produced the data.</summary>
        public EdgebreakerConnectivityMethod method;

        DecoderBuffer m_Buffer;
        DecoderBuffer m_SymbolBuffer;
        RAnsBitDecoder m_StartFaceDecoder;
        UnsafeList<RAnsBitDecoder> m_AttributeConnectivityDecoders;
        int m_NumAttributeData;

        // Valence decoder state.
        [NativeDisableUnsafePtrRestriction]
        CornerTable* m_CornerTable;
        int m_NumVertices;
        UnsafeList<int> m_VertexValences;
        uint m_LastSymbol;
        int m_ActiveContext;
        int m_MinValence;
        int m_MaxValence;
        UnsafeList<IntPtr> m_ContextSymbols;
        UnsafeList<int> m_ContextCounters;

        /// <summary>
        /// Points the traversal decoder at the remaining connectivity data.
        /// </summary>
        /// <param name="sourceBuffer">Decoder buffer positioned at the traversal data.</param>
        /// <param name="cornerTable">Corner table filled during the traversal.</param>
        public void Init(DecoderBuffer* sourceBuffer, CornerTable* cornerTable)
        {
            m_Buffer.Init(sourceBuffer->DataHead, sourceBuffer->RemainingSize, sourceBuffer->BitstreamVersion);
            m_CornerTable = cornerTable;
            m_LastSymbol = EdgebreakerTopology.invalid;
            m_ActiveContext = -1;
            m_MinValence = 2;
            m_MaxValence = 7;
        }

        /// <summary>
        /// Sets the number of vertices the encoder produced, including split vertices.
        /// </summary>
        /// <param name="numVertices">Expected vertex count.</param>
        public void SetNumEncodedVertices(int numVertices)
        {
            m_NumVertices = numVertices;
        }

        /// <summary>
        /// Sets the number of non-position attributes whose connectivity must be decoded.
        /// </summary>
        /// <param name="numData">Number of attribute data entries.</param>
        public void SetNumAttributeData(int numData)
        {
            m_NumAttributeData = numData;
        }

        /// <summary>
        /// Reads the traversal headers.
        /// </summary>
        /// <param name="outBuffer">Receives a buffer positioned after the traversal data.</param>
        /// <returns>False on malformed input.</returns>
        public bool Start(DecoderBuffer* outBuffer)
        {
            if (method == EdgebreakerConnectivityMethod.Valence)
            {
                return StartValence(outBuffer);
            }

            if (!DecodeTraversalSymbols())
            {
                return false;
            }
            if (!DecodeStartFaces())
            {
                return false;
            }
            if (!DecodeAttributeSeams())
            {
                return false;
            }
            *outBuffer = m_Buffer;
            return true;
        }

        bool StartValence(DecoderBuffer* outBuffer)
        {
            if (!DecodeStartFaces())
            {
                return false;
            }
            if (!DecodeAttributeSeams())
            {
                return false;
            }
            *outBuffer = m_Buffer;

            m_MinValence = 2;
            m_MaxValence = 7;

            if (m_NumVertices < 0)
            {
                return false;
            }
            if (!m_VertexValences.IsCreated)
            {
                m_VertexValences = new UnsafeList<int>(m_NumVertices, Allocator.Persistent);
            }
            m_VertexValences.Resize(m_NumVertices, NativeArrayOptions.ClearMemory);

            var numUniqueValences = m_MaxValence - m_MinValence + 1;
            m_ContextSymbols = new UnsafeList<IntPtr>(numUniqueValences, Allocator.Persistent);
            m_ContextSymbols.Resize(numUniqueValences, NativeArrayOptions.ClearMemory);
            m_ContextCounters = new UnsafeList<int>(numUniqueValences, Allocator.Persistent);
            m_ContextCounters.Resize(numUniqueValences, NativeArrayOptions.ClearMemory);

            for (var i = 0; i < numUniqueValences; ++i)
            {
                if (!outBuffer->DecodeVarintU32(out var numSymbols))
                {
                    return false;
                }
                if (numSymbols > (uint)m_CornerTable->NumFaces)
                {
                    return false;
                }
                if (numSymbols > 0)
                {
                    var symbols = Unmanaged.New<UnsafeList<uint>>();
                    *symbols = new UnsafeList<uint>((int)numSymbols, Allocator.Persistent);
                    symbols->Resize((int)numSymbols, NativeArrayOptions.ClearMemory);
                    m_ContextSymbols[i] = (IntPtr)symbols;
                    if (!SymbolDecoding.DecodeSymbols(numSymbols, 1, outBuffer, symbols->Ptr))
                    {
                        return false;
                    }
                    // Symbols are consumed from the back.
                    m_ContextCounters[i] = (int)numSymbols;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns whether the next start face is an interior face.
        /// </summary>
        /// <returns>True for an interior start face.</returns>
        public bool DecodeStartFaceConfiguration()
        {
            var startFaceDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_StartFaceDecoder);
            return startFaceDecoder->DecodeNextBit();
        }

        /// <summary>
        /// Decodes the next edgebreaker symbol.
        /// </summary>
        /// <returns>The topology bit pattern of the symbol.</returns>
        public uint DecodeSymbol()
        {
            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                return DecodeSymbolStandard();
            }

            if (m_ActiveContext != -1)
            {
                var contextCounter = --m_ContextCounters.ElementAt(m_ActiveContext);
                if (contextCounter < 0)
                {
                    return EdgebreakerTopology.invalid;
                }
                var symbols = (UnsafeList<uint>*)m_ContextSymbols[m_ActiveContext];
                if (symbols == null)
                {
                    return EdgebreakerTopology.invalid;
                }
                var symbolId = (*symbols)[contextCounter];
                if (symbolId > 4)
                {
                    return EdgebreakerTopology.invalid;
                }
                m_LastSymbol = EdgebreakerTopology.SymbolToTopology(symbolId);
            }
            else
            {
                // The first symbol is always E.
                m_LastSymbol = EdgebreakerTopology.e;
            }
            return m_LastSymbol;
        }

        uint DecodeSymbolStandard()
        {
            var symbolBuffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref m_SymbolBuffer);
            symbolBuffer->DecodeLeastSignificantBits32(1, out var symbol);
            if (symbol == EdgebreakerTopology.c)
            {
                return symbol;
            }
            symbolBuffer->DecodeLeastSignificantBits32(2, out var symbolSuffix);
            return symbol | (symbolSuffix << 1);
        }

        /// <summary>
        /// Updates the valence bookkeeping after a new active corner was reached.
        /// </summary>
        /// <param name="corner">The new active corner.</param>
        public void NewActiveCornerReached(int corner)
        {
            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                return;
            }
            var next = CornerTable.Next(corner);
            var prev = CornerTable.Previous(corner);
            var valences = m_VertexValences.Ptr;
            switch (m_LastSymbol)
            {
                case EdgebreakerTopology.c:
                case EdgebreakerTopology.s:
                    valences[m_CornerTable->Vertex(next)] += 1;
                    valences[m_CornerTable->Vertex(prev)] += 1;
                    break;
                case EdgebreakerTopology.r:
                    valences[m_CornerTable->Vertex(corner)] += 1;
                    valences[m_CornerTable->Vertex(next)] += 1;
                    valences[m_CornerTable->Vertex(prev)] += 2;
                    break;
                case EdgebreakerTopology.l:
                    valences[m_CornerTable->Vertex(corner)] += 1;
                    valences[m_CornerTable->Vertex(next)] += 2;
                    valences[m_CornerTable->Vertex(prev)] += 1;
                    break;
                case EdgebreakerTopology.e:
                    valences[m_CornerTable->Vertex(corner)] += 2;
                    valences[m_CornerTable->Vertex(next)] += 2;
                    valences[m_CornerTable->Vertex(prev)] += 2;
                    break;
            }

            // Select the entropy context for the next symbol.
            var activeValence = valences[m_CornerTable->Vertex(next)];
            int clampedValence;
            if (activeValence < m_MinValence)
            {
                clampedValence = m_MinValence;
            }
            else if (activeValence > m_MaxValence)
            {
                clampedValence = m_MaxValence;
            }
            else
            {
                clampedValence = activeValence;
            }
            m_ActiveContext = clampedValence - m_MinValence;
        }

        /// <summary>
        /// Merges the valence bookkeeping of two vertices.
        /// </summary>
        /// <param name="destination">Vertex that remains.</param>
        /// <param name="source">Vertex that is merged away.</param>
        public void MergeVertices(int destination, int source)
        {
            if (method != EdgebreakerConnectivityMethod.Valence)
            {
                return;
            }
            m_VertexValences.ElementAt(destination) += m_VertexValences[source];
        }

        /// <summary>
        /// Returns whether the next processed edge is an attribute seam.
        /// </summary>
        /// <param name="attribute">Index of the non-position attribute.</param>
        /// <returns>True if the edge is a seam.</returns>
        public bool DecodeAttributeSeam(int attribute)
        {
            return m_AttributeConnectivityDecoders.ElementAt(attribute).DecodeNextBit();
        }

        /// <summary>
        /// Finishes the traversal decoding.
        /// </summary>
        public void Done()
        {
            var symbolBuffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref m_SymbolBuffer);
            if (symbolBuffer->BitDecoderActive)
            {
                symbolBuffer->EndBitDecoding();
            }
        }

        bool DecodeTraversalSymbols()
        {
            m_SymbolBuffer = m_Buffer;
            var symbolBuffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref m_SymbolBuffer);
            if (!symbolBuffer->StartBitDecoding(true, out var traversalSize))
            {
                return false;
            }
            m_Buffer = m_SymbolBuffer;
            if (traversalSize > (ulong)m_Buffer.RemainingSize)
            {
                return false;
            }
            m_Buffer.Advance((long)traversalSize);
            return true;
        }

        bool DecodeStartFaces()
        {
            var buffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref m_Buffer);
            var startFaceDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_StartFaceDecoder);
            return startFaceDecoder->StartDecoding(buffer);
        }

        bool DecodeAttributeSeams()
        {
            if (m_NumAttributeData <= 0)
            {
                return true;
            }
            m_AttributeConnectivityDecoders =
                new UnsafeList<RAnsBitDecoder>(m_NumAttributeData, Allocator.Persistent);
            m_AttributeConnectivityDecoders.Resize(m_NumAttributeData, NativeArrayOptions.ClearMemory);
            var buffer = (DecoderBuffer*)UnsafeUtility.AddressOf(ref m_Buffer);
            for (var i = 0; i < m_NumAttributeData; ++i)
            {
                if (!m_AttributeConnectivityDecoders.ElementAt(i).StartDecoding(buffer))
                {
                    return false;
                }
            }
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_AttributeConnectivityDecoders.IsCreated)
            {
                m_AttributeConnectivityDecoders.Dispose();
            }
            if (m_VertexValences.IsCreated)
            {
                m_VertexValences.Dispose();
            }
            if (m_ContextSymbols.IsCreated)
            {
                for (var i = 0; i < m_ContextSymbols.Length; ++i)
                {
                    var symbols = (UnsafeList<uint>*)m_ContextSymbols[i];
                    if (symbols != null)
                    {
                        symbols->Dispose();
                        Unmanaged.Free(symbols);
                    }
                }
                m_ContextSymbols.Dispose();
            }
            if (m_ContextCounters.IsCreated)
            {
                m_ContextCounters.Dispose();
            }
        }
    }
}
