// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using UnityEngine.Rendering;

namespace Draco
{
    unsafe struct DracoAttributeInstance : IDisposable
    {
        public NativeAttribute* attribute { get; private set; }

        public readonly NativeAttribute Value => *attribute;

        public DracoAttributeInstance(NativeAttribute* attribute)
        {
            this.attribute = attribute;
        }

        public void Dispose()
        {
            if (attribute != null)
            {
                var tmp = attribute;
                DracoInstance.ReleaseDracoAttribute(&tmp);
                attribute = null;
            }
        }

        public VertexAttributeFormat? GetVertexAttributeFormat(bool normalized = false)
        {
            return DracoInstance.GetVertexAttributeFormat(Value.dataType, normalized);
        }
    }
}
