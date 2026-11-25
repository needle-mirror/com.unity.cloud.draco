// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Decodes the values of a single attribute that was encoded with one of Draco's
    /// sequential attribute encoders.
    /// Combines <c>draco::SequentialAttributeDecoder</c> and its three subclasses.
    /// </summary>
    unsafe struct SequentialAttributeDecoder : IDisposable
    {
        /// <summary>Which encoder produced the data.</summary>
        public SequentialAttributeEncoderType encoderType;
        /// <summary>Index of the decoded attribute within the geometry.</summary>
        public AttributeId attributeId;

        [NativeDisableUnsafePtrRestriction]
        DracoDecoderState* m_Decoder;
        /// <summary>
        /// The decoded attribute. Resolved on each use and held only as a local <c>ref</c>, because the
        /// geometry's attribute list may reallocate while later attributes decoders are still appending to it.
        /// </summary>
        ref PointAttribute Attribute
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref m_Decoder->geometry->GetAttribute(attributeId);
        }
        // Owned by value. Each decoder has at most one portable attribute, and the decoder list is sized once to
        // the exact attribute count before any address into it is taken, so this needs neither its own allocation
        // nor an arena. m_HasPortableAttribute replaces the old "pointer is null" test.
        PointAttribute m_PortableAttribute;
        bool m_HasPortableAttribute;

        /// <summary>
        /// Address of the owned portable attribute. Callers outside this struct still take a pointer, and it stays
        /// valid because the decoder list is sized once, before any decoder is initialized.
        /// </summary>
        PointAttribute* PortableAttributePtr
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                fixed (PointAttribute* ptr = &m_PortableAttribute)
                {
                    return ptr;
                }
            }
        }

        PredictionScheme m_PredictionScheme;
        bool m_HasPredictionScheme;

        // Quantization transform parameters.
        UnsafeList<float> m_MinValues;
        float m_Range;
        int m_QuantizationBits;

        // Octahedron transform parameters.
        int m_OctahedronQuantizationBits;

        /// <summary>
        /// Binds the decoder to an attribute of the geometry.
        /// </summary>
        /// <param name="decoder">Owning decoder state.</param>
        /// <param name="id">Attribute index within the geometry.</param>
        /// <returns>False when the attribute is unsuitable for the encoder type.</returns>
        public bool Init(DracoDecoderState* decoder, AttributeId id)
        {
            m_Decoder = decoder;
            attributeId = id;
            if (!decoder->geometry->HasAttribute(id))
            {
                return false;
            }

            ref var attribute = ref Attribute;
            switch (encoderType)
            {
                case SequentialAttributeEncoderType.Quantization:
                    // Only floating point attributes can be quantized.
                    return attribute.dataType == DataType.Float32;
                case SequentialAttributeEncoderType.Normals:
                    return attribute.numComponents == 3 && attribute.dataType == DataType.Float32;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Returns the attribute in its portable (pre-transform) representation.
        /// Mirrors <c>SequentialAttributeDecoder::GetPortableAttribute</c>.
        /// </summary>
        /// <returns>Pointer to the portable attribute, or null when there is none.</returns>
        public PointAttribute* GetPortableAttribute()
        {
            if (!m_HasPortableAttribute)
            {
                return null;
            }
            // Copy the point to value mapping over to the portable attribute if needed.
            ref var attribute = ref Attribute;
            if (!attribute.identityMapping && m_PortableAttribute.identityMapping)
            {
                var mapSize = attribute.IndicesMapSize;
                m_PortableAttribute.SetExplicitMapping(mapSize);
                for (var i = 0; i < mapSize; ++i)
                {
                    m_PortableAttribute.SetPointMapEntry(i, attribute.MappedIndex(i));
                }
            }
            return PortableAttributePtr;
        }

        /// <summary>
        /// Decodes the attribute into its portable representation.
        /// </summary>
        /// <param name="pointIds">Decoding order of the points.</param>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodePortableAttribute(UnsafeList<int>* pointIds, DecoderBuffer* buffer)
        {
            ref var attribute = ref Attribute;
            if (attribute.numComponents <= 0 || !attribute.Reset(pointIds->Length))
            {
                return false;
            }
            return DecodeValues(pointIds, buffer);
        }

        /// <summary>
        /// Decodes the data the portable transform needs, such as quantization parameters.
        /// </summary>
        /// <param name="pointIds">Decoding order of the points.</param>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodeDataNeededByPortableTransform(UnsafeList<int>* pointIds, DecoderBuffer* buffer)
        {
            switch (encoderType)
            {
                case SequentialAttributeEncoderType.Quantization:
                    return DecodeQuantizedDataInfo();
                case SequentialAttributeEncoderType.Normals:
                    if (!buffer->Decode(out byte quantizationBits))
                    {
                        return false;
                    }
                    m_OctahedronQuantizationBits = quantizationBits;
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Converts the decoded portable values back into the attribute's original format.
        /// </summary>
        /// <param name="pointIds">Decoding order of the points.</param>
        /// <returns>False on malformed input.</returns>
        public bool TransformAttributeToOriginalFormat(UnsafeList<int>* pointIds)
        {
            if (encoderType == SequentialAttributeEncoderType.Generic)
            {
                return true;
            }
            return StoreValues((uint)pointIds->Length);
        }

        bool DecodeValues(UnsafeList<int>* pointIds, DecoderBuffer* buffer)
        {
            if (encoderType == SequentialAttributeEncoderType.Generic)
            {
                // Raw attribute values in their original format.
                var numValues = pointIds->Length;
                ref var attribute = ref Attribute;
                var entrySize = attribute.byteStride;
                var destination = attribute.buffer.Ptr;
                for (var i = 0; i < numValues; ++i)
                {
                    if (!buffer->Decode(destination + (long)i * entrySize, entrySize))
                    {
                        return false;
                    }
                }
                return true;
            }

            // Decode the prediction scheme.
            if (!buffer->Decode(out sbyte predictionSchemeMethod))
            {
                return false;
            }
            if (predictionSchemeMethod < (sbyte)PredictionSchemeMethod.None
                || predictionSchemeMethod >= (sbyte)PredictionSchemeMethod.Count)
            {
                return false;
            }
            if (predictionSchemeMethod != (sbyte)PredictionSchemeMethod.None)
            {
                if (!buffer->Decode(out sbyte predictionTransformType))
                {
                    return false;
                }
                if (predictionTransformType < (sbyte)PredictionSchemeTransformType.None
                    || predictionTransformType >= (sbyte)PredictionSchemeTransformType.Count)
                {
                    return false;
                }
                if (!CreateIntPredictionScheme(
                        (PredictionSchemeMethod)predictionSchemeMethod,
                        (PredictionSchemeTransformType)predictionTransformType))
                {
                    return false;
                }
            }

            if (m_HasPredictionScheme && !InitPredictionScheme())
            {
                return false;
            }

            return DecodeIntegerValues(pointIds, buffer);
        }

        bool CreateIntPredictionScheme(
            PredictionSchemeMethod predictionMethod, PredictionSchemeTransformType transformType)
        {
            if (encoderType == SequentialAttributeEncoderType.Normals)
            {
                // Normals support only the canonicalized octahedron transform.
                if (transformType != PredictionSchemeTransformType.NormalOctahedronCanonicalized)
                {
                    return false;
                }
            }
            else if (transformType != PredictionSchemeTransformType.Wrap)
            {
                // All other attributes support only the wrap transform.
                return false;
            }

            // The multi-parallelogram and the deprecated texture coordinate schemes were
            // only produced by encoders older than the supported bitstream versions.
            if (predictionMethod == PredictionSchemeMethod.None
                || predictionMethod == PredictionSchemeMethod.MeshMultiParallelogram
                || predictionMethod == PredictionSchemeMethod.MeshTexCoordsDeprecated)
            {
                return false;
            }

            var meshData = m_Decoder->GetMeshPredictionSchemeData(attributeId, predictionMethod);
            var resolvedMethod = meshData.IsInitialized ? predictionMethod : PredictionSchemeMethod.Difference;
            m_PredictionScheme = PredictionScheme.Create(resolvedMethod, transformType, meshData);
            m_HasPredictionScheme = true;
            return true;
        }

        bool InitPredictionScheme()
        {
            var numParents = m_PredictionScheme.GetNumParentAttributes();
            for (var i = 0; i < numParents; ++i)
            {
                var parentType = m_PredictionScheme.GetParentAttributeType(i);
                var parentId = m_Decoder->geometry->GetNamedAttributeId(parentType);
                if (!parentId.IsValid)
                {
                    return false; // Requested attribute does not exist.
                }
                var portable = m_Decoder->GetPortableAttribute(parentId);
                if (portable == null || !m_PredictionScheme.SetParentAttribute(portable))
                {
                    return false;
                }
            }
            return true;
        }

        int GetNumValueComponents()
        {
            // Normals are always quantized into two octahedral components.
            return encoderType == SequentialAttributeEncoderType.Normals
                ? 2
                : Attribute.numComponents;
        }

        void PreparePortableAttribute(int numEntries, int numComponents)
        {
            ref var attribute = ref Attribute;
            m_HasPortableAttribute = true;
            m_PortableAttribute.Init(
                attribute.attributeType, (byte)numComponents, DataType.Int32, false, numEntries);
            m_PortableAttribute.SetIdentityMapping();
            m_PortableAttribute.uniqueId = attribute.uniqueId;
        }

        int* GetPortableAttributeData()
        {
            if (!m_HasPortableAttribute || m_PortableAttribute.numUniqueEntries == 0)
            {
                return null;
            }
            return (int*)m_PortableAttribute.GetAddress(0);
        }

        bool DecodeIntegerValues(UnsafeList<int>* pointIds, DecoderBuffer* buffer)
        {
            var numComponents = GetNumValueComponents();
            if (numComponents <= 0)
            {
                return false;
            }
            var numEntries = pointIds->Length;
            var numValues = (long)numEntries * numComponents;
            PreparePortableAttribute(numEntries, numComponents);
            var portableAttributeData = GetPortableAttributeData();
            if (portableAttributeData == null)
            {
                return false;
            }

            if (!buffer->Decode(out byte compressed))
            {
                return false;
            }
            if (compressed > 0)
            {
                if (!SymbolDecoding.DecodeSymbols(
                        (uint)numValues, numComponents, buffer, (uint*)portableAttributeData))
                {
                    return false;
                }
            }
            else
            {
                if (!buffer->Decode(out byte numBytes))
                {
                    return false;
                }
                if (numBytes == DataTypeUtils.DataTypeLength(DataType.Int32))
                {
                    if (m_PortableAttribute.buffer.Length < sizeof(int) * numValues)
                    {
                        return false;
                    }
                    if (!buffer->Decode(portableAttributeData, sizeof(int) * numValues))
                    {
                        return false;
                    }
                }
                else
                {
                    if (m_PortableAttribute.buffer.Length < numBytes * numValues)
                    {
                        return false;
                    }
                    if (buffer->RemainingSize < (long)numBytes * numValues)
                    {
                        return false;
                    }
                    for (long i = 0; i < numValues; ++i)
                    {
                        if (!buffer->Decode(portableAttributeData + i, numBytes))
                        {
                            return false;
                        }
                    }
                }
            }

            if (numValues > 0 && (!m_HasPredictionScheme || !m_PredictionScheme.AreCorrectionsPositive()))
            {
                BitUtils.ConvertSymbolsToSignedInts(
                    (uint*)portableAttributeData, (int)numValues, portableAttributeData);
            }

            if (m_HasPredictionScheme)
            {
                if (!m_PredictionScheme.DecodePredictionData(buffer))
                {
                    return false;
                }
                if (numValues > 0)
                {
                    if (!m_PredictionScheme.ComputeOriginalValues(
                            portableAttributeData, portableAttributeData,
                            (int)numValues, numComponents, pointIds->Ptr))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        bool DecodeQuantizedDataInfo()
        {
            // The quantization transform only needs the component count, which is the same
            // for the portable and the target attribute.
            var numComponents = m_HasPortableAttribute
                ? m_PortableAttribute.numComponents
                : Attribute.numComponents;

            var buffer = &m_Decoder->buffer;
            {
                if (!m_MinValues.IsCreated)
                {
                    m_MinValues = new UnsafeList<float>(numComponents, Allocator.Persistent);
                }
                m_MinValues.Resize(numComponents, NativeArrayOptions.UninitializedMemory);
                if (!buffer->Decode(m_MinValues.Ptr, sizeof(float) * numComponents))
                {
                    return false;
                }
                if (!buffer->Decode(out m_Range))
                {
                    return false;
                }
                if (!buffer->Decode(out byte quantizationBits))
                {
                    return false;
                }
                // Draco supports up to 30 bit quantization.
                if (quantizationBits < 1 || quantizationBits > 30)
                {
                    return false;
                }
                m_QuantizationBits = quantizationBits;
            }
            return true;
        }

        bool StoreValues(uint numValues)
        {
            switch (encoderType)
            {
                case SequentialAttributeEncoderType.Integer:
                    return StoreIntegerValues(numValues);
                case SequentialAttributeEncoderType.Quantization:
                    return DequantizeValues();
                case SequentialAttributeEncoderType.Normals:
                    return InverseOctahedralTransform();
                default:
                    return false;
            }
        }

        bool StoreIntegerValues(uint numValues)
        {
            ref var attribute = ref Attribute;
            var numComponents = attribute.numComponents;
            var portableAttributeData = GetPortableAttributeData();
            var destination = attribute.buffer.Ptr;
            var valueId = 0;

            switch (attribute.dataType)
            {
                case DataType.UInt8:
                case DataType.Int8:
                {
                    for (uint i = 0; i < numValues; ++i)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            destination[i * numComponents + c] = (byte)portableAttributeData[valueId++];
                        }
                    }
                    return true;
                }
                case DataType.UInt16:
                case DataType.Int16:
                {
                    var typed = (ushort*)destination;
                    for (uint i = 0; i < numValues; ++i)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            typed[i * numComponents + c] = (ushort)portableAttributeData[valueId++];
                        }
                    }
                    return true;
                }
                case DataType.UInt32:
                case DataType.Int32:
                {
                    var typed = (uint*)destination;
                    for (uint i = 0; i < numValues; ++i)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            typed[i * numComponents + c] = (uint)portableAttributeData[valueId++];
                        }
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        bool DequantizeValues()
        {
            ref var attribute = ref Attribute;
            if (attribute.dataType != DataType.Float32)
            {
                return false;
            }
            var maxQuantizedValue = (int)((1u << m_QuantizationBits) - 1);
            var numComponents = attribute.numComponents;
            var dequantizer = new Dequantizer();
            if (!dequantizer.Init(m_Range, maxQuantizedValue))
            {
                return false;
            }
            var sourceData = (int*)m_PortableAttribute.GetAddress(0);
            var destination = (float*)attribute.buffer.Ptr;
            var minValues = m_MinValues.Ptr;
            var numValues = attribute.numUniqueEntries;

            var quantizedValueId = 0;
            for (var i = 0; i < numValues; ++i)
            {
                for (var c = 0; c < numComponents; ++c)
                {
                    var value = dequantizer.DequantizeFloat(sourceData[quantizedValueId++]);
                    destination[i * numComponents + c] = value + minValues[c];
                }
            }
            return true;
        }

        bool InverseOctahedralTransform()
        {
            ref var attribute = ref Attribute;
            if (attribute.dataType != DataType.Float32)
            {
                return false;
            }
            if (attribute.numComponents != 3)
            {
                return false;
            }
            var toolBox = OctahedronToolBox.CreateUninitialized();
            if (!toolBox.SetQuantizationBits(m_OctahedronQuantizationBits))
            {
                return false;
            }
            var numPoints = attribute.numUniqueEntries;
            var sourceData = (int*)m_PortableAttribute.GetAddress(0);
            var destination = (float*)attribute.buffer.Ptr;
            for (var i = 0; i < numPoints; ++i)
            {
                var s = sourceData[i * 2];
                var t = sourceData[i * 2 + 1];
                toolBox.QuantizedOctahedralCoordsToUnitVector(s, t, destination + i * 3);
            }
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_HasPredictionScheme)
            {
                m_PredictionScheme.Dispose();
                m_HasPredictionScheme = false;
            }
            if (m_MinValues.IsCreated)
            {
                m_MinValues.Dispose();
            }
            if (m_HasPortableAttribute)
            {
                m_PortableAttribute.Dispose();
                m_HasPortableAttribute = false;
            }
        }
    }

    /// <summary>
    /// Decodes a group of attributes that share the same decoding order.
    /// Mirrors <c>draco::AttributesDecoder</c> and
    /// <c>draco::SequentialAttributeDecodersController</c>.
    /// </summary>
    unsafe struct AttributesDecoder : IDisposable
    {
        /// <summary>
        /// Quantization parameters of one kd-tree coded floating point attribute.
        /// </summary>
        struct KdTreeQuantization
        {
            public UnsafeList<float> minValues;
            public float range;
            public int quantizationBits;
        }

        [NativeDisableUnsafePtrRestriction]
        DracoDecoderState* m_Decoder;

        UnsafeList<int> m_PointAttributeIds;
        UnsafeList<int> m_PointAttributeToLocalIdMap;
        UnsafeList<SequentialAttributeDecoder> m_SequentialDecoders;
        UnsafeList<int> m_PointIds;
        PointsSequencer m_Sequencer;

        /// <summary>True when the attributes were encoded with the kd-tree encoder.</summary>
        bool m_IsKdTree;
        UnsafeList<IntPtr> m_QuantizedPortableAttributes;
        UnsafeList<KdTreeQuantization> m_QuantizationTransforms;
        UnsafeList<int> m_MinSignedValues;
        IntegerPointsKdTreeDecoder m_KdTreeDecoder;

        /// <summary>
        /// Initializes the decoder with the sequencer that determines the decoding order.
        /// </summary>
        /// <param name="decoder">Owning decoder state.</param>
        /// <param name="sequencer">Sequencer producing the point order.</param>
        public void Init(DracoDecoderState* decoder, PointsSequencer sequencer)
        {
            InitCommon(decoder);
            m_Sequencer = sequencer;
        }

        /// <summary>
        /// Initializes the decoder for kd-tree encoded attributes.
        /// </summary>
        /// <param name="decoder">Owning decoder state.</param>
        public void InitKdTree(DracoDecoderState* decoder)
        {
            InitCommon(decoder);
            m_IsKdTree = true;
            m_QuantizedPortableAttributes = new UnsafeList<IntPtr>(4, Allocator.Persistent);
            m_QuantizationTransforms = new UnsafeList<KdTreeQuantization>(4, Allocator.Persistent);
            m_MinSignedValues = new UnsafeList<int>(4, Allocator.Persistent);
        }

        void InitCommon(DracoDecoderState* decoder)
        {
            m_Decoder = decoder;
            m_PointAttributeIds = new UnsafeList<int>(4, Allocator.Persistent);
            m_PointAttributeToLocalIdMap = new UnsafeList<int>(4, Allocator.Persistent);
            m_SequentialDecoders = new UnsafeList<SequentialAttributeDecoder>(4, Allocator.Persistent);
            m_PointIds = new UnsafeList<int>(0, Allocator.Persistent);
        }

        /// <summary>Number of attributes this decoder handles.</summary>
        public int NumAttributes => m_PointAttributeIds.Length;

        /// <summary>
        /// Returns the geometry attribute index of one of the handled attributes.
        /// </summary>
        /// <param name="index">Local index.</param>
        /// <returns>Attribute index within the geometry.</returns>
        public AttributeId GetAttributeId(LocalAttributeId index)
        {
            return new AttributeId(m_PointAttributeIds[index.value]);
        }

        LocalAttributeId GetLocalIdForPointAttribute(AttributeId pointAttributeId)
        {
            if (pointAttributeId.value >= m_PointAttributeToLocalIdMap.Length)
            {
                return LocalAttributeId.Invalid;
            }
            return new LocalAttributeId(m_PointAttributeToLocalIdMap[pointAttributeId.value]);
        }

        /// <summary>
        /// Returns the portable representation of one of the handled attributes.
        /// </summary>
        /// <param name="pointAttributeId">Attribute index within the geometry.</param>
        /// <returns>Pointer to the portable attribute, or null when unavailable.</returns>
        public PointAttribute* GetPortableAttribute(AttributeId pointAttributeId)
        {
            if (m_IsKdTree)
            {
                return null;
            }
            var localId = GetLocalIdForPointAttribute(pointAttributeId);
            if (!localId.IsValid)
            {
                return null;
            }
            return m_SequentialDecoders.ElementAt(localId.value).GetPortableAttribute();
        }

        /// <summary>
        /// Reads the attribute descriptors and creates the per attribute decoders.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodeAttributesDecoderData(DecoderBuffer* buffer)
        {
            if (!buffer->DecodeVarintU32(out var numAttributes))
            {
                return false;
            }

            if (numAttributes == 0)
            {
                return false;
            }
            // At least five bytes of descriptor data are expected per attribute.
            if (numAttributes > 5 * buffer->RemainingSize)
            {
                return false;
            }

            m_PointAttributeIds.Resize((int)numAttributes, NativeArrayOptions.ClearMemory);
            var geometry = m_Decoder->geometry;
            for (var i = 0; i < numAttributes; ++i)
            {
                if (!buffer->Decode(out byte attributeType)
                    || !buffer->Decode(out byte dataType)
                    || !buffer->Decode(out byte numComponents)
                    || !buffer->Decode(out byte normalized))
                {
                    return false;
                }
                if (attributeType >= (byte)AttributeType.Generic + 1)
                {
                    return false;
                }
                if (dataType == (byte)DataType.Invalid || dataType > (byte)DataType.Bool)
                {
                    return false;
                }
                if (numComponents == 0)
                {
                    return false;
                }

                if (!buffer->DecodeVarintU32(out var uniqueId))
                {
                    return false;
                }

                var attribute = new PointAttribute();
                attribute.Init(
                    (AttributeType)attributeType,
                    numComponents,
                    (DataType)dataType,
                    normalized > 0,
                    0);
                attribute.uniqueId = uniqueId;
                var attributeIndex = geometry->AddAttribute(attribute);
                m_PointAttributeIds[i] = attributeIndex.value;

                if (attributeIndex.value >= m_PointAttributeToLocalIdMap.Length)
                {
                    var oldLength = m_PointAttributeToLocalIdMap.Length;
                    m_PointAttributeToLocalIdMap.Resize(
                        attributeIndex.value + 1, NativeArrayOptions.UninitializedMemory);
                    for (var j = oldLength; j <= attributeIndex.value; ++j)
                    {
                        m_PointAttributeToLocalIdMap[j] = DracoConstants.invalidIndex;
                    }
                }
                m_PointAttributeToLocalIdMap[attributeIndex.value] = i;
            }

            if (m_IsKdTree)
            {
                // The kd-tree decoder has no per attribute sub-decoders.
                return true;
            }

            // Read the encoder type of each attribute and create the matching decoder.
            m_SequentialDecoders.Resize((int)numAttributes, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < numAttributes; ++i)
            {
                if (!buffer->Decode(out byte decoderType))
                {
                    return false;
                }
                if (decoderType > (byte)SequentialAttributeEncoderType.Normals)
                {
                    return false;
                }
                ref var sequentialDecoder = ref m_SequentialDecoders.ElementAt(i);
                sequentialDecoder.encoderType = (SequentialAttributeEncoderType)decoderType;
                if (!sequentialDecoder.Init(m_Decoder, new AttributeId(m_PointAttributeIds[i])))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Decodes all attribute values.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodeAttributes(DecoderBuffer* buffer)
        {
            if (m_IsKdTree)
            {
                return DecodeKdTreeAttributes(buffer);
            }
            var pointIds = (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_PointIds);
            {
                if (!m_Sequencer.GenerateSequence(pointIds))
                {
                    return false;
                }
                var numAttributes = NumAttributes;
                for (var i = 0; i < numAttributes; ++i)
                {
                    ref var attribute = ref m_Decoder->geometry->GetAttribute(GetAttributeId(new LocalAttributeId(i)));
                    if (!m_Sequencer.UpdatePointToAttributeIndexMapping(ref attribute))
                    {
                        return false;
                    }
                }

                for (var i = 0; i < numAttributes; ++i)
                {
                    if (!m_SequentialDecoders.ElementAt(i).DecodePortableAttribute(pointIds, buffer))
                    {
                        return false;
                    }
                }
                for (var i = 0; i < numAttributes; ++i)
                {
                    if (!m_SequentialDecoders.ElementAt(i)
                            .DecodeDataNeededByPortableTransform(pointIds, buffer))
                    {
                        return false;
                    }
                }
                for (var i = 0; i < numAttributes; ++i)
                {
                    if (!m_SequentialDecoders.ElementAt(i).TransformAttributeToOriginalFormat(pointIds))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        #region Kd-tree attributes

        bool DecodeKdTreeAttributes(DecoderBuffer* buffer)
        {
            if (!DecodeKdTreePortableAttributes(buffer))
            {
                return false;
            }
            if (!DecodeKdTreeTransformData(buffer))
            {
                return false;
            }
            if (!TransformKdTreeAttributesToOriginalFormat())
            {
                return false;
            }
            return true;
        }

        bool DecodeKdTreePortableAttributes(DecoderBuffer* buffer)
        {
            if (!buffer->Decode(out byte compressionLevel))
            {
                return false;
            }
            if (compressionLevel > 6)
            {
                return false;
            }
            var numPoints = m_Decoder->geometry->numPoints;
            var numAttributes = NumAttributes;

            var sink = new KdTreePointSink();
            sink.Init(numAttributes);

            var totalDimensionality = 0u;
            var success = true;
            for (var i = 0; i < numAttributes; ++i)
            {
                ref var attribute = ref m_Decoder->geometry->GetAttribute(GetAttributeId(new LocalAttributeId(i)));
                // All attributes share the point count and use identity mapping.
                attribute.Reset(numPoints);
                attribute.SetIdentityMapping();

                PointAttribute* target;
                switch (attribute.dataType)
                {
                    case DataType.UInt32:
                    case DataType.UInt16:
                    case DataType.UInt8:
                        // These can be decoded into directly. Taking the address is safe here: all attributes were
                        // added before decoding started, and the sink doesn't outlive this method.
                        target = (PointAttribute*)UnsafeUtility.AddressOf(ref attribute);
                        break;
                    case DataType.Int32:
                    case DataType.Int16:
                    case DataType.Int8:
                        // Signed values are reconstructed from an offset decoded later.
                        for (var c = 0; c < attribute.numComponents; ++c)
                        {
                            m_MinSignedValues.Add(0);
                        }
                        target = (PointAttribute*)UnsafeUtility.AddressOf(ref attribute);
                        break;
                    case DataType.Float32:
                    {
                        // Floating point values are decoded as quantized integers and
                        // dequantized afterwards.
                        var portable = Unmanaged.New<PointAttribute>();
                        portable->Init(
                            attribute.attributeType,
                            attribute.numComponents,
                            DataType.UInt32,
                            false,
                            numPoints);
                        portable->SetIdentityMapping();
                        m_QuantizedPortableAttributes.Add((IntPtr)portable);
                        target = portable;
                        break;
                    }
                    default:
                        success = false;
                        target = null;
                        break;
                }
                if (!success)
                {
                    break;
                }

                var dataSize = DataTypeUtils.DataTypeLength(target->dataType);
                sink.AddTarget(target, (int)totalDimensionality, dataSize, target->numComponents);
                totalDimensionality += target->numComponents;
            }

            if (success)
            {
                m_KdTreeDecoder.Init(compressionLevel, totalDimensionality);
                success = m_KdTreeDecoder.DecodePoints(buffer, ref sink, (uint)numPoints)
                    && m_KdTreeDecoder.NumDecodedPoints == numPoints;
            }

            sink.Dispose();
            return success;
        }

        bool DecodeKdTreeTransformData(DecoderBuffer* buffer)
        {
            for (var i = 0; i < NumAttributes; ++i)
            {
                ref var attribute = ref m_Decoder->geometry->GetAttribute(GetAttributeId(new LocalAttributeId(i)));
                if (attribute.dataType != DataType.Float32)
                {
                    continue;
                }
                var numComponents = (int)attribute.numComponents;
                var minValues = new UnsafeList<float>(numComponents, Allocator.Persistent);
                minValues.Resize(numComponents, NativeArrayOptions.UninitializedMemory);
                if (!buffer->Decode(minValues.Ptr, sizeof(float) * numComponents))
                {
                    minValues.Dispose();
                    return false;
                }
                if (!buffer->Decode(out float maxValueDif))
                {
                    minValues.Dispose();
                    return false;
                }
                if (!buffer->Decode(out byte quantizationBits) || quantizationBits > 31)
                {
                    minValues.Dispose();
                    return false;
                }
                m_QuantizationTransforms.Add(new KdTreeQuantization
                {
                    minValues = minValues,
                    range = maxValueDif,
                    quantizationBits = quantizationBits
                });
            }

            // Offsets that turn the decoded unsigned values back into signed ones.
            for (var i = 0; i < m_MinSignedValues.Length; ++i)
            {
                if (!buffer->DecodeVarintI32(out var value))
                {
                    return false;
                }
                m_MinSignedValues[i] = value;
            }
            return true;
        }

        bool TransformKdTreeAttributesToOriginalFormat()
        {
            if (m_QuantizedPortableAttributes.Length == 0 && m_MinSignedValues.Length == 0)
            {
                return true;
            }
            var numProcessedQuantized = 0;
            var numProcessedSignedComponents = 0;
            for (var i = 0; i < NumAttributes; ++i)
            {
                ref var attribute = ref m_Decoder->geometry->GetAttribute(GetAttributeId(new LocalAttributeId(i)));
                switch (attribute.dataType)
                {
                    case DataType.Int32:
                    case DataType.Int16:
                    case DataType.Int8:
                        if (!TransformAttributeBackToSignedType(ref attribute, numProcessedSignedComponents))
                        {
                            return false;
                        }
                        numProcessedSignedComponents += attribute.numComponents;
                        break;
                    case DataType.Float32:
                    {
                        if (numProcessedQuantized >= m_QuantizationTransforms.Length)
                        {
                            return false;
                        }
                        var source = (PointAttribute*)m_QuantizedPortableAttributes[numProcessedQuantized];
                        var transform = m_QuantizationTransforms[numProcessedQuantized];
                        numProcessedQuantized++;

                        var maxQuantizedValue = (int)((1u << transform.quantizationBits) - 1);
                        var dequantizer = new Dequantizer();
                        if (!dequantizer.Init(transform.range, maxQuantizedValue))
                        {
                            return false;
                        }
                        var numComponents = (int)attribute.numComponents;
                        var sourceData = (uint*)source->GetAddress(0);
                        var destination = (float*)attribute.buffer.Ptr;
                        var minValues = transform.minValues.Ptr;
                        var valueId = 0;
                        for (var v = 0; v < attribute.numUniqueEntries; ++v)
                        {
                            for (var c = 0; c < numComponents; ++c)
                            {
                                var value = dequantizer.DequantizeFloat((int)sourceData[valueId++]);
                                destination[v * numComponents + c] = value + minValues[c];
                            }
                        }
                        break;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Turns the decoded unsigned values of a signed integer attribute back into signed
        /// ones by adding the per component minimum that was decoded separately.
        /// Mirrors <c>KdTreeAttributesDecoder::TransformAttributeBackToSignedType</c>.
        /// </summary>
        bool TransformAttributeBackToSignedType(
            ref PointAttribute attribute, int numProcessedSignedComponents)
        {
            var numComponents = (int)attribute.numComponents;
            var numValues = attribute.numUniqueEntries;
            var minValues = m_MinSignedValues.Ptr + numProcessedSignedComponents;
            if (numProcessedSignedComponents + numComponents > m_MinSignedValues.Length)
            {
                return false;
            }

            switch (attribute.dataType)
            {
                case DataType.Int32:
                {
                    var data = (uint*)attribute.buffer.Ptr;
                    for (var v = 0; v < numValues; ++v)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            var index = v * numComponents + c;
                            var value = data[index];
                            if (value > int.MaxValue)
                            {
                                return false;
                            }
                            ((int*)data)[index] = (int)value + minValues[c];
                        }
                    }
                    return true;
                }
                case DataType.Int16:
                {
                    var data = (ushort*)attribute.buffer.Ptr;
                    for (var v = 0; v < numValues; ++v)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            var index = v * numComponents + c;
                            var value = data[index];
                            ((short*)data)[index] = (short)(value + minValues[c]);
                        }
                    }
                    return true;
                }
                case DataType.Int8:
                {
                    var data = attribute.buffer.Ptr;
                    for (var v = 0; v < numValues; ++v)
                    {
                        for (var c = 0; c < numComponents; ++c)
                        {
                            var index = v * numComponents + c;
                            var value = data[index];
                            ((sbyte*)data)[index] = (sbyte)(value + minValues[c]);
                        }
                    }
                    return true;
                }
                default:
                    return false;
            }
        }

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_QuantizedPortableAttributes.IsCreated)
            {
                for (var i = 0; i < m_QuantizedPortableAttributes.Length; ++i)
                {
                    var portable = (PointAttribute*)m_QuantizedPortableAttributes[i];
                    portable->Dispose();
                    Unmanaged.Free(portable);
                }
                m_QuantizedPortableAttributes.Dispose();
            }
            if (m_QuantizationTransforms.IsCreated)
            {
                for (var i = 0; i < m_QuantizationTransforms.Length; ++i)
                {
                    var transform = m_QuantizationTransforms[i];
                    if (transform.minValues.IsCreated)
                    {
                        transform.minValues.Dispose();
                    }
                }
                m_QuantizationTransforms.Dispose();
            }
            if (m_MinSignedValues.IsCreated)
            {
                m_MinSignedValues.Dispose();
            }
            m_KdTreeDecoder.Dispose();
            if (m_SequentialDecoders.IsCreated)
            {
                for (var i = 0; i < m_SequentialDecoders.Length; ++i)
                {
                    m_SequentialDecoders.ElementAt(i).Dispose();
                }
                m_SequentialDecoders.Dispose();
            }
            if (m_PointAttributeIds.IsCreated)
            {
                m_PointAttributeIds.Dispose();
            }
            if (m_PointAttributeToLocalIdMap.IsCreated)
            {
                m_PointAttributeToLocalIdMap.Dispose();
            }
            if (m_PointIds.IsCreated)
            {
                m_PointIds.Dispose();
            }
            m_Sequencer.Dispose();
        }
    }
}
