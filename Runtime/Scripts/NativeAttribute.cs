// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;

namespace Unity.Cloud.Draco
{
    [StructLayout(LayoutKind.Sequential)]
    struct NativeAttribute
    {
        // ReSharper disable MemberCanBePrivate.Local
        public AttributeType attributeType;
        public DataType dataType;
        public int numComponents;
        public int uniqueId;
        // ReSharper restore MemberCanBePrivate.Local
        /// <summary>
        /// Attribute index plus one, so that a default descriptor refers to no attribute.
        /// </summary>
        int m_AttributeIdPlusOne;

        /// <summary>
        /// Index of the decoded <see cref="Codec.PointAttribute"/> within the mesh's geometry.
        /// Invalid for a default descriptor.
        /// </summary>
        public Codec.AttributeId attributeId
        {
            readonly get => new Codec.AttributeId(m_AttributeIdPlusOne - 1);
            set => m_AttributeIdPlusOne = value.value + 1;
        }
    }
}
