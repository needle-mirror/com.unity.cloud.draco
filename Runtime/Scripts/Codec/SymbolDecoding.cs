// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Decodes symbols encoded with Draco's rANS symbol encoder, including the probability table.
    /// Mirrors <c>draco::RAnsSymbolDecoder</c>.
    /// </summary>
    unsafe struct RAnsSymbolDecoder : IDisposable
    {
        UnsafeList<uint> m_ProbabilityTable;
        uint m_NumSymbols;
        RAnsDecoder m_Ans;

        /// <summary>
        /// Number of symbols in the decoded alphabet.
        /// </summary>
        public uint NumSymbols => m_NumSymbols;

        /// <summary>
        /// Reads the probability table from the buffer and builds the look-up table.
        /// </summary>
        /// <param name="uniqueSymbolsBitLength">Bit length of the largest symbol.</param>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False if the table is malformed.</returns>
        public bool Create(int uniqueSymbolsBitLength, DecoderBuffer* buffer)
        {
            m_Ans.Init(RAnsDecoder.ComputePrecisionFromUniqueSymbolsBitLength(uniqueSymbolsBitLength));

            if (buffer->BitstreamVersion == 0)
            {
                return false;
            }

            if (!buffer->DecodeVarintU32(out m_NumSymbols))
            {
                return false;
            }

            // Guard against unreasonably large symbol counts. The probability table needs at
            // least |num_symbols| / 64 bytes because run-length coded zero entries cover at
            // most 64 symbols per byte.
            if (m_NumSymbols / 64 > buffer->RemainingSize)
            {
                return false;
            }

            m_ProbabilityTable = new UnsafeList<uint>((int)m_NumSymbols, Allocator.Persistent);
            m_ProbabilityTable.Resize((int)m_NumSymbols, NativeArrayOptions.ClearMemory);
            if (m_NumSymbols == 0)
            {
                return true;
            }

            for (uint i = 0; i < m_NumSymbols; ++i)
            {
                if (!buffer->Decode(out byte probabilityData))
                {
                    return false;
                }
                // The token is stored in the two least significant bits. Values 0-2 denote the
                // number of extra bytes, value 3 denotes run-length coded zero probabilities.
                var token = probabilityData & 3;
                if (token == 3)
                {
                    var offset = (uint)(probabilityData >> 2);
                    if (i + offset >= m_NumSymbols)
                    {
                        return false;
                    }
                    for (uint j = 0; j < offset + 1; ++j)
                    {
                        m_ProbabilityTable[(int)(i + j)] = 0;
                    }
                    i += offset;
                }
                else
                {
                    var extraBytes = token;
                    var probability = (uint)(probabilityData >> 2);
                    for (var b = 0; b < extraBytes; ++b)
                    {
                        if (!buffer->Decode(out byte extraByte))
                        {
                            return false;
                        }
                        // Shift by 8 bits per extra byte, minus the two token bits.
                        probability |= (uint)extraByte << (8 * (b + 1) - 2);
                    }
                    m_ProbabilityTable[(int)i] = probability;
                }
            }

            return m_Ans.BuildLookUpTable(m_ProbabilityTable.Ptr, m_NumSymbols);
        }

        /// <summary>
        /// Starts decoding symbols and advances the buffer past the rANS payload.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on buffer underflow or malformed data.</returns>
        public bool StartDecoding(DecoderBuffer* buffer)
        {
            if (!buffer->DecodeVarintU64(out var bytesEncoded))
            {
                return false;
            }
            if (bytesEncoded > (ulong)buffer->RemainingSize)
            {
                return false;
            }
            var dataHead = buffer->DataHead;
            buffer->Advance((long)bytesEncoded);
            return m_Ans.ReadInit(dataHead, (int)bytesEncoded) == 0;
        }

        /// <summary>
        /// Decodes the next symbol.
        /// </summary>
        /// <returns>The decoded symbol.</returns>
        public uint DecodeSymbol()
        {
            return m_Ans.Read();
        }

        /// <summary>
        /// Finishes decoding.
        /// </summary>
        public void EndDecoding()
        {
            m_Ans.ReadEnd();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_ProbabilityTable.IsCreated)
            {
                m_ProbabilityTable.Dispose();
            }
            m_Ans.Dispose();
        }
    }

    /// <summary>
    /// Entry point for decoding entropy coded symbol sequences.
    /// Mirrors <c>draco/compression/entropy/symbol_decoding.cc</c>.
    /// </summary>
    static unsafe class SymbolDecoding
    {
        /// <summary>
        /// Decodes a sequence of symbols using the scheme recorded in the bit-stream.
        /// </summary>
        /// <param name="numValues">Number of values to decode.</param>
        /// <param name="numComponents">Number of components per value tuple.</param>
        /// <param name="srcBuffer">Source buffer.</param>
        /// <param name="outValues">Destination for the decoded symbols.</param>
        /// <returns>False on malformed input.</returns>
        public static bool DecodeSymbols(
            uint numValues,
            int numComponents,
            DecoderBuffer* srcBuffer,
            uint* outValues
            )
        {
            if (numValues == 0)
            {
                return true;
            }
            if (!srcBuffer->Decode(out byte scheme))
            {
                return false;
            }
            switch ((SymbolCodingMethod)scheme)
            {
                case SymbolCodingMethod.Tagged:
                    return DecodeTaggedSymbols(numValues, numComponents, srcBuffer, outValues);
                case SymbolCodingMethod.Raw:
                    return DecodeRawSymbols(numValues, srcBuffer, outValues);
                default:
                    return false;
            }
        }

        static bool DecodeTaggedSymbols(
            uint numValues,
            int numComponents,
            DecoderBuffer* srcBuffer,
            uint* outValues
            )
        {
            var tagDecoder = new RAnsSymbolDecoder();
            var success = DecodeTaggedSymbolsInternal(
                ref tagDecoder, numValues, numComponents, srcBuffer, outValues);
            tagDecoder.Dispose();
            return success;
        }

        static bool DecodeTaggedSymbolsInternal(
            ref RAnsSymbolDecoder tagDecoder,
            uint numValues,
            int numComponents,
            DecoderBuffer* srcBuffer,
            uint* outValues
            )
        {
            if (!tagDecoder.Create(5, srcBuffer))
            {
                return false;
            }
            if (!tagDecoder.StartDecoding(srcBuffer))
            {
                return false;
            }
            if (numValues > 0 && tagDecoder.NumSymbols == 0)
            {
                return false;
            }

            // The buffer now points behind the encoded tag data, at the values.
            srcBuffer->StartBitDecoding(false, out _);
            var valueId = 0;
            for (uint i = 0; i < numValues; i += (uint)numComponents)
            {
                var bitLength = tagDecoder.DecodeSymbol();
                for (var j = 0; j < numComponents; ++j)
                {
                    if (!srcBuffer->DecodeLeastSignificantBits32(bitLength, out var value))
                    {
                        return false;
                    }
                    outValues[valueId++] = value;
                }
            }
            tagDecoder.EndDecoding();
            srcBuffer->EndBitDecoding();
            return true;
        }

        static bool DecodeRawSymbols(uint numValues, DecoderBuffer* srcBuffer, uint* outValues)
        {
            if (!srcBuffer->Decode(out byte maxBitLength))
            {
                return false;
            }
            if (maxBitLength < 1 || maxBitLength > 18)
            {
                return false;
            }

            var decoder = new RAnsSymbolDecoder();
            var success = DecodeRawSymbolsInternal(ref decoder, maxBitLength, numValues, srcBuffer, outValues);
            decoder.Dispose();
            return success;
        }

        static bool DecodeRawSymbolsInternal(
            ref RAnsSymbolDecoder decoder,
            int maxBitLength,
            uint numValues,
            DecoderBuffer* srcBuffer,
            uint* outValues
            )
        {
            if (!decoder.Create(maxBitLength, srcBuffer))
            {
                return false;
            }
            if (numValues > 0 && decoder.NumSymbols == 0)
            {
                return false;
            }
            if (!decoder.StartDecoding(srcBuffer))
            {
                return false;
            }
            for (uint i = 0; i < numValues; ++i)
            {
                outValues[i] = decoder.DecodeSymbol();
            }
            decoder.EndDecoding();
            return true;
        }
    }
}
