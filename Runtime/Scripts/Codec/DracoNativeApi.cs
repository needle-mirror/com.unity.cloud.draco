// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco
{
    using Codec;

    /// <summary>
    /// Managed replacement for the former native Draco Unity plug-in. The entry points and
    /// their semantics mirror <c>draco/unity/draco_unity_plugin.cc</c> so the surrounding
    /// job code can stay unchanged.
    /// </summary>
    static unsafe class DracoNativeApi
    {
        /// <summary>
        /// Releases the geometry backing a decoded mesh. Safe to call more than once.
        /// </summary>
        /// <param name="resources">Decode resources. Cleared on return.</param>
        public static void ReleaseDracoMesh(ref DracoResources resources)
        {
            var mesh = resources.mesh;
            if (mesh == null)
            {
                return;
            }
            var geometry = (DecodedGeometry*)mesh->privateMesh;
            if (geometry != null)
            {
                geometry->Dispose();
                Unmanaged.Free(geometry);
            }
            Unmanaged.Free(mesh);
            resources.mesh = null;
        }

        /// <summary>
        /// Releases attribute data produced by <see cref="GetAttributeData"/>.
        /// Safe to call more than once.
        /// </summary>
        /// <param name="data">Attribute data. Cleared on return.</param>
        public static void ReleaseDracoData(ref NativeData data)
        {
            if (data.data != IntPtr.Zero)
            {
                UnsafeUtility.Free((void*)data.data, Allocator.Persistent);
            }
            data = default;
        }

        /// <summary>
        /// Decodes the header and the connectivity of a Draco stream. Point clouds are
        /// decoded completely.
        /// </summary>
        /// <param name="encodedData">Compressed Draco data. Must stay valid until step two completed.</param>
        /// <param name="resources">Receives the decoded mesh.</param>
        /// <param name="decoderHandle">Receives the decoder state, to be passed to
        /// <see cref="DecodeDracoMeshStep2"/>.</param>
        /// <returns>Zero on success, a negative Draco error code otherwise.</returns>
        public static int DecodeDracoMeshStep1(
            NativeArray<byte>.ReadOnly encodedData,
            out DracoResources resources,
            out DracoDecoderHandle decoderHandle
            )
        {
            resources = default;
            decoderHandle = default;
            var data = (byte*)encodedData.GetUnsafeReadOnlyPtr();
            var length = encodedData.Length;

            if (!DracoDecoderState.GetEncodedGeometryType(data, length, out var geometryType))
            {
                return -2;
            }

            var geometry = Unmanaged.New<DecodedGeometry>();
            geometry->Init(geometryType == EncodedGeometryType.PointCloud);

            var state = Unmanaged.New<DracoDecoderState>();
            state->geometryType = geometryType;

            if (!state->DecodeStep1(data, length, geometry))
            {
                state->Dispose();
                Unmanaged.Free(state);
                geometry->Dispose();
                Unmanaged.Free(geometry);
                return geometryType == EncodedGeometryType.TriangularMesh ? -4 : -5;
            }

            if (geometryType == EncodedGeometryType.PointCloud)
            {
                // Point clouds are decoded in a single step, matching the reference plug-in.
                if (!state->DecodeStep2())
                {
                    state->Dispose();
                    Unmanaged.Free(state);
                    geometry->Dispose();
                    Unmanaged.Free(geometry);
                    return -5;
                }
            }

            var mesh = Unmanaged.New<NativeMesh>();
            mesh->numFaces = geometryType == EncodedGeometryType.PointCloud ? 0 : geometry->NumFaces;
            mesh->numVertices = geometry->numPoints;
            mesh->numAttributes = geometry->NumAttributes;
            mesh->isPointCloud = geometryType == EncodedGeometryType.PointCloud;
            mesh->privateMesh = geometry;

            resources = new DracoResources(mesh);
            decoderHandle = new DracoDecoderHandle(state);
            return 0;
        }

        /// <summary>
        /// Decodes the attribute values of a mesh and releases the decoder state.
        /// </summary>
        /// <param name="resources">Mesh produced by <see cref="DecodeDracoMeshStep1"/>. Stays alive until
        /// <see cref="ReleaseDracoMesh"/>.</param>
        /// <param name="decoderHandle">Decoder state produced by <see cref="DecodeDracoMeshStep1"/>. Freed here,
        /// so the caller's copy must not be used afterwards.</param>
        /// <returns>Zero on success, a negative Draco error code otherwise.</returns>
        public static int DecodeDracoMeshStep2(DracoResources resources, DracoDecoderHandle decoderHandle)
        {
            var mesh = resources.mesh;
            var state = decoderHandle.decoder;
            if (mesh == null || state == null)
            {
                return -1;
            }

            var result = 0;
            if (!mesh->isPointCloud)
            {
                if (!state->DecodeStep2())
                {
                    result = -4;
                }
                else
                {
                    // Attribute decoding can add points, keep the mesh description in sync.
                    var geometry = (DecodedGeometry*)mesh->privateMesh;
                    mesh->numVertices = geometry->numPoints;
                    mesh->numAttributes = geometry->NumAttributes;
                }
            }

            state->Dispose();
            Unmanaged.Free(state);
            return result;
        }

        /// <summary>
        /// Looks up an attribute by its semantic type.
        /// </summary>
        /// <param name="mesh">Decoded mesh.</param>
        /// <param name="type">Attribute type.</param>
        /// <param name="index">Index among the attributes of that type.</param>
        /// <param name="descriptor">Receives the attribute descriptor.</param>
        /// <returns>True when the attribute exists.</returns>
        public static bool GetAttributeByType(
            NativeMesh* mesh, AttributeType type, int index, out NativeAttribute descriptor)
        {
            descriptor = default;
            if (mesh == null)
            {
                return false;
            }
            var geometry = (DecodedGeometry*)mesh->privateMesh;
            var attributeId = geometry->GetNamedAttributeId(type, index);
            if (!geometry->HasAttribute(attributeId))
            {
                return false;
            }
            descriptor = CreateAttributeDescriptor(geometry, attributeId);
            return true;
        }

        /// <summary>
        /// Looks up an attribute by its unique identifier.
        /// </summary>
        /// <param name="mesh">Decoded mesh.</param>
        /// <param name="uniqueId">Unique identifier.</param>
        /// <param name="descriptor">Receives the attribute descriptor.</param>
        /// <returns>True when the attribute exists.</returns>
        public static bool GetAttributeByUniqueId(
            NativeMesh* mesh, int uniqueId, out NativeAttribute descriptor)
        {
            descriptor = default;
            if (mesh == null)
            {
                return false;
            }
            var geometry = (DecodedGeometry*)mesh->privateMesh;
            var attributeId = geometry->GetAttributeIdByUniqueId((uint)uniqueId);
            if (!geometry->HasAttribute(attributeId))
            {
                return false;
            }
            descriptor = CreateAttributeDescriptor(geometry, attributeId);
            return true;
        }

        static NativeAttribute CreateAttributeDescriptor(DecodedGeometry* geometry, AttributeId attributeId)
        {
            ref var attribute = ref geometry->GetAttribute(attributeId);
            return new NativeAttribute
            {
                attributeType = attribute.attributeType,
                dataType = attribute.dataType,
                numComponents = attribute.numComponents,
                uniqueId = (int)attribute.uniqueId,
                attributeId = attributeId
            };
        }

        /// <summary>
        /// Writes the triangle indices into the provided buffer.
        /// </summary>
        /// <param name="mesh">Decoded mesh.</param>
        /// <param name="dataType">Index type, either <see cref="DataType.UInt16"/> or <see cref="DataType.UInt32"/>.</param>
        /// <param name="indices">Destination buffer.</param>
        /// <param name="indicesCount">Capacity of the destination buffer in indices.</param>
        /// <param name="flip">If true, the winding order is reversed.</param>
        /// <returns>True on success.</returns>
        public static bool GetMeshIndices(
            NativeMesh* mesh,
            DataType dataType,
            void* indices,
            int indicesCount,
            bool flip
            )
        {
            if (mesh == null || indices == null || mesh->isPointCloud)
            {
                return false;
            }
            var geometry = (DecodedGeometry*)mesh->privateMesh;
            var numFaces = geometry->NumFaces;
            if (indicesCount < numFaces * 3)
            {
                return false;
            }
            var faces = geometry->Faces;

            switch (dataType)
            {
                case DataType.UInt16:
                {
                    var destination = (ushort*)indices;
                    if (flip)
                    {
                        for (var i = 0; i < numFaces; ++i)
                        {
                            var face = faces[i];
                            destination[i * 3] = (ushort)face.x;
                            destination[i * 3 + 1] = (ushort)face.z;
                            destination[i * 3 + 2] = (ushort)face.y;
                        }
                    }
                    else
                    {
                        for (var i = 0; i < numFaces; ++i)
                        {
                            var face = faces[i];
                            destination[i * 3] = (ushort)face.x;
                            destination[i * 3 + 1] = (ushort)face.y;
                            destination[i * 3 + 2] = (ushort)face.z;
                        }
                    }
                    return true;
                }
                case DataType.UInt32:
                {
                    var destination = (uint*)indices;
                    if (flip)
                    {
                        for (var i = 0; i < numFaces; ++i)
                        {
                            var face = faces[i];
                            destination[i * 3] = (uint)face.x;
                            destination[i * 3 + 1] = (uint)face.z;
                            destination[i * 3 + 2] = (uint)face.y;
                        }
                    }
                    else
                    {
                        UnsafeUtility.MemCpy(destination, faces, (long)numFaces * 3 * sizeof(uint));
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// Copies the values of an attribute, one entry per point, into a freshly allocated buffer.
        /// </summary>
        /// <param name="mesh">Decoded mesh.</param>
        /// <param name="attributeDescriptor">Attribute descriptor.</param>
        /// <param name="data">Receives the allocated data. Release with <see cref="ReleaseDracoData"/>.</param>
        /// <param name="flip">If true, a right to left handed coordinate conversion is applied.</param>
        /// <param name="componentStride">Distance between two entries, in components.</param>
        /// <returns>True on success.</returns>
        public static bool GetAttributeData(
            NativeMesh* mesh,
            in NativeAttribute attributeDescriptor,
            out NativeData data,
            bool flip,
            int componentStride
            )
        {
            data = default;
            if (mesh == null)
            {
                return false;
            }
            var geometry = (DecodedGeometry*)mesh->privateMesh;
            if (!geometry->HasAttribute(attributeDescriptor.attributeId))
            {
                return false;
            }
            ref var attribute = ref geometry->GetAttribute(attributeDescriptor.attributeId);

            var componentSize = DataTypeUtils.DataTypeLength(attribute.dataType);
            if (componentSize <= 0)
            {
                return false;
            }
            var numComponents = attribute.numComponents;
            if (numComponents < 1 || numComponents > 4)
            {
                return false;
            }

            var numPoints = geometry->numPoints;
            var totalSize = (long)numPoints * componentStride * componentSize;
            var destination = (byte*)UnsafeUtility.Malloc(
                totalSize <= 0 ? 1 : totalSize, componentSize, Allocator.Persistent);
            UnsafeUtility.MemClear(destination, totalSize <= 0 ? 1 : totalSize);

            var entrySize = numComponents * componentSize;
            var strideBytes = componentStride * componentSize;
            var success = true;
            for (var i = 0; i < numPoints; ++i)
            {
                var valueIndex = attribute.MappedIndex(i);
                var source = attribute.GetAddress(valueIndex);
                // The reference implementation validates every component address.
                if (!attribute.IsAddressValid(source + entrySize - componentSize))
                {
                    success = false;
                    break;
                }
                UnsafeUtility.MemCpy(destination + (long)i * strideBytes, source, entrySize);
            }

            if (success && flip)
            {
                SpaceConversion.Convert(
                    destination, numPoints, strideBytes, numComponents, attribute.dataType);
            }

            if (!success)
            {
                UnsafeUtility.Free(destination, Allocator.Persistent);
                return false;
            }

            data = new NativeData
            {
                dataType = attribute.dataType,
                data = (IntPtr)destination
            };
            return true;
        }
    }
}
