// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Decodes an integer point cloud encoded with Draco's dynamic kd-tree encoder.
    /// Mirrors <c>draco::DynamicIntegerPointsKdTreeDecoder</c>. The compression level
    /// selects the bit decoders and whether the split axis is coded explicitly, which the
    /// reference implementation expresses through compile time policies.
    /// </summary>
    unsafe struct IntegerPointsKdTreeDecoder : IDisposable
    {
        struct DecodingStatus
        {
            public uint numRemainingPoints;
            public uint lastAxis;
            public int stackPosition;
        }

        int m_CompressionLevel;
        uint m_BitLength;
        uint m_NumPoints;
        uint m_NumDecodedPoints;
        uint m_Dimension;

        // Compression levels 0 and 1 read numbers directly, 2 and 3 use a plain rANS bit
        // decoder and 4 and up use the folded variant. Only level 6 codes the axis.
        DirectBitDecoder m_NumbersDirect;
        RAnsBitDecoder m_NumbersRAns;
        FoldedBit32RAnsDecoder m_NumbersFolded;
        DirectBitDecoder m_RemainingBitsDecoder;
        DirectBitDecoder m_AxisDecoder;
        DirectBitDecoder m_HalfDecoder;

        UnsafeList<uint> m_Point;
        UnsafeList<uint> m_Axes;
        UnsafeList<uint> m_BaseStack;
        UnsafeList<uint> m_LevelsStack;
        UnsafeList<DecodingStatus> m_StatusStack;

        /// <summary>Number of points decoded by the last run.</summary>
        public uint NumDecodedPoints => m_NumDecodedPoints;

        bool SelectAxis => m_CompressionLevel == 6;

        /// <summary>
        /// Configures the decoder.
        /// </summary>
        /// <param name="compressionLevel">Compression level in the range zero to six.</param>
        /// <param name="dimension">Total number of dimensions across all attributes.</param>
        public void Init(int compressionLevel, uint dimension)
        {
            m_CompressionLevel = compressionLevel;
            m_Dimension = dimension;
        }

        void DecodeNumber(int numBits, out uint value)
        {
            if (m_CompressionLevel < 2)
            {
                var decoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_NumbersDirect);
                decoder->DecodeLeastSignificantBits32(numBits, out value);
            }
            else if (m_CompressionLevel < 4)
            {
                var decoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_NumbersRAns);
                decoder->DecodeLeastSignificantBits32(numBits, out value);
            }
            else
            {
                var decoder = (FoldedBit32RAnsDecoder*)UnsafeUtility.AddressOf(ref m_NumbersFolded);
                decoder->DecodeLeastSignificantBits32(numBits, out value);
            }
        }

        bool StartNumbersDecoding(DecoderBuffer* buffer)
        {
            if (m_CompressionLevel < 2)
            {
                var decoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_NumbersDirect);
                return decoder->StartDecoding(buffer);
            }
            if (m_CompressionLevel < 4)
            {
                var decoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_NumbersRAns);
                return decoder->StartDecoding(buffer);
            }
            var folded = (FoldedBit32RAnsDecoder*)UnsafeUtility.AddressOf(ref m_NumbersFolded);
            return folded->StartDecoding(buffer);
        }

        /// <summary>
        /// Decodes the kd-tree and writes each decoded point through <paramref name="sink"/>.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <param name="sink">Receiver of the decoded points.</param>
        /// <param name="maxPoints">Maximum number of points the sink accepts.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodePoints(DecoderBuffer* buffer, ref KdTreePointSink sink, uint maxPoints)
        {
            if (!buffer->Decode(out m_BitLength))
            {
                return false;
            }
            if (m_BitLength > 32)
            {
                return false;
            }
            if (!buffer->Decode(out m_NumPoints))
            {
                return false;
            }
            if (m_NumPoints == 0)
            {
                return true;
            }
            if (m_NumPoints > maxPoints)
            {
                return false;
            }
            m_NumDecodedPoints = 0;

            if (!StartNumbersDecoding(buffer))
            {
                return false;
            }
            var remainingBits = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_RemainingBitsDecoder);
            if (!remainingBits->StartDecoding(buffer))
            {
                return false;
            }
            var axisDecoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_AxisDecoder);
            if (!axisDecoder->StartDecoding(buffer))
            {
                return false;
            }
            var halfDecoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_HalfDecoder);
            if (!halfDecoder->StartDecoding(buffer))
            {
                return false;
            }

            return DecodeInternal(m_NumPoints, ref sink);
        }

        uint GetAxis(uint numRemainingPoints, uint* levels, uint lastAxis)
        {
            if (!SelectAxis)
            {
                return lastAxis == m_Dimension - 1 ? 0 : lastAxis + 1;
            }

            uint bestAxis = 0;
            if (numRemainingPoints < 64)
            {
                for (uint axis = 1; axis < m_Dimension; ++axis)
                {
                    if (levels[bestAxis] > levels[axis])
                    {
                        bestAxis = axis;
                    }
                }
            }
            else
            {
                var axisDecoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_AxisDecoder);
                axisDecoder->DecodeLeastSignificantBits32(4, out bestAxis);
            }
            return bestAxis;
        }

        bool DecodeInternal(uint numPoints, ref KdTreePointSink sink)
        {
            var dimension = (int)m_Dimension;
            // The stack depth is bounded by the number of subdivisions per axis, plus one
            // extra entry for the second leaf.
            var stackEntries = 32 * dimension + 1;
            AllocateStacks(stackEntries, dimension);

            var baseStack = m_BaseStack.Ptr;
            var levelsStack = m_LevelsStack.Ptr;
            var point = m_Point.Ptr;
            var axes = m_Axes.Ptr;

            m_StatusStack.Clear();
            m_StatusStack.Add(new DecodingStatus
            {
                numRemainingPoints = numPoints,
                lastAxis = 0,
                stackPosition = 0
            });

            var remainingBits = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_RemainingBitsDecoder);
            var halfDecoder = (DirectBitDecoder*)UnsafeUtility.AddressOf(ref m_HalfDecoder);

            while (m_StatusStack.Length > 0)
            {
                var status = m_StatusStack[m_StatusStack.Length - 1];
                m_StatusStack.RemoveAt(m_StatusStack.Length - 1);

                var numRemainingPoints = status.numRemainingPoints;
                var lastAxis = status.lastAxis;
                var stackPosition = status.stackPosition;
                var oldBase = baseStack + (long)stackPosition * dimension;
                var levels = levelsStack + (long)stackPosition * dimension;

                if (numRemainingPoints > numPoints)
                {
                    return false;
                }
                if (stackPosition + 1 >= stackEntries)
                {
                    return false;
                }

                var axis = GetAxis(numRemainingPoints, levels, lastAxis);
                if (axis >= m_Dimension)
                {
                    return false;
                }

                var level = levels[axis];

                // Every axis is fully subdivided, the remaining points are all identical.
                if (m_BitLength - level == 0)
                {
                    for (uint i = 0; i < numRemainingPoints; i++)
                    {
                        sink.Write(oldBase);
                        ++m_NumDecodedPoints;
                    }
                    continue;
                }

                // One or two remaining points are cheaper to decode bit by bit.
                if (numRemainingPoints <= 2)
                {
                    axes[0] = axis;
                    for (var i = 1; i < dimension; i++)
                    {
                        axes[i] = axes[i - 1] == m_Dimension - 1 ? 0 : axes[i - 1] + 1;
                    }
                    for (uint i = 0; i < numRemainingPoints; ++i)
                    {
                        for (var j = 0; j < dimension; j++)
                        {
                            var a = axes[j];
                            point[a] = 0;
                            var numRemainingBits = (int)(m_BitLength - levels[a]);
                            if (numRemainingBits != 0)
                            {
                                if (!remainingBits->DecodeLeastSignificantBits32(
                                        numRemainingBits, out var value))
                                {
                                    return false;
                                }
                                point[a] = value;
                            }
                            point[a] = oldBase[a] | point[a];
                        }
                        sink.Write(point);
                        ++m_NumDecodedPoints;
                    }
                    continue;
                }

                if (m_NumDecodedPoints > m_NumPoints)
                {
                    return false;
                }

                var numRemainingBitsForLevel = (int)(m_BitLength - level);
                var modifier = 1u << (numRemainingBitsForLevel - 1);

                var nextBase = baseStack + (long)(stackPosition + 1) * dimension;
                UnsafeUtility.MemCpy(nextBase, oldBase, (long)dimension * sizeof(uint));
                nextBase[axis] += modifier;

                var incomingBits = BitUtils.MostSignificantBit(numRemainingPoints);
                DecodeNumber(incomingBits, out var number);

                var firstHalf = numRemainingPoints / 2;
                if (firstHalf < number)
                {
                    return false; // Invalid number.
                }
                firstHalf -= number;
                var secondHalf = numRemainingPoints - firstHalf;

                if (firstHalf != secondHalf)
                {
                    if (!halfDecoder->DecodeNextBit())
                    {
                        (firstHalf, secondHalf) = (secondHalf, firstHalf);
                    }
                }

                levels[axis] += 1;
                var nextLevels = levelsStack + (long)(stackPosition + 1) * dimension;
                UnsafeUtility.MemCpy(nextLevels, levels, (long)dimension * sizeof(uint));

                if (firstHalf != 0)
                {
                    m_StatusStack.Add(new DecodingStatus
                    {
                        numRemainingPoints = firstHalf,
                        lastAxis = axis,
                        stackPosition = stackPosition
                    });
                }
                if (secondHalf != 0)
                {
                    m_StatusStack.Add(new DecodingStatus
                    {
                        numRemainingPoints = secondHalf,
                        lastAxis = axis,
                        stackPosition = stackPosition + 1
                    });
                }
            }
            return true;
        }

        void AllocateStacks(int stackEntries, int dimension)
        {
            var total = stackEntries * dimension;
            if (!m_BaseStack.IsCreated)
            {
                m_BaseStack = new UnsafeList<uint>(total, Allocator.Persistent);
                m_LevelsStack = new UnsafeList<uint>(total, Allocator.Persistent);
                m_Point = new UnsafeList<uint>(dimension, Allocator.Persistent);
                m_Axes = new UnsafeList<uint>(dimension, Allocator.Persistent);
                m_StatusStack = new UnsafeList<DecodingStatus>(64, Allocator.Persistent);
            }
            m_BaseStack.Resize(total, NativeArrayOptions.ClearMemory);
            m_LevelsStack.Resize(total, NativeArrayOptions.ClearMemory);
            m_Point.Resize(dimension, NativeArrayOptions.ClearMemory);
            m_Axes.Resize(dimension, NativeArrayOptions.ClearMemory);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_NumbersDirect.Dispose();
            m_NumbersFolded.Dispose();
            m_RemainingBitsDecoder.Dispose();
            m_AxisDecoder.Dispose();
            m_HalfDecoder.Dispose();
            if (m_BaseStack.IsCreated)
            {
                m_BaseStack.Dispose();
                m_LevelsStack.Dispose();
                m_Point.Dispose();
                m_Axes.Dispose();
                m_StatusStack.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes decoded kd-tree points into the target attributes.
    /// Mirrors <c>draco::PointAttributeVectorOutputIterator</c>.
    /// </summary>
    unsafe struct KdTreePointSink : IDisposable
    {
        struct Target
        {
            [NativeDisableUnsafePtrRestriction]
            public PointAttribute* attribute;
            public int offset;
            public int dataSize;
            public int numComponents;
        }

        UnsafeList<Target> m_Targets;
        UnsafeList<byte> m_Scratch;
        int m_PointId;

        /// <summary>
        /// Prepares the sink for the given number of target attributes.
        /// </summary>
        /// <param name="numTargets">Number of attributes.</param>
        public void Init(int numTargets)
        {
            m_Targets = new UnsafeList<Target>(numTargets, Allocator.Persistent);
            m_Scratch = new UnsafeList<byte>(64, Allocator.Persistent);
            m_PointId = 0;
        }

        /// <summary>
        /// Registers a target attribute.
        /// </summary>
        /// <param name="attribute">Attribute to write to.</param>
        /// <param name="dimensionOffset">Offset of the attribute within the point vector.</param>
        /// <param name="dataSize">Size of a single component in bytes.</param>
        /// <param name="numComponents">Number of components.</param>
        public void AddTarget(PointAttribute* attribute, int dimensionOffset, int dataSize, int numComponents)
        {
            m_Targets.Add(new Target
            {
                attribute = attribute,
                offset = dimensionOffset,
                dataSize = dataSize,
                numComponents = numComponents
            });
            var required = dataSize * numComponents;
            if (m_Scratch.Length < required)
            {
                m_Scratch.Resize(required, NativeArrayOptions.ClearMemory);
            }
        }

        /// <summary>
        /// Writes one decoded point into all target attributes.
        /// </summary>
        /// <param name="values">The decoded point, one unsigned value per dimension.</param>
        public void Write(uint* values)
        {
            for (var i = 0; i < m_Targets.Length; ++i)
            {
                var target = m_Targets[i];
                var attribute = target.attribute;
                var valueIndex = attribute->MappedIndex(m_PointId);
                if (valueIndex >= attribute->numUniqueEntries)
                {
                    continue;
                }
                var source = values + target.offset;
                if (target.dataSize < 4)
                {
                    // Narrow the decoded 32 bit values down to the attribute's data type.
                    var destination = m_Scratch.Ptr;
                    for (var c = 0; c < target.numComponents; ++c)
                    {
                        UnsafeUtility.MemCpy(
                            destination + c * target.dataSize, source + c, target.dataSize);
                    }
                    attribute->SetAttributeValue(valueIndex, destination);
                }
                else
                {
                    attribute->SetAttributeValue(valueIndex, source);
                }
            }
            ++m_PointId;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Targets.IsCreated)
            {
                m_Targets.Dispose();
            }
            if (m_Scratch.IsCreated)
            {
                m_Scratch.Dispose();
            }
        }
    }
}
