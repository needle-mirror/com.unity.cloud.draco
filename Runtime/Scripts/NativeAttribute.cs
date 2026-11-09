// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.InteropServices;

namespace Unity.Cloud.Draco
{
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct NativeAttribute
    {
        // ReSharper disable MemberCanBePrivate.Local
        public AttributeType attributeType;
        public DataType dataType;
        public int numComponents;
        public int uniqueId;
        // ReSharper restore MemberCanBePrivate.Local
        /// <summary>
        /// Pointer to the decoded <see cref="Codec.PointAttribute"/> that backs this attribute.
        /// </summary>
        public void* privateAttribute;
    }
}
