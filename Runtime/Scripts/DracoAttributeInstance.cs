// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using UnityEngine.Rendering;

namespace Unity.Cloud.Draco
{
    /// <summary>
    /// A decoded attribute descriptor. Holds no ownership: the attribute values live in the
    /// decoded geometry, which is released with the mesh.
    /// </summary>
    unsafe struct DracoAttributeInstance
    {
        /// <summary>The attribute descriptor. Only meaningful when <see cref="IsValid"/> is true.</summary>
        public NativeAttribute Value { get; private set; }

        /// <summary>True when this instance refers to an actual attribute.</summary>
        public readonly bool IsValid => Value.attributeId.IsValid;

        public DracoAttributeInstance(in NativeAttribute attribute)
        {
            Value = attribute;
        }

        public VertexAttributeFormat? GetVertexAttributeFormat(bool normalized = false)
        {
            return DracoInstance.GetVertexAttributeFormat(Value.dataType, normalized);
        }
    }
}
