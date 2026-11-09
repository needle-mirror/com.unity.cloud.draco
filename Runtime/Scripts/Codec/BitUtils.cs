// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Bit manipulation helpers used throughout the Draco codec.
    /// Mirrors <c>draco/core/bit_utils.h</c>.
    /// </summary>
    static class BitUtils
    {
        /// <summary>
        /// Counts the number of set bits in a 32 bit integer.
        /// </summary>
        /// <param name="n">Input value.</param>
        /// <returns>Number of set bits.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CountOneBits32(uint n)
        {
            n -= (n >> 1) & 0x55555555u;
            n = ((n >> 2) & 0x33333333u) + (n & 0x33333333u);
            return (int)((((n + (n >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }

        /// <summary>
        /// Reverses the bit order of a 32 bit integer.
        /// </summary>
        /// <param name="n">Input value.</param>
        /// <returns>Bit-reversed value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReverseBits32(uint n)
        {
            n = ((n >> 1) & 0x55555555u) | ((n & 0x55555555u) << 1);
            n = ((n >> 2) & 0x33333333u) | ((n & 0x33333333u) << 2);
            n = ((n >> 4) & 0x0F0F0F0Fu) | ((n & 0x0F0F0F0Fu) << 4);
            n = ((n >> 8) & 0x00FF00FFu) | ((n & 0x00FF00FFu) << 8);
            return (n >> 16) | (n << 16);
        }

        /// <summary>
        /// Copies <paramref name="numBits"/> bits from <paramref name="src"/> into <paramref name="dst"/>.
        /// </summary>
        /// <param name="dst">Destination value, modified in place.</param>
        /// <param name="dstOffset">Bit offset within the destination.</param>
        /// <param name="src">Source value.</param>
        /// <param name="srcOffset">Bit offset within the source.</param>
        /// <param name="numBits">Number of bits to copy.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBits32(ref uint dst, int dstOffset, uint src, int srcOffset, int numBits)
        {
            var mask = (~0u >> (32 - numBits)) << dstOffset;
            dst = (dst & ~mask) | (((src >> srcOffset) << dstOffset) & mask);
        }

        /// <summary>
        /// Returns the index of the most significant set bit. Undefined for zero.
        /// </summary>
        /// <param name="n">Input value.</param>
        /// <returns>Zero based index of the most significant set bit.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MostSignificantBit(uint n)
        {
            var msb = 0;
            if (n == 0)
            {
                return -1;
            }
            if ((0xFFFF0000u & n) != 0) { n >>= 16; msb |= 16; }
            if ((0x0000FF00u & n) != 0) { n >>= 8; msb |= 8; }
            if ((0x000000F0u & n) != 0) { n >>= 4; msb |= 4; }
            if ((0x0000000Cu & n) != 0) { n >>= 2; msb |= 2; }
            if ((0x00000002u & n) != 0) { msb |= 1; }
            return msb;
        }

        /// <summary>
        /// Maps a signed integer onto an unsigned symbol suitable for entropy coding.
        /// </summary>
        /// <param name="value">Signed input value.</param>
        /// <returns>Unsigned symbol.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ConvertSignedIntToSymbol(int value)
        {
            if (value >= 0)
            {
                return (uint)value << 1;
            }
            value = -(value + 1);
            return ((uint)value << 1) | 1;
        }

        /// <summary>
        /// Maps an entropy coded symbol back onto a signed integer.
        /// </summary>
        /// <param name="value">Unsigned symbol.</param>
        /// <returns>Signed value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ConvertSymbolToSignedInt(uint value)
        {
            var isPositive = (value & 1) == 0;
            value >>= 1;
            if (isPositive)
            {
                return (int)value;
            }
            return -(int)value - 1;
        }

        /// <summary>
        /// Converts an array of symbols into signed integers in place.
        /// </summary>
        /// <param name="input">Source symbols.</param>
        /// <param name="count">Number of values.</param>
        /// <param name="output">Destination signed values.</param>
        public static unsafe void ConvertSymbolsToSignedInts(uint* input, int count, int* output)
        {
            for (var i = 0; i < count; ++i)
            {
                output[i] = ConvertSymbolToSignedInt(input[i]);
            }
        }
    }
}
