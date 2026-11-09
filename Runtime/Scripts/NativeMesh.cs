// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;

namespace Unity.Cloud.Draco
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct NativeMesh
    {
        public int numFaces;
        public int numVertices;
        // ReSharper disable once MemberCanBePrivate.Local
        public int numAttributes;
        public bool isPointCloud;
        /// <summary>
        /// Pointer to the decoded <see cref="Codec.DecodedGeometry"/> that backs this mesh.
        /// </summary>
        public void* privateMesh;
    }
}
