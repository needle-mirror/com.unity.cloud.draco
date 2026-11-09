// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Storage for the values of a single geometry attribute plus the mapping from point
    /// indices to attribute value indices.
    /// Mirrors <c>draco::PointAttribute</c> merged with its <c>draco::GeometryAttribute</c> base.
    /// </summary>
    unsafe struct PointAttribute : IDisposable
    {
        /// <summary>Semantic type of the attribute.</summary>
        public AttributeType attributeType;
        /// <summary>Component data type.</summary>
        public DataType dataType;
        /// <summary>Number of components per value.</summary>
        public byte numComponents;
        /// <summary>Whether integer values represent a normalized 0-1 range.</summary>
        public bool normalized;
        /// <summary>Distance in bytes between two consecutive values.</summary>
        public int byteStride;
        /// <summary>Offset in bytes of the first value inside the buffer.</summary>
        public int byteOffset;
        /// <summary>Unique identifier of the attribute within the geometry.</summary>
        public uint uniqueId;

        /// <summary>Raw value storage.</summary>
        public UnsafeList<byte> buffer;

        /// <summary>True when point indices equal attribute value indices.</summary>
        public bool identityMapping;
        /// <summary>Explicit point index to attribute value index mapping.</summary>
        public UnsafeList<int> indicesMap;
        /// <summary>Number of unique attribute values.</summary>
        public int numUniqueEntries;

        /// <summary>
        /// Initializes the attribute description and allocates storage.
        /// Mirrors <c>PointAttribute::Init</c>.
        /// </summary>
        /// <param name="type">Semantic attribute type.</param>
        /// <param name="componentCount">Number of components per value.</param>
        /// <param name="componentType">Component data type.</param>
        /// <param name="isNormalized">Whether integers represent a normalized range.</param>
        /// <param name="numAttributeValues">Number of unique values to allocate.</param>
        public void Init(
            AttributeType type,
            byte componentCount,
            DataType componentType,
            bool isNormalized,
            int numAttributeValues
            )
        {
            attributeType = type;
            numComponents = componentCount;
            dataType = componentType;
            normalized = isNormalized;
            byteStride = DataTypeUtils.DataTypeLength(componentType) * componentCount;
            byteOffset = 0;
            identityMapping = true;
            Reset(numAttributeValues);
        }

        /// <summary>
        /// Resizes the value storage.
        /// Mirrors <c>PointAttribute::Reset</c>.
        /// </summary>
        /// <param name="numAttributeValues">Number of unique values.</param>
        /// <returns>False if the attribute description is invalid.</returns>
        public bool Reset(int numAttributeValues)
        {
            if (byteStride <= 0)
            {
                return false;
            }
            var size = byteStride * numAttributeValues;
            if (buffer.IsCreated)
            {
                buffer.Resize(size, NativeArrayOptions.ClearMemory);
            }
            else
            {
                buffer = new UnsafeList<byte>(size, Allocator.Persistent);
                buffer.Resize(size, NativeArrayOptions.ClearMemory);
            }
            numUniqueEntries = numAttributeValues;
            return true;
        }

        /// <summary>
        /// Switches to identity mapping between point and value indices.
        /// </summary>
        public void SetIdentityMapping()
        {
            identityMapping = true;
            if (indicesMap.IsCreated)
            {
                indicesMap.Clear();
            }
        }

        /// <summary>
        /// Switches to an explicit point to value index mapping.
        /// </summary>
        /// <param name="numPoints">Number of points in the geometry.</param>
        public void SetExplicitMapping(int numPoints)
        {
            identityMapping = false;
            if (!indicesMap.IsCreated)
            {
                indicesMap = new UnsafeList<int>(numPoints, Allocator.Persistent);
            }
            indicesMap.Resize(numPoints, NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < numPoints; i++)
            {
                indicesMap[i] = DracoConstants.invalidIndex;
            }
        }

        /// <summary>
        /// Assigns an attribute value index to a point.
        /// </summary>
        /// <param name="pointIndex">Point index.</param>
        /// <param name="entryIndex">Attribute value index.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPointMapEntry(int pointIndex, int entryIndex)
        {
            indicesMap[pointIndex] = entryIndex;
        }

        /// <summary>
        /// Maps a point index onto an attribute value index.
        /// </summary>
        /// <param name="pointIndex">Point index.</param>
        /// <returns>Attribute value index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MappedIndex(int pointIndex)
        {
            return identityMapping ? pointIndex : indicesMap[pointIndex];
        }

        /// <summary>
        /// Number of entries in the explicit index map, zero for identity mapping.
        /// </summary>
        public int IndicesMapSize => identityMapping ? 0 : indicesMap.Length;

        /// <summary>
        /// Returns the address of an attribute value.
        /// </summary>
        /// <param name="valueIndex">Attribute value index.</param>
        /// <returns>Pointer to the value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte* GetAddress(int valueIndex)
        {
            return buffer.Ptr + byteOffset + (long)byteStride * valueIndex;
        }

        /// <summary>
        /// Checks whether an address lies within the attribute's buffer.
        /// </summary>
        /// <param name="address">Address to test.</param>
        /// <returns>True if the address is inside the buffer.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAddressValid(byte* address)
        {
            return buffer.Ptr + buffer.Length > address;
        }

        /// <summary>
        /// Writes a whole attribute value.
        /// </summary>
        /// <param name="valueIndex">Attribute value index.</param>
        /// <param name="value">Source, at least <see cref="byteStride"/> bytes.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAttributeValue(int valueIndex, void* value)
        {
            UnsafeUtility.MemCpy(buffer.Ptr + (long)byteStride * valueIndex, value, byteStride);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (buffer.IsCreated)
            {
                buffer.Dispose();
            }
            if (indicesMap.IsCreated)
            {
                indicesMap.Dispose();
            }
        }
    }

    /// <summary>
    /// Helpers for Draco's <see cref="DataType"/> enumeration.
    /// Mirrors <c>draco/core/draco_types.cc</c>.
    /// </summary>
    static class DataTypeUtils
    {
        /// <summary>
        /// Size of a single value of the given data type, in bytes.
        /// </summary>
        /// <param name="dataType">Data type.</param>
        /// <returns>Size in bytes, or -1 for invalid types.</returns>
        public static int DataTypeLength(DataType dataType)
        {
            switch (dataType)
            {
                case DataType.Int8:
                case DataType.UInt8:
                case DataType.Bool:
                    return 1;
                case DataType.Int16:
                case DataType.UInt16:
                    return 2;
                case DataType.Int32:
                case DataType.UInt32:
                case DataType.Float32:
                    return 4;
                case DataType.Int64:
                case DataType.UInt64:
                case DataType.Float64:
                    return 8;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// True for signed and unsigned integer types including <see cref="DataType.Bool"/>.
        /// </summary>
        /// <param name="dataType">Data type.</param>
        /// <returns>True if the type is integral.</returns>
        public static bool IsDataTypeIntegral(DataType dataType)
        {
            switch (dataType)
            {
                case DataType.Int8:
                case DataType.UInt8:
                case DataType.Int16:
                case DataType.UInt16:
                case DataType.Int32:
                case DataType.UInt32:
                case DataType.Int64:
                case DataType.UInt64:
                case DataType.Bool:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Dequantizes values that were quantized by Draco's <c>Quantizer</c>.
    /// Mirrors <c>draco::Dequantizer</c>.
    /// </summary>
    struct Dequantizer
    {
        float m_Delta;

        /// <summary>
        /// Initializes the dequantizer from the quantization range.
        /// </summary>
        /// <param name="range">Range that was used during quantization.</param>
        /// <param name="maxQuantizedValue">Maximum quantized value.</param>
        /// <returns>False if <paramref name="maxQuantizedValue"/> is not positive.</returns>
        public bool Init(float range, int maxQuantizedValue)
        {
            if (maxQuantizedValue <= 0)
            {
                return false;
            }
            m_Delta = range / maxQuantizedValue;
            return true;
        }

        /// <summary>
        /// Dequantizes a single value.
        /// </summary>
        /// <param name="value">Quantized value.</param>
        /// <returns>Dequantized floating point value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float DequantizeFloat(int value)
        {
            return value * m_Delta;
        }
    }
}
