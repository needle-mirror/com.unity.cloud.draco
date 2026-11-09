// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Low level Asymmetric Numeral Systems encoding primitives.
    /// Mirrors the write side of <c>draco/compression/entropy/ans.h</c>.
    /// </summary>
    static unsafe class AnsEncoding
    {
        /// <summary>
        /// Points a coder at its output buffer and resets its state.
        /// Mirrors <c>ans_write_init</c>.
        /// </summary>
        /// <param name="ans">Coder state.</param>
        /// <param name="buffer">Destination memory.</param>
        public static void WriteInit(AnsCoder* ans, byte* buffer)
        {
            ans->buffer = buffer;
            ans->bufferOffset = 0;
            ans->state = Ans.baseL;
        }

        /// <summary>
        /// Serializes the final coder state.
        /// Mirrors <c>ans_write_end</c>.
        /// </summary>
        /// <param name="ans">Coder state.</param>
        /// <returns>Number of bytes written.</returns>
        public static int WriteEnd(AnsCoder* ans)
        {
            var state = ans->state - Ans.baseL;
            if (state < 1 << 6)
            {
                ans->buffer[ans->bufferOffset] = (byte)state;
                return ans->bufferOffset + 1;
            }
            if (state < 1 << 14)
            {
                var value = (0x01u << 14) + state;
                ans->buffer[ans->bufferOffset] = (byte)(value & 0xff);
                ans->buffer[ans->bufferOffset + 1] = (byte)((value >> 8) & 0xff);
                return ans->bufferOffset + 2;
            }
            if (state < 1 << 22)
            {
                var value = (0x02u << 22) + state;
                ans->buffer[ans->bufferOffset] = (byte)(value & 0xff);
                ans->buffer[ans->bufferOffset + 1] = (byte)((value >> 8) & 0xff);
                ans->buffer[ans->bufferOffset + 2] = (byte)((value >> 16) & 0xff);
                return ans->bufferOffset + 3;
            }
            // The state is too large to be serialized.
            return ans->bufferOffset;
        }

        /// <summary>
        /// Writes a single bit using rABS with descending spread.
        /// Mirrors <c>rabs_desc_write</c>.
        /// </summary>
        /// <param name="ans">Coder state.</param>
        /// <param name="value">Bit to write.</param>
        /// <param name="probabilityZero">Probability of a zero bit, scaled to 8 bits.</param>
        public static void RabsWrite(AnsCoder* ans, int value, byte probabilityZero)
        {
            var p = Ans.probabilityPrecision8 - probabilityZero;
            var ls = value != 0 ? p : probabilityZero;
            if (ans->state >= Ans.baseL / Ans.probabilityPrecision8 * Ans.baseIo * ls)
            {
                ans->buffer[ans->bufferOffset++] = (byte)(ans->state % Ans.baseIo);
                ans->state /= Ans.baseIo;
            }
            var quotient = ans->state / ls;
            var remainder = ans->state % ls;
            ans->state = quotient * Ans.probabilityPrecision8 + remainder + (value != 0 ? 0 : p);
        }
    }

    /// <summary>
    /// Encodes a sequence of bits with rANS, using a static probability derived from the
    /// total counts of zero and one bits.
    /// Mirrors <c>draco::RAnsBitEncoder</c>.
    /// </summary>
    unsafe struct RAnsBitEncoder : IDisposable
    {
        ulong m_ZeroCount;
        ulong m_OneCount;
        UnsafeList<uint> m_Bits;
        uint m_LocalBits;
        int m_NumLocalBits;

        /// <summary>
        /// Prepares the encoder. Must be called before any encode call.
        /// </summary>
        public void StartEncoding()
        {
            if (!m_Bits.IsCreated)
            {
                m_Bits = new UnsafeList<uint>(64, Allocator.Persistent);
            }
            Clear();
        }

        void Clear()
        {
            m_ZeroCount = 0;
            m_OneCount = 0;
            if (m_Bits.IsCreated)
            {
                m_Bits.Clear();
            }
            m_LocalBits = 0;
            m_NumLocalBits = 0;
        }

        /// <summary>
        /// Encodes a single bit.
        /// </summary>
        /// <param name="bit">Bit to encode.</param>
        public void EncodeBit(bool bit)
        {
            if (bit)
            {
                m_OneCount++;
                m_LocalBits |= 1u << m_NumLocalBits;
            }
            else
            {
                m_ZeroCount++;
            }
            m_NumLocalBits++;

            if (m_NumLocalBits == 32)
            {
                m_Bits.Add(m_LocalBits);
                m_NumLocalBits = 0;
                m_LocalBits = 0;
            }
        }

        /// <summary>
        /// Encodes the least significant bits of a value.
        /// </summary>
        /// <param name="numBits">Number of bits, in the range 1 to 32.</param>
        /// <param name="value">Value whose least significant bits are encoded.</param>
        public void EncodeLeastSignificantBits32(int numBits, uint value)
        {
            var reversed = BitUtils.ReverseBits32(value) >> (32 - numBits);
            var ones = BitUtils.CountOneBits32(reversed);
            m_ZeroCount += (ulong)(numBits - ones);
            m_OneCount += (ulong)ones;

            var remaining = 32 - m_NumLocalBits;

            if (numBits <= remaining)
            {
                BitUtils.CopyBits32(ref m_LocalBits, m_NumLocalBits, reversed, 0, numBits);
                m_NumLocalBits += numBits;
                if (m_NumLocalBits == 32)
                {
                    m_Bits.Add(m_LocalBits);
                    m_LocalBits = 0;
                    m_NumLocalBits = 0;
                }
            }
            else
            {
                BitUtils.CopyBits32(ref m_LocalBits, m_NumLocalBits, reversed, 0, remaining);
                m_Bits.Add(m_LocalBits);
                m_LocalBits = 0;
                BitUtils.CopyBits32(ref m_LocalBits, 0, reversed, remaining, numBits - remaining);
                m_NumLocalBits = numBits - remaining;
            }
        }

        /// <summary>
        /// Flushes the encoded bits into the target buffer.
        /// </summary>
        /// <param name="targetBuffer">Destination buffer.</param>
        public void EndEncoding(ref EncoderBuffer targetBuffer)
        {
            var total = m_OneCount + m_ZeroCount;
            if (total == 0)
            {
                total++;
            }

            // The probability interval [0,1] is mapped onto [0,256]. Since the coding scheme
            // cannot represent probabilities of zero or one, the value is clamped to [1,255].
            var zeroProbabilityRaw = (uint)(m_ZeroCount / (double)total * 256.0 + 0.5);

            var zeroProbability = (byte)255;
            if (zeroProbabilityRaw < 255)
            {
                zeroProbability = (byte)zeroProbabilityRaw;
            }
            if (zeroProbability == 0)
            {
                zeroProbability = 1;
            }

            // Space for a 32 bit integer per stored word plus some slack.
            var bufferSize = (m_Bits.Length + 8) * 8;
            var buffer = (byte*)UnsafeUtility.Malloc(bufferSize, 4, Allocator.Temp);
            UnsafeUtility.MemClear(buffer, bufferSize);
            var ansCoder = new AnsCoder();
            AnsEncoding.WriteInit(&ansCoder, buffer);

            for (var i = m_NumLocalBits - 1; i >= 0; --i)
            {
                var bit = (m_LocalBits >> i) & 1;
                AnsEncoding.RabsWrite(&ansCoder, (int)bit, zeroProbability);
            }
            for (var w = m_Bits.Length - 1; w >= 0; --w)
            {
                var bits = m_Bits[w];
                for (var i = 31; i >= 0; --i)
                {
                    var bit = (bits >> i) & 1;
                    AnsEncoding.RabsWrite(&ansCoder, (int)bit, zeroProbability);
                }
            }

            var sizeInBytes = AnsEncoding.WriteEnd(&ansCoder);
            targetBuffer.Encode(zeroProbability);
            targetBuffer.EncodeVarintU32((uint)sizeInBytes);
            targetBuffer.Encode(buffer, sizeInBytes);

            UnsafeUtility.Free(buffer, Allocator.Temp);
            Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Bits.IsCreated)
            {
                m_Bits.Dispose();
            }
        }
    }

    /// <summary>
    /// Stores bits verbatim while exposing the arithmetic coder interface.
    /// Mirrors <c>draco::DirectBitEncoder</c>.
    /// </summary>
    unsafe struct DirectBitEncoder : IDisposable
    {
        UnsafeList<uint> m_Bits;
        uint m_LocalBits;
        int m_NumLocalBits;

        /// <summary>
        /// Prepares the encoder. Must be called before any encode call.
        /// </summary>
        public void StartEncoding()
        {
            if (!m_Bits.IsCreated)
            {
                m_Bits = new UnsafeList<uint>(64, Allocator.Persistent);
            }
            Clear();
        }

        void Clear()
        {
            if (m_Bits.IsCreated)
            {
                m_Bits.Clear();
            }
            m_LocalBits = 0;
            m_NumLocalBits = 0;
        }

        /// <summary>
        /// Encodes a single bit.
        /// </summary>
        /// <param name="bit">Bit to encode.</param>
        public void EncodeBit(bool bit)
        {
            if (bit)
            {
                m_LocalBits |= 1u << (31 - m_NumLocalBits);
            }
            m_NumLocalBits++;
            if (m_NumLocalBits == 32)
            {
                m_Bits.Add(m_LocalBits);
                m_NumLocalBits = 0;
                m_LocalBits = 0;
            }
        }

        /// <summary>
        /// Encodes the least significant bits of a value.
        /// </summary>
        /// <param name="numBits">Number of bits, in the range 1 to 32.</param>
        /// <param name="value">Value whose least significant bits are encoded.</param>
        public void EncodeLeastSignificantBits32(int numBits, uint value)
        {
            var remaining = 32 - m_NumLocalBits;

            // Drop the leading bits that should not be encoded.
            value <<= 32 - numBits;
            if (numBits <= remaining)
            {
                value >>= m_NumLocalBits;
                m_LocalBits |= value;
                m_NumLocalBits += numBits;
                if (m_NumLocalBits == 32)
                {
                    m_Bits.Add(m_LocalBits);
                    m_LocalBits = 0;
                    m_NumLocalBits = 0;
                }
            }
            else
            {
                value >>= 32 - numBits;
                m_NumLocalBits = numBits - remaining;
                var valueHigh = value >> m_NumLocalBits;
                m_LocalBits |= valueHigh;
                m_Bits.Add(m_LocalBits);
                m_LocalBits = value << (32 - m_NumLocalBits);
            }
        }

        /// <summary>
        /// Flushes the encoded bits into the target buffer.
        /// </summary>
        /// <param name="targetBuffer">Destination buffer.</param>
        public void EndEncoding(ref EncoderBuffer targetBuffer)
        {
            m_Bits.Add(m_LocalBits);
            var sizeInBytes = (uint)m_Bits.Length * 4;
            targetBuffer.Encode(sizeInBytes);
            targetBuffer.Encode(m_Bits.Ptr, sizeInBytes);
            Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Bits.IsCreated)
            {
                m_Bits.Dispose();
            }
        }
    }

    /// <summary>
    /// Treats every bit position of a 32 bit integer as a separate rANS context, which pays
    /// off when the leading bits are likely to be zero.
    /// Mirrors <c>draco::FoldedBit32Encoder</c> instantiated with <c>RAnsBitEncoder</c>.
    /// </summary>
    unsafe struct FoldedBit32RAnsEncoder : IDisposable
    {
        const int k_NumEncoders = 32;

        // Thirty-two per bit-position encoders followed by the encoder used for plain bits.
        UnsafeList<RAnsBitEncoder> m_Encoders;

        /// <summary>
        /// Prepares all sub encoders. Must be called before any encode call.
        /// </summary>
        public void StartEncoding()
        {
            if (!m_Encoders.IsCreated)
            {
                m_Encoders = new UnsafeList<RAnsBitEncoder>(k_NumEncoders + 1, Allocator.Persistent);
                m_Encoders.Resize(k_NumEncoders + 1, NativeArrayOptions.ClearMemory);
            }
            for (var i = 0; i < k_NumEncoders + 1; ++i)
            {
                m_Encoders.ElementAt(i).StartEncoding();
            }
        }

        /// <summary>
        /// Encodes a single bit with the dedicated bit encoder.
        /// </summary>
        /// <param name="bit">Bit to encode.</param>
        public void EncodeBit(bool bit)
        {
            m_Encoders.ElementAt(k_NumEncoders).EncodeBit(bit);
        }

        /// <summary>
        /// Encodes the least significant bits of a value, most significant bit first, each
        /// into its own context.
        /// </summary>
        /// <param name="numBits">Number of bits, in the range 1 to 32.</param>
        /// <param name="value">Value whose least significant bits are encoded.</param>
        public void EncodeLeastSignificantBits32(int numBits, uint value)
        {
            var selector = 1u << (numBits - 1);
            for (var i = 0; i < numBits; ++i)
            {
                var bit = (value & selector) != 0;
                m_Encoders.ElementAt(i).EncodeBit(bit);
                selector >>= 1;
            }
        }

        /// <summary>
        /// Flushes all sub encoders into the target buffer.
        /// </summary>
        /// <param name="targetBuffer">Destination buffer.</param>
        public void EndEncoding(ref EncoderBuffer targetBuffer)
        {
            for (var i = 0; i < k_NumEncoders + 1; ++i)
            {
                m_Encoders.ElementAt(i).EndEncoding(ref targetBuffer);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!m_Encoders.IsCreated)
            {
                return;
            }
            for (var i = 0; i < m_Encoders.Length; ++i)
            {
                m_Encoders.ElementAt(i).Dispose();
            }
            m_Encoders.Dispose();
        }
    }
}
