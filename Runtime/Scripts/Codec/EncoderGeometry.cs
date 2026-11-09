// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Encoder settings, combining the global speed options and the per attribute type
    /// quantization options of <c>draco::EncoderOptions</c>.
    /// </summary>
    struct EncoderOptions
    {
        public int encodingSpeed;
        public int decodingSpeed;

        public int positionQuantizationBits;
        public int normalQuantizationBits;
        public int texCoordQuantizationBits;
        public int colorQuantizationBits;
        public int genericQuantizationBits;

        /// <summary>
        /// Default settings, matching <c>EncoderOptions::CreateDefaultOptions</c> combined with
        /// the defaults of the Draco for Unity plug-in.
        /// </summary>
        public static EncoderOptions Default => new EncoderOptions
        {
            encodingSpeed = 0,
            decodingSpeed = 0,
            positionQuantizationBits = 14,
            normalQuantizationBits = 10,
            texCoordQuantizationBits = 12,
            colorQuantizationBits = 10,
            genericQuantizationBits = 12
        };

        /// <summary>
        /// The faster of the two speed settings, which limits the available features.
        /// Mirrors <c>EncoderOptionsBase::GetSpeed</c>.
        /// </summary>
        public int Speed => math.max(encodingSpeed, decodingSpeed);

        /// <summary>
        /// Quantization bits configured for an attribute type.
        /// </summary>
        /// <param name="type">Attribute type.</param>
        /// <returns>Number of quantization bits, or -1 when the type has no setting.</returns>
        public int GetQuantizationBits(AttributeType type)
        {
            switch (type)
            {
                case AttributeType.Position:
                    return positionQuantizationBits;
                case AttributeType.Normal:
                    return normalQuantizationBits;
                case AttributeType.TextureCoordinate:
                    return texCoordQuantizationBits;
                case AttributeType.Color:
                    return colorQuantizationBits;
                case AttributeType.Generic:
                    return genericQuantizationBits;
                default:
                    return -1;
            }
        }
    }

    /// <summary>
    /// Geometry handed to the encoder, holding the attribute values and, for meshes, the
    /// triangle connectivity.
    /// Mirrors <c>draco::PointCloud</c> and <c>draco::Mesh</c> on the encoding side.
    /// </summary>
    unsafe struct EncoderGeometry : IDisposable
    {
        /// <summary>True when the geometry has no connectivity.</summary>
        public bool isPointCloud;
        /// <summary>Number of points.</summary>
        public int numPoints;
        /// <summary>Triangle point indices, three per face.</summary>
        public UnsafeList<int3> faces;
        /// <summary>Attribute values.</summary>
        public UnsafeList<PointAttribute> attributes;

        /// <summary>Number of attributes.</summary>
        public int NumAttributes => attributes.IsCreated ? attributes.Length : 0;
        /// <summary>Number of faces.</summary>
        public int NumFaces => faces.IsCreated ? faces.Length : 0;

        /// <summary>
        /// Allocates the geometry.
        /// </summary>
        /// <param name="pointCloud">True to create a point cloud without connectivity.</param>
        /// <param name="pointCount">Number of points.</param>
        public void Init(bool pointCloud, int pointCount)
        {
            isPointCloud = pointCloud;
            numPoints = pointCount;
            faces = new UnsafeList<int3>(0, Allocator.Persistent);
            attributes = new UnsafeList<PointAttribute>(4, Allocator.Persistent);
        }

        /// <summary>
        /// Returns one of the attributes.
        /// </summary>
        /// <param name="index">Attribute index.</param>
        /// <returns>Pointer to the attribute.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PointAttribute* Attribute(int index)
        {
            return attributes.Ptr + index;
        }

        /// <summary>
        /// Appends an attribute with identity mapping between points and values.
        /// Mirrors <c>PointCloud::AddAttribute</c>.
        /// </summary>
        /// <param name="type">Semantic attribute type.</param>
        /// <param name="componentCount">Number of components per value.</param>
        /// <param name="componentType">Component data type.</param>
        /// <returns>Index of the new attribute.</returns>
        public int AddAttribute(AttributeType type, byte componentCount, DataType componentType)
        {
            var attribute = new PointAttribute();
            attribute.Init(type, componentCount, componentType, false, numPoints);
            attribute.uniqueId = (uint)attributes.Length;
            attributes.Add(attribute);
            return attributes.Length - 1;
        }

        /// <summary>
        /// Index of the first attribute of a given type.
        /// Mirrors <c>PointCloud::GetNamedAttributeId</c>.
        /// </summary>
        /// <param name="type">Attribute type.</param>
        /// <returns>Attribute index, or -1 when no such attribute exists.</returns>
        public int GetNamedAttributeId(AttributeType type)
        {
            for (var i = 0; i < NumAttributes; ++i)
            {
                if (Attribute(i)->attributeType == type)
                {
                    return i;
                }
            }
            return DracoConstants.invalidIndex;
        }

        /// <summary>
        /// Maps a corner onto the point index it refers to.
        /// Mirrors <c>Mesh::CornerToPointId</c>.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Point index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CornerToPointId(int corner)
        {
            return ((int*)faces.Ptr)[corner];
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (attributes.IsCreated)
            {
                for (var i = 0; i < attributes.Length; ++i)
                {
                    attributes.ElementAt(i).Dispose();
                }
                attributes.Dispose();
            }
            if (faces.IsCreated)
            {
                faces.Dispose();
            }
        }
    }

    /// <summary>
    /// Quantizes floating point values that are centered around zero.
    /// Mirrors <c>draco::Quantizer</c>.
    /// </summary>
    struct Quantizer
    {
        float m_InverseDelta;

        /// <summary>
        /// Initializes the quantizer.
        /// </summary>
        /// <param name="range">Range of the input values.</param>
        /// <param name="maxQuantizedValue">Largest quantized value.</param>
        public void Init(float range, int maxQuantizedValue)
        {
            m_InverseDelta = maxQuantizedValue / range;
        }

        /// <summary>
        /// Quantizes a single value, rounding half way cases up.
        /// </summary>
        /// <param name="value">Input value.</param>
        /// <returns>Quantized value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int QuantizeFloat(float value)
        {
            value *= m_InverseDelta;
            return (int)math.floor(value + 0.5f);
        }
    }

    /// <summary>
    /// Converts floating point attribute values into quantized integers.
    /// Mirrors <c>draco::AttributeQuantizationTransform</c> on the encoding side.
    /// </summary>
    unsafe struct AttributeQuantizationTransform : IDisposable
    {
        int m_QuantizationBits;
        UnsafeList<float> m_MinValues;
        float m_Range;

        /// <summary>True once the parameters were computed or set.</summary>
        public bool IsInitialized => m_QuantizationBits != -1 && m_QuantizationBits != 0;

        /// <summary>
        /// Resets the transform to the uninitialized state.
        /// </summary>
        public void Init()
        {
            m_QuantizationBits = -1;
            m_Range = 0f;
        }

        static bool IsQuantizationValid(int quantizationBits)
        {
            // Only up to 30 bit quantization is supported.
            return quantizationBits >= 1 && quantizationBits <= 30;
        }

        /// <summary>
        /// Derives the quantization parameters from the attribute values.
        /// Mirrors <c>AttributeQuantizationTransform::ComputeParameters</c>.
        /// </summary>
        /// <param name="attribute">Source attribute.</param>
        /// <param name="quantizationBits">Number of quantization bits.</param>
        /// <returns>False on invalid settings or non-finite values.</returns>
        public bool ComputeParameters(PointAttribute* attribute, int quantizationBits)
        {
            if (m_QuantizationBits != -1)
            {
                return false; // Already initialized.
            }
            if (!IsQuantizationValid(quantizationBits))
            {
                return false;
            }
            m_QuantizationBits = quantizationBits;

            var numComponents = (int)attribute->numComponents;
            m_Range = 0f;
            if (!m_MinValues.IsCreated)
            {
                m_MinValues = new UnsafeList<float>(numComponents, Allocator.Persistent);
            }
            m_MinValues.Resize(numComponents, NativeArrayOptions.ClearMemory);
            var minValues = m_MinValues.Ptr;
            var maxValues = stackalloc float[numComponents];
            var attributeValue = stackalloc float[numComponents];

            var first = (float*)attribute->GetAddress(0);
            for (var c = 0; c < numComponents; ++c)
            {
                minValues[c] = first[c];
                maxValues[c] = first[c];
            }

            for (var i = 1; i < attribute->numUniqueEntries; ++i)
            {
                var value = (float*)attribute->GetAddress(i);
                for (var c = 0; c < numComponents; ++c)
                {
                    attributeValue[c] = value[c];
                    if (float.IsNaN(attributeValue[c]))
                    {
                        return false;
                    }
                    if (minValues[c] > attributeValue[c])
                    {
                        minValues[c] = attributeValue[c];
                    }
                    if (maxValues[c] < attributeValue[c])
                    {
                        maxValues[c] = attributeValue[c];
                    }
                }
            }
            for (var c = 0; c < numComponents; ++c)
            {
                if (float.IsNaN(minValues[c]) || float.IsInfinity(minValues[c])
                    || float.IsNaN(maxValues[c]) || float.IsInfinity(maxValues[c]))
                {
                    return false;
                }
                var dif = maxValues[c] - minValues[c];
                if (dif > m_Range)
                {
                    m_Range = dif;
                }
            }

            // When all values are identical, a unit range keeps the quantization well defined.
            if (m_Range == 0f)
            {
                m_Range = 1f;
            }
            return true;
        }

        /// <summary>
        /// Creates the integer attribute that receives the quantized values.
        /// Mirrors <c>AttributeTransform::InitTransformedAttribute</c>.
        /// </summary>
        /// <param name="source">Source attribute.</param>
        /// <param name="numEntries">Number of values to allocate.</param>
        /// <returns>The transformed attribute.</returns>
        public static PointAttribute InitTransformedAttribute(
            PointAttribute* source, int numEntries)
        {
            var attribute = new PointAttribute();
            // Quantized values are unsigned, which the kd-tree encoder relies on.
            attribute.Init(
                source->attributeType, source->numComponents, DataType.UInt32, false, numEntries);
            attribute.uniqueId = source->uniqueId;
            return attribute;
        }

        /// <summary>
        /// Quantizes the attribute values in the order given by <paramref name="pointIds"/>.
        /// Mirrors <c>AttributeQuantizationTransform::GeneratePortableAttribute</c>.
        /// </summary>
        /// <param name="attribute">Source attribute.</param>
        /// <param name="pointIds">Encoding order of the points, or null for point order.</param>
        /// <param name="numPointIds">Number of entries in <paramref name="pointIds"/>.</param>
        /// <param name="target">Destination attribute.</param>
        public void TransformAttribute(
            PointAttribute* attribute, int* pointIds, int numPointIds, PointAttribute* target)
        {
            var numComponents = (int)attribute->numComponents;
            var portableData = (int*)target->GetAddress(0);
            var maxQuantizedValue = (1 << m_QuantizationBits) - 1;
            var quantizer = new Quantizer();
            quantizer.Init(m_Range, maxQuantizedValue);
            var minValues = m_MinValues.Ptr;
            var dstIndex = 0;
            var count = pointIds != null ? numPointIds : numPointIds;
            for (var i = 0; i < count; ++i)
            {
                var pointId = pointIds != null ? pointIds[i] : i;
                var valueIndex = attribute->MappedIndex(pointId);
                var value = (float*)attribute->GetAddress(valueIndex);
                for (var c = 0; c < numComponents; ++c)
                {
                    portableData[dstIndex++] = quantizer.QuantizeFloat(value[c] - minValues[c]);
                }
            }
        }

        /// <summary>
        /// Writes the parameters the decoder needs to reverse the quantization.
        /// Mirrors <c>AttributeQuantizationTransform::EncodeParameters</c>.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False when the transform is not initialized.</returns>
        public bool EncodeParameters(ref EncoderBuffer buffer)
        {
            if (!IsInitialized)
            {
                return false;
            }
            buffer.Encode(m_MinValues.Ptr, (long)sizeof(float) * m_MinValues.Length);
            buffer.Encode(m_Range);
            buffer.Encode((byte)m_QuantizationBits);
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_MinValues.IsCreated)
            {
                m_MinValues.Dispose();
            }
        }
    }

    /// <summary>
    /// Converts unit length normals into quantized octahedral coordinates.
    /// Mirrors <c>draco::AttributeOctahedronTransform</c> on the encoding side.
    /// </summary>
    unsafe struct AttributeOctahedronTransform
    {
        int m_QuantizationBits;

        /// <summary>True once the quantization precision was set.</summary>
        public bool IsInitialized => m_QuantizationBits != -1 && m_QuantizationBits != 0;

        /// <summary>
        /// Resets the transform to the uninitialized state.
        /// </summary>
        public void Init()
        {
            m_QuantizationBits = -1;
        }

        /// <summary>
        /// Sets the quantization precision.
        /// </summary>
        /// <param name="quantizationBits">Number of quantization bits.</param>
        public void SetParameters(int quantizationBits)
        {
            m_QuantizationBits = quantizationBits;
        }

        /// <summary>
        /// Creates the two component integer attribute that receives the octahedral coordinates.
        /// </summary>
        /// <param name="source">Source attribute.</param>
        /// <param name="numEntries">Number of values to allocate.</param>
        /// <returns>The transformed attribute.</returns>
        public static PointAttribute InitTransformedAttribute(
            PointAttribute* source, int numEntries)
        {
            var attribute = new PointAttribute();
            // Octahedral coordinates are unsigned, matching the reference transform.
            attribute.Init(source->attributeType, 2, DataType.UInt32, false, numEntries);
            attribute.uniqueId = source->uniqueId;
            return attribute;
        }

        /// <summary>
        /// Converts the normals in the order given by <paramref name="pointIds"/>.
        /// Mirrors <c>AttributeOctahedronTransform::GeneratePortableAttribute</c>.
        /// </summary>
        /// <param name="attribute">Source attribute.</param>
        /// <param name="pointIds">Encoding order of the points.</param>
        /// <param name="numPointIds">Number of entries in <paramref name="pointIds"/>.</param>
        /// <param name="target">Destination attribute.</param>
        /// <returns>False when the quantization precision is out of range.</returns>
        public bool TransformAttribute(
            PointAttribute* attribute, int* pointIds, int numPointIds, PointAttribute* target)
        {
            var portableData = (int*)target->GetAddress(0);
            var converter = OctahedronToolBox.CreateUninitialized();
            if (!converter.SetQuantizationBits(m_QuantizationBits))
            {
                return false;
            }
            var attributeValue = stackalloc float[3];
            var dstIndex = 0;
            for (var i = 0; i < numPointIds; ++i)
            {
                var pointId = pointIds != null ? pointIds[i] : i;
                var valueIndex = attribute->MappedIndex(pointId);
                var value = (float*)attribute->GetAddress(valueIndex);
                attributeValue[0] = value[0];
                attributeValue[1] = value[1];
                attributeValue[2] = value[2];
                converter.FloatVectorToQuantizedOctahedralCoords(
                    attributeValue, out var s, out var t);
                portableData[dstIndex++] = s;
                portableData[dstIndex++] = t;
            }
            return true;
        }

        /// <summary>
        /// Writes the parameters the decoder needs to reverse the transform.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False when the transform is not initialized.</returns>
        public bool EncodeParameters(ref EncoderBuffer buffer)
        {
            if (!IsInitialized)
            {
                return false;
            }
            buffer.Encode((byte)m_QuantizationBits);
            return true;
        }
    }
}
