// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Unity.Cloud.Draco
{
    using Codec;

    /// <summary>
    /// The decoded mesh. Created by <see cref="DracoNativeApi.DecodeDracoMeshStep1"/> and released by
    /// <see cref="DracoNativeApi.ReleaseDracoMesh"/>, so it outlives the whole decode.
    /// </summary>
    unsafe struct DracoResources
    {
        /// <summary>Decoded mesh description. Null once released.</summary>
        public NativeMesh* mesh;

        public DracoResources(NativeMesh* mesh)
        {
            this.mesh = mesh;
        }
    }

    /// <summary>
    /// The decoder state, which is only alive between the two decode steps.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="DracoResources"/>. It used to be a second field there, and because the
    /// handle is shared read-only with every job that runs after step two, that field could not be nulled out when
    /// step two frees the state: a job that writes the handle would force a <c>JobHandle.Complete()</c> before the
    /// main thread may read the decoded mesh. It therefore stayed behind as a dangling pointer for the rest of the
    /// decode, guarded only by a comment. Keeping it in its own short-lived container means the jobs that run after
    /// step two never see it at all.
    /// </remarks>
    unsafe struct DracoDecoderHandle
    {
        /// <summary>Decoder state. Freed by <see cref="DracoNativeApi.DecodeDracoMeshStep2"/>.</summary>
        public DracoDecoderState* decoder;

        public DracoDecoderHandle(DracoDecoderState* decoder)
        {
            this.decoder = decoder;
        }
    }
}
