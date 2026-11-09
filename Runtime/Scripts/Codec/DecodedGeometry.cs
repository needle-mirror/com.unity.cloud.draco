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
    /// Allocation helpers for unmanaged decoder state.
    /// </summary>
    static unsafe class Unmanaged
    {
        /// <summary>
        /// Allocates and zero initializes a single value in persistent unmanaged memory.
        /// </summary>
        /// <typeparam name="T">Value type to allocate.</typeparam>
        /// <returns>Pointer to the allocation.</returns>
        public static T* New<T>() where T : unmanaged
        {
            var ptr = (T*)UnsafeUtility.Malloc(sizeof(T), UnsafeUtility.AlignOf<T>(), Allocator.Persistent);
            UnsafeUtility.MemClear(ptr, sizeof(T));
            return ptr;
        }

        /// <summary>
        /// Frees memory allocated with <see cref="New{T}"/>.
        /// </summary>
        /// <typeparam name="T">Value type that was allocated.</typeparam>
        /// <param name="ptr">Pointer to free. May be null.</param>
        public static void Free<T>(T* ptr) where T : unmanaged
        {
            if (ptr != null)
            {
                UnsafeUtility.Free(ptr, Allocator.Persistent);
            }
        }
    }

    /// <summary>
    /// A decoded point cloud or triangular mesh.
    /// Mirrors <c>draco::PointCloud</c> and <c>draco::Mesh</c>.
    /// </summary>
    unsafe struct DecodedGeometry : IDisposable
    {
        /// <summary>Number of points in the geometry.</summary>
        public int numPoints;
        /// <summary>True when the geometry has no faces.</summary>
        public bool isPointCloud;

        /// <summary>
        /// The geometry's attributes, stored by value. An <see cref="AttributeId"/> is an index into this list.
        /// </summary>
        /// <remarks>
        /// Every attribute is appended by <c>DecodeAttributesDecoderData</c> before any decoder is initialized, and
        /// nothing appends afterwards, so addresses taken from here stay valid for the rest of the decode. The
        /// decoder-owned portable attributes are deliberately *not* in this list: they are created during decoding,
        /// so appending them here would reallocate and dangle the pointers the attribute decoders hold.
        /// </remarks>
        UnsafeList<PointAttribute> m_Attributes;

        /// <summary>Triangle indices, three point indices per face.</summary>
        UnsafeList<int3> m_Faces;

        /// <summary>
        /// Initializes empty storage.
        /// </summary>
        /// <param name="pointCloud">True to mark the geometry as a point cloud.</param>
        public void Init(bool pointCloud)
        {
            numPoints = 0;
            isPointCloud = pointCloud;
            m_Attributes = new UnsafeList<PointAttribute>(8, Allocator.Persistent);
            m_Faces = new UnsafeList<int3>(pointCloud ? 0 : 64, Allocator.Persistent);
        }

        /// <summary>Number of attributes.</summary>
        public int NumAttributes => m_Attributes.Length;

        /// <summary>Number of faces.</summary>
        public int NumFaces => m_Faces.Length;

        /// <summary>All faces of the geometry.</summary>
        public int3* Faces => m_Faces.Ptr;

        /// <summary>
        /// Appends a new attribute and takes ownership of it.
        /// </summary>
        /// <param name="attribute">Attribute to copy into the geometry's storage.</param>
        /// <returns>Index of the new attribute.</returns>
        public AttributeId AddAttribute(in PointAttribute attribute)
        {
            m_Attributes.Add(attribute);
            return new AttributeId(m_Attributes.Length - 1);
        }

        /// <summary>
        /// Returns an attribute by index.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>Pointer to the attribute, or null when out of range.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PointAttribute* GetAttribute(AttributeId attributeId)
        {
            if (!attributeId.IsValid || attributeId.value >= m_Attributes.Length)
            {
                return null;
            }
            return m_Attributes.Ptr + attributeId.value;
        }

        /// <summary>
        /// Returns the index of the n-th attribute of a given type.
        /// Mirrors <c>PointCloud::GetNamedAttributeId</c>.
        /// </summary>
        /// <param name="type">Attribute type.</param>
        /// <param name="index">Index among the attributes of that type.</param>
        /// <returns>Attribute index, or -1 when not found.</returns>
        public AttributeId GetNamedAttributeId(AttributeType type, int index = 0)
        {
            var count = 0;
            for (var i = 0; i < m_Attributes.Length; ++i)
            {
                var attribute = m_Attributes.Ptr + i;
                if (attribute->attributeType != type)
                {
                    continue;
                }
                if (count == index)
                {
                    return new AttributeId(i);
                }
                ++count;
            }
            return AttributeId.Invalid;
        }

        /// <summary>
        /// Returns the n-th attribute of a given type.
        /// Mirrors <c>PointCloud::GetNamedAttribute</c>.
        /// </summary>
        /// <param name="type">Attribute type.</param>
        /// <param name="index">Index among the attributes of that type.</param>
        /// <returns>Pointer to the attribute, or null when not found.</returns>
        public PointAttribute* GetNamedAttribute(AttributeType type, int index = 0)
        {
            var id = GetNamedAttributeId(type, index);
            return id.IsValid ? GetAttribute(id) : null;
        }

        /// <summary>
        /// Returns the attribute with the given unique identifier.
        /// Mirrors <c>PointCloud::GetAttributeByUniqueId</c>.
        /// </summary>
        /// <param name="uniqueId">Unique identifier.</param>
        /// <returns>Pointer to the attribute, or null when not found.</returns>
        public PointAttribute* GetAttributeByUniqueId(uint uniqueId)
        {
            for (var i = 0; i < m_Attributes.Length; ++i)
            {
                var attribute = m_Attributes.Ptr + i;
                if (attribute->uniqueId == uniqueId)
                {
                    return attribute;
                }
            }
            return null;
        }

        /// <summary>
        /// Appends a triangle.
        /// Mirrors <c>Mesh::AddFace</c>.
        /// </summary>
        /// <param name="face">Three point indices.</param>
        public void AddFace(int3 face)
        {
            m_Faces.Add(face);
        }

        /// <summary>
        /// Preallocates storage for faces.
        /// </summary>
        /// <param name="numFaces">Expected face count.</param>
        public void SetNumFaces(int numFaces)
        {
            m_Faces.Resize(numFaces, NativeArrayOptions.ClearMemory);
        }

        /// <summary>
        /// Writes a face.
        /// </summary>
        /// <param name="faceIndex">Face index.</param>
        /// <param name="face">The three point indices of the face.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetFace(int faceIndex, int3 face)
        {
            m_Faces[faceIndex] = face;
        }

        /// <summary>
        /// Returns the point index a mesh corner maps to.
        /// Mirrors <c>Mesh::CornerToPointId</c>.
        /// </summary>
        /// <param name="corner">Corner index.</param>
        /// <returns>Point index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CornerToPointId(int corner)
        {
            return m_Faces[corner / 3][corner % 3];
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Attributes.IsCreated)
            {
                for (var i = 0; i < m_Attributes.Length; ++i)
                {
                    m_Attributes.ElementAt(i).Dispose();
                }
                m_Attributes.Dispose();
            }
            if (m_Faces.IsCreated)
            {
                m_Faces.Dispose();
            }
        }
    }
}
