// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine.Rendering;

namespace Draco
{
    sealed class StubAttribute : AttributeBase
    {
        public override int numComponents { get; }

        public StubAttribute(
            VertexAttribute attribute,
            VertexAttributeFormat format,
            int numComponents
            ) : base(attribute, format)
        {
            this.numComponents = numComponents;
        }

        protected override void Dispose(bool disposing) { }
    }
}
