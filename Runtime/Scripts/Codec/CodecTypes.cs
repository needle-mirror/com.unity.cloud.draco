// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Geometry type of an encoded Draco stream.
    /// Mirrors <c>draco::EncodedGeometryType</c>.
    /// </summary>
    enum EncodedGeometryType
    {
        Invalid = -1,
        PointCloud = 0,
        TriangularMesh = 1,
        Count
    }

    /// <summary>
    /// Mirrors <c>draco::PointCloudEncodingMethod</c>.
    /// </summary>
    enum PointCloudEncodingMethod
    {
        Sequential = 0,
        KdTree = 1
    }

    /// <summary>
    /// Mirrors <c>draco::MeshEncoderMethod</c>.
    /// </summary>
    enum MeshEncoderMethod
    {
        Sequential = 0,
        Edgebreaker = 1
    }

    /// <summary>
    /// Mirrors <c>draco::AttributeEncoderType</c>.
    /// </summary>
    enum AttributeEncoderType
    {
        Basic = 0,
        MeshTraversal = 1,
        KdTree = 2
    }

    /// <summary>
    /// Mirrors <c>draco::SequentialAttributeEncoderType</c>.
    /// </summary>
    enum SequentialAttributeEncoderType
    {
        Generic = 0,
        Integer = 1,
        Quantization = 2,
        Normals = 3
    }

    /// <summary>
    /// Mirrors <c>draco::PredictionSchemeMethod</c>.
    /// </summary>
    enum PredictionSchemeMethod
    {
        None = -2,
        Undefined = -1,
        Difference = 0,
        MeshParallelogram = 1,
        MeshMultiParallelogram = 2,
        MeshTexCoordsDeprecated = 3,
        MeshConstrainedMultiParallelogram = 4,
        MeshTexCoordsPortable = 5,
        MeshGeometricNormal = 6,
        Count
    }

    /// <summary>
    /// Mirrors <c>draco::PredictionSchemeTransformType</c>.
    /// </summary>
    enum PredictionSchemeTransformType
    {
        None = -1,
        Delta = 0,
        Wrap = 1,
        NormalOctahedron = 2,
        NormalOctahedronCanonicalized = 3,
        Count
    }

    /// <summary>
    /// Mirrors <c>draco::MeshTraversalMethod</c>.
    /// </summary>
    enum MeshTraversalMethod
    {
        DepthFirst = 0,
        PredictionDegree = 1,
        Count
    }

    /// <summary>
    /// Mirrors <c>draco::MeshEdgebreakerConnectivityEncodingMethod</c>.
    /// </summary>
    enum EdgebreakerConnectivityMethod
    {
        Standard = 0,
        Predictive = 1,
        Valence = 2
    }

    /// <summary>
    /// Mirrors <c>draco::SymbolCodingMethod</c>.
    /// </summary>
    enum SymbolCodingMethod
    {
        Tagged = 0,
        Raw = 1,
        Count
    }

    /// <summary>
    /// Draco stream header (V1).
    /// Mirrors <c>draco::DracoHeader</c>.
    /// </summary>
    struct DracoHeader
    {
        public byte versionMajor;
        public byte versionMinor;
        public byte encoderType;
        public byte encoderMethod;
        public ushort flags;

        public ushort BitstreamVersion => DracoConstants.BitstreamVersion(versionMajor, versionMinor);
    }

    /// <summary>
    /// Shared constants of the Draco bit-stream.
    /// </summary>
    static class DracoConstants
    {
        public const byte pointCloudBitstreamVersionMajor = 2;
        public const byte pointCloudBitstreamVersionMinor = 3;
        public const byte meshBitstreamVersionMajor = 2;
        public const byte meshBitstreamVersionMinor = 2;

        public const ushort metadataFlagMask = 0x8000;

        /// <summary>
        /// Combines major and minor version into the packed representation Draco uses.
        /// Mirrors the <c>DRACO_BITSTREAM_VERSION</c> macro.
        /// </summary>
        /// <param name="major">Major version.</param>
        /// <param name="minor">Minor version.</param>
        /// <returns>Packed bit-stream version.</returns>
        public static ushort BitstreamVersion(byte major, byte minor)
        {
            return (ushort)((major << 8) | minor);
        }

        /// <summary>
        /// Value used to signal an invalid/unset index.
        /// </summary>
        public const int invalidIndex = -1;

        /// <summary>
        /// Corner table's invalid corner/vertex/face index.
        /// </summary>
        public const int invalidCornerIndex = -1;
    }
}
