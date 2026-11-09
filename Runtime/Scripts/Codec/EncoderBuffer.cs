// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Growable output buffer supporting byte aligned writes and variable length bit writes.
    /// Mirrors <c>draco::EncoderBuffer</c>.
    /// </summary>
    unsafe struct EncoderBuffer : IDisposable
    {
        UnsafeList<byte> m_Buffer;

        // Bit encoding state. A non-zero reserved byte count marks bit encoding mode.
        long m_BitEncoderReservedBytes;
        long m_BitEncoderStart;
        long m_BitOffset;
        bool m_EncodeBitSequenceSize;

        /// <summary>
        /// Allocates the underlying storage.
        /// </summary>
        /// <param name="initialCapacity">Initial capacity in bytes.</param>
        public void Init(int initialCapacity = 1024)
        {
            m_Buffer = new UnsafeList<byte>(initialCapacity, Allocator.Persistent);
            m_BitEncoderReservedBytes = 0;
        }

        /// <summary>True once <see cref="Init"/> has been called.</summary>
        public bool IsCreated => m_Buffer.IsCreated;

        /// <summary>Number of bytes written so far.</summary>
        public int Length => m_Buffer.Length;

        /// <summary>Pointer to the encoded data.</summary>
        public byte* Data => m_Buffer.Ptr;

        /// <summary>True while the buffer is in bit encoding mode.</summary>
        public bool BitEncoderActive => m_BitEncoderReservedBytes > 0;

        /// <summary>
        /// Grows or shrinks the buffer to an exact byte count.
        /// Mirrors <c>EncoderBuffer::Resize</c>.
        /// </summary>
        /// <param name="numBytes">New length in bytes.</param>
        public void SetLength(long numBytes)
        {
            m_Buffer.Resize((int)numBytes, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// Discards all encoded data.
        /// </summary>
        public void Clear()
        {
            m_Buffer.Clear();
            m_BitEncoderReservedBytes = 0;
        }

        /// <summary>
        /// Appends an unmanaged value.
        /// </summary>
        /// <typeparam name="T">Value type to append.</typeparam>
        /// <param name="value">Value to append.</param>
        /// <returns>False while in bit encoding mode.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Encode<T>(T value) where T : unmanaged
        {
            if (BitEncoderActive)
            {
                return false;
            }
            var size = sizeof(T);
            var offset = m_Buffer.Length;
            m_Buffer.Resize(offset + size, NativeArrayOptions.UninitializedMemory);
            *(T*)(m_Buffer.Ptr + offset) = value;
            return true;
        }

        /// <summary>
        /// Appends raw bytes.
        /// </summary>
        /// <param name="data">Source data.</param>
        /// <param name="dataSize">Number of bytes to append.</param>
        /// <returns>False while in bit encoding mode.</returns>
        public bool Encode(void* data, long dataSize)
        {
            if (BitEncoderActive)
            {
                return false;
            }
            var offset = m_Buffer.Length;
            m_Buffer.Resize(offset + (int)dataSize, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(m_Buffer.Ptr + offset, data, dataSize);
            return true;
        }

        /// <summary>
        /// Appends an unsigned varint.
        /// Mirrors <c>draco::EncodeVarint</c> for unsigned types.
        /// </summary>
        /// <param name="value">Value to append.</param>
        /// <returns>False while in bit encoding mode.</returns>
        public bool EncodeVarintU64(ulong value)
        {
            while (true)
            {
                var outByte = (byte)(value & 0x7F);
                if (value >= 1 << 7)
                {
                    outByte |= 1 << 7;
                    if (!Encode(outByte))
                    {
                        return false;
                    }
                    value >>= 7;
                    continue;
                }
                return Encode(outByte);
            }
        }

        /// <summary>
        /// Appends an unsigned varint.
        /// </summary>
        /// <param name="value">Value to append.</param>
        /// <returns>False while in bit encoding mode.</returns>
        public bool EncodeVarintU32(uint value)
        {
            return EncodeVarintU64(value);
        }

        /// <summary>
        /// Appends a signed varint.
        /// Mirrors <c>draco::EncodeVarint</c> for signed types.
        /// </summary>
        /// <param name="value">Value to append.</param>
        /// <returns>False while in bit encoding mode.</returns>
        public bool EncodeVarintI32(int value)
        {
            return EncodeVarintU32(BitUtils.ConvertSignedIntToSymbol(value));
        }

        /// <summary>
        /// Switches the buffer into bit encoding mode.
        /// </summary>
        /// <param name="requiredBits">Upper bound of the number of bits to write.</param>
        /// <param name="encodeSize">True to prefix the sequence with its encoded size.</param>
        /// <returns>False if already in bit encoding mode or the size is invalid.</returns>
        public bool StartBitEncoding(long requiredBits, bool encodeSize)
        {
            if (BitEncoderActive)
            {
                return false;
            }
            if (requiredBits <= 0)
            {
                return false;
            }
            m_EncodeBitSequenceSize = encodeSize;
            var requiredBytes = (requiredBits + 7) / 8;
            m_BitEncoderReservedBytes = requiredBytes;
            var bufferStartSize = (long)m_Buffer.Length;
            if (encodeSize)
            {
                // Reserve room for the size, which is written once the sequence ends.
                bufferStartSize += sizeof(ulong);
            }
            m_Buffer.Resize((int)(bufferStartSize + requiredBytes), NativeArrayOptions.ClearMemory);
            m_BitEncoderStart = bufferStartSize;
            m_BitOffset = 0;
            return true;
        }

        /// <summary>
        /// Writes up to 32 bits. Only valid in bit encoding mode.
        /// </summary>
        /// <param name="numBits">Number of bits to write.</param>
        /// <param name="value">Value whose least significant bits are written.</param>
        /// <returns>False if not in bit encoding mode.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EncodeLeastSignificantBits32(int numBits, uint value)
        {
            if (!BitEncoderActive)
            {
                return false;
            }
            var bitBuffer = m_Buffer.Ptr + m_BitEncoderStart;
            for (var bit = 0; bit < numBits; ++bit)
            {
                var bitValue = (byte)((value >> bit) & 1);
                var byteOffset = m_BitOffset / 8;
                var bitShift = (int)(m_BitOffset % 8);
                bitBuffer[byteOffset] = (byte)((bitBuffer[byteOffset] & ~(1 << bitShift)) | (bitValue << bitShift));
                m_BitOffset++;
            }
            return true;
        }

        /// <summary>
        /// Leaves bit encoding mode, writing the sequence size when requested and shrinking
        /// the buffer to the bytes actually used.
        /// </summary>
        public void EndBitEncoding()
        {
            if (!BitEncoderActive)
            {
                return;
            }
            var encodedBits = m_BitOffset;
            var encodedBytes = (encodedBits + 7) / 8;

            if (m_EncodeBitSequenceSize)
            {
                // The size was reserved as eight bytes but is stored as a varint, so the
                // encoded data has to move back by the difference.
                var sizeStart = m_BitEncoderStart - sizeof(ulong);
                var sizeBuffer = new EncoderBuffer();
                sizeBuffer.Init(16);
                sizeBuffer.EncodeVarintU64((ulong)encodedBytes);
                var sizeLength = sizeBuffer.Length;

                var destination = m_Buffer.Ptr + sizeStart + sizeLength;
                var source = m_Buffer.Ptr + sizeStart + sizeof(ulong);
                UnsafeUtility.MemMove(destination, source, encodedBytes);
                UnsafeUtility.MemCpy(m_Buffer.Ptr + sizeStart, sizeBuffer.Data, sizeLength);
                sizeBuffer.Dispose();

                m_BitEncoderReservedBytes += sizeof(ulong) - sizeLength;
            }

            m_Buffer.Resize(
                (int)(m_Buffer.Length - m_BitEncoderReservedBytes + encodedBytes),
                NativeArrayOptions.UninitializedMemory);
            m_BitEncoderReservedBytes = 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Buffer.IsCreated)
            {
                m_Buffer.Dispose();
            }
        }
    }
}
