// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Identifies which binary bit decoder implementation a traversal decoder uses.
    /// </summary>
    enum BinaryDecoderType
    {
        RAns = 0,
        Direct = 1
    }

    /// <summary>
    /// Decodes a sequence of bits encoded with Draco's rANS bit encoder.
    /// Mirrors <c>draco::RAnsBitDecoder</c>.
    /// </summary>
    unsafe struct RAnsBitDecoder
    {
        AnsDecoder m_AnsDecoder;
        byte m_ProbabilityZero;

        /// <summary>
        /// Reads the encoder header and prepares decoding.
        /// </summary>
        /// <param name="sourceBuffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool StartDecoding(DecoderBuffer* sourceBuffer)
        {
            if (!sourceBuffer->Decode(out m_ProbabilityZero))
            {
                return false;
            }

            if (!sourceBuffer->DecodeVarintU32(out var sizeInBytes))
            {
                return false;
            }

            if (sizeInBytes > sourceBuffer->RemainingSize)
            {
                return false;
            }

            var ans = (AnsDecoder*)UnsafeUtility.AddressOf(ref m_AnsDecoder);
            if (Ans.ReadInit(ans, sourceBuffer->DataHead, (int)sizeInBytes) != 0)
            {
                return false;
            }
            sourceBuffer->Advance(sizeInBytes);
            return true;
        }

        /// <summary>
        /// Decodes the next bit.
        /// </summary>
        /// <returns>True if the bit is set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool DecodeNextBit()
        {
            var ans = (AnsDecoder*)UnsafeUtility.AddressOf(ref m_AnsDecoder);
            return Ans.RabsRead(ans, m_ProbabilityZero) > 0;
        }

        /// <summary>
        /// Decodes the next <paramref name="numBits"/> bits, most significant bit first.
        /// </summary>
        /// <param name="numBits">Number of bits to decode (1 to 32).</param>
        /// <param name="value">Decoded value.</param>
        public void DecodeLeastSignificantBits32(int numBits, out uint value)
        {
            uint result = 0;
            while (numBits > 0)
            {
                result = (result << 1) + (DecodeNextBit() ? 1u : 0u);
                --numBits;
            }
            value = result;
        }

        /// <summary>
        /// Finishes decoding.
        /// </summary>
        public void EndDecoding() { }
    }

    /// <summary>
    /// Decodes a sequence of bits stored verbatim as 32 bit words.
    /// Mirrors <c>draco::DirectBitDecoder</c>.
    /// </summary>
    unsafe struct DirectBitDecoder : IDisposable
    {
        UnsafeList<uint> m_Bits;
        int m_Position;
        int m_NumUsedBits;

        /// <summary>
        /// Reads the encoded bit data into an internal buffer.
        /// </summary>
        /// <param name="sourceBuffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool StartDecoding(DecoderBuffer* sourceBuffer)
        {
            Clear();
            if (!sourceBuffer->Decode(out uint sizeInBytes))
            {
                return false;
            }
            // The encoder always writes whole 32 bit elements.
            if (sizeInBytes == 0 || (sizeInBytes & 0x3) != 0)
            {
                return false;
            }
            if (sizeInBytes > sourceBuffer->RemainingSize)
            {
                return false;
            }
            var num32BitElements = (int)(sizeInBytes / 4);
            m_Bits = new UnsafeList<uint>(num32BitElements, Allocator.Persistent);
            m_Bits.Resize(num32BitElements, NativeArrayOptions.UninitializedMemory);
            if (!sourceBuffer->Decode(m_Bits.Ptr, sizeInBytes))
            {
                return false;
            }
            m_Position = 0;
            m_NumUsedBits = 0;
            return true;
        }

        /// <summary>
        /// Decodes the next bit.
        /// </summary>
        /// <returns>True if the bit is set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool DecodeNextBit()
        {
            var selector = 1u << (31 - m_NumUsedBits);
            if (m_Position == m_Bits.Length)
            {
                return false;
            }
            var bit = (m_Bits[m_Position] & selector) != 0;
            ++m_NumUsedBits;
            if (m_NumUsedBits == 32)
            {
                ++m_Position;
                m_NumUsedBits = 0;
            }
            return bit;
        }

        /// <summary>
        /// Decodes the next <paramref name="numBits"/> bits, most significant bit first.
        /// </summary>
        /// <param name="numBits">Number of bits to decode (1 to 32).</param>
        /// <param name="value">Decoded value.</param>
        /// <returns>False on buffer underflow.</returns>
        public bool DecodeLeastSignificantBits32(int numBits, out uint value)
        {
            value = 0;
            var remaining = 32 - m_NumUsedBits;
            if (numBits <= remaining)
            {
                if (m_Position == m_Bits.Length)
                {
                    return false;
                }
                value = (m_Bits[m_Position] << m_NumUsedBits) >> (32 - numBits);
                m_NumUsedBits += numBits;
                if (m_NumUsedBits == 32)
                {
                    ++m_Position;
                    m_NumUsedBits = 0;
                }
            }
            else
            {
                if (m_Position + 1 == m_Bits.Length)
                {
                    return false;
                }
                var valueLeft = m_Bits[m_Position] << m_NumUsedBits;
                m_NumUsedBits = numBits - remaining;
                ++m_Position;
                var valueRight = m_Bits[m_Position] >> (32 - m_NumUsedBits);
                value = (valueLeft >> (32 - m_NumUsedBits - remaining)) | valueRight;
            }
            return true;
        }

        /// <summary>
        /// Finishes decoding.
        /// </summary>
        public void EndDecoding() { }

        void Clear()
        {
            if (m_Bits.IsCreated)
            {
                m_Bits.Dispose();
            }
            m_NumUsedBits = 0;
            m_Position = 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Clear();
        }
    }

    /// <summary>
    /// Decodes 32 bit integers where each bit position is coded by its own sub-decoder.
    /// Mirrors <c>draco::FoldedBit32Decoder</c> instantiated with <see cref="RAnsBitDecoder"/>.
    /// </summary>
    unsafe struct FoldedBit32RAnsDecoder : IDisposable
    {
        UnsafeList<RAnsBitDecoder> m_FoldedDecoders;
        RAnsBitDecoder m_BitDecoder;

        /// <summary>
        /// Initializes all 32 per-bit sub-decoders plus the dedicated bit decoder.
        /// </summary>
        /// <param name="sourceBuffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool StartDecoding(DecoderBuffer* sourceBuffer)
        {
            if (!m_FoldedDecoders.IsCreated)
            {
                m_FoldedDecoders = new UnsafeList<RAnsBitDecoder>(32, Allocator.Persistent);
                m_FoldedDecoders.Resize(32, NativeArrayOptions.ClearMemory);
            }
            var decoders = m_FoldedDecoders.Ptr;
            for (var i = 0; i < 32; i++)
            {
                if (!decoders[i].StartDecoding(sourceBuffer))
                {
                    return false;
                }
            }
            var bitDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_BitDecoder);
            return bitDecoder->StartDecoding(sourceBuffer);
        }

        /// <summary>
        /// Decodes the next bit from the dedicated bit decoder.
        /// </summary>
        /// <returns>True if the bit is set.</returns>
        public bool DecodeNextBit()
        {
            var bitDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_BitDecoder);
            return bitDecoder->DecodeNextBit();
        }

        /// <summary>
        /// Decodes an integer, reading each bit from its dedicated sub-decoder.
        /// </summary>
        /// <param name="numBits">Number of bits to decode.</param>
        /// <param name="value">Decoded value.</param>
        public void DecodeLeastSignificantBits32(int numBits, out uint value)
        {
            var decoders = m_FoldedDecoders.Ptr;
            uint result = 0;
            for (var i = 0; i < numBits; ++i)
            {
                var bit = decoders[i].DecodeNextBit();
                result = (result << 1) + (bit ? 1u : 0u);
            }
            value = result;
        }

        /// <summary>
        /// Finishes decoding of all sub-decoders.
        /// </summary>
        public void EndDecoding() { }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_FoldedDecoders.IsCreated)
            {
                m_FoldedDecoders.Dispose();
            }
        }
    }

}
