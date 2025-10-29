// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine.Rendering;

namespace Draco
{
    abstract class AttributeBase : IDisposable
    {
        public readonly VertexAttribute attribute;
        public VertexAttributeFormat format;

        public ExtendedVertexAttributeDescriptor attributeDescriptor { get; set; }

        protected AttributeBase(VertexAttribute attribute, VertexAttributeFormat format)
        {
            this.attribute = attribute;
            this.format = format;
        }

        public abstract int numComponents { get; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);

        public VertexAttributeDescriptor GetVertexAttributeDescriptor()
        {
            return new VertexAttributeDescriptor(attribute, format, numComponents);
        }
    }
}
