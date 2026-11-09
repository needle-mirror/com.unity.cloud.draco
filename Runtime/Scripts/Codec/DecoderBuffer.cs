// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Bit-level reader used while a <see cref="DecoderBuffer"/> is in bit decoding mode.
    /// Mirrors <c>draco::DecoderBuffer::BitDecoder</c>.
    /// </summary>
    unsafe struct BitDecoder
    {
        byte* m_Buffer;
        byte* m_BufferEnd;
        ulong m_BitOffset;

        /// <summary>
        /// Points the decoder at a new bit buffer and rewinds the bit offset.
        /// </summary>
        /// <param name="data">Start of the bit buffer.</param>
        /// <param name="size">Size of the bit buffer in bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset(byte* data, long size)
        {
            m_BitOffset = 0;
            m_Buffer = data;
            m_BufferEnd = data + size;
        }

        /// <summary>
        /// Number of bits consumed so far.
        /// </summary>
        public ulong BitsDecoded => m_BitOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int GetBit()
        {
            var off = m_BitOffset;
            var byteOffset = (long)(off >> 3);
            var bitShift = (int)(off & 0x7);
            if (m_Buffer + byteOffset < m_BufferEnd)
            {
                var bit = (m_Buffer[byteOffset] >> bitShift) & 1;
                m_BitOffset = off + 1;
                return bit;
            }
            return 0;
        }

        /// <summary>
        /// Reads <paramref name="numBits"/> bits.
        /// </summary>
        /// <param name="numBits">Number of bits to read (at most 32).</param>
        /// <param name="value">Resulting value.</param>
        /// <returns>False if more than 32 bits were requested.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBits(uint numBits, out uint value)
        {
            if (numBits > 32)
            {
                value = 0;
                return false;
            }
            uint result = 0;
            for (uint bit = 0; bit < numBits; ++bit)
            {
                result |= (uint)GetBit() << (int)bit;
            }
            value = result;
            return true;
        }
    }

    /// <summary>
    /// Read-only view over encoded Draco data providing byte and bit level decoding.
    /// Mirrors <c>draco::DecoderBuffer</c>.
    /// </summary>
    unsafe struct DecoderBuffer
    {
        byte* m_Data;
        long m_DataSize;
        long m_Pos;
        BitDecoder m_BitDecoder;
        bool m_BitMode;
        ushort m_BitstreamVersion;

        /// <summary>
        /// Initializes the buffer with the given memory range.
        /// </summary>
        /// <param name="data">Pointer to encoded data.</param>
        /// <param name="dataSize">Size of the data in bytes.</param>
        public void Init(byte* data, long dataSize)
        {
            Init(data, dataSize, m_BitstreamVersion);
        }

        /// <summary>
        /// Initializes the buffer with the given memory range and bit-stream version.
        /// </summary>
        /// <param name="data">Pointer to encoded data.</param>
        /// <param name="dataSize">Size of the data in bytes.</param>
        /// <param name="version">Draco bit-stream version.</param>
        public void Init(byte* data, long dataSize, ushort version)
        {
            m_Data = data;
            m_DataSize = dataSize;
            m_BitstreamVersion = version;
            m_Pos = 0;
            m_BitMode = false;
        }

        /// <summary>
        /// Draco bit-stream version associated with this buffer.
        /// </summary>
        public ushort BitstreamVersion
        {
            get => m_BitstreamVersion;
            set => m_BitstreamVersion = value;
        }

        /// <summary>
        /// Pointer to the data at the current read position.
        /// </summary>
        public byte* DataHead => m_Data + m_Pos;

        /// <summary>
        /// Number of bytes left to read.
        /// </summary>
        public long RemainingSize => m_DataSize - m_Pos;

        /// <summary>
        /// True while the buffer is in bit decoding mode.
        /// </summary>
        public bool BitDecoderActive => m_BitMode;

        /// <summary>
        /// Skips <paramref name="bytes"/> bytes.
        /// </summary>
        /// <param name="bytes">Number of bytes to skip.</param>
        public void Advance(long bytes)
        {
            m_Pos += bytes;
        }

        /// <summary>
        /// Switches the buffer into bit decoding mode.
        /// </summary>
        /// <param name="decodeSize">True if the encoded bit sequence is prefixed with its size.</param>
        /// <param name="outSize">Size of the bit sequence, if <paramref name="decodeSize"/> is true.</param>
        /// <returns>False on buffer underflow.</returns>
        public bool StartBitDecoding(bool decodeSize, out ulong outSize)
        {
            outSize = 0;
            if (decodeSize && !DecodeVarintU64(out outSize))
            {
                return false;
            }
            m_BitMode = true;
            m_BitDecoder.Reset(DataHead, RemainingSize);
            return true;
        }

        /// <summary>
        /// Leaves bit decoding mode and re-aligns the read position to the next byte.
        /// </summary>
        public void EndBitDecoding()
        {
            m_BitMode = false;
            var bitsDecoded = m_BitDecoder.BitsDecoded;
            var bytesDecoded = (long)((bitsDecoded + 7) / 8);
            m_Pos += bytesDecoded;
        }

        /// <summary>
        /// Reads up to 32 bits. Only valid in bit decoding mode.
        /// </summary>
        /// <param name="numBits">Number of bits to read.</param>
        /// <param name="value">Resulting value.</param>
        /// <returns>False if not in bit decoding mode or too many bits requested.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool DecodeLeastSignificantBits32(uint numBits, out uint value)
        {
            if (!m_BitMode)
            {
                value = 0;
                return false;
            }
            return m_BitDecoder.GetBits(numBits, out value);
        }

        /// <summary>
        /// Reads an unmanaged value and advances the read position.
        /// </summary>
        /// <typeparam name="T">Value type to read.</typeparam>
        /// <param name="value">Resulting value.</param>
        /// <returns>False on buffer underflow.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Decode<T>(out T value) where T : unmanaged
        {
            var size = sizeof(T);
            if (m_DataSize < m_Pos + size)
            {
                value = default;
                return false;
            }
            value = *(T*)(m_Data + m_Pos);
            m_Pos += size;
            return true;
        }

        /// <summary>
        /// Copies raw bytes out of the buffer and advances the read position.
        /// </summary>
        /// <param name="destination">Destination memory.</param>
        /// <param name="sizeToDecode">Number of bytes to copy.</param>
        /// <returns>False on buffer underflow.</returns>
        public bool Decode(void* destination, long sizeToDecode)
        {
            if (m_DataSize < m_Pos + sizeToDecode)
            {
                return false;
            }
            UnsafeUtility.MemCpy(destination, m_Data + m_Pos, sizeToDecode);
            m_Pos += sizeToDecode;
            return true;
        }

        /// <summary>
        /// Decodes an unsigned varint (up to 64 bit).
        /// </summary>
        /// <param name="value">Decoded value.</param>
        /// <returns>False on buffer underflow or malformed varint.</returns>
        public bool DecodeVarintU64(out ulong value)
        {
            value = 0;
            var shift = 0;
            while (true)
            {
                if (shift > 63)
                {
                    return false;
                }
                if (!Decode(out byte input))
                {
                    return false;
                }
                value |= (ulong)(input & 0x7F) << shift;
                if ((input & 0x80) == 0)
                {
                    return true;
                }
                shift += 7;
            }
        }

        /// <summary>
        /// Decodes an unsigned varint into a 32 bit value.
        /// </summary>
        /// <param name="value">Decoded value.</param>
        /// <returns>False on buffer underflow or malformed varint.</returns>
        public bool DecodeVarintU32(out uint value)
        {
            value = 0;
            var shift = 0;
            while (true)
            {
                if (shift > 31)
                {
                    return false;
                }
                if (!Decode(out byte input))
                {
                    return false;
                }
                value |= (uint)(input & 0x7F) << shift;
                if ((input & 0x80) == 0)
                {
                    return true;
                }
                shift += 7;
            }
        }

        /// <summary>
        /// Decodes a signed varint into a 32 bit value.
        /// </summary>
        /// <param name="value">Decoded value.</param>
        /// <returns>False on buffer underflow or malformed varint.</returns>
        public bool DecodeVarintI32(out int value)
        {
            if (!DecodeVarintU32(out var symbol))
            {
                value = 0;
                return false;
            }
            value = BitUtils.ConvertSymbolToSignedInt(symbol);
            return true;
        }
    }
}
