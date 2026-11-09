// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Bit encoder used for the point counts of the kd-tree algorithm. Which of the three
    /// implementations is used depends on the compression level.
    /// Mirrors the <c>NumbersEncoder</c> of
    /// <c>DynamicIntegerPointsKdTreeEncoderCompressionPolicy</c>.
    /// </summary>
    unsafe struct KdTreeNumbersEncoder : IDisposable
    {
        /// <summary>Which implementation is used.</summary>
        public enum Kind
        {
            Direct,
            RAns,
            Folded
        }

        Kind m_Kind;
        DirectBitEncoder m_Direct;
        RAnsBitEncoder m_RAns;
        FoldedBit32RAnsEncoder m_Folded;

        /// <summary>
        /// Selects the implementation that matches a compression level.
        /// </summary>
        /// <param name="compressionLevel">Compression level between zero and six.</param>
        /// <returns>The matching implementation.</returns>
        public static Kind KindForCompressionLevel(int compressionLevel)
        {
            if (compressionLevel >= 4)
            {
                return Kind.Folded;
            }
            return compressionLevel >= 2 ? Kind.RAns : Kind.Direct;
        }

        /// <summary>
        /// Prepares the encoder.
        /// </summary>
        /// <param name="kind">Implementation to use.</param>
        public void StartEncoding(Kind kind)
        {
            m_Kind = kind;
            switch (kind)
            {
                case Kind.Direct:
                    m_Direct.StartEncoding();
                    break;
                case Kind.RAns:
                    m_RAns.StartEncoding();
                    break;
                default:
                    m_Folded.StartEncoding();
                    break;
            }
        }

        /// <summary>
        /// Encodes the least significant bits of a value.
        /// </summary>
        /// <param name="numBits">Number of bits.</param>
        /// <param name="value">Value to encode.</param>
        public void EncodeLeastSignificantBits32(int numBits, uint value)
        {
            switch (m_Kind)
            {
                case Kind.Direct:
                    m_Direct.EncodeLeastSignificantBits32(numBits, value);
                    break;
                case Kind.RAns:
                    m_RAns.EncodeLeastSignificantBits32(numBits, value);
                    break;
                default:
                    m_Folded.EncodeLeastSignificantBits32(numBits, value);
                    break;
            }
        }

        /// <summary>
        /// Flushes the encoded bits into the target buffer.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        public void EndEncoding(ref EncoderBuffer buffer)
        {
            switch (m_Kind)
            {
                case Kind.Direct:
                    m_Direct.EndEncoding(ref buffer);
                    break;
                case Kind.RAns:
                    m_RAns.EndEncoding(ref buffer);
                    break;
                default:
                    m_Folded.EndEncoding(ref buffer);
                    break;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_Direct.Dispose();
            m_RAns.Dispose();
            m_Folded.Dispose();
        }
    }

    /// <summary>
    /// Encodes an integer point cloud by recursively splitting it in the middle of the axis
    /// that keeps the points as clustered as possible, storing the deviation from an even
    /// split. The order of the points is not preserved.
    /// Mirrors <c>draco::DynamicIntegerPointsKdTreeEncoder</c>.
    /// </summary>
    unsafe struct DynamicIntegerPointsKdTreeEncoder : IDisposable
    {
        int m_CompressionLevel;
        bool m_SelectAxis;
        int m_Dimension;
        uint m_BitLength;
        uint m_NumPoints;

        KdTreeNumbersEncoder m_NumbersEncoder;
        DirectBitEncoder m_RemainingBitsEncoder;
        DirectBitEncoder m_AxisEncoder;
        DirectBitEncoder m_HalfEncoder;

        UnsafeList<uint> m_Deviations;
        UnsafeList<uint> m_NumRemainingBits;
        UnsafeList<uint> m_Axes;
        // Flattened stacks of per axis base values and refinement levels.
        UnsafeList<uint> m_BaseStack;
        UnsafeList<uint> m_LevelsStack;
        UnsafeList<EncodingStatus> m_StatusStack;

        struct EncodingStatus
        {
            public int begin;
            public int end;
            public uint lastAxis;
            public uint stackPos;
        }

        /// <summary>
        /// Allocates the encoder.
        /// </summary>
        /// <param name="dimension">Number of components per point.</param>
        /// <param name="compressionLevel">Compression level between zero and six.</param>
        public void Init(int dimension, int compressionLevel)
        {
            m_Dimension = dimension;
            m_CompressionLevel = compressionLevel;
            m_SelectAxis = compressionLevel >= 6;
            m_Deviations = AllocateZeroed(dimension);
            m_NumRemainingBits = AllocateZeroed(dimension);
            m_Axes = AllocateZeroed(dimension);
            var stackSize = (32 * dimension + 1) * dimension;
            m_BaseStack = AllocateZeroed(stackSize);
            m_LevelsStack = AllocateZeroed(stackSize);
            m_StatusStack = new UnsafeList<EncodingStatus>(64, Allocator.Persistent);
        }

        static UnsafeList<uint> AllocateZeroed(int length)
        {
            var list = new UnsafeList<uint>(length, Allocator.Persistent);
            list.Resize(length, NativeArrayOptions.ClearMemory);
            return list;
        }

        /// <summary>
        /// Encodes the points into the buffer.
        /// Mirrors <c>DynamicIntegerPointsKdTreeEncoder::EncodePoints</c>.
        /// </summary>
        /// <param name="points">Point values, <paramref name="numPoints"/> times the dimension.</param>
        /// <param name="numPoints">Number of points.</param>
        /// <param name="bitLength">Highest bit used by any coordinate.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>True on success.</returns>
        public bool EncodePoints(uint* points, int numPoints, uint bitLength, ref EncoderBuffer buffer)
        {
            m_BitLength = bitLength;
            m_NumPoints = (uint)numPoints;

            buffer.Encode(m_BitLength);
            buffer.Encode(m_NumPoints);
            if (m_NumPoints == 0)
            {
                return true;
            }

            m_NumbersEncoder.StartEncoding(
                KdTreeNumbersEncoder.KindForCompressionLevel(m_CompressionLevel));
            m_RemainingBitsEncoder.StartEncoding();
            m_AxisEncoder.StartEncoding();
            m_HalfEncoder.StartEncoding();

            EncodeInternal(points, numPoints);

            m_NumbersEncoder.EndEncoding(ref buffer);
            m_RemainingBitsEncoder.EndEncoding(ref buffer);
            m_AxisEncoder.EndEncoding(ref buffer);
            m_HalfEncoder.EndEncoding(ref buffer);
            return true;
        }

        uint GetAndEncodeAxis(
            uint* points, int begin, int end, uint* oldBase, uint* levels, uint lastAxis)
        {
            if (!m_SelectAxis)
            {
                return (lastAxis + 1) % (uint)m_Dimension;
            }

            // For many points the axis that keeps as many points as possible bundled is
            // selected. For few points the least refined axis is used instead.
            uint bestAxis = 0;
            if (end - begin < 64)
            {
                for (uint axis = 1; axis < m_Dimension; ++axis)
                {
                    if (levels[bestAxis] > levels[axis])
                    {
                        bestAxis = axis;
                    }
                }
                return bestAxis;
            }

            var size = (uint)(end - begin);
            var deviations = m_Deviations.Ptr;
            var numRemainingBits = m_NumRemainingBits.Ptr;
            for (var i = 0; i < m_Dimension; ++i)
            {
                deviations[i] = 0;
                numRemainingBits[i] = m_BitLength - levels[i];
                if (numRemainingBits[i] <= 0)
                {
                    continue;
                }
                var split = oldBase[i] + (1u << (int)(numRemainingBits[i] - 1));
                for (var p = begin; p != end; ++p)
                {
                    if (points[p * m_Dimension + i] < split)
                    {
                        deviations[i] += 1;
                    }
                }
                deviations[i] = math.max(size - deviations[i], deviations[i]);
            }

            uint maxValue = 0;
            bestAxis = 0;
            for (uint i = 0; i < m_Dimension; ++i)
            {
                // Only axes that can still be subdivided are considered.
                if (numRemainingBits[i] == 0)
                {
                    continue;
                }
                if (maxValue < deviations[i])
                {
                    maxValue = deviations[i];
                    bestAxis = i;
                }
            }
            m_AxisEncoder.EncodeLeastSignificantBits32(4, bestAxis);
            return bestAxis;
        }

        void EncodeInternal(uint* points, int numPoints)
        {
            var dimension = m_Dimension;
            var baseStack = m_BaseStack.Ptr;
            var levelsStack = m_LevelsStack.Ptr;
            for (var i = 0; i < dimension; ++i)
            {
                baseStack[i] = 0;
                levelsStack[i] = 0;
            }

            m_StatusStack.Clear();
            m_StatusStack.Add(new EncodingStatus
            {
                begin = 0,
                end = numPoints,
                lastAxis = 0,
                stackPos = 0
            });

            while (m_StatusStack.Length > 0)
            {
                var status = m_StatusStack[m_StatusStack.Length - 1];
                m_StatusStack.RemoveAt(m_StatusStack.Length - 1);

                var begin = status.begin;
                var end = status.end;
                var lastAxis = status.lastAxis;
                var stackPos = (int)status.stackPos;
                var oldBase = baseStack + stackPos * dimension;
                var levels = levelsStack + stackPos * dimension;

                var axis = GetAndEncodeAxis(points, begin, end, oldBase, levels, lastAxis);
                var level = levels[axis];
                var numRemainingPoints = (uint)(end - begin);

                // All axes are subdivided to the end.
                if (m_BitLength - level == 0)
                {
                    continue;
                }

                // Fast path for one or two remaining points. Handling two as well gives a
                // small additional speed up.
                if (numRemainingPoints <= 2)
                {
                    var axes = m_Axes.Ptr;
                    axes[0] = axis;
                    for (var i = 1; i < dimension; ++i)
                    {
                        axes[i] = (axes[i - 1] + 1) % (uint)dimension;
                    }
                    for (var i = 0; i < numRemainingPoints; ++i)
                    {
                        var point = points + (begin + i) * dimension;
                        for (var j = 0; j < dimension; ++j)
                        {
                            var numRemainingBits = m_BitLength - levels[axes[j]];
                            if (numRemainingBits != 0)
                            {
                                m_RemainingBitsEncoder.EncodeLeastSignificantBits32(
                                    (int)numRemainingBits, point[axes[j]]);
                            }
                        }
                    }
                    continue;
                }

                var remainingBits = m_BitLength - level;
                var modifier = 1u << (int)(remainingBits - 1);
                var newBase = baseStack + (stackPos + 1) * dimension;
                for (var i = 0; i < dimension; ++i)
                {
                    newBase[i] = oldBase[i];
                }
                newBase[axis] += modifier;

                var split = Partition(points, begin, end, (int)axis, newBase[axis]);

                // Number of points in the first and second half.
                var requiredBits = BitUtils.MostSignificantBit(numRemainingPoints);

                var firstHalf = (uint)(split - begin);
                var secondHalf = (uint)(end - split);
                var left = firstHalf < secondHalf;

                if (firstHalf != secondHalf)
                {
                    m_HalfEncoder.EncodeBit(left);
                }

                if (left)
                {
                    m_NumbersEncoder.EncodeLeastSignificantBits32(
                        requiredBits, numRemainingPoints / 2 - firstHalf);
                }
                else
                {
                    m_NumbersEncoder.EncodeLeastSignificantBits32(
                        requiredBits, numRemainingPoints / 2 - secondHalf);
                }

                levels[axis] += 1;
                var newLevels = levelsStack + (stackPos + 1) * dimension;
                for (var i = 0; i < dimension; ++i)
                {
                    newLevels[i] = levels[i];
                }
                if (split != begin)
                {
                    m_StatusStack.Add(new EncodingStatus
                    {
                        begin = begin,
                        end = split,
                        lastAxis = axis,
                        stackPos = (uint)stackPos
                    });
                }
                if (split != end)
                {
                    m_StatusStack.Add(new EncodingStatus
                    {
                        begin = split,
                        end = end,
                        lastAxis = axis,
                        stackPos = (uint)(stackPos + 1)
                    });
                }
            }
        }

        /// <summary>
        /// Moves all points whose coordinate on <paramref name="axis"/> is smaller than
        /// <paramref name="value"/> to the front, returning the index of the first point that
        /// is not. Replicates the two pointer algorithm of <c>std::partition</c> for
        /// bidirectional iterators, which the reference encoder relies on.
        /// </summary>
        int Partition(uint* points, int first, int last, int axis, uint value)
        {
            var dimension = m_Dimension;
            while (true)
            {
                // Advance from the left past the elements that belong to the first group.
                while (true)
                {
                    if (first == last)
                    {
                        return first;
                    }
                    if (points[first * dimension + axis] >= value)
                    {
                        break;
                    }
                    ++first;
                }
                // Move in from the right until an element of the first group is found.
                do
                {
                    if (first == --last)
                    {
                        return first;
                    }
                } while (points[last * dimension + axis] >= value);

                var a = points + first * dimension;
                var b = points + last * dimension;
                for (var i = 0; i < dimension; ++i)
                {
                    var tmp = a[i];
                    a[i] = b[i];
                    b[i] = tmp;
                }
                ++first;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            m_NumbersEncoder.Dispose();
            m_RemainingBitsEncoder.Dispose();
            m_AxisEncoder.Dispose();
            m_HalfEncoder.Dispose();
            DisposeIfCreated(ref m_Deviations);
            DisposeIfCreated(ref m_NumRemainingBits);
            DisposeIfCreated(ref m_Axes);
            DisposeIfCreated(ref m_BaseStack);
            DisposeIfCreated(ref m_LevelsStack);
            if (m_StatusStack.IsCreated)
            {
                m_StatusStack.Dispose();
            }
        }

        static void DisposeIfCreated(ref UnsafeList<uint> list)
        {
            if (list.IsCreated)
            {
                list.Dispose();
            }
        }
    }

    /// <summary>
    /// Encodes all attributes of a point cloud jointly with the kd-tree algorithm.
    /// Mirrors <c>draco::KdTreeAttributesEncoder</c>.
    /// </summary>
    static unsafe class KdTreeAttributesEncoding
    {
        /// <summary>
        /// Transforms, encodes and finally stores the transform parameters of all attributes.
        /// Mirrors <c>AttributesEncoder::EncodeAttributes</c> for the kd-tree encoder.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="attributesEncoder">Attributes encoder holding the attribute ids.</param>
        /// <param name="quantizationTransforms">Per attribute quantization transforms.</param>
        /// <param name="quantizedAttributes">Quantized attribute values.</param>
        /// <param name="minSignedValues">Component minima of the signed integer attributes.</param>
        /// <param name="numComponents">Total number of components across all attributes.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False on unsupported input.</returns>
        public static bool Encode(
            DracoEncoderState* encoder,
            AttributesEncoder* attributesEncoder,
            ref UnsafeList<AttributeQuantizationTransform> quantizationTransforms,
            ref UnsafeList<PointAttribute> quantizedAttributes,
            ref UnsafeList<int> minSignedValues,
            ref int numComponents,
            ref EncoderBuffer buffer
            )
        {
            if (!TransformAttributes(
                    encoder, attributesEncoder, ref quantizationTransforms,
                    ref quantizedAttributes, ref minSignedValues, ref numComponents))
            {
                return false;
            }
            if (!EncodePortableAttributes(
                    encoder, attributesEncoder, ref quantizedAttributes, ref minSignedValues,
                    numComponents, ref buffer))
            {
                return false;
            }
            // The transform parameters follow the values, matching the decoding order.
            for (var i = 0; i < quantizationTransforms.Length; ++i)
            {
                quantizationTransforms.ElementAt(i).EncodeParameters(ref buffer);
            }
            for (var i = 0; i < minSignedValues.Length; ++i)
            {
                buffer.EncodeVarintI32(minSignedValues[i]);
            }
            return true;
        }

        static bool TransformAttributes(
            DracoEncoderState* encoder,
            AttributesEncoder* attributesEncoder,
            ref UnsafeList<AttributeQuantizationTransform> quantizationTransforms,
            ref UnsafeList<PointAttribute> quantizedAttributes,
            ref UnsafeList<int> minSignedValues,
            ref int numComponents
            )
        {
            var numPoints = encoder->geometry.numPoints;
            var numAttributes = attributesEncoder->NumAttributes;
            if (!quantizationTransforms.IsCreated)
            {
                quantizationTransforms = new UnsafeList<AttributeQuantizationTransform>(
                    numAttributes, Allocator.Persistent);
                quantizedAttributes = new UnsafeList<PointAttribute>(
                    numAttributes, Allocator.Persistent);
                minSignedValues = new UnsafeList<int>(numAttributes * 4, Allocator.Persistent);
            }

            var totalComponents = 0;
            for (var i = 0; i < numAttributes; ++i)
            {
                var attribute = encoder->geometry.Attribute(attributesEncoder->GetAttributeId(i));
                totalComponents += attribute->numComponents;
            }
            numComponents = totalComponents;

            for (var i = 0; i < numAttributes; ++i)
            {
                var attributeId = attributesEncoder->GetAttributeId(i);
                var attribute = encoder->geometry.Attribute(attributeId);
                if (attribute->dataType == DataType.Float32)
                {
                    // Floating point attributes have to be quantized.
                    var transform = new AttributeQuantizationTransform();
                    transform.Init();
                    var quantizationBits =
                        encoder->options.GetQuantizationBits(attribute->attributeType);
                    if (quantizationBits < 1)
                    {
                        transform.Dispose();
                        return false;
                    }
                    if (!transform.ComputeParameters(attribute, quantizationBits))
                    {
                        transform.Dispose();
                        return false;
                    }
                    var portable = AttributeQuantizationTransform.InitTransformedAttribute(
                        attribute, numPoints);
                    transform.TransformAttribute(
                        attribute, null, numPoints,
                        (PointAttribute*)UnsafeUtility.AddressOf(ref portable));
                    quantizationTransforms.Add(transform);
                    quantizedAttributes.Add(portable);
                }
                else if (attribute->dataType == DataType.Int32
                    || attribute->dataType == DataType.Int16
                    || attribute->dataType == DataType.Int8)
                {
                    // Signed values are shifted into the unsigned range that the core kd-tree
                    // algorithm expects.
                    var componentCount = (int)attribute->numComponents;
                    var minValue = stackalloc int[componentCount];
                    var actValue = stackalloc int[componentCount];
                    for (var c = 0; c < componentCount; ++c)
                    {
                        minValue[c] = int.MaxValue;
                    }
                    for (var avi = 0; avi < attribute->numUniqueEntries; ++avi)
                    {
                        AttributeValueConversion.ConvertToInt32(
                            attribute, avi, componentCount, actValue);
                        for (var c = 0; c < componentCount; ++c)
                        {
                            if (minValue[c] > actValue[c])
                            {
                                minValue[c] = actValue[c];
                            }
                        }
                    }
                    for (var c = 0; c < componentCount; ++c)
                    {
                        minSignedValues.Add(minValue[c]);
                    }
                }
            }
            return true;
        }

        static bool EncodePortableAttributes(
            DracoEncoderState* encoder,
            AttributesEncoder* attributesEncoder,
            ref UnsafeList<PointAttribute> quantizedAttributes,
            ref UnsafeList<int> minSignedValues,
            int numComponents,
            ref EncoderBuffer buffer
            )
        {
            // Compression levels above six have no viable algorithm yet.
            var compressionLevel = math.min(10 - encoder->options.Speed, 6);
            if (compressionLevel == 6 && numComponents > 15)
            {
                // Axis selection is encoded with four bits, so it cannot be used here.
                compressionLevel = 5;
            }
            buffer.Encode((byte)compressionLevel);

            var numPoints = encoder->geometry.numPoints;
            var pointVector = new UnsafeList<uint>(numPoints * numComponents, Allocator.Temp);
            pointVector.Resize(numPoints * numComponents, NativeArrayOptions.ClearMemory);
            var points = pointVector.Ptr;

            var numProcessedComponents = 0;
            var numProcessedQuantizedAttributes = 0;
            var numProcessedSignedComponents = 0;
            var success = true;
            for (var i = 0; i < attributesEncoder->NumAttributes; ++i)
            {
                var attributeId = attributesEncoder->GetAttributeId(i);
                var attribute = encoder->geometry.Attribute(attributeId);
                PointAttribute* sourceAttribute;
                switch (attribute->dataType)
                {
                    case DataType.UInt32:
                    case DataType.UInt16:
                    case DataType.UInt8:
                    case DataType.Int32:
                    case DataType.Int16:
                    case DataType.Int8:
                        // The original attribute can be used as is.
                        sourceAttribute = attribute;
                        break;
                    case DataType.Float32:
                        // The quantized attribute is used instead.
                        sourceAttribute = (PointAttribute*)UnsafeUtility.AddressOf(
                            ref quantizedAttributes.ElementAt(numProcessedQuantizedAttributes));
                        numProcessedQuantizedAttributes++;
                        break;
                    default:
                        success = false;
                        sourceAttribute = null;
                        break;
                }
                if (!success)
                {
                    break;
                }

                var componentCount = (int)sourceAttribute->numComponents;
                if (sourceAttribute->dataType == DataType.UInt32)
                {
                    // Same type as the point vector, so the values can be copied directly.
                    for (var p = 0; p < numPoints; ++p)
                    {
                        var avi = sourceAttribute->MappedIndex(p);
                        var source = (uint*)sourceAttribute->GetAddress(avi);
                        var destination = points + p * numComponents + numProcessedComponents;
                        for (var c = 0; c < componentCount; ++c)
                        {
                            destination[c] = source[c];
                        }
                    }
                }
                else if (sourceAttribute->dataType == DataType.Int32
                    || sourceAttribute->dataType == DataType.Int16
                    || sourceAttribute->dataType == DataType.Int8)
                {
                    var signedPoint = stackalloc int[componentCount];
                    for (var p = 0; p < numPoints; ++p)
                    {
                        var avi = sourceAttribute->MappedIndex(p);
                        AttributeValueConversion.ConvertToInt32(
                            sourceAttribute, avi, componentCount, signedPoint);
                        var destination = points + p * numComponents + numProcessedComponents;
                        for (var c = 0; c < componentCount; ++c)
                        {
                            destination[c] = (uint)(signedPoint[c]
                                - minSignedValues[numProcessedSignedComponents + c]);
                        }
                    }
                    numProcessedSignedComponents += componentCount;
                }
                else
                {
                    var value = stackalloc int[componentCount];
                    for (var p = 0; p < numPoints; ++p)
                    {
                        var avi = sourceAttribute->MappedIndex(p);
                        AttributeValueConversion.ConvertToInt32(
                            sourceAttribute, avi, componentCount, value);
                        var destination = points + p * numComponents + numProcessedComponents;
                        for (var c = 0; c < componentCount; ++c)
                        {
                            destination[c] = (uint)value[c];
                        }
                    }
                }
                numProcessedComponents += componentCount;
            }

            if (!success)
            {
                pointVector.Dispose();
                return false;
            }

            // Largest bit length needed for the kd-tree encoding.
            var numBits = 0;
            for (var i = 0; i < numPoints * numComponents; ++i)
            {
                if (points[i] <= 0)
                {
                    continue;
                }
                var msb = BitUtils.MostSignificantBit(points[i]) + 1;
                if (msb > numBits)
                {
                    numBits = msb;
                }
            }

            var pointsEncoder = new DynamicIntegerPointsKdTreeEncoder();
            pointsEncoder.Init(numComponents, compressionLevel);
            var result = pointsEncoder.EncodePoints(
                points, numPoints, (uint)numBits, ref buffer);
            pointsEncoder.Dispose();
            pointVector.Dispose();
            return result;
        }
    }
}
