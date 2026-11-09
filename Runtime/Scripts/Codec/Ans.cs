// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// State of an Asymmetric Numeral Systems decoder.
    /// Mirrors <c>draco::AnsDecoder</c>.
    /// </summary>
    unsafe struct AnsDecoder
    {
        public byte* buffer;
        public int bufferOffset;
        public uint state;
    }

    /// <summary>
    /// Symbol probability entry of the rANS decoder.
    /// Mirrors <c>draco::rans_sym</c>.
    /// </summary>
    struct RansSym
    {
        public uint probability;
        /// <summary>Cumulative probability, not inclusive.</summary>
        public uint cumulativeProbability;
    }

    /// <summary>
    /// Low level Asymmetric Numeral Systems primitives shared by the bit and symbol coders.
    /// Mirrors <c>draco/compression/entropy/ans.h</c>.
    /// </summary>
    static unsafe class Ans
    {
        public const uint probabilityPrecision8 = 256u;
        public const uint baseL = 4096u;
        public const int baseIo = 256;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint MemGetLe16(byte* mem)
        {
            return (uint)(mem[0] | (mem[1] << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint MemGetLe24(byte* mem)
        {
            return (uint)(mem[0] | (mem[1] << 8) | (mem[2] << 16));
        }

        /// <summary>
        /// Initializes an rABS decoder from the tail of the encoded buffer.
        /// Mirrors <c>ans_read_init</c>.
        /// </summary>
        /// <param name="ans">Decoder state to initialize.</param>
        /// <param name="buffer">Encoded data.</param>
        /// <param name="offset">Number of encoded bytes.</param>
        /// <returns>Zero on success, non-zero on error.</returns>
        public static int ReadInit(AnsDecoder* ans, byte* buffer, int offset)
        {
            if (offset < 1)
            {
                return 1;
            }
            ans->buffer = buffer;
            var x = (uint)(buffer[offset - 1] >> 6);
            if (x == 0)
            {
                ans->bufferOffset = offset - 1;
                ans->state = (uint)(buffer[offset - 1] & 0x3F);
            }
            else if (x == 1)
            {
                if (offset < 2)
                {
                    return 1;
                }
                ans->bufferOffset = offset - 2;
                ans->state = MemGetLe16(buffer + offset - 2) & 0x3FFF;
            }
            else if (x == 2)
            {
                if (offset < 3)
                {
                    return 1;
                }
                ans->bufferOffset = offset - 3;
                ans->state = MemGetLe24(buffer + offset - 3) & 0x3FFFFF;
            }
            else
            {
                return 1;
            }
            ans->state += baseL;
            if (ans->state >= baseL * baseIo)
            {
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// Reads a single bit using rABS with descending spread.
        /// Mirrors <c>rabs_desc_read</c>.
        /// </summary>
        /// <param name="ans">Decoder state.</param>
        /// <param name="probabilityZero">Probability of a zero bit, scaled to 8 bits.</param>
        /// <returns>The decoded bit.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int RabsRead(AnsDecoder* ans, byte probabilityZero)
        {
            var p = probabilityPrecision8 - probabilityZero;
            if (ans->state < baseL && ans->bufferOffset > 0)
            {
                ans->state = ans->state * baseIo + ans->buffer[--ans->bufferOffset];
            }
            var x = ans->state;
            var quotient = x / probabilityPrecision8;
            var remainder = x % probabilityPrecision8;
            var xn = quotient * p;
            var value = remainder < p;
            ans->state = value ? xn + remainder : x - xn - p;
            return value ? 1 : 0;
        }

        /// <summary>
        /// Returns true when the decoder consumed exactly the expected amount of data.
        /// Mirrors <c>ans_read_end</c>.
        /// </summary>
        /// <param name="ans">Decoder state.</param>
        /// <returns>True if the final state matches the initial base state.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadEnd(AnsDecoder* ans)
        {
            return ans->state == baseL;
        }
    }

    /// <summary>
    /// rANS decoder for multi symbol alphabets with a run-time selectable precision.
    /// Mirrors <c>draco::RAnsDecoder</c>.
    /// </summary>
    unsafe struct RAnsDecoder : IDisposable
    {
        int m_PrecisionBits;
        int m_Precision;
        int m_BaseL;

        UnsafeList<uint> m_LookUpTable;
        UnsafeList<RansSym> m_ProbabilityTable;
        AnsDecoder m_Ans;

        /// <summary>
        /// Configures the precision of the decoder. Must match the encoder.
        /// </summary>
        /// <param name="precisionBits">Number of precision bits (12 to 20).</param>
        public void Init(int precisionBits)
        {
            m_PrecisionBits = precisionBits;
            m_Precision = 1 << precisionBits;
            m_BaseL = m_Precision * 4;
        }

        /// <summary>
        /// Computes the rANS precision Draco uses for a given symbol bit length.
        /// Mirrors <c>ComputeRAnsPrecisionFromUniqueSymbolsBitLength</c>.
        /// </summary>
        /// <param name="symbolsBitLength">Bit length of the largest symbol.</param>
        /// <returns>Precision in bits, clamped to the range 12 to 20.</returns>
        public static int ComputePrecisionFromUniqueSymbolsBitLength(int symbolsBitLength)
        {
            var unclamped = 3 * symbolsBitLength / 2;
            if (unclamped < 12)
            {
                return 12;
            }
            return unclamped > 20 ? 20 : unclamped;
        }

        /// <summary>
        /// Builds the symbol look-up table from the decoded probability table.
        /// Mirrors <c>rans_build_look_up_table</c>.
        /// </summary>
        /// <param name="tokenProbabilities">Per symbol probabilities.</param>
        /// <param name="numSymbols">Number of symbols.</param>
        /// <returns>False if the probabilities do not add up to the precision.</returns>
        public bool BuildLookUpTable(uint* tokenProbabilities, uint numSymbols)
        {
            m_LookUpTable = new UnsafeList<uint>(m_Precision, Allocator.Persistent);
            m_LookUpTable.Resize(m_Precision, NativeArrayOptions.UninitializedMemory);
            m_ProbabilityTable = new UnsafeList<RansSym>((int)numSymbols, Allocator.Persistent);
            m_ProbabilityTable.Resize((int)numSymbols, NativeArrayOptions.UninitializedMemory);

            uint cumulativeProbability = 0;
            uint activeProbability = 0;
            for (uint i = 0; i < numSymbols; ++i)
            {
                m_ProbabilityTable[(int)i] = new RansSym
                {
                    probability = tokenProbabilities[i],
                    cumulativeProbability = cumulativeProbability
                };
                cumulativeProbability += tokenProbabilities[i];
                if (cumulativeProbability > m_Precision)
                {
                    return false;
                }
                for (var j = activeProbability; j < cumulativeProbability; ++j)
                {
                    m_LookUpTable[(int)j] = i;
                }
                activeProbability = cumulativeProbability;
            }
            return cumulativeProbability == m_Precision;
        }

        /// <summary>
        /// Initializes the decoder state from the tail of the encoded buffer.
        /// Mirrors <c>read_init</c>.
        /// </summary>
        /// <param name="buffer">Encoded data.</param>
        /// <param name="offset">Number of encoded bytes.</param>
        /// <returns>Zero on success, non-zero on error.</returns>
        public int ReadInit(byte* buffer, int offset)
        {
            if (offset < 1)
            {
                return 1;
            }
            m_Ans.buffer = buffer;
            var x = (uint)(buffer[offset - 1] >> 6);
            if (x == 0)
            {
                m_Ans.bufferOffset = offset - 1;
                m_Ans.state = (uint)(buffer[offset - 1] & 0x3F);
            }
            else if (x == 1)
            {
                if (offset < 2)
                {
                    return 1;
                }
                m_Ans.bufferOffset = offset - 2;
                m_Ans.state = (uint)(buffer[offset - 2] | (buffer[offset - 1] << 8)) & 0x3FFF;
            }
            else if (x == 2)
            {
                if (offset < 3)
                {
                    return 1;
                }
                m_Ans.bufferOffset = offset - 3;
                m_Ans.state = (uint)(buffer[offset - 3] | (buffer[offset - 2] << 8) | (buffer[offset - 1] << 16)) & 0x3FFFFF;
            }
            else
            {
                if (offset < 4)
                {
                    return 1;
                }
                m_Ans.bufferOffset = offset - 4;
                m_Ans.state = (buffer[offset - 4]
                    | ((uint)buffer[offset - 3] << 8)
                    | ((uint)buffer[offset - 2] << 16)
                    | ((uint)buffer[offset - 1] << 24)) & 0x3FFFFFFF;
            }
            m_Ans.state += (uint)m_BaseL;
            if (m_Ans.state >= (ulong)m_BaseL * Ans.baseIo)
            {
                return 1;
            }
            return 0;
        }

        /// <summary>
        /// Decodes a single symbol.
        /// Mirrors <c>rans_read</c>.
        /// </summary>
        /// <returns>The decoded symbol.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read()
        {
            while (m_Ans.state < m_BaseL && m_Ans.bufferOffset > 0)
            {
                m_Ans.state = m_Ans.state * Ans.baseIo + m_Ans.buffer[--m_Ans.bufferOffset];
            }
            var quotient = m_Ans.state >> m_PrecisionBits;
            var remainder = m_Ans.state & (uint)(m_Precision - 1);
            var symbol = m_LookUpTable[(int)remainder];
            var sym = m_ProbabilityTable[(int)symbol];
            m_Ans.state = quotient * sym.probability + remainder - sym.cumulativeProbability;
            return symbol;
        }

        /// <summary>
        /// Returns true when the decoder consumed exactly the expected amount of data.
        /// </summary>
        /// <returns>True if the final state matches the initial base state.</returns>
        public bool ReadEnd()
        {
            return m_Ans.state == m_BaseL;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_LookUpTable.IsCreated)
            {
                m_LookUpTable.Dispose();
            }
            if (m_ProbabilityTable.IsCreated)
            {
                m_ProbabilityTable.Dispose();
            }
        }
    }
}
