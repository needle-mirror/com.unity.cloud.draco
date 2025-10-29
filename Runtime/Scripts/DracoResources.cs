// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Draco
{
    unsafe struct DracoResources
    {
        public readonly NativeMesh* mesh;
        public readonly void* decoder;
        public readonly void* buffer;

        public DracoResources(NativeMesh* mesh, void* decoder, void* buffer)
        {
            this.mesh = mesh;
            this.decoder = decoder;
            this.buffer = buffer;
        }
    }
}
