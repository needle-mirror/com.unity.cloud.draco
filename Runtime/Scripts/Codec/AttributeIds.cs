// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Identifies an attribute by its position in the geometry's attribute list.
    /// </summary>
    /// <remarks>
    /// Draco has three distinct index spaces around attributes, and all three used to be a bare <c>int</c>:
    /// this one, <see cref="LocalAttributeId"/> and <see cref="AttributesDecoderId"/>. They are converted into
    /// each other through explicit maps, so mixing them up silently reads the wrong attribute. The wrappers
    /// exist to make the compiler reject that; they are single field readonly structs and cost nothing.
    /// </remarks>
    readonly struct AttributeId : IEquatable<AttributeId>
    {
        /// <summary>The attribute index within the geometry, or -1.</summary>
        public readonly int value;

        /// <summary>An id that refers to no attribute.</summary>
        public static AttributeId Invalid => new AttributeId(DracoConstants.invalidIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AttributeId(int value)
        {
            this.value = value;
        }

        /// <summary>True when this refers to an attribute.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value >= 0;
        }

        public bool Equals(AttributeId other) => value == other.value;
        public override bool Equals(object obj) => obj is AttributeId other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(AttributeId a, AttributeId b) => a.value == b.value;
        public static bool operator !=(AttributeId a, AttributeId b) => a.value != b.value;
        public override string ToString() => value.ToString();
    }

    /// <summary>
    /// Identifies an attribute by its position within the one
    /// <see cref="AttributesDecoder"/> or <see cref="AttributesEncoder"/> that handles it.
    /// </summary>
    /// <remarks>
    /// Only meaningful together with the decoder or encoder it belongs to. Map it to an
    /// <see cref="AttributeId"/> with <c>GetAttributeId</c> before touching the geometry.
    /// </remarks>
    readonly struct LocalAttributeId : IEquatable<LocalAttributeId>
    {
        /// <summary>The index within the handling decoder or encoder, or -1.</summary>
        public readonly int value;

        /// <summary>An id that refers to no attribute.</summary>
        public static LocalAttributeId Invalid => new LocalAttributeId(DracoConstants.invalidIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LocalAttributeId(int value)
        {
            this.value = value;
        }

        /// <summary>True when this refers to an attribute.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value >= 0;
        }

        public bool Equals(LocalAttributeId other) => value == other.value;
        public override bool Equals(object obj) => obj is LocalAttributeId other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(LocalAttributeId a, LocalAttributeId b) => a.value == b.value;
        public static bool operator !=(LocalAttributeId a, LocalAttributeId b) => a.value != b.value;
        public override string ToString() => value.ToString();
    }

    /// <summary>
    /// Identifies one of the attributes decoders or encoders of a Draco stream.
    /// </summary>
    readonly struct AttributesDecoderId : IEquatable<AttributesDecoderId>
    {
        /// <summary>The index within the decoder or encoder list, or -1.</summary>
        public readonly int value;

        /// <summary>An id that refers to no attributes decoder.</summary>
        public static AttributesDecoderId Invalid => new AttributesDecoderId(DracoConstants.invalidIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AttributesDecoderId(int value)
        {
            this.value = value;
        }

        /// <summary>True when this refers to an attributes decoder.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => value >= 0;
        }

        public bool Equals(AttributesDecoderId other) => value == other.value;
        public override bool Equals(object obj) => obj is AttributesDecoderId other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(AttributesDecoderId a, AttributesDecoderId b) => a.value == b.value;
        public static bool operator !=(AttributesDecoderId a, AttributesDecoderId b) => a.value != b.value;
        public override string ToString() => value.ToString();
    }
}
