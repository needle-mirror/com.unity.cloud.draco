// SPDX-FileCopyrightText: 2023 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine;

namespace Draco
{
    /// <summary>
    /// Decode setting.
    /// </summary>
    [Flags]
    public enum DecodeSettings
    {
        /// <summary>
        /// No setting active.
        /// </summary>
        None = 0,

        /// <summary>
        /// If this is set to true (or <see cref="RequireTangents"/> is true), a normals vertex attribute is added
        /// regardless whether the Draco data contains actual normals. If they are missing, normals get calculated
        /// whenever a <see cref="Mesh"/> is returned. When <see cref="Mesh.MeshData"/> is used, normals have to be
        /// calculated manually afterward.
        /// </summary>
        /// <seealso cref="DecodeResult.calculateNormals"/>
        RequireNormals = 1,

        /// <summary>
        /// If this is set to true, normals and tangents vertex attributes are added regardless whether the Draco data
        /// contains actual normals. If they are missing, normals and tangents get calculated whenever
        /// a <see cref="Mesh"/> is returned. When <see cref="Mesh.MeshData"/> is used, normals and tangents have to be
        /// calculated manually afterward.
        /// </summary>
        RequireTangents = 1 << 1,

        /// <summary>
        /// Enforces vertex buffer layout with the highest compatibility. Enable this if you want to use blend shapes on the
        /// resulting mesh.
        /// </summary>
        ForceUnityVertexLayout = 1 << 2,

        /// <summary>
        /// If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity) by inverting the
        /// x-axis.
        /// </summary>
        ConvertSpace = 1 << 3,

        /// <summary>
        /// Do not calculate an axis aligned bounding box, depicting the positional minimum and maximum extents of the mesh.
        /// </summary>
        DontCalculateBounds = 1 << 4,

        /// <summary>
        /// Require both tangents and normals. Useful since tangents imply requirement for normals.
        /// </summary>
        RequireNormalsAndTangents = RequireNormals | RequireTangents,

        /// <summary>
        /// Default decode flags. Only space conversion is enabled.
        /// </summary>
        Default = ConvertSpace
    }
}
