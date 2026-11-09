// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco
{
    using Codec;

    /// <summary>
    /// Managed replacement for the former native Draco Unity encoder plug-in. The entry
    /// points and their semantics mirror <c>draco/unity/draco_unity_enc_plugin.cc</c> so the
    /// surrounding job code can stay unchanged.
    /// </summary>
    static unsafe class DracoEncoderApi
    {
        /// <summary>
        /// Creates an encoder for a triangular mesh.
        /// </summary>
        /// <param name="vertexCount">Number of vertices.</param>
        /// <returns>Handle of the new encoder.</returns>
        public static IntPtr EncoderCreate(int vertexCount)
        {
            var encoder = Unmanaged.New<DracoEncoderState>();
            encoder->Init(false, vertexCount);
            return (IntPtr)encoder;
        }

        /// <summary>
        /// Creates an encoder for a point cloud.
        /// </summary>
        /// <param name="pointCount">Number of points.</param>
        /// <returns>Handle of the new encoder.</returns>
        public static IntPtr EncoderCreatePointCloud(int pointCount)
        {
            var encoder = Unmanaged.New<DracoEncoderState>();
            encoder->Init(true, pointCount);
            return (IntPtr)encoder;
        }

        /// <summary>
        /// Releases an encoder and all its resources.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        public static void EncoderRelease(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }
            var encoder = (DracoEncoderState*)handle;
            encoder->Dispose();
            Unmanaged.Free(encoder);
        }

        /// <summary>
        /// Sets the desired encoding and decoding speed.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="encodingSpeed">Encoding speed, zero is slowest and smallest.</param>
        /// <param name="decodingSpeed">Decoding speed, zero is slowest and smallest.</param>
        public static void SetCompressionSpeed(
            IntPtr handle, int encodingSpeed, int decodingSpeed)
        {
            var encoder = (DracoEncoderState*)handle;
            encoder->options.encodingSpeed = encodingSpeed;
            encoder->options.decodingSpeed = decodingSpeed;
        }

        /// <summary>
        /// Sets the quantization precision per attribute type.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="position">Position quantization bits.</param>
        /// <param name="normal">Normal quantization bits.</param>
        /// <param name="uv">Texture coordinate quantization bits.</param>
        /// <param name="color">Color quantization bits.</param>
        /// <param name="generic">Generic attribute quantization bits.</param>
        public static void SetQuantizationBits(
            IntPtr handle, int position, int normal, int uv, int color, int generic)
        {
            var encoder = (DracoEncoderState*)handle;
            encoder->options.positionQuantizationBits = position;
            encoder->options.normalQuantizationBits = normal;
            encoder->options.texCoordQuantizationBits = uv;
            encoder->options.colorQuantizationBits = color;
            encoder->options.genericQuantizationBits = generic;
        }

        /// <summary>
        /// Encodes the geometry that was set on the encoder.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="sequentialEncode">Unused. The managed encoder always uses the
        /// edgebreaker method for meshes and the kd-tree method for point clouds.</param>
        /// <returns>False when the geometry cannot be encoded.</returns>
        public static bool Encode(IntPtr handle, bool sequentialEncode)
        {
            var encoder = (DracoEncoderState*)handle;
            return encoder->Encode();
        }

        /// <summary>
        /// Number of vertices the decoder is going to produce.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <returns>Number of vertices.</returns>
        public static uint GetEncodedVertexCount(IntPtr handle)
        {
            var encoder = (DracoEncoderState*)handle;
            return (uint)encoder->numEncodedPoints;
        }

        /// <summary>
        /// Number of triangle indices the decoder is going to produce.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <returns>Number of indices.</returns>
        public static uint GetEncodedIndexCount(IntPtr handle)
        {
            var encoder = (DracoEncoderState*)handle;
            return (uint)(encoder->numEncodedFaces * 3);
        }

        /// <summary>
        /// Provides access to the encoded data.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="data">Receives the address of the encoded data.</param>
        /// <param name="size">Receives the length of the encoded data in bytes.</param>
        public static void GetEncodeBuffer(IntPtr handle, out void* data, out ulong size)
        {
            var encoder = (DracoEncoderState*)handle;
            data = encoder->buffer.Data;
            size = (ulong)encoder->buffer.Length;
        }

        /// <summary>
        /// Sets the triangle indices of a mesh, reversing the winding order to convert from
        /// Unity's left handed to Draco's right handed coordinate system.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="indexCount">Number of indices.</param>
        /// <param name="indices">32 bit index data.</param>
        public static void SetIndices(IntPtr handle, uint indexCount, uint* indices)
        {
            var encoder = (DracoEncoderState*)handle;
            var faceCount = (int)(indexCount / 3);
            ref var geometry = ref encoder->geometry;
            geometry.faces.Resize(faceCount, NativeArrayOptions.UninitializedMemory);
            var faces = geometry.faces.Ptr;
            for (var i = 0; i < faceCount; ++i)
            {
                faces[i] = new int3(
                    (int)indices[3 * i],
                    (int)indices[3 * i + 2],
                    (int)indices[3 * i + 1]);
            }
        }

        /// <summary>
        /// Adds an attribute and copies its values into the encoder.
        /// </summary>
        /// <param name="handle">Encoder handle.</param>
        /// <param name="attributeType">Semantic attribute type.</param>
        /// <param name="dracoDataType">Component data type.</param>
        /// <param name="componentCount">Number of components per value.</param>
        /// <param name="stride">Distance in bytes between two source values.</param>
        /// <param name="flip">If true, a left to right handed coordinate conversion is applied.</param>
        /// <param name="data">Source values.</param>
        /// <returns>Index of the new attribute.</returns>
        public static uint SetAttribute(
            IntPtr handle,
            int attributeType,
            DataType dracoDataType,
            int componentCount,
            int stride,
            bool flip,
            IntPtr data
            )
        {
            var encoder = (DracoEncoderState*)handle;
            ref var geometry = ref encoder->geometry;
            var count = geometry.numPoints;
            var id = geometry.AddAttribute(
                (AttributeType)attributeType, (byte)componentCount, dracoDataType);
            var attribute = geometry.Attribute(id);
            var source = (byte*)data;

            for (var i = 0; i < count; ++i)
            {
                attribute->SetAttributeValue(i, source + (long)i * stride);
            }
            if (flip)
            {
                SpaceConversion.Convert(
                    attribute->buffer.Ptr, count, attribute->byteStride, componentCount, dracoDataType);
            }
            return (uint)id;
        }
    }
}
