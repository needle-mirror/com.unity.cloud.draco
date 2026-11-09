// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// State of an Asymmetric Numeral Systems encoder.
    /// Mirrors <c>draco::AnsCoder</c>.
    /// </summary>
    unsafe struct AnsCoder
    {
        public byte* buffer;
        public int bufferOffset;
        public uint state;
    }

    /// <summary>
    /// rANS encoder for multi symbol alphabets with a run-time selectable precision.
    /// Mirrors <c>draco::RAnsEncoder</c>.
    /// </summary>
    unsafe struct RAnsEncoder
    {
        int m_PrecisionBits;
        int m_Precision;
        int m_BaseL;
        AnsCoder m_Ans;

        /// <summary>
        /// Configures the precision of the encoder.
        /// </summary>
        /// <param name="precisionBits">Number of precision bits.</param>
        public void Init(int precisionBits)
        {
            m_PrecisionBits = precisionBits;
            m_Precision = 1 << precisionBits;
            m_BaseL = m_Precision * 4;
        }

        /// <summary>
        /// Points the encoder at its output buffer.
        /// </summary>
        /// <param name="buffer">Destination memory.</param>
        public void WriteInit(byte* buffer)
        {
            m_Ans.buffer = buffer;
            m_Ans.bufferOffset = 0;
            m_Ans.state = (uint)m_BaseL;
        }

        /// <summary>
        /// Flushes the encoder state.
        /// </summary>
        /// <returns>Number of bytes written.</returns>
        public int WriteEnd()
        {
            var state = m_Ans.state - (uint)m_BaseL;
            if (state < 1 << 6)
            {
                m_Ans.buffer[m_Ans.bufferOffset] = (byte)state;
                return m_Ans.bufferOffset + 1;
            }
            if (state < 1 << 14)
            {
                var value = (0x01u << 14) + state;
                m_Ans.buffer[m_Ans.bufferOffset] = (byte)(value & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 1] = (byte)((value >> 8) & 0xff);
                return m_Ans.bufferOffset + 2;
            }
            if (state < 1 << 22)
            {
                var value = (0x02u << 22) + state;
                m_Ans.buffer[m_Ans.bufferOffset] = (byte)(value & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 1] = (byte)((value >> 8) & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 2] = (byte)((value >> 16) & 0xff);
                return m_Ans.bufferOffset + 3;
            }
            if (state < 1 << 30)
            {
                var value = (0x03u << 30) + state;
                m_Ans.buffer[m_Ans.bufferOffset] = (byte)(value & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 1] = (byte)((value >> 8) & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 2] = (byte)((value >> 16) & 0xff);
                m_Ans.buffer[m_Ans.bufferOffset + 3] = (byte)((value >> 24) & 0xff);
                return m_Ans.bufferOffset + 4;
            }
            // The state is too large to be serialized.
            return m_Ans.bufferOffset;
        }

        /// <summary>
        /// Encodes a single symbol.
        /// Mirrors <c>rans_write</c>.
        /// </summary>
        /// <param name="probability">Probability of the symbol.</param>
        /// <param name="cumulativeProbability">Cumulative probability of the symbol.</param>
        public void Write(uint probability, uint cumulativeProbability)
        {
            while (m_Ans.state >= (ulong)m_BaseL / (ulong)m_Precision * Ans.baseIo * probability)
            {
                m_Ans.buffer[m_Ans.bufferOffset++] = (byte)(m_Ans.state % Ans.baseIo);
                m_Ans.state /= Ans.baseIo;
            }
            m_Ans.state = (m_Ans.state / probability) * (uint)m_Precision
                + m_Ans.state % probability + cumulativeProbability;
        }
    }

    /// <summary>
    /// Encodes symbols with the rANS algorithm, including the probability table.
    /// Mirrors <c>draco::RAnsSymbolEncoder</c>.
    /// </summary>
    unsafe struct RAnsSymbolEncoder : IDisposable
    {
        UnsafeList<RansSym> m_ProbabilityTable;
        uint m_NumSymbols;
        ulong m_NumExpectedBits;
        RAnsEncoder m_Ans;
        int m_Precision;
        long m_BufferOffset;

        /// <summary>
        /// Builds the probability table from symbol frequencies and writes it to the buffer.
        /// </summary>
        /// <param name="uniqueSymbolsBitLength">Bit length of the largest symbol.</param>
        /// <param name="frequencies">Per symbol frequencies.</param>
        /// <param name="numSymbols">Number of entries in <paramref name="frequencies"/>.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False if a valid probability table cannot be built.</returns>
        public bool Create(
            int uniqueSymbolsBitLength, ulong* frequencies, int numSymbols, ref EncoderBuffer buffer)
        {
            var precisionBits = RAnsDecoder.ComputePrecisionFromUniqueSymbolsBitLength(uniqueSymbolsBitLength);
            m_Precision = 1 << precisionBits;
            m_Ans.Init(precisionBits);

            ulong totalFrequency = 0;
            var maxValidSymbol = 0;
            for (var i = 0; i < numSymbols; ++i)
            {
                totalFrequency += frequencies[i];
                if (frequencies[i] > 0)
                {
                    maxValidSymbol = i;
                }
            }
            numSymbols = maxValidSymbol + 1;
            m_NumSymbols = (uint)numSymbols;
            m_ProbabilityTable = new UnsafeList<RansSym>(numSymbols, Allocator.Persistent);
            m_ProbabilityTable.Resize(numSymbols, NativeArrayOptions.ClearMemory);

            var totalFrequencyD = (double)totalFrequency;
            var precisionD = (double)m_Precision;

            // Rescale the normalized frequencies into the range [1, precision - 1] so that
            // the total probability equals the precision.
            var totalRansProbability = 0;
            for (var i = 0; i < numSymbols; ++i)
            {
                var frequency = frequencies[i];
                var probability = frequency / totalFrequencyD;
                var ransProbability = (uint)(probability * precisionD + 0.5f);
                if (ransProbability == 0 && frequency > 0)
                {
                    ransProbability = 1;
                }
                m_ProbabilityTable.ElementAt(i).probability = ransProbability;
                totalRansProbability += (int)ransProbability;
            }

            if (totalRansProbability != m_Precision)
            {
                if (!AdjustProbabilities(numSymbols, ref totalRansProbability, precisionD))
                {
                    return false;
                }
            }

            // Compute the cumulative distribution.
            uint totalProbability = 0;
            for (var i = 0; i < numSymbols; ++i)
            {
                m_ProbabilityTable.ElementAt(i).cumulativeProbability = totalProbability;
                totalProbability += m_ProbabilityTable[i].probability;
            }
            if (totalProbability != m_Precision)
            {
                return false;
            }

            // Estimate the encoded size from the Shannon entropy.
            double numBits = 0;
            for (var i = 0; i < numSymbols; ++i)
            {
                var probability = m_ProbabilityTable[i].probability;
                if (probability == 0)
                {
                    continue;
                }
                var normalizedProbability = probability / precisionD;
                numBits += frequencies[i] * math.log2(normalizedProbability);
            }
            m_NumExpectedBits = (ulong)math.ceil(-numBits);

            return EncodeTable(ref buffer);
        }

        bool AdjustProbabilities(int numSymbols, ref int totalRansProbability, double precisionD)
        {
            // Sort symbol indices by ascending probability, keeping the order stable so the
            // result matches the reference implementation.
            var sorted = new UnsafeList<int>(numSymbols, Allocator.Temp);
            sorted.Resize(numSymbols, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numSymbols; ++i)
            {
                sorted[i] = i;
            }
            StableSortByProbability(ref sorted, numSymbols);

            var success = true;
            if (totalRansProbability < m_Precision)
            {
                // Rare case, give the extra precision to the most frequent symbol.
                m_ProbabilityTable.ElementAt(sorted[numSymbols - 1]).probability +=
                    (uint)(m_Precision - totalRansProbability);
            }
            else
            {
                // Over-allocated precision, rescale all probabilities down.
                var error = totalRansProbability - m_Precision;
                while (error > 0)
                {
                    var actualTotalProbabilityD = (double)totalRansProbability;
                    var actualRelativeErrorD = precisionD / actualTotalProbabilityD;
                    for (var j = numSymbols - 1; j > 0; --j)
                    {
                        var symbolId = sorted[j];
                        var probability = m_ProbabilityTable[symbolId].probability;
                        if (probability <= 1)
                        {
                            if (j == numSymbols - 1)
                            {
                                success = false; // The most frequent symbol would be empty.
                            }
                            break;
                        }
                        var newProbability = (int)math.floor(actualRelativeErrorD * probability);
                        var fix = (int)probability - newProbability;
                        if (fix == 0)
                        {
                            fix = 1;
                        }
                        if (fix >= (int)probability)
                        {
                            fix = (int)probability - 1;
                        }
                        if (fix > error)
                        {
                            fix = error;
                        }
                        m_ProbabilityTable.ElementAt(symbolId).probability -= (uint)fix;
                        totalRansProbability -= fix;
                        error -= fix;
                        if (totalRansProbability == m_Precision)
                        {
                            break;
                        }
                    }
                    if (!success)
                    {
                        break;
                    }
                }
            }
            sorted.Dispose();
            return success;
        }

        void StableSortByProbability(ref UnsafeList<int> indices, int count)
        {
            // Insertion sort keeps equal probabilities in their original order, matching
            // std::stable_sort in the reference implementation.
            for (var i = 1; i < count; ++i)
            {
                var key = indices[i];
                var keyProbability = m_ProbabilityTable[key].probability;
                var j = i - 1;
                while (j >= 0 && m_ProbabilityTable[indices[j]].probability > keyProbability)
                {
                    indices[j + 1] = indices[j];
                    --j;
                }
                indices[j + 1] = key;
            }
        }

        bool EncodeTable(ref EncoderBuffer buffer)
        {
            buffer.EncodeVarintU32(m_NumSymbols);
            // The probabilities use a varint style encoding where the first two bits hold
            // the number of extra bytes.
            for (uint i = 0; i < m_NumSymbols; ++i)
            {
                var probability = m_ProbabilityTable[(int)i].probability;
                var numExtraBytes = 0;
                if (probability >= 1 << 6)
                {
                    numExtraBytes++;
                    if (probability >= 1 << 14)
                    {
                        numExtraBytes++;
                        if (probability >= 1 << 22)
                        {
                            // Unreachable, the maximum precision is 20 bits.
                            return false;
                        }
                    }
                }
                if (probability == 0)
                {
                    // A zero probability sets the first two bits and uses the remaining six
                    // to store the offset to the next symbol with non-zero probability.
                    uint offset = 0;
                    for (; offset < (1 << 6) - 1; ++offset)
                    {
                        // The last symbol always has a non-zero probability, so this stays
                        // within bounds.
                        if (m_ProbabilityTable[(int)(i + offset + 1)].probability > 0)
                        {
                            break;
                        }
                    }
                    buffer.Encode((byte)((offset << 2) | 3));
                    i += offset;
                }
                else
                {
                    buffer.Encode((byte)((probability << 2) | (uint)(numExtraBytes & 3)));
                    for (var b = 0; b < numExtraBytes; ++b)
                    {
                        buffer.Encode((byte)(probability >> (8 * (b + 1) - 2)));
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Reserves the output space and initializes the rANS state.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        public void StartEncoding(ref EncoderBuffer buffer)
        {
            // Reserve extra storage to be safe.
            var requiredBits = 2 * m_NumExpectedBits + 32;
            m_BufferOffset = buffer.Length;
            var requiredBytes = (long)((requiredBits + 7) / 8);
            buffer.SetLength(m_BufferOffset + requiredBytes + sizeof(ulong));
            m_Ans.WriteInit(buffer.Data + m_BufferOffset);
        }

        /// <summary>
        /// Encodes a single symbol.
        /// </summary>
        /// <param name="symbol">Symbol to encode.</param>
        public void EncodeSymbol(uint symbol)
        {
            var sym = m_ProbabilityTable[(int)symbol];
            m_Ans.Write(sym.probability, sym.cumulativeProbability);
        }

        /// <summary>
        /// Flushes the rANS state and prefixes the payload with its size.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        public void EndEncoding(ref EncoderBuffer buffer)
        {
            var bytesWritten = (ulong)m_Ans.WriteEnd();

            var sizeBuffer = new EncoderBuffer();
            sizeBuffer.Init(16);
            sizeBuffer.EncodeVarintU64(bytesWritten);
            var sizeLength = sizeBuffer.Length;

            var source = buffer.Data + m_BufferOffset;
            UnsafeUtility.MemMove(source + sizeLength, source, (long)bytesWritten);
            UnsafeUtility.MemCpy(source, sizeBuffer.Data, sizeLength);
            sizeBuffer.Dispose();

            buffer.SetLength(m_BufferOffset + (long)bytesWritten + sizeLength);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_ProbabilityTable.IsCreated)
            {
                m_ProbabilityTable.Dispose();
            }
        }
    }

    /// <summary>
    /// Entry point for entropy coding symbol sequences.
    /// Mirrors <c>draco/compression/entropy/symbol_encoding.cc</c>.
    /// </summary>
    static unsafe class SymbolEncoding
    {
        const int k_MaxTagSymbolBitLength = 32;
        const int k_MaxRawEncodingBitLength = 18;
        const int k_DefaultSymbolCodingCompressionLevel = 7;

        /// <summary>
        /// Encodes a sequence of symbols, choosing between the tagged and the raw scheme.
        /// </summary>
        /// <param name="symbols">Symbols to encode.</param>
        /// <param name="numValues">Number of symbols.</param>
        /// <param name="numComponents">Number of components per value tuple.</param>
        /// <param name="targetBuffer">Destination buffer.</param>
        /// <returns>False on failure.</returns>
        public static bool EncodeSymbols(
            uint* symbols, int numValues, int numComponents, ref EncoderBuffer targetBuffer)
        {
            return EncodeSymbols(
                symbols, numValues, numComponents,
                k_DefaultSymbolCodingCompressionLevel, ref targetBuffer);
        }

        /// <inheritdoc cref="EncodeSymbols(uint*,int,int,ref EncoderBuffer)"/>
        /// <param name="compressionLevel">Symbol coding compression level, between zero and
        /// ten. Higher levels allocate more rANS precision bits.</param>
        public static bool EncodeSymbols(
            uint* symbols,
            int numValues,
            int numComponents,
            int compressionLevel,
            ref EncoderBuffer targetBuffer
            )
        {
            if (numValues < 0)
            {
                return false;
            }
            if (numValues == 0)
            {
                return true;
            }
            if (numComponents <= 0)
            {
                numComponents = 1;
            }

            var numTuples = (numValues + numComponents - 1) / numComponents;
            var bitLengths = new UnsafeList<uint>(numTuples, Allocator.Temp);
            ComputeBitLengths(symbols, numValues, numComponents, ref bitLengths, out var maxValue);

            var taggedSchemeTotalBits = ApproximateTaggedSchemeBits(ref bitLengths, numComponents);
            var rawSchemeTotalBits = ApproximateRawSchemeBits(
                symbols, numValues, maxValue, out var numUniqueSymbols);

            // Largest single entry value that the raw scheme can represent.
            var maxValueBitLength = BitUtils.MostSignificantBit(math.max(1u, maxValue)) + 1;

            SymbolCodingMethod method;
            if (taggedSchemeTotalBits < rawSchemeTotalBits
                || maxValueBitLength > k_MaxRawEncodingBitLength)
            {
                method = SymbolCodingMethod.Tagged;
            }
            else
            {
                method = SymbolCodingMethod.Raw;
            }

            targetBuffer.Encode((byte)method);
            bool result;
            if (method == SymbolCodingMethod.Tagged)
            {
                result = EncodeTaggedSymbols(
                    symbols, numValues, numComponents, ref bitLengths, ref targetBuffer);
            }
            else
            {
                result = EncodeRawSymbols(
                    symbols, numValues, maxValue, numUniqueSymbols, compressionLevel,
                    ref targetBuffer);
            }
            bitLengths.Dispose();
            return result;
        }

        /// <summary>
        /// Computes the bit length of each value tuple, using the largest component.
        /// </summary>
        static void ComputeBitLengths(
            uint* symbols,
            int numValues,
            int numComponents,
            ref UnsafeList<uint> outBitLengths,
            out uint outMaxValue
            )
        {
            outMaxValue = 0;
            for (var i = 0; i < numValues; i += numComponents)
            {
                var maxComponentValue = symbols[i];
                for (var j = 1; j < numComponents; ++j)
                {
                    if (maxComponentValue < symbols[i + j])
                    {
                        maxComponentValue = symbols[i + j];
                    }
                }
                var valueMsbPosition = 0;
                if (maxComponentValue > 0)
                {
                    valueMsbPosition = BitUtils.MostSignificantBit(maxComponentValue);
                }
                if (maxComponentValue > outMaxValue)
                {
                    outMaxValue = maxComponentValue;
                }
                outBitLengths.Add((uint)(valueMsbPosition + 1));
            }
        }

        static long ApproximateTaggedSchemeBits(ref UnsafeList<uint> bitLengths, int numComponents)
        {
            ulong totalBitLength = 0;
            for (var i = 0; i < bitLengths.Length; ++i)
            {
                totalBitLength += bitLengths[i];
            }
            var tagBits = ShannonEntropy.Compute(
                bitLengths.Ptr, bitLengths.Length, 32, out var numUniqueSymbols);
            var tagTableBits = ApproximateRAnsFrequencyTableBits(numUniqueSymbols, numUniqueSymbols);
            return tagBits + tagTableBits + (long)totalBitLength * numComponents;
        }

        static long ApproximateRawSchemeBits(
            uint* symbols, int numSymbols, uint maxValue, out int outNumUniqueSymbols)
        {
            var dataBits = ShannonEntropy.Compute(
                symbols, numSymbols, (int)maxValue, out outNumUniqueSymbols);
            var tableBits = ApproximateRAnsFrequencyTableBits((int)maxValue, outNumUniqueSymbols);
            return tableBits + dataBits;
        }

        /// <summary>
        /// Estimates the size of the frequency table for the given symbols.
        /// Mirrors <c>ApproximateRAnsFrequencyTableBits</c>.
        /// </summary>
        static long ApproximateRAnsFrequencyTableBits(int maxValue, int numUniqueSymbols)
        {
            var tableZeroFrequencyBits = 8L * (numUniqueSymbols + (maxValue - numUniqueSymbols) / 64);
            return 8L * numUniqueSymbols + tableZeroFrequencyBits;
        }

        static bool EncodeTaggedSymbols(
            uint* symbols,
            int numValues,
            int numComponents,
            ref UnsafeList<uint> bitLengths,
            ref EncoderBuffer targetBuffer
            )
        {
            // One frequency entry per possible bit length.
            var frequencies = stackalloc ulong[k_MaxTagSymbolBitLength];
            UnsafeUtility.MemClear(frequencies, k_MaxTagSymbolBitLength * sizeof(ulong));
            for (var i = 0; i < bitLengths.Length; ++i)
            {
                ++frequencies[bitLengths[i]];
            }

            var valueBuffer = new EncoderBuffer();
            valueBuffer.Init(1024);
            var valueBits = (ulong)k_MaxTagSymbolBitLength * (ulong)numValues;

            var tagEncoder = new RAnsSymbolEncoder();
            var success = tagEncoder.Create(5, frequencies, k_MaxTagSymbolBitLength, ref targetBuffer);
            if (success)
            {
                tagEncoder.StartEncoding(ref targetBuffer);
                valueBuffer.StartBitEncoding((long)valueBits, false);

                // rANS needs the tags in reverse order while the values stay in order.
                for (var i = numValues - numComponents; i >= 0; i -= numComponents)
                {
                    var bitLength = bitLengths[i / numComponents];
                    tagEncoder.EncodeSymbol(bitLength);

                    var j = numValues - numComponents - i;
                    var valueBitLength = (int)bitLengths[j / numComponents];
                    for (var c = 0; c < numComponents; ++c)
                    {
                        valueBuffer.EncodeLeastSignificantBits32(valueBitLength, symbols[j + c]);
                    }
                }
                tagEncoder.EndEncoding(ref targetBuffer);
                valueBuffer.EndBitEncoding();

                targetBuffer.Encode(valueBuffer.Data, valueBuffer.Length);
            }

            valueBuffer.Dispose();
            tagEncoder.Dispose();
            return success;
        }

        static bool EncodeRawSymbols(
            uint* symbols,
            int numValues,
            uint maxEntryValue,
            int numUniqueSymbols,
            int compressionLevel,
            ref EncoderBuffer targetBuffer
            )
        {
            var symbolBits = 0;
            if (numUniqueSymbols > 0)
            {
                symbolBits = BitUtils.MostSignificantBit((uint)numUniqueSymbols);
            }
            var uniqueSymbolsBitLength = symbolBits + 1;
            if (uniqueSymbolsBitLength > k_MaxRawEncodingBitLength)
            {
                return false;
            }

            // The compression level shifts the bit length, which controls how many bits
            // rANS allocates. This mapping is part of the bit-stream.
            if (compressionLevel < 4)
            {
                uniqueSymbolsBitLength -= 2;
            }
            else if (compressionLevel < 6)
            {
                uniqueSymbolsBitLength -= 1;
            }
            else if (compressionLevel > 9)
            {
                uniqueSymbolsBitLength += 2;
            }
            else if (compressionLevel > 7)
            {
                uniqueSymbolsBitLength += 1;
            }
            uniqueSymbolsBitLength = math.min(
                math.max(1, uniqueSymbolsBitLength), k_MaxRawEncodingBitLength);
            targetBuffer.Encode((byte)uniqueSymbolsBitLength);

            var numFrequencies = (int)maxEntryValue + 1;
            var frequencies = new UnsafeList<ulong>(numFrequencies, Allocator.Temp);
            frequencies.Resize(numFrequencies, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < numValues; ++i)
            {
                ++frequencies.ElementAt((int)symbols[i]);
            }

            var encoder = new RAnsSymbolEncoder();
            var success = encoder.Create(
                uniqueSymbolsBitLength, frequencies.Ptr, numFrequencies, ref targetBuffer);
            if (success)
            {
                encoder.StartEncoding(ref targetBuffer);
                for (var i = numValues - 1; i >= 0; --i)
                {
                    encoder.EncodeSymbol(symbols[i]);
                }
                encoder.EndEncoding(ref targetBuffer);
            }

            frequencies.Dispose();
            encoder.Dispose();
            return success;
        }
    }

    /// <summary>
    /// Shannon entropy helpers used to pick between symbol coding schemes.
    /// Mirrors <c>draco/compression/entropy/shannon_entropy.cc</c>.
    /// </summary>
    static unsafe class ShannonEntropy
    {
        /// <summary>
        /// Computes the Shannon entropy of a symbol sequence, in bits.
        /// </summary>
        /// <param name="symbols">Input symbols.</param>
        /// <param name="numSymbols">Number of symbols.</param>
        /// <param name="maxValue">Largest symbol value.</param>
        /// <param name="outNumUniqueSymbols">Number of distinct symbols.</param>
        /// <returns>Entropy in bits.</returns>
        public static long Compute(
            uint* symbols, int numSymbols, int maxValue, out int outNumUniqueSymbols)
        {
            var numUniqueSymbols = 0;
            var frequencies = new UnsafeList<int>(maxValue + 1, Allocator.Temp);
            frequencies.Resize(maxValue + 1, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < numSymbols; ++i)
            {
                ++frequencies.ElementAt((int)symbols[i]);
            }
            double totalBits = 0;
            double numSymbolsD = numSymbols;
            for (var i = 0; i < maxValue + 1; ++i)
            {
                if (frequencies[i] > 0)
                {
                    ++numUniqueSymbols;
                    totalBits += frequencies[i] * math.log2(frequencies[i] / numSymbolsD);
                }
            }
            frequencies.Dispose();
            outNumUniqueSymbols = numUniqueSymbols;
            // The entropy sum is always negative.
            return (long)-totalBits;
        }
    }
}
