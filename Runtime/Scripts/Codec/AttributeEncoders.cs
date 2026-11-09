// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Encodes the values of a single attribute in a given point order.
    /// Combines <c>draco::SequentialAttributeEncoder</c> and its integer, quantization and
    /// normal specializations into a single dispatchable value type.
    /// </summary>
    unsafe struct SequentialAttributeEncoder : IDisposable
    {
        /// <summary>Identifier written to the bit-stream so the decoder can match it.</summary>
        public SequentialAttributeEncoderType uniqueId;
        /// <summary>Index of the encoded attribute within the geometry.</summary>
        public int attributeId;

        [NativeDisableUnsafePtrRestriction]
        PointAttribute* m_Attribute;

        PointAttribute m_PortableAttribute;
        bool m_HasPortableAttribute;
        bool m_IsParentEncoder;

        PredictionSchemeEncoder m_PredictionScheme;
        bool m_HasPredictionScheme;

        AttributeQuantizationTransform m_QuantizationTransform;
        AttributeOctahedronTransform m_OctahedronTransform;

        UnsafeList<int> m_ParentAttributes;
        UnsafeList<int> m_EncodedData;

        /// <summary>Number of attributes that have to be encoded before this one.</summary>
        public int NumParentAttributes => m_ParentAttributes.IsCreated ? m_ParentAttributes.Length : 0;

        /// <summary>
        /// Returns one of the parent attribute indices.
        /// </summary>
        /// <param name="index">Parent index.</param>
        /// <returns>Attribute index of the parent.</returns>
        public int GetParentAttributeId(int index)
        {
            return m_ParentAttributes[index];
        }

        /// <summary>
        /// The attribute holding the losslessly encoded values, which is safe to predict from.
        /// </summary>
        /// <returns>Pointer to the portable attribute, or the source attribute.</returns>
        public PointAttribute* GetPortableAttribute()
        {
            if (m_HasPortableAttribute)
            {
                return (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
            }
            return m_Attribute;
        }

        /// <summary>
        /// Marks this encoder as a parent of another encoder, which forces the portable
        /// attribute to carry a point to value mapping.
        /// </summary>
        public void MarkParentAttribute()
        {
            m_IsParentEncoder = true;
        }

        /// <summary>
        /// Picks the encoder that matches the attribute's data type and quantization settings.
        /// Mirrors <c>SequentialAttributeEncodersController::CreateSequentialEncoder</c>.
        /// </summary>
        /// <param name="attribute">Attribute to encode.</param>
        /// <param name="options">Encoder settings.</param>
        /// <returns>The matching encoder type.</returns>
        public static SequentialAttributeEncoderType SelectEncoderType(
            PointAttribute* attribute, EncoderOptions options)
        {
            switch (attribute->dataType)
            {
                case DataType.UInt8:
                case DataType.Int8:
                case DataType.UInt16:
                case DataType.Int16:
                case DataType.UInt32:
                case DataType.Int32:
                    return SequentialAttributeEncoderType.Integer;
                case DataType.Float32:
                    if (options.GetQuantizationBits(attribute->attributeType) > 0)
                    {
                        if (attribute->attributeType == AttributeType.Normal)
                        {
                            // Normals are only supported as quantized float vectors.
                            return SequentialAttributeEncoderType.Normals;
                        }
                        return SequentialAttributeEncoderType.Quantization;
                    }
                    break;
            }
            return SequentialAttributeEncoderType.Generic;
        }

        /// <summary>
        /// Initializes the encoder and creates its prediction scheme.
        /// Mirrors the <c>Init</c> methods of the sequential attribute encoders.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="attribute">Attribute to encode.</param>
        /// <param name="id">Index of the attribute within the geometry.</param>
        /// <param name="encoderType">Encoder type to use.</param>
        /// <returns>False when the attribute cannot be encoded with this encoder.</returns>
        public bool Init(
            DracoEncoderState* encoder,
            PointAttribute* attribute,
            int id,
            SequentialAttributeEncoderType encoderType
            )
        {
            uniqueId = encoderType;
            m_Attribute = attribute;
            attributeId = id;
            m_ParentAttributes = new UnsafeList<int>(1, Allocator.Persistent);
            m_QuantizationTransform.Init();
            m_OctahedronTransform.Init();

            if (encoderType == SequentialAttributeEncoderType.Generic)
            {
                return true;
            }

            if (encoderType == SequentialAttributeEncoderType.Integer)
            {
                switch (attribute->dataType)
                {
                    case DataType.Int8:
                    case DataType.UInt8:
                    case DataType.Int16:
                    case DataType.UInt16:
                    case DataType.Int32:
                    case DataType.UInt32:
                        break;
                    default:
                        return false;
                }
            }

            var quantizationBits = encoder->options.GetQuantizationBits(attribute->attributeType);
            if (encoderType == SequentialAttributeEncoderType.Quantization)
            {
                if (attribute->dataType != DataType.Float32)
                {
                    return false;
                }
                if (quantizationBits < 1)
                {
                    return false;
                }
                if (!m_QuantizationTransform.ComputeParameters(attribute, quantizationBits))
                {
                    return false;
                }
            }
            else if (encoderType == SequentialAttributeEncoderType.Normals)
            {
                // Only three component normals are supported.
                if (attribute->numComponents != 3)
                {
                    return false;
                }
                if (quantizationBits < 1)
                {
                    return false;
                }
                m_OctahedronTransform.SetParameters(quantizationBits);
            }

            if (!CreatePredictionScheme(encoder, quantizationBits))
            {
                return false;
            }
            return true;
        }

        bool CreatePredictionScheme(DracoEncoderState* encoder, int quantizationBits)
        {
            var method = PredictionSchemeSelection.GetPredictionMethodFromOptions();
            PredictionSchemeTransformType transform;
            if (uniqueId == SequentialAttributeEncoderType.Normals)
            {
                // Normals use the canonicalized octahedron transform and only support the
                // difference and geometric normal predictors.
                transform = PredictionSchemeTransformType.NormalOctahedronCanonicalized;
                var defaultMethod = PredictionSchemeSelection.SelectPredictionMethod(
                    attributeId, encoder);
                method = defaultMethod == PredictionSchemeMethod.MeshGeometricNormal
                    ? PredictionSchemeMethod.MeshGeometricNormal
                    : PredictionSchemeMethod.Difference;
            }
            else
            {
                transform = PredictionSchemeTransformType.Wrap;
            }

            if (!PredictionSchemeSelection.CreateForEncoder(
                    method, attributeId, encoder, transform, out m_PredictionScheme))
            {
                m_HasPredictionScheme = false;
                return true; // No prediction is used.
            }
            m_HasPredictionScheme = true;

            if (uniqueId == SequentialAttributeEncoderType.Normals)
            {
                var maxQuantizedValue = (1 << quantizationBits) - 1;
                if (!m_PredictionScheme.SetMaxQuantizedValue(maxQuantizedValue))
                {
                    m_PredictionScheme.Dispose();
                    m_HasPredictionScheme = false;
                    return true;
                }
            }

            // Register the attributes the prediction scheme needs as parents.
            for (var i = 0; i < m_PredictionScheme.GetNumParentAttributes(); ++i)
            {
                var parentType = m_PredictionScheme.GetParentAttributeType(i);
                var parentId = encoder->geometry.GetNamedAttributeId(parentType);
                if (parentId == DracoConstants.invalidIndex)
                {
                    // The requested attribute does not exist, so no prediction is used.
                    m_PredictionScheme.Dispose();
                    m_HasPredictionScheme = false;
                    m_ParentAttributes.Clear();
                    return true;
                }
                m_ParentAttributes.Add(parentId);
                encoder->MarkParentAttribute(parentId);
            }
            return true;
        }

        /// <summary>
        /// Converts the attribute values into the form that is encoded losslessly.
        /// Mirrors <c>TransformAttributeToPortableFormat</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="pointIds">Encoding order of the points.</param>
        /// <returns>False on unsupported input.</returns>
        public bool TransformAttributeToPortableFormat(
            DracoEncoderState* encoder, ref UnsafeList<int> pointIds)
        {
            if (uniqueId == SequentialAttributeEncoderType.Generic)
            {
                return true; // Values are stored verbatim.
            }

            var numEntries = pointIds.Length;
            if (!PrepareValues(encoder, ref pointIds, numEntries))
            {
                return false;
            }

            // A parent attribute needs a point to value mapping so that other encoders can
            // look up its portable values.
            if (!m_IsParentEncoder)
            {
                return true;
            }
            var portable = (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
            var numPoints = encoder->geometry.numPoints;
            // Map original attribute value indices onto the new, encoding order based ones.
            var valueToValueMap = new UnsafeList<int>(
                m_Attribute->numUniqueEntries, Allocator.Temp);
            valueToValueMap.Resize(m_Attribute->numUniqueEntries, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < pointIds.Length; ++i)
            {
                valueToValueMap[m_Attribute->MappedIndex(pointIds[i])] = i;
            }
            if (portable->identityMapping)
            {
                portable->SetExplicitMapping(numPoints);
            }
            for (var i = 0; i < numPoints; ++i)
            {
                portable->SetPointMapEntry(i, valueToValueMap[m_Attribute->MappedIndex(i)]);
            }
            valueToValueMap.Dispose();
            return true;
        }

        bool PrepareValues(DracoEncoderState* encoder, ref UnsafeList<int> pointIds, int numEntries)
        {
            if (m_HasPortableAttribute)
            {
                m_PortableAttribute.Dispose();
                m_HasPortableAttribute = false;
            }

            switch (uniqueId)
            {
                case SequentialAttributeEncoderType.Quantization:
                {
                    m_PortableAttribute = AttributeQuantizationTransform.InitTransformedAttribute(
                        m_Attribute, numEntries);
                    m_HasPortableAttribute = true;
                    var portable = (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
                    m_QuantizationTransform.TransformAttribute(
                        m_Attribute, pointIds.Ptr, numEntries, portable);
                    return true;
                }
                case SequentialAttributeEncoderType.Normals:
                {
                    m_PortableAttribute = AttributeOctahedronTransform.InitTransformedAttribute(
                        m_Attribute, numEntries);
                    m_HasPortableAttribute = true;
                    var portable = (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
                    return m_OctahedronTransform.TransformAttribute(
                        m_Attribute, pointIds.Ptr, numEntries, portable);
                }
                default:
                {
                    // Integer attributes are widened to 32 bit signed integers.
                    var numComponents = (int)m_Attribute->numComponents;
                    m_PortableAttribute = new PointAttribute();
                    m_PortableAttribute.Init(
                        m_Attribute->attributeType, m_Attribute->numComponents,
                        DataType.Int32, false, numEntries);
                    m_PortableAttribute.uniqueId = m_Attribute->uniqueId;
                    m_HasPortableAttribute = true;
                    var portable = (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
                    if (encoder->geometry.numPoints > 0)
                    {
                        portable->SetExplicitMapping(encoder->geometry.numPoints);
                    }
                    var data = (int*)portable->GetAddress(0);
                    var dstIndex = 0;
                    for (var i = 0; i < pointIds.Length; ++i)
                    {
                        var valueIndex = m_Attribute->MappedIndex(pointIds[i]);
                        AttributeValueConversion.ConvertToInt32(
                            m_Attribute, valueIndex, numComponents, data + dstIndex);
                        dstIndex += numComponents;
                    }
                    return true;
                }
            }
        }

        /// <summary>
        /// Losslessly encodes the portable attribute values.
        /// Mirrors <c>EncodePortableAttribute</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="pointIds">Encoding order of the points.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False on failure.</returns>
        public bool EncodePortableAttribute(
            DracoEncoderState* encoder, ref UnsafeList<int> pointIds, ref EncoderBuffer buffer)
        {
            if (uniqueId == SequentialAttributeEncoderType.Generic)
            {
                // Raw values in their native format.
                var entrySize = m_Attribute->byteStride;
                for (var i = 0; i < pointIds.Length; ++i)
                {
                    var entryId = m_Attribute->MappedIndex(pointIds[i]);
                    buffer.Encode(m_Attribute->GetAddress(entryId), entrySize);
                }
                return true;
            }
            return EncodeIntegerValues(encoder, ref pointIds, ref buffer);
        }

        bool EncodeIntegerValues(
            DracoEncoderState* encoder, ref UnsafeList<int> pointIds, ref EncoderBuffer buffer)
        {
            if (m_Attribute->numUniqueEntries == 0)
            {
                return true;
            }

            var predictionMethod = PredictionSchemeMethod.None;
            if (m_HasPredictionScheme)
            {
                // Resolve the portable data of the parent attributes.
                for (var i = 0; i < m_PredictionScheme.GetNumParentAttributes(); ++i)
                {
                    var parentType = m_PredictionScheme.GetParentAttributeType(i);
                    var parentId = encoder->geometry.GetNamedAttributeId(parentType);
                    if (parentId == DracoConstants.invalidIndex)
                    {
                        return false;
                    }
                    if (!m_PredictionScheme.SetParentAttribute(
                            encoder->GetPortableAttribute(parentId)))
                    {
                        return false;
                    }
                }
                predictionMethod = m_PredictionScheme.method;
            }
            buffer.Encode((sbyte)predictionMethod);
            if (m_HasPredictionScheme)
            {
                buffer.Encode((sbyte)m_PredictionScheme.transformType);
            }

            var portable = (PointAttribute*)UnsafeUtility.AddressOf(ref m_PortableAttribute);
            var numComponents = (int)portable->numComponents;
            var numValues = numComponents * portable->numUniqueEntries;
            var portableData = (int*)portable->GetAddress(0);

            // The portable data has to stay intact because other attributes may predict from
            // it, so all encoding steps operate on a copy.
            if (!m_EncodedData.IsCreated)
            {
                m_EncodedData = new UnsafeList<int>(numValues, Allocator.Persistent);
            }
            m_EncodedData.Resize(numValues, NativeArrayOptions.ClearMemory);
            var encodedData = m_EncodedData.Ptr;

            if (m_HasPredictionScheme)
            {
                if (!m_PredictionScheme.ComputeCorrectionValues(
                        portableData, encodedData, numValues, numComponents, pointIds.Ptr))
                {
                    return false;
                }
            }

            if (!m_HasPredictionScheme || !m_PredictionScheme.AreCorrectionsPositive())
            {
                var input = m_HasPredictionScheme ? encodedData : portableData;
                var symbols = (uint*)encodedData;
                for (var i = 0; i < numValues; ++i)
                {
                    symbols[i] = BitUtils.ConvertSignedIntToSymbol(input[i]);
                }
            }

            // Built in attribute compression is always enabled by the Draco for Unity encoder.
            buffer.Encode((byte)1);
            var compressionLevel = 10 - encoder->options.Speed;
            if (!SymbolEncoding.EncodeSymbols(
                    (uint*)encodedData, pointIds.Length * numComponents, numComponents,
                    compressionLevel, ref buffer))
            {
                return false;
            }

            if (m_HasPredictionScheme)
            {
                return m_PredictionScheme.EncodePredictionData(ref buffer);
            }
            return true;
        }

        /// <summary>
        /// Writes the data the decoder needs to reverse the portable transform.
        /// Mirrors <c>EncodeDataNeededByPortableTransform</c>.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False when the transform data cannot be written.</returns>
        public bool EncodeDataNeededByPortableTransform(ref EncoderBuffer buffer)
        {
            switch (uniqueId)
            {
                case SequentialAttributeEncoderType.Quantization:
                    return m_QuantizationTransform.EncodeParameters(ref buffer);
                case SequentialAttributeEncoderType.Normals:
                    return m_OctahedronTransform.EncodeParameters(ref buffer);
                default:
                    return true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_HasPortableAttribute)
            {
                m_PortableAttribute.Dispose();
                m_HasPortableAttribute = false;
            }
            if (m_HasPredictionScheme)
            {
                m_PredictionScheme.Dispose();
                m_HasPredictionScheme = false;
            }
            m_QuantizationTransform.Dispose();
            if (m_ParentAttributes.IsCreated)
            {
                m_ParentAttributes.Dispose();
            }
            if (m_EncodedData.IsCreated)
            {
                m_EncodedData.Dispose();
            }
        }
    }

    /// <summary>
    /// Encodes a set of attributes, either sequentially in a traversal defined order or
    /// jointly with the kd-tree algorithm.
    /// Combines <c>draco::SequentialAttributeEncodersController</c> and
    /// <c>draco::KdTreeAttributesEncoder</c>.
    /// </summary>
    unsafe struct AttributesEncoder : IDisposable
    {
        /// <summary>Which of the two encoding strategies is used.</summary>
        public AttributeEncoderType type;

        UnsafeList<int> m_PointAttributeIds;
        UnsafeList<int> m_PointAttributeToLocalIdMap;

        // Sequential encoding state.
        UnsafeList<SequentialAttributeEncoder> m_SequentialEncoders;
        UnsafeList<bool> m_MarkedAsParent;
        UnsafeList<int> m_PointIds;
        PointsSequencer m_Sequencer;
        bool m_HasSequencer;

        // Kd-tree encoding state.
        UnsafeList<AttributeQuantizationTransform> m_QuantizationTransforms;
        UnsafeList<PointAttribute> m_QuantizedPortableAttributes;
        UnsafeList<int> m_MinSignedValues;
        int m_NumComponents;

        /// <summary>Number of attributes handled by this encoder.</summary>
        public int NumAttributes => m_PointAttributeIds.IsCreated ? m_PointAttributeIds.Length : 0;

        /// <summary>
        /// Creates an encoder that encodes attributes one after another.
        /// </summary>
        /// <param name="sequencer">Sequencer defining the point order.</param>
        /// <param name="attributeId">First attribute to encode.</param>
        /// <returns>An initialized attributes encoder.</returns>
        public static AttributesEncoder CreateSequential(PointsSequencer sequencer, int attributeId)
        {
            var encoder = new AttributesEncoder
            {
                type = AttributeEncoderType.Basic,
                m_Sequencer = sequencer,
                m_HasSequencer = true
            };
            encoder.InitLists();
            encoder.AddAttributeId(attributeId);
            return encoder;
        }

        /// <summary>
        /// Creates an encoder that encodes all attributes jointly with the kd-tree algorithm.
        /// </summary>
        /// <param name="attributeId">First attribute to encode.</param>
        /// <returns>An initialized attributes encoder.</returns>
        public static AttributesEncoder CreateKdTree(int attributeId)
        {
            var encoder = new AttributesEncoder
            {
                type = AttributeEncoderType.KdTree
            };
            encoder.InitLists();
            encoder.AddAttributeId(attributeId);
            return encoder;
        }

        void InitLists()
        {
            m_PointAttributeIds = new UnsafeList<int>(4, Allocator.Persistent);
            m_PointAttributeToLocalIdMap = new UnsafeList<int>(4, Allocator.Persistent);
        }

        /// <summary>
        /// Adds another attribute to this encoder.
        /// </summary>
        /// <param name="id">Attribute index.</param>
        public void AddAttributeId(int id)
        {
            m_PointAttributeIds.Add(id);
            if (id >= m_PointAttributeToLocalIdMap.Length)
            {
                var oldLength = m_PointAttributeToLocalIdMap.Length;
                m_PointAttributeToLocalIdMap.Resize(id + 1, NativeArrayOptions.UninitializedMemory);
                for (var i = oldLength; i <= id; ++i)
                {
                    m_PointAttributeToLocalIdMap[i] = DracoConstants.invalidIndex;
                }
            }
            m_PointAttributeToLocalIdMap[id] = m_PointAttributeIds.Length - 1;
        }

        /// <summary>
        /// Replaces the attribute order with the given one.
        /// </summary>
        /// <param name="order">Local attribute indices in their new order.</param>
        public void SetAttributeIds(ref UnsafeList<int> order)
        {
            var previous = new UnsafeList<int>(m_PointAttributeIds.Length, Allocator.Temp);
            for (var i = 0; i < m_PointAttributeIds.Length; ++i)
            {
                previous.Add(m_PointAttributeIds[i]);
            }
            m_PointAttributeIds.Clear();
            m_PointAttributeToLocalIdMap.Clear();
            for (var i = 0; i < order.Length; ++i)
            {
                AddAttributeId(previous[order[i]]);
            }
            previous.Dispose();
        }

        /// <summary>
        /// Returns the attribute index of one of the encoded attributes.
        /// </summary>
        /// <param name="index">Local index within this encoder.</param>
        /// <returns>Attribute index within the geometry.</returns>
        public int GetAttributeId(int index)
        {
            return m_PointAttributeIds[index];
        }

        int GetLocalId(int pointAttributeId)
        {
            if (pointAttributeId >= m_PointAttributeToLocalIdMap.Length)
            {
                return DracoConstants.invalidIndex;
            }
            return m_PointAttributeToLocalIdMap[pointAttributeId];
        }

        /// <summary>
        /// Number of attributes that have to be encoded before the given attribute.
        /// </summary>
        /// <param name="pointAttributeId">Attribute index.</param>
        /// <returns>Number of parent attributes.</returns>
        public int NumParentAttributes(int pointAttributeId)
        {
            if (type != AttributeEncoderType.Basic)
            {
                return 0;
            }
            var localId = GetLocalId(pointAttributeId);
            if (localId < 0 || localId >= m_SequentialEncoders.Length)
            {
                return 0;
            }
            return m_SequentialEncoders.ElementAt(localId).NumParentAttributes;
        }

        /// <summary>
        /// Returns one of the parent attributes of the given attribute.
        /// </summary>
        /// <param name="pointAttributeId">Attribute index.</param>
        /// <param name="parentIndex">Parent index.</param>
        /// <returns>Attribute index of the parent.</returns>
        public int GetParentAttributeId(int pointAttributeId, int parentIndex)
        {
            if (type != AttributeEncoderType.Basic)
            {
                return DracoConstants.invalidIndex;
            }
            var localId = GetLocalId(pointAttributeId);
            if (localId < 0 || localId >= m_SequentialEncoders.Length)
            {
                return DracoConstants.invalidIndex;
            }
            return m_SequentialEncoders.ElementAt(localId).GetParentAttributeId(parentIndex);
        }

        /// <summary>
        /// Marks one of the encoded attributes as a parent of another attribute.
        /// </summary>
        /// <param name="pointAttributeId">Attribute index.</param>
        /// <returns>False when the attribute is not handled by this encoder.</returns>
        public bool MarkParentAttribute(int pointAttributeId)
        {
            if (type != AttributeEncoderType.Basic)
            {
                return false;
            }
            var localId = GetLocalId(pointAttributeId);
            if (localId < 0)
            {
                return false;
            }
            // The encoder may not exist yet, so the flag is stored separately.
            if (!m_MarkedAsParent.IsCreated)
            {
                m_MarkedAsParent = new UnsafeList<bool>(localId + 1, Allocator.Persistent);
            }
            if (m_MarkedAsParent.Length <= localId)
            {
                var oldLength = m_MarkedAsParent.Length;
                m_MarkedAsParent.Resize(localId + 1, NativeArrayOptions.UninitializedMemory);
                for (var i = oldLength; i <= localId; ++i)
                {
                    m_MarkedAsParent[i] = false;
                }
            }
            m_MarkedAsParent[localId] = true;

            if (m_SequentialEncoders.IsCreated && localId < m_SequentialEncoders.Length)
            {
                m_SequentialEncoders.ElementAt(localId).MarkParentAttribute();
            }
            return true;
        }

        /// <summary>
        /// Returns the losslessly encoded values of one of the encoded attributes.
        /// </summary>
        /// <param name="pointAttributeId">Attribute index.</param>
        /// <returns>Pointer to the portable attribute, or null.</returns>
        public PointAttribute* GetPortableAttribute(int pointAttributeId)
        {
            if (type != AttributeEncoderType.Basic)
            {
                return null;
            }
            var localId = GetLocalId(pointAttributeId);
            if (localId < 0 || localId >= m_SequentialEncoders.Length)
            {
                return null;
            }
            return m_SequentialEncoders.ElementAt(localId).GetPortableAttribute();
        }

        /// <summary>
        /// Creates the per attribute encoders and initializes them.
        /// Mirrors <c>SequentialAttributeEncodersController::Init</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <returns>False when one of the attributes cannot be encoded.</returns>
        public bool Init(DracoEncoderState* encoder)
        {
            if (type != AttributeEncoderType.Basic)
            {
                return true;
            }
            var numAttributes = NumAttributes;
            m_SequentialEncoders = new UnsafeList<SequentialAttributeEncoder>(
                numAttributes, Allocator.Persistent);
            m_SequentialEncoders.Resize(numAttributes, NativeArrayOptions.ClearMemory);
            for (var i = 0; i < numAttributes; ++i)
            {
                var attributeId = GetAttributeId(i);
                var attribute = encoder->geometry.Attribute(attributeId);
                var encoderType = SequentialAttributeEncoder.SelectEncoderType(
                    attribute, encoder->options);
                ref var sequentialEncoder = ref m_SequentialEncoders.ElementAt(i);
                if (!sequentialEncoder.Init(encoder, attribute, attributeId, encoderType))
                {
                    return false;
                }
                if (m_MarkedAsParent.IsCreated && i < m_MarkedAsParent.Length
                    && m_MarkedAsParent[i])
                {
                    sequentialEncoder.MarkParentAttribute();
                }
            }
            return true;
        }

        /// <summary>
        /// Writes the attribute descriptors and, for sequential encoding, the per attribute
        /// encoder identifiers.
        /// Mirrors <c>AttributesEncoder::EncodeAttributesEncoderData</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>True on success.</returns>
        public bool EncodeAttributesEncoderData(
            DracoEncoderState* encoder, ref EncoderBuffer buffer)
        {
            buffer.EncodeVarintU32((uint)NumAttributes);
            for (var i = 0; i < NumAttributes; ++i)
            {
                var attribute = encoder->geometry.Attribute(GetAttributeId(i));
                buffer.Encode((byte)attribute->attributeType);
                buffer.Encode((byte)attribute->dataType);
                buffer.Encode(attribute->numComponents);
                buffer.Encode(attribute->normalized ? (byte)1 : (byte)0);
                buffer.EncodeVarintU32(attribute->uniqueId);
            }
            if (type != AttributeEncoderType.Basic)
            {
                return true;
            }
            for (var i = 0; i < m_SequentialEncoders.Length; ++i)
            {
                buffer.Encode((byte)m_SequentialEncoders.ElementAt(i).uniqueId);
            }
            return true;
        }

        /// <summary>
        /// Encodes all attribute values.
        /// Mirrors <c>AttributesEncoder::EncodeAttributes</c>.
        /// </summary>
        /// <param name="encoder">Parent encoder state.</param>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False on failure.</returns>
        public bool EncodeAttributes(DracoEncoderState* encoder, ref EncoderBuffer buffer)
        {
            if (type == AttributeEncoderType.KdTree)
            {
                return KdTreeAttributesEncoding.Encode(
                    encoder,
                    (AttributesEncoder*)UnsafeUtility.AddressOf(ref this),
                    ref m_QuantizationTransforms,
                    ref m_QuantizedPortableAttributes,
                    ref m_MinSignedValues,
                    ref m_NumComponents,
                    ref buffer);
            }

            if (!m_HasSequencer)
            {
                return false;
            }
            if (!m_PointIds.IsCreated)
            {
                m_PointIds = new UnsafeList<int>(encoder->geometry.numPoints, Allocator.Persistent);
            }
            var sequencer = (PointsSequencer*)UnsafeUtility.AddressOf(ref m_Sequencer);
            var pointIds = (UnsafeList<int>*)UnsafeUtility.AddressOf(ref m_PointIds);
            if (!sequencer->GenerateSequence(pointIds))
            {
                return false;
            }

            for (var i = 0; i < m_SequentialEncoders.Length; ++i)
            {
                if (!m_SequentialEncoders.ElementAt(i).TransformAttributeToPortableFormat(
                        encoder, ref m_PointIds))
                {
                    return false;
                }
            }
            for (var i = 0; i < m_SequentialEncoders.Length; ++i)
            {
                if (!m_SequentialEncoders.ElementAt(i).EncodePortableAttribute(
                        encoder, ref m_PointIds, ref buffer))
                {
                    return false;
                }
            }
            // The transform data follows the values, matching the decoding order.
            for (var i = 0; i < m_SequentialEncoders.Length; ++i)
            {
                if (!m_SequentialEncoders.ElementAt(i).EncodeDataNeededByPortableTransform(
                        ref buffer))
                {
                    return false;
                }
            }
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_PointAttributeIds.IsCreated)
            {
                m_PointAttributeIds.Dispose();
            }
            if (m_PointAttributeToLocalIdMap.IsCreated)
            {
                m_PointAttributeToLocalIdMap.Dispose();
            }
            if (m_SequentialEncoders.IsCreated)
            {
                for (var i = 0; i < m_SequentialEncoders.Length; ++i)
                {
                    m_SequentialEncoders.ElementAt(i).Dispose();
                }
                m_SequentialEncoders.Dispose();
            }
            if (m_MarkedAsParent.IsCreated)
            {
                m_MarkedAsParent.Dispose();
            }
            if (m_PointIds.IsCreated)
            {
                m_PointIds.Dispose();
            }
            if (m_HasSequencer)
            {
                m_Sequencer.Dispose();
                m_HasSequencer = false;
            }
            if (m_QuantizationTransforms.IsCreated)
            {
                for (var i = 0; i < m_QuantizationTransforms.Length; ++i)
                {
                    m_QuantizationTransforms.ElementAt(i).Dispose();
                }
                m_QuantizationTransforms.Dispose();
            }
            if (m_QuantizedPortableAttributes.IsCreated)
            {
                for (var i = 0; i < m_QuantizedPortableAttributes.Length; ++i)
                {
                    m_QuantizedPortableAttributes.ElementAt(i).Dispose();
                }
                m_QuantizedPortableAttributes.Dispose();
            }
            if (m_MinSignedValues.IsCreated)
            {
                m_MinSignedValues.Dispose();
            }
        }
    }

    /// <summary>
    /// Chooses the prediction scheme for an attribute based on the encoder settings.
    /// Mirrors <c>draco/compression/attributes/prediction_schemes/prediction_scheme_encoder_factory.cc</c>.
    /// </summary>
    static unsafe class PredictionSchemeSelection
    {
        /// <summary>
        /// The prediction scheme requested through the encoder options. Draco for Unity never
        /// sets one explicitly, so the choice is left to <see cref="SelectPredictionMethod"/>.
        /// </summary>
        /// <returns>Always <see cref="PredictionSchemeMethod.Undefined"/>.</returns>
        public static PredictionSchemeMethod GetPredictionMethodFromOptions()
        {
            return PredictionSchemeMethod.Undefined;
        }

        /// <summary>
        /// Picks the prediction method for an attribute based on the geometry type, the
        /// attribute type and the speed settings.
        /// Mirrors <c>SelectPredictionMethod</c>.
        /// </summary>
        /// <param name="attributeId">Attribute index.</param>
        /// <param name="encoder">Encoder state.</param>
        /// <returns>The selected prediction method.</returns>
        public static PredictionSchemeMethod SelectPredictionMethod(
            int attributeId, DracoEncoderState* encoder)
        {
            var options = encoder->options;
            var speed = options.Speed;
            if (speed >= 10)
            {
                // Fastest setting that still compresses.
                return PredictionSchemeMethod.Difference;
            }
            if (encoder->geometry.isPointCloud)
            {
                return PredictionSchemeMethod.Difference;
            }

            var attribute = encoder->geometry.Attribute(attributeId);
            var attributeQuantization = options.GetQuantizationBits(attribute->attributeType);
            if (attributeQuantization != -1
                && attribute->attributeType == AttributeType.TextureCoordinate
                && attribute->numComponents == 2)
            {
                // The texture coordinate predictor needs positions that are either integer or
                // quantized. For numerical reasons the position quantization is limited to 21
                // bits and 2 * positionQuantization + attributeQuantization must stay below 64.
                var positionId = encoder->geometry.GetNamedAttributeId(AttributeType.Position);
                var isPositionValid = false;
                if (positionId != DracoConstants.invalidIndex)
                {
                    var position = encoder->geometry.Attribute(positionId);
                    if (DataTypeUtils.IsDataTypeIntegral(position->dataType))
                    {
                        isPositionValid = true;
                    }
                    else
                    {
                        var positionQuantization =
                            options.GetQuantizationBits(position->attributeType);
                        if (positionQuantization > 0 && positionQuantization <= 21
                            && 2 * positionQuantization + attributeQuantization < 64)
                        {
                            isPositionValid = true;
                        }
                    }
                }

                if (isPositionValid && speed < 4)
                {
                    // Texture coordinate prediction for speeds 0 to 3.
                    return PredictionSchemeMethod.MeshTexCoordsPortable;
                }
            }
            if (attribute->attributeType == AttributeType.Normal)
            {
                if (speed < 4)
                {
                    // Geometric normal prediction for speeds 0 to 3, which also needs positions
                    // that are either integer or quantized.
                    var positionId = encoder->geometry.GetNamedAttributeId(AttributeType.Position);
                    if (positionId != DracoConstants.invalidIndex)
                    {
                        var position = encoder->geometry.Attribute(positionId);
                        if (DataTypeUtils.IsDataTypeIntegral(position->dataType)
                            || options.GetQuantizationBits(position->attributeType) > 0)
                        {
                            return PredictionSchemeMethod.MeshGeometricNormal;
                        }
                    }
                }
                return PredictionSchemeMethod.Difference;
            }
            if (speed >= 8)
            {
                return PredictionSchemeMethod.Difference;
            }
            if (speed >= 2 || encoder->geometry.numPoints < 40)
            {
                // Parallelogram prediction for speeds 2 to 7, or when the overhead of the
                // constrained multi-parallelogram would outweigh its benefit.
                return PredictionSchemeMethod.MeshParallelogram;
            }
            // Constrained multi-parallelogram for speeds 0 and 1.
            return PredictionSchemeMethod.MeshConstrainedMultiParallelogram;
        }

        /// <summary>
        /// Creates a prediction scheme encoder for the given method.
        /// Mirrors <c>CreatePredictionSchemeForEncoder</c>.
        /// </summary>
        /// <param name="method">Requested method, or undefined to let the encoder choose.</param>
        /// <param name="attributeId">Attribute index.</param>
        /// <param name="encoder">Encoder state.</param>
        /// <param name="transform">Transform to apply to the correction values.</param>
        /// <param name="scheme">The created prediction scheme.</param>
        /// <returns>False when no prediction should be used.</returns>
        public static bool CreateForEncoder(
            PredictionSchemeMethod method,
            int attributeId,
            DracoEncoderState* encoder,
            PredictionSchemeTransformType transform,
            out PredictionSchemeEncoder scheme
            )
        {
            scheme = default;
            if (method == PredictionSchemeMethod.Undefined)
            {
                method = SelectPredictionMethod(attributeId, encoder);
            }
            if (method == PredictionSchemeMethod.None)
            {
                return false;
            }
            if (!encoder->geometry.isPointCloud && method != PredictionSchemeMethod.Difference)
            {
                if (encoder->TryGetMeshPredictionSchemeData(attributeId, out var meshData))
                {
                    scheme = PredictionSchemeEncoder.Create(method, transform, meshData);
                    if (scheme.IsInitialized)
                    {
                        return true;
                    }
                    scheme.Dispose();
                }
                // Fall through to the delta encoder.
            }
            scheme = PredictionSchemeEncoder.Create(
                PredictionSchemeMethod.Difference, transform, default);
            return true;
        }
    }
}
