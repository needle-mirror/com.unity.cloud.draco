// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Draco.Encode
{
    /// <summary>
    /// Optional flags that alter how a mesh is encoded.
    /// </summary>
    [Flags]
    public enum EncodeOptions
    {
        /// <summary>
        /// Default behavior. Vertex attributes are encoded with the dimension of the source mesh.
        /// </summary>
        None = 0,

        /// <summary>
        /// Encodes bone weights (<see cref="UnityEngine.Rendering.VertexAttribute.BlendWeight"/>) and
        /// bone indices (<see cref="UnityEngine.Rendering.VertexAttribute.BlendIndices"/>) with four components,
        /// padding missing components with zeros. Required for glTF, where JOINTS_0 and WEIGHTS_0 must be VEC4.
        /// </summary>
        PadSkinAttributesToFour = 1,
    }
}
