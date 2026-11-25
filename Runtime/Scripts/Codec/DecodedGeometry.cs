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
        /// Appending reallocates the list and moves every attribute, so element addresses are not stable while
        /// attributes are still being added (each attributes decoder appends its own during
        /// <c>DecodeAttributesDecoderData</c>). Don't keep pointers or references into this list across that phase;
        /// store the <see cref="AttributeId"/> and resolve it with <see cref="GetAttribute"/> where needed.
        /// The decoder-owned portable attributes are deliberately *not* in this list.
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
        /// Checks whether an attribute index is in range.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <returns>True if <paramref name="attributeId"/> refers to an existing attribute.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAttribute(AttributeId attributeId)
        {
            return attributeId.IsValid && attributeId.value < m_Attributes.Length;
        }

        /// <summary>
        /// Returns a reference to an attribute by index. It's a reference rather than a pointer on purpose: it can't
        /// be stored in a field, so it can't outlive a reallocation of the attribute list. Keep it in a local only.
        /// </summary>
        /// <param name="attributeId">Attribute index. Must satisfy <see cref="HasAttribute"/>.</param>
        /// <returns>Reference to the attribute.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref PointAttribute GetAttribute(AttributeId attributeId)
        {
            return ref m_Attributes.ElementAt(attributeId.value);
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
                if (m_Attributes.ElementAt(i).attributeType != type)
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
        /// Returns the index of the attribute with the given unique identifier.
        /// Mirrors <c>PointCloud::GetAttributeIdByUniqueId</c>.
        /// </summary>
        /// <param name="uniqueId">Unique identifier.</param>
        /// <returns>Attribute index, or -1 when not found.</returns>
        public AttributeId GetAttributeIdByUniqueId(uint uniqueId)
        {
            for (var i = 0; i < m_Attributes.Length; ++i)
            {
                if (m_Attributes.ElementAt(i).uniqueId == uniqueId)
                {
                    return new AttributeId(i);
                }
            }
            return AttributeId.Invalid;
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
