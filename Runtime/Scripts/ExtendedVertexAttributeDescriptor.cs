// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Draco
{
    class ExtendedVertexAttributeDescriptor
    {
        public VertexAttributeDescriptor attribute;
        public int offset;

        public ExtendedVertexAttributeDescriptor(VertexAttributeDescriptor attribute)
        {
            this.attribute = attribute;
        }

        public int elementSize => attribute.dimension * attribute.format.GetByteSize();

        public static int CompareByStreamAndOffset(ExtendedVertexAttributeDescriptor a, ExtendedVertexAttributeDescriptor b)
        {
            var result = a.attribute.stream.CompareTo(b.attribute.stream);
            if (result == 0)
                result = a.offset.CompareTo(b.offset);
            return result;
        }
    }
}
