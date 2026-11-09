// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Connectivity data a mesh prediction scheme operates on.
    /// Mirrors <c>draco::MeshPredictionSchemeData</c>.
    /// </summary>
    unsafe struct MeshPredictionSchemeData
    {
        /// <summary>Connectivity used for the prediction.</summary>
        public CornerTableView cornerTable;
        /// <summary>Maps a vertex onto the order in which its attribute value was decoded.</summary>
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<int>* vertexToDataMap;
        /// <summary>Maps a decoded attribute value onto the corner it was decoded at.</summary>
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<int>* dataToCornerMap;

        /// <summary>True once all members are set.</summary>
        public bool IsInitialized =>
            cornerTable.IsValid && vertexToDataMap != null && dataToCornerMap != null;
    }

    /// <summary>
    /// Reverses the prediction applied to integer attribute values during encoding.
    /// Combines Draco's prediction scheme decoder hierarchy and its decoding transforms
    /// into a single dispatchable value type so the decoder stays Burst compatible.
    /// </summary>
    unsafe struct PredictionScheme : IDisposable
    {
        const int k_MaxNumParallelograms = 4;

        /// <summary>Prediction method used by the encoder.</summary>
        public PredictionSchemeMethod method;
        /// <summary>Transform applied to the correction values.</summary>
        public PredictionSchemeTransformType transformType;

        // Wrap transform state.
        int m_NumComponents;
        int m_MinValue;
        int m_MaxValue;
        int m_MaxDif;
        UnsafeList<int> m_ClampedValue;

        // Normal octahedron transform state.
        OctahedronToolBox m_TransformOctahedron;

        // Mesh connectivity.
        MeshPredictionSchemeData m_MeshData;

        // Constrained multi-parallelogram state.
        UnsafeList<bool> m_IsCreaseEdge0;
        UnsafeList<bool> m_IsCreaseEdge1;
        UnsafeList<bool> m_IsCreaseEdge2;
        UnsafeList<bool> m_IsCreaseEdge3;

        // Texture coordinate predictor state.
        UnsafeList<bool> m_Orientations;
        int m_NumOrientationsDecoded;
        [NativeDisableUnsafePtrRestriction]
        PointAttribute* m_PositionAttribute;

        // Geometric normal predictor state.
        RAnsBitDecoder m_FlipNormalBitDecoder;
        OctahedronToolBox m_PredictorOctahedron;

        // Scratch buffers.
        UnsafeList<int> m_Scratch;

        /// <summary>
        /// Creates a prediction scheme for the given method and transform.
        /// </summary>
        /// <param name="predictionMethod">Prediction method from the bit-stream.</param>
        /// <param name="transform">Transform type from the bit-stream.</param>
        /// <param name="meshData">Mesh connectivity, or a default value for non-mesh schemes.</param>
        /// <returns>An initialized prediction scheme.</returns>
        public static PredictionScheme Create(
            PredictionSchemeMethod predictionMethod,
            PredictionSchemeTransformType transform,
            MeshPredictionSchemeData meshData
            )
        {
            return new PredictionScheme
            {
                method = predictionMethod,
                transformType = transform,
                m_MeshData = meshData,
                m_TransformOctahedron = OctahedronToolBox.CreateUninitialized(),
                m_PredictorOctahedron = OctahedronToolBox.CreateUninitialized()
            };
        }

        /// <summary>
        /// True when the scheme is usable with the data it was given.
        /// </summary>
        public bool IsInitialized
        {
            get
            {
                switch (method)
                {
                    case PredictionSchemeMethod.Difference:
                        return true;
                    case PredictionSchemeMethod.MeshTexCoordsPortable:
                    case PredictionSchemeMethod.MeshGeometricNormal:
                        return m_PositionAttribute != null && m_MeshData.IsInitialized;
                    default:
                        return m_MeshData.IsInitialized;
                }
            }
        }

        /// <summary>
        /// True when the transform keeps correction values non-negative.
        /// </summary>
        /// <returns>True for the octahedron transform.</returns>
        public bool AreCorrectionsPositive()
        {
            return transformType == PredictionSchemeTransformType.NormalOctahedronCanonicalized;
        }

        /// <summary>
        /// Number of attributes this scheme predicts from.
        /// </summary>
        /// <returns>One for the schemes that need positions, zero otherwise.</returns>
        public int GetNumParentAttributes()
        {
            switch (method)
            {
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                case PredictionSchemeMethod.MeshGeometricNormal:
                    return 1;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Type of a parent attribute this scheme predicts from.
        /// </summary>
        /// <param name="index">Parent attribute index.</param>
        /// <returns>The required attribute type.</returns>
        public AttributeType GetParentAttributeType(int index)
        {
            switch (method)
            {
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                case PredictionSchemeMethod.MeshGeometricNormal:
                    return AttributeType.Position;
                default:
                    return AttributeType.Invalid;
            }
        }

        /// <summary>
        /// Assigns a parent attribute used for the prediction.
        /// </summary>
        /// <param name="attribute">Portable parent attribute.</param>
        /// <returns>False if the attribute is not a three component position attribute.</returns>
        public bool SetParentAttribute(PointAttribute* attribute)
        {
            if (attribute == null || attribute->attributeType != AttributeType.Position)
            {
                return false;
            }
            if (attribute->numComponents != 3)
            {
                return false;
            }
            m_PositionAttribute = attribute;
            return true;
        }

        #region Transform

        void TransformInit(int numComponents)
        {
            m_NumComponents = numComponents;
            if (transformType == PredictionSchemeTransformType.Wrap)
            {
                if (!m_ClampedValue.IsCreated)
                {
                    m_ClampedValue = new UnsafeList<int>(numComponents, Allocator.Persistent);
                }
                m_ClampedValue.Resize(numComponents, NativeArrayOptions.ClearMemory);
            }
        }

        bool InitCorrectionBounds()
        {
            var dif = (long)m_MaxValue - m_MinValue;
            if (dif < 0 || dif >= int.MaxValue)
            {
                return false;
            }
            m_MaxDif = 1 + (int)dif;
            return true;
        }

        /// <summary>
        /// Reads the transform specific data from the bit-stream.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        bool DecodeTransformData(DecoderBuffer* buffer)
        {
            switch (transformType)
            {
                case PredictionSchemeTransformType.Wrap:
                {
                    if (!buffer->Decode(out int minValue))
                    {
                        return false;
                    }
                    if (!buffer->Decode(out int maxValue))
                    {
                        return false;
                    }
                    if (minValue > maxValue)
                    {
                        return false;
                    }
                    m_MinValue = minValue;
                    m_MaxValue = maxValue;
                    return InitCorrectionBounds();
                }
                case PredictionSchemeTransformType.NormalOctahedronCanonicalized:
                {
                    if (!buffer->Decode(out int maxQuantizedValue))
                    {
                        return false;
                    }
                    if (!buffer->Decode(out int _))
                    {
                        return false;
                    }
                    if (!SetMaxQuantizedValue(maxQuantizedValue))
                    {
                        return false;
                    }
                    // Guard against malformed input.
                    var bits = m_TransformOctahedron.QuantizationBits;
                    return bits >= 2 && bits <= 30;
                }
                default:
                    return false;
            }
        }

        bool SetMaxQuantizedValue(int maxQuantizedValue)
        {
            if (maxQuantizedValue % 2 == 0)
            {
                return false;
            }
            var q = BitUtils.MostSignificantBit((uint)maxQuantizedValue) + 1;
            return m_TransformOctahedron.SetQuantizationBits(q);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int* ClampPredictedValue(int* predictedValue)
        {
            var clamped = m_ClampedValue.Ptr;
            for (var i = 0; i < m_NumComponents; ++i)
            {
                if (predictedValue[i] > m_MaxValue)
                {
                    clamped[i] = m_MaxValue;
                }
                else if (predictedValue[i] < m_MinValue)
                {
                    clamped[i] = m_MinValue;
                }
                else
                {
                    clamped[i] = predictedValue[i];
                }
            }
            return clamped;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ComputeOriginalValue(int* predictedValues, int* correctionValues, int* outOriginalValues)
        {
            switch (transformType)
            {
                case PredictionSchemeTransformType.Wrap:
                {
                    predictedValues = ClampPredictedValue(predictedValues);
                    // Unsigned arithmetic avoids signed overflow on malformed input while
                    // producing identical results for valid data.
                    for (var i = 0; i < m_NumComponents; ++i)
                    {
                        outOriginalValues[i] = (int)((uint)predictedValues[i] + (uint)correctionValues[i]);
                        if (outOriginalValues[i] > m_MaxValue)
                        {
                            outOriginalValues[i] -= m_MaxDif;
                        }
                        else if (outOriginalValues[i] < m_MinValue)
                        {
                            outOriginalValues[i] += m_MaxDif;
                        }
                    }
                    break;
                }
                case PredictionSchemeTransformType.NormalOctahedronCanonicalized:
                {
                    ComputeOriginalValueOctahedronCanonicalized(
                        predictedValues[0], predictedValues[1],
                        correctionValues[0], correctionValues[1],
                        out outOriginalValues[0], out outOriginalValues[1]);
                    break;
                }
            }
        }

        void ComputeOriginalValueOctahedronCanonicalized(
            int predS, int predT, int corrS, int corrT, out int origS, out int origT)
        {
            var center = m_TransformOctahedron.CenterValue;
            var s = predS - center;
            var t = predT - center;

            var predIsInDiamond = m_TransformOctahedron.IsInDiamond(s, t);
            if (!predIsInDiamond)
            {
                m_TransformOctahedron.InvertDiamond(ref s, ref t);
            }
            var predIsInBottomLeft = IsInBottomLeft(s, t);
            var rotationCount = GetRotationCount(s, t);
            if (!predIsInBottomLeft)
            {
                RotatePoint(s, t, rotationCount, out s, out t);
            }

            var oS = m_TransformOctahedron.ModMax((int)((uint)s + (uint)corrS));
            var oT = m_TransformOctahedron.ModMax((int)((uint)t + (uint)corrT));

            if (!predIsInBottomLeft)
            {
                var reverseRotationCount = (4 - rotationCount) % 4;
                RotatePoint(oS, oT, reverseRotationCount, out oS, out oT);
            }
            if (!predIsInDiamond)
            {
                m_TransformOctahedron.InvertDiamond(ref oS, ref oT);
            }

            origS = oS + center;
            origT = oT + center;
        }

        static int GetRotationCount(int signX, int signY)
        {
            if (signX == 0)
            {
                if (signY == 0)
                {
                    return 0;
                }
                return signY > 0 ? 3 : 1;
            }
            if (signX > 0)
            {
                return signY >= 0 ? 2 : 1;
            }
            return signY <= 0 ? 0 : 3;
        }

        static void RotatePoint(int x, int y, int rotationCount, out int outX, out int outY)
        {
            switch (rotationCount)
            {
                case 1:
                    outX = y;
                    outY = -x;
                    break;
                case 2:
                    outX = -x;
                    outY = -y;
                    break;
                case 3:
                    outX = -y;
                    outY = x;
                    break;
                default:
                    outX = x;
                    outY = y;
                    break;
            }
        }

        static bool IsInBottomLeft(int x, int y)
        {
            if (x == 0 && y == 0)
            {
                return true;
            }
            return x < 0 && y <= 0;
        }

        #endregion

        #region Prediction data

        /// <summary>
        /// Reads the prediction specific data from the bit-stream.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <returns>False on malformed input.</returns>
        public bool DecodePredictionData(DecoderBuffer* buffer)
        {
            switch (method)
            {
                case PredictionSchemeMethod.MeshConstrainedMultiParallelogram:
                    if (!DecodeConstrainedMultiParallelogramData(buffer))
                    {
                        return false;
                    }
                    return DecodeTransformData(buffer);
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                    if (!DecodeTexCoordsPortableData(buffer))
                    {
                        return false;
                    }
                    return DecodeTransformData(buffer);
                case PredictionSchemeMethod.MeshGeometricNormal:
                    // The geometric normal scheme reads the transform data first.
                    if (!DecodeTransformData(buffer))
                    {
                        return false;
                    }
                    var flipDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_FlipNormalBitDecoder);
                    return flipDecoder->StartDecoding(buffer);
                default:
                    return DecodeTransformData(buffer);
            }
        }

        bool DecodeConstrainedMultiParallelogramData(DecoderBuffer* buffer)
        {
            for (var i = 0; i < k_MaxNumParallelograms; ++i)
            {
                if (!buffer->DecodeVarintU32(out var numFlags))
                {
                    return false;
                }
                if (numFlags > m_MeshData.cornerTable.NumCorners)
                {
                    return false;
                }
                if (numFlags == 0)
                {
                    continue;
                }
                var list = new UnsafeList<bool>((int)numFlags, Allocator.Persistent);
                list.Resize((int)numFlags, NativeArrayOptions.UninitializedMemory);
                var decoder = new RAnsBitDecoder();
                if (!decoder.StartDecoding(buffer))
                {
                    list.Dispose();
                    return false;
                }
                for (uint j = 0; j < numFlags; ++j)
                {
                    list[(int)j] = decoder.DecodeNextBit();
                }
                decoder.EndDecoding();
                SetCreaseEdgeList(i, list);
            }
            return true;
        }

        void SetCreaseEdgeList(int index, UnsafeList<bool> list)
        {
            switch (index)
            {
                case 0:
                    m_IsCreaseEdge0 = list;
                    break;
                case 1:
                    m_IsCreaseEdge1 = list;
                    break;
                case 2:
                    m_IsCreaseEdge2 = list;
                    break;
                default:
                    m_IsCreaseEdge3 = list;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        UnsafeList<bool> GetCreaseEdgeList(int index)
        {
            switch (index)
            {
                case 0:
                    return m_IsCreaseEdge0;
                case 1:
                    return m_IsCreaseEdge1;
                case 2:
                    return m_IsCreaseEdge2;
                default:
                    return m_IsCreaseEdge3;
            }
        }

        bool DecodeTexCoordsPortableData(DecoderBuffer* buffer)
        {
            // At most one orientation is consumed per attribute entry. Rejecting larger counts
            // prevents malformed input from triggering huge allocations.
            if (!buffer->Decode(out int numOrientations)
                || numOrientations < 0
                || numOrientations > m_MeshData.dataToCornerMap->Length)
            {
                return false;
            }
            m_Orientations = new UnsafeList<bool>(numOrientations, Allocator.Persistent);
            m_Orientations.Resize(numOrientations, NativeArrayOptions.UninitializedMemory);
            m_NumOrientationsDecoded = numOrientations;

            var lastOrientation = true;
            var decoder = new RAnsBitDecoder();
            if (!decoder.StartDecoding(buffer))
            {
                return false;
            }
            for (var i = 0; i < numOrientations; ++i)
            {
                if (!decoder.DecodeNextBit())
                {
                    lastOrientation = !lastOrientation;
                }
                m_Orientations[i] = lastOrientation;
            }
            decoder.EndDecoding();
            return true;
        }

        #endregion

        #region Original value reconstruction

        /// <summary>
        /// Reverses the prediction, turning correction values back into attribute values.
        /// </summary>
        /// <param name="inCorrections">Decoded correction values.</param>
        /// <param name="outData">Destination for the reconstructed values.</param>
        /// <param name="size">Total number of components across all entries.</param>
        /// <param name="numComponents">Number of components per entry.</param>
        /// <param name="entryToPointIdMap">Maps entries onto point indices.</param>
        /// <returns>False on malformed input.</returns>
        public bool ComputeOriginalValues(
            int* inCorrections,
            int* outData,
            int size,
            int numComponents,
            int* entryToPointIdMap
            )
        {
            switch (method)
            {
                case PredictionSchemeMethod.Difference:
                    return ComputeOriginalValuesDelta(inCorrections, outData, size, numComponents);
                case PredictionSchemeMethod.MeshParallelogram:
                    return ComputeOriginalValuesParallelogram(inCorrections, outData, size, numComponents);
                case PredictionSchemeMethod.MeshConstrainedMultiParallelogram:
                    return ComputeOriginalValuesConstrainedMultiParallelogram(
                        inCorrections, outData, size, numComponents);
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                    return ComputeOriginalValuesTexCoordsPortable(
                        inCorrections, outData, numComponents, entryToPointIdMap);
                case PredictionSchemeMethod.MeshGeometricNormal:
                    return ComputeOriginalValuesGeometricNormal(
                        inCorrections, outData, size, numComponents, entryToPointIdMap);
                default:
                    return false;
            }
        }

        int* GetScratch(int count)
        {
            if (!m_Scratch.IsCreated)
            {
                m_Scratch = new UnsafeList<int>(count, Allocator.Persistent);
            }
            if (m_Scratch.Length < count)
            {
                m_Scratch.Resize(count, NativeArrayOptions.ClearMemory);
            }
            return m_Scratch.Ptr;
        }

        bool ComputeOriginalValuesDelta(int* inCorrections, int* outData, int size, int numComponents)
        {
            TransformInit(numComponents);
            var zeroValues = GetScratch(numComponents);
            UnsafeUtility.MemClear(zeroValues, (long)numComponents * sizeof(int));
            ComputeOriginalValue(zeroValues, inCorrections, outData);

            for (var i = numComponents; i < size; i += numComponents)
            {
                ComputeOriginalValue(outData + i - numComponents, inCorrections + i, outData + i);
            }
            return true;
        }

        /// <summary>
        /// Reads the three attribute entries forming the parallelogram at a corner.
        /// Mirrors <c>GetParallelogramEntries</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void GetParallelogramEntries(
            int corner,
            ref CornerTableView table,
            int* vertexToDataMap,
            out int oppEntry,
            out int nextEntry,
            out int prevEntry
            )
        {
            oppEntry = vertexToDataMap[table.Vertex(corner)];
            nextEntry = vertexToDataMap[table.Vertex(CornerTable.Next(corner))];
            prevEntry = vertexToDataMap[table.Vertex(CornerTable.Previous(corner))];
        }

        /// <summary>
        /// Computes the parallelogram prediction at a corner.
        /// Mirrors <c>ComputeParallelogramPrediction</c>.
        /// </summary>
        internal static bool ComputeParallelogramPrediction(
            int dataEntryId,
            int corner,
            ref CornerTableView table,
            int* vertexToDataMap,
            int* inData,
            int numComponents,
            int* outPrediction
            )
        {
            var oppCorner = table.Opposite(corner);
            if (oppCorner < 0)
            {
                return false;
            }
            GetParallelogramEntries(
                oppCorner, ref table, vertexToDataMap,
                out var vertOpp, out var vertNext, out var vertPrev);
            if (vertOpp < dataEntryId && vertNext < dataEntryId && vertPrev < dataEntryId)
            {
                var vOppOff = vertOpp * numComponents;
                var vNextOff = vertNext * numComponents;
                var vPrevOff = vertPrev * numComponents;
                for (var c = 0; c < numComponents; ++c)
                {
                    long next = inData[vNextOff + c];
                    long prev = inData[vPrevOff + c];
                    long opp = inData[vOppOff + c];
                    outPrediction[c] = (int)(next + prev - opp);
                }
                return true;
            }
            return false; // Not all data required for the prediction is available.
        }

        bool ComputeOriginalValuesParallelogram(int* inCorrections, int* outData, int size, int numComponents)
        {
            TransformInit(numComponents);

            var table = m_MeshData.cornerTable;
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;

            var predVals = GetScratch(numComponents);
            UnsafeUtility.MemClear(predVals, (long)numComponents * sizeof(int));

            ComputeOriginalValue(predVals, inCorrections, outData);

            var cornerMapSize = m_MeshData.dataToCornerMap->Length;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;
            if ((long)cornerMapSize * numComponents > size)
            {
                return false;
            }
            for (var p = 1; p < cornerMapSize; ++p)
            {
                var cornerId = dataToCornerMap[p];
                var dstOffset = p * numComponents;
                if (!ComputeParallelogramPrediction(
                        p, cornerId, ref table, vertexToDataMap, outData, numComponents, predVals))
                {
                    // Some of the parallelogram vertices are not decoded yet, fall back to
                    // delta coding against the previously decoded entry.
                    var srcOffset = (p - 1) * numComponents;
                    ComputeOriginalValue(
                        outData + srcOffset, inCorrections + dstOffset, outData + dstOffset);
                }
                else
                {
                    ComputeOriginalValue(predVals, inCorrections + dstOffset, outData + dstOffset);
                }
            }
            return true;
        }

        bool ComputeOriginalValuesConstrainedMultiParallelogram(
            int* inCorrections, int* outData, int size, int numComponents)
        {
            TransformInit(numComponents);

            // Predictions for every parallelogram at the current vertex, the accumulated
            // multi-parallelogram prediction and the crease flag read positions.
            var scratch = GetScratch(numComponents * (k_MaxNumParallelograms + 1) + k_MaxNumParallelograms);
            var predVals = scratch;
            var multiPredVals = scratch + numComponents * k_MaxNumParallelograms;
            var isCreaseEdgePos = scratch + numComponents * (k_MaxNumParallelograms + 1);
            UnsafeUtility.MemClear(
                scratch,
                (long)(numComponents * (k_MaxNumParallelograms + 1) + k_MaxNumParallelograms) * sizeof(int));

            ComputeOriginalValue(predVals, inCorrections, outData);

            var table = m_MeshData.cornerTable;
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var cornerMapSize = m_MeshData.dataToCornerMap->Length;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;
            if ((long)cornerMapSize * numComponents > size)
            {
                return false;
            }

            for (var p = 1; p < cornerMapSize; ++p)
            {
                var startCornerId = dataToCornerMap[p];

                var cornerId = startCornerId;
                var numParallelograms = 0;
                var firstPass = true;
                while (cornerId >= 0)
                {
                    if (ComputeParallelogramPrediction(
                            p, cornerId, ref table, vertexToDataMap, outData, numComponents,
                            predVals + numParallelograms * numComponents))
                    {
                        ++numParallelograms;
                        if (numParallelograms == k_MaxNumParallelograms)
                        {
                            break;
                        }
                    }

                    // Swing left first, then continue right from the start corner once a
                    // boundary is reached.
                    if (firstPass)
                    {
                        cornerId = table.SwingLeft(cornerId);
                    }
                    else
                    {
                        cornerId = table.SwingRight(cornerId);
                    }
                    if (cornerId == startCornerId)
                    {
                        break;
                    }
                    if (cornerId < 0 && firstPass)
                    {
                        firstPass = false;
                        cornerId = table.SwingRight(startCornerId);
                    }
                }

                var numUsedParallelograms = 0;
                if (numParallelograms > 0)
                {
                    for (var i = 0; i < numComponents; ++i)
                    {
                        multiPredVals[i] = 0;
                    }
                    for (var i = 0; i < numParallelograms; ++i)
                    {
                        var context = numParallelograms - 1;
                        var pos = isCreaseEdgePos[context]++;
                        var creaseEdges = GetCreaseEdgeList(context);
                        if (!creaseEdges.IsCreated || creaseEdges.Length <= pos)
                        {
                            return false;
                        }
                        var isCrease = creaseEdges[pos];
                        if (!isCrease)
                        {
                            ++numUsedParallelograms;
                            var src = predVals + i * numComponents;
                            for (var j = 0; j < numComponents; ++j)
                            {
                                multiPredVals[j] = (int)((uint)multiPredVals[j] + (uint)src[j]);
                            }
                        }
                    }
                }

                var dstOffset = p * numComponents;
                if (numUsedParallelograms == 0)
                {
                    var srcOffset = (p - 1) * numComponents;
                    ComputeOriginalValue(
                        outData + srcOffset, inCorrections + dstOffset, outData + dstOffset);
                }
                else
                {
                    for (var c = 0; c < numComponents; ++c)
                    {
                        multiPredVals[c] /= numUsedParallelograms;
                    }
                    ComputeOriginalValue(
                        multiPredVals, inCorrections + dstOffset, outData + dstOffset);
                }
            }
            return true;
        }

        #endregion

        #region Texture coordinate prediction

        void GetPositionForEntryId(int entryId, int* entryToPointIdMap, long* outPosition)
        {
            var pointId = entryToPointIdMap[entryId];
            var valueIndex = m_PositionAttribute->MappedIndex(pointId);
            AttributeValueConversion.ConvertToInt64(m_PositionAttribute, valueIndex, 3, outPosition);
        }

        bool ComputeOriginalValuesTexCoordsPortable(
            int* inCorrections, int* outData, int numComponents, int* entryToPointIdMap)
        {
            if (numComponents != 2)
            {
                return false;
            }
            TransformInit(numComponents);

            var predictedValue = stackalloc int[2];
            var cornerMapSize = m_MeshData.dataToCornerMap->Length;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;

            // Orientations are consumed from the back, mirroring the reference decoder.
            var orientationIndex = m_NumOrientationsDecoded;

            for (var p = 0; p < cornerMapSize; ++p)
            {
                var cornerId = dataToCornerMap[p];
                if (!ComputePredictedTexCoord(
                        cornerId, outData, p, entryToPointIdMap, ref orientationIndex, predictedValue))
                {
                    return false;
                }

                var dstOffset = p * numComponents;
                ComputeOriginalValue(predictedValue, inCorrections + dstOffset, outData + dstOffset);
            }
            return true;
        }

        bool ComputePredictedTexCoord(
            int cornerId,
            int* data,
            int dataId,
            int* entryToPointIdMap,
            ref int orientationIndex,
            int* predictedValue
            )
        {
            var table = m_MeshData.cornerTable;
            var nextCornerId = CornerTable.Next(cornerId);
            var prevCornerId = CornerTable.Previous(cornerId);

            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var nextVertId = table.Vertex(nextCornerId);
            var prevVertId = table.Vertex(prevCornerId);
            var nextDataId = vertexToDataMap[nextVertId];
            var prevDataId = vertexToDataMap[prevVertId];

            if (prevDataId < dataId && nextDataId < dataId)
            {
                // Both neighbouring corners already have decoded UV coordinates.
                var nUvX = (long)data[nextDataId * 2];
                var nUvY = (long)data[nextDataId * 2 + 1];
                var pUvX = (long)data[prevDataId * 2];
                var pUvY = (long)data[prevDataId * 2 + 1];
                if (pUvX == nUvX && pUvY == nUvY)
                {
                    // No reliable prediction on a degenerate UV triangle.
                    predictedValue[0] = (int)pUvX;
                    predictedValue[1] = (int)pUvY;
                    return true;
                }

                var tipPos = stackalloc long[3];
                var nextPos = stackalloc long[3];
                var prevPos = stackalloc long[3];
                GetPositionForEntryId(dataId, entryToPointIdMap, tipPos);
                GetPositionForEntryId(nextDataId, entryToPointIdMap, nextPos);
                GetPositionForEntryId(prevDataId, entryToPointIdMap, prevPos);

                var pnX = prevPos[0] - nextPos[0];
                var pnY = prevPos[1] - nextPos[1];
                var pnZ = prevPos[2] - nextPos[2];
                var pnNorm2Squared = (ulong)(pnX * pnX + pnY * pnY + pnZ * pnZ);

                if (pnNorm2Squared != 0)
                {
                    var cnX = tipPos[0] - nextPos[0];
                    var cnY = tipPos[1] - nextPos[1];
                    var cnZ = tipPos[2] - nextPos[2];
                    var cnDotPn = pnX * cnX + pnY * cnY + pnZ * cnZ;

                    var pnUvX = pUvX - nUvX;
                    var pnUvY = pUvY - nUvY;

                    // Guard against the overflow of the scaled integer arithmetic below.
                    var nUvAbsMax = MathUtils.Max(MathUtils.Abs(nUvX), MathUtils.Abs(nUvY));
                    if ((ulong)nUvAbsMax > long.MaxValue / pnNorm2Squared)
                    {
                        return false;
                    }
                    var pnUvAbsMax = MathUtils.Max(MathUtils.Abs(pnUvX), MathUtils.Abs(pnUvY));
                    if (pnUvAbsMax != 0 && MathUtils.Abs(cnDotPn) > long.MaxValue / pnUvAbsMax)
                    {
                        return false;
                    }
                    var xUvX = nUvX * (long)pnNorm2Squared + cnDotPn * pnUvX;
                    var xUvY = nUvY * (long)pnNorm2Squared + cnDotPn * pnUvY;

                    var pnAbsMax = MathUtils.Max(MathUtils.Max(MathUtils.Abs(pnX), MathUtils.Abs(pnY)), MathUtils.Abs(pnZ));
                    if (pnAbsMax != 0 && MathUtils.Abs(cnDotPn) > long.MaxValue / pnAbsMax)
                    {
                        return false;
                    }

                    // Squared length of vector CX in position space.
                    var xPosX = nextPos[0] + cnDotPn * pnX / (long)pnNorm2Squared;
                    var xPosY = nextPos[1] + cnDotPn * pnY / (long)pnNorm2Squared;
                    var xPosZ = nextPos[2] + cnDotPn * pnZ / (long)pnNorm2Squared;
                    var cxX = tipPos[0] - xPosX;
                    var cxY = tipPos[1] - xPosY;
                    var cxZ = tipPos[2] - xPosZ;
                    var cxNorm2Squared = (ulong)(cxX * cxX + cxY * cxY + cxZ * cxZ);

                    // CX_UV is PN_UV rotated by 90 degrees and scaled by
                    // CX.Norm2() * PN.Norm2(), keeping the scaled coordinate space.
                    var cxUvX = pnUvY;
                    var cxUvY = -pnUvX;
                    var normSquared = MathUtils.IntSqrt(cxNorm2Squared * pnNorm2Squared);
                    cxUvX *= (long)normSquared;
                    cxUvY *= (long)normSquared;

                    if (orientationIndex <= 0)
                    {
                        return false;
                    }
                    var orientation = m_Orientations[--orientationIndex];

                    // Unsigned arithmetic avoids signed overflow while producing identical
                    // results for valid data.
                    long predictedUvX;
                    long predictedUvY;
                    if (orientation)
                    {
                        predictedUvX = (long)((ulong)xUvX + (ulong)cxUvX) / (long)pnNorm2Squared;
                        predictedUvY = (long)((ulong)xUvY + (ulong)cxUvY) / (long)pnNorm2Squared;
                    }
                    else
                    {
                        predictedUvX = (long)((ulong)xUvX - (ulong)cxUvX) / (long)pnNorm2Squared;
                        predictedUvY = (long)((ulong)xUvY - (ulong)cxUvY) / (long)pnNorm2Squared;
                    }
                    predictedValue[0] = (int)predictedUvX;
                    predictedValue[1] = (int)predictedUvY;
                    return true;
                }
            }

            // Fall back to delta coding when the neighbouring UVs or the positions cannot
            // be used for the prediction.
            var dataOffset = 0;
            if (prevDataId < dataId)
            {
                dataOffset = prevDataId * 2;
            }
            if (nextDataId < dataId)
            {
                dataOffset = nextDataId * 2;
            }
            else
            {
                if (dataId > 0)
                {
                    dataOffset = (dataId - 1) * 2;
                }
                else
                {
                    predictedValue[0] = 0;
                    predictedValue[1] = 0;
                    return true;
                }
            }
            predictedValue[0] = data[dataOffset];
            predictedValue[1] = data[dataOffset + 1];
            return true;
        }

        #endregion

        #region Geometric normal prediction

        bool ComputeOriginalValuesGeometricNormal(
            int* inCorrections, int* outData, int size, int numComponents, int* entryToPointIdMap)
        {
            if (numComponents != 2)
            {
                return false;
            }
            m_PredictorOctahedron.SetQuantizationBits(m_TransformOctahedron.QuantizationBits);
            TransformInit(numComponents);

            var cornerMapSize = m_MeshData.dataToCornerMap->Length;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;
            if ((long)cornerMapSize * numComponents > size)
            {
                return false;
            }

            var predNormal3d = stackalloc int[3];
            var predNormalOct = stackalloc int[2];

            for (var dataId = 0; dataId < cornerMapSize; ++dataId)
            {
                var cornerId = dataToCornerMap[dataId];
                ComputePredictedNormal(cornerId, entryToPointIdMap, predNormal3d);

                m_PredictorOctahedron.CanonicalizeIntegerVector(predNormal3d);
                var flipDecoder = (RAnsBitDecoder*)UnsafeUtility.AddressOf(ref m_FlipNormalBitDecoder);
                var flip = flipDecoder->DecodeNextBit();
                if (flip)
                {
                    predNormal3d[0] = -predNormal3d[0];
                    predNormal3d[1] = -predNormal3d[1];
                    predNormal3d[2] = -predNormal3d[2];
                }
                m_PredictorOctahedron.IntegerVectorToQuantizedOctahedralCoords(
                    predNormal3d, out predNormalOct[0], out predNormalOct[1]);

                var dataOffset = dataId * 2;
                ComputeOriginalValue(predNormalOct, inCorrections + dataOffset, outData + dataOffset);
            }
            return true;
        }

        void GetPositionForCorner(int corner, int* entryToPointIdMap, long* outPosition)
        {
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var vertId = m_MeshData.cornerTable.Vertex(corner);
            var dataId = vertexToDataMap[vertId];
            GetPositionForEntryId(dataId, entryToPointIdMap, outPosition);
        }

        void ComputePredictedNormal(int cornerId, int* entryToPointIdMap, int* prediction)
        {
            var table = m_MeshData.cornerTable;

            var posCent = stackalloc long[3];
            var posNext = stackalloc long[3];
            var posPrev = stackalloc long[3];
            GetPositionForCorner(cornerId, entryToPointIdMap, posCent);

            // Accumulate area weighted face normals around the vertex.
            ulong normalX = 0;
            ulong normalY = 0;
            ulong normalZ = 0;

            var startCorner = cornerId;
            var corner = startCorner;
            var leftTraversal = true;
            while (corner >= 0)
            {
                var cNext = CornerTable.Next(corner);
                var cPrev = CornerTable.Previous(corner);
                GetPositionForCorner(cNext, entryToPointIdMap, posNext);
                GetPositionForCorner(cPrev, entryToPointIdMap, posPrev);

                var deltaNextX = posNext[0] - posCent[0];
                var deltaNextY = posNext[1] - posCent[1];
                var deltaNextZ = posNext[2] - posCent[2];
                var deltaPrevX = posPrev[0] - posCent[0];
                var deltaPrevY = posPrev[1] - posCent[1];
                var deltaPrevZ = posPrev[2] - posCent[2];

                var crossX = deltaNextY * deltaPrevZ - deltaNextZ * deltaPrevY;
                var crossY = deltaNextZ * deltaPrevX - deltaNextX * deltaPrevZ;
                var crossZ = deltaNextX * deltaPrevY - deltaNextY * deltaPrevX;

                // Unsigned arithmetic avoids signed overflow on degenerate input.
                normalX += (ulong)crossX;
                normalY += (ulong)crossY;
                normalZ += (ulong)crossZ;

                // Advance the vertex ring iterator.
                if (leftTraversal)
                {
                    corner = table.SwingLeft(corner);
                    if (corner < 0)
                    {
                        corner = table.SwingRight(startCorner);
                        leftTraversal = false;
                    }
                    else if (corner == startCorner)
                    {
                        corner = DracoConstants.invalidCornerIndex;
                    }
                }
                else
                {
                    corner = table.SwingRight(corner);
                }
            }

            var nx = (long)normalX;
            var ny = (long)normalY;
            var nz = (long)normalZ;

            const long upperBound = 1 << 29;
            var absSum = MathUtils.Abs(nx) + MathUtils.Abs(ny) + MathUtils.Abs(nz);
            if (absSum > upperBound)
            {
                var quotient = absSum / upperBound;
                nx /= quotient;
                ny /= quotient;
                nz /= quotient;
            }
            prediction[0] = (int)nx;
            prediction[1] = (int)ny;
            prediction[2] = (int)nz;
        }

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_ClampedValue.IsCreated)
            {
                m_ClampedValue.Dispose();
            }
            if (m_IsCreaseEdge0.IsCreated)
            {
                m_IsCreaseEdge0.Dispose();
            }
            if (m_IsCreaseEdge1.IsCreated)
            {
                m_IsCreaseEdge1.Dispose();
            }
            if (m_IsCreaseEdge2.IsCreated)
            {
                m_IsCreaseEdge2.Dispose();
            }
            if (m_IsCreaseEdge3.IsCreated)
            {
                m_IsCreaseEdge3.Dispose();
            }
            if (m_Orientations.IsCreated)
            {
                m_Orientations.Dispose();
            }
            if (m_Scratch.IsCreated)
            {
                m_Scratch.Dispose();
            }
        }
    }

    /// <summary>
    /// Integer math helpers shared with the reference implementation.
    /// Mirrors <c>draco/core/math_utils.h</c>.
    /// </summary>
    static class MathUtils
    {
        /// <summary>
        /// Absolute value of a 64 bit integer.
        /// </summary>
        /// <param name="value">Input value.</param>
        /// <returns>Absolute value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Abs(long value)
        {
            return value < 0 ? -value : value;
        }

        /// <summary>
        /// Larger of two 64 bit integers.
        /// </summary>
        /// <param name="a">First value.</param>
        /// <param name="b">Second value.</param>
        /// <returns>The larger value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Max(long a, long b)
        {
            return a > b ? a : b;
        }

        /// <summary>
        /// Deterministic integer square root.
        /// Mirrors <c>draco::IntSqrt</c>.
        /// </summary>
        /// <param name="number">Input value.</param>
        /// <returns>Floor of the square root.</returns>
        public static ulong IntSqrt(ulong number)
        {
            if (number == 0)
            {
                return 0;
            }
            // Start from an estimate of log2(number).
            var actNumber = number;
            ulong squareRoot = 1;
            while (actNumber >= 2)
            {
                squareRoot *= 2;
                actNumber /= 4;
            }
            // Newton's method converges from above after the first iteration, so the
            // squared estimate exceeding the input is a valid termination condition.
            do
            {
                squareRoot = (squareRoot + number / squareRoot) / 2;
            } while (squareRoot * squareRoot > number);
            return squareRoot;
        }
    }

    /// <summary>
    /// Conversion of attribute values into wider integer types.
    /// </summary>
    static unsafe class AttributeValueConversion
    {
        /// <summary>
        /// Reads an attribute value and widens each component to 64 bit.
        /// </summary>
        /// <param name="attribute">Source attribute.</param>
        /// <param name="valueIndex">Attribute value index.</param>
        /// <param name="numComponents">Number of components to read. Components the attribute
        /// does not have are set to zero.</param>
        /// <param name="output">Destination for the widened components.</param>
        public static void ConvertToInt64(
            PointAttribute* attribute, int valueIndex, int numComponents, long* output)
        {
            var address = attribute->GetAddress(valueIndex);
            var available = math.min((int)attribute->numComponents, numComponents);
            for (var i = 0; i < available; ++i)
            {
                output[i] = ReadComponent(address, attribute->dataType, i);
            }
            for (var i = available; i < numComponents; ++i)
            {
                output[i] = 0;
            }
        }

        /// <summary>
        /// Reads an attribute value and narrows or widens each component to 32 bit.
        /// Mirrors <c>GeometryAttribute::ConvertValue&lt;int32_t&gt;</c> for integral types.
        /// </summary>
        /// <param name="attribute">Source attribute.</param>
        /// <param name="valueIndex">Attribute value index.</param>
        /// <param name="numComponents">Number of components to read. Components the attribute
        /// does not have are set to zero.</param>
        /// <param name="output">Destination for the converted components.</param>
        public static void ConvertToInt32(
            PointAttribute* attribute, int valueIndex, int numComponents, int* output)
        {
            var address = attribute->GetAddress(valueIndex);
            var available = math.min((int)attribute->numComponents, numComponents);
            for (var i = 0; i < available; ++i)
            {
                output[i] = attribute->dataType == DataType.Float32
                    ? (int)((float*)address)[i]
                    : (int)ReadComponent(address, attribute->dataType, i);
            }
            for (var i = available; i < numComponents; ++i)
            {
                output[i] = 0;
            }
        }

        static long ReadComponent(byte* address, DataType dataType, int index)
        {
            switch (dataType)
            {
                case DataType.Int8:
                    return ((sbyte*)address)[index];
                case DataType.UInt8:
                    return address[index];
                case DataType.Int16:
                    return ((short*)address)[index];
                case DataType.UInt16:
                    return ((ushort*)address)[index];
                case DataType.Int32:
                    return ((int*)address)[index];
                case DataType.UInt32:
                    return ((uint*)address)[index];
                case DataType.Int64:
                    return ((long*)address)[index];
                case DataType.UInt64:
                    return (long)((ulong*)address)[index];
                case DataType.Float32:
                    return (long)((float*)address)[index];
                case DataType.Float64:
                    return (long)((double*)address)[index];
                default:
                    return 0;
            }
        }
    }
}
