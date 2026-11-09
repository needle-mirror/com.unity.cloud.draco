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
    /// Keeps track of the Shannon entropy of a stream of symbols, allowing the entropy after
    /// a hypothetical addition to be queried without changing the stream.
    /// Mirrors <c>draco::ShannonEntropyTracker</c>.
    /// </summary>
    unsafe struct ShannonEntropyTracker : IDisposable
    {
        /// <summary>
        /// Entropy of the symbols pushed so far.
        /// Mirrors <c>ShannonEntropyTracker::EntropyData</c>.
        /// </summary>
        public struct EntropyData
        {
            public double entropyNorm;
            public int numValues;
            public int maxSymbol;
            public int numUniqueSymbols;
        }

        UnsafeList<int> m_Frequencies;
        EntropyData m_EntropyData;

        /// <summary>
        /// Allocates the frequency table.
        /// </summary>
        public void Init()
        {
            m_Frequencies = new UnsafeList<int>(64, Allocator.Persistent);
            m_EntropyData = new EntropyData();
        }

        /// <summary>
        /// Adds symbols to the stream and returns the updated entropy.
        /// </summary>
        /// <param name="symbols">Symbols to add.</param>
        /// <param name="numSymbols">Number of symbols.</param>
        /// <returns>Entropy of the stream including the new symbols.</returns>
        public EntropyData Push(uint* symbols, int numSymbols)
        {
            return UpdateSymbols(symbols, numSymbols, true);
        }

        /// <summary>
        /// Returns the entropy the stream would have if the symbols were added, leaving the
        /// stream unchanged.
        /// </summary>
        /// <param name="symbols">Symbols to evaluate.</param>
        /// <param name="numSymbols">Number of symbols.</param>
        /// <returns>Hypothetical entropy of the stream.</returns>
        public EntropyData Peek(uint* symbols, int numSymbols)
        {
            return UpdateSymbols(symbols, numSymbols, false);
        }

        EntropyData UpdateSymbols(uint* symbols, int numSymbols, bool pushChanges)
        {
            var result = m_EntropyData;
            result.numValues += numSymbols;
            for (var i = 0; i < numSymbols; ++i)
            {
                var symbol = (int)symbols[i];
                if (m_Frequencies.Length <= symbol)
                {
                    var oldLength = m_Frequencies.Length;
                    m_Frequencies.Resize(symbol + 1, NativeArrayOptions.UninitializedMemory);
                    for (var j = oldLength; j <= symbol; ++j)
                    {
                        m_Frequencies[j] = 0;
                    }
                }

                // Rather than recomputing the whole entropy sum, an entropy norm defined as
                // sum(frequency * log2(frequency)) is tracked, from which the entropy follows
                // as log2(N) - entropyNorm / N. Only the changed symbol contributes.
                var oldSymbolEntropyNorm = 0.0;
                var frequency = m_Frequencies[symbol];
                if (frequency > 1)
                {
                    oldSymbolEntropyNorm = frequency * math.log2((double)frequency);
                }
                else if (frequency == 0)
                {
                    result.numUniqueSymbols++;
                    if (symbol > result.maxSymbol)
                    {
                        result.maxSymbol = symbol;
                    }
                }
                frequency++;
                m_Frequencies[symbol] = frequency;
                var newSymbolEntropyNorm = frequency * math.log2((double)frequency);

                result.entropyNorm += newSymbolEntropyNorm - oldSymbolEntropyNorm;
            }
            if (pushChanges)
            {
                m_EntropyData = result;
            }
            else
            {
                // Only peeking, so revert the frequency table.
                for (var i = 0; i < numSymbols; ++i)
                {
                    var symbol = (int)symbols[i];
                    m_Frequencies[symbol] = m_Frequencies[symbol] - 1;
                }
            }
            return result;
        }

        /// <summary>
        /// Number of bits needed to store the symbols described by <paramref name="entropyData"/>.
        /// </summary>
        /// <param name="entropyData">Entropy of a symbol stream.</param>
        /// <returns>Number of bits.</returns>
        public static long GetNumberOfDataBits(EntropyData entropyData)
        {
            if (entropyData.numValues < 2)
            {
                return 0;
            }
            return (long)math.ceil(
                entropyData.numValues * math.log2((double)entropyData.numValues)
                - entropyData.entropyNorm);
        }

        /// <summary>
        /// Number of bits the rANS frequency table for the given entropy would occupy.
        /// </summary>
        /// <param name="entropyData">Entropy of a symbol stream.</param>
        /// <returns>Number of bits.</returns>
        public static long GetNumberOfRAnsTableBits(EntropyData entropyData)
        {
            return ApproximateRAnsFrequencyTableBits(
                entropyData.maxSymbol + 1, entropyData.numUniqueSymbols);
        }

        static long ApproximateRAnsFrequencyTableBits(int maxValue, int numUniqueSymbols)
        {
            var tableZeroFrequencyBits =
                8L * (numUniqueSymbols + (maxValue - numUniqueSymbols) / 64);
            return 8L * numUniqueSymbols + tableZeroFrequencyBits;
        }

        /// <summary>
        /// Shannon entropy of a sequence of Boolean values, between zero and one.
        /// Mirrors <c>draco::ComputeBinaryShannonEntropy</c>.
        /// </summary>
        /// <param name="numValues">Total number of values.</param>
        /// <param name="numTrueValues">Number of values that are true.</param>
        /// <returns>Entropy between zero and one.</returns>
        public static double ComputeBinaryShannonEntropy(uint numValues, uint numTrueValues)
        {
            if (numValues == 0)
            {
                return 0;
            }
            // Zero entropy data sets can exit early.
            if (numTrueValues == 0 || numValues == numTrueValues)
            {
                return 0;
            }
            var trueFrequency = (double)numTrueValues / numValues;
            var falseFrequency = 1.0 - trueFrequency;
            return -(trueFrequency * math.log2(trueFrequency)
                + falseFrequency * math.log2(falseFrequency));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_Frequencies.IsCreated)
            {
                m_Frequencies.Dispose();
            }
        }
    }

    /// <summary>
    /// Turns attribute values into correction values that compress better.
    /// Combines Draco's prediction scheme encoder hierarchy and its encoding transforms into
    /// a single dispatchable value type, matching <see cref="PredictionScheme"/> on the
    /// decoding side.
    /// </summary>
    unsafe struct PredictionSchemeEncoder : IDisposable
    {
        const int k_MaxNumParallelograms = 4;

        /// <summary>Prediction method to apply.</summary>
        public PredictionSchemeMethod method;
        /// <summary>Transform applied to the correction values.</summary>
        public PredictionSchemeTransformType transformType;

        // Wrap transform state.
        int m_NumComponents;
        int m_MinValue;
        int m_MaxValue;
        int m_MaxDif;
        int m_MinCorrection;
        int m_MaxCorrection;
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
        ShannonEntropyTracker m_EntropyTracker;

        // Texture coordinate predictor state.
        UnsafeList<bool> m_Orientations;
        [NativeDisableUnsafePtrRestriction]
        PointAttribute* m_PositionAttribute;

        // Geometric normal predictor state.
        RAnsBitEncoder m_FlipNormalBitEncoder;
        OctahedronToolBox m_PredictorOctahedron;

        // Scratch buffers.
        UnsafeList<int> m_Scratch;

        /// <summary>
        /// Creates a prediction scheme encoder for the given method and transform.
        /// </summary>
        /// <param name="predictionMethod">Prediction method to apply.</param>
        /// <param name="transform">Transform to apply to the correction values.</param>
        /// <param name="meshData">Mesh connectivity, or a default value for non-mesh schemes.</param>
        /// <returns>An initialized prediction scheme encoder.</returns>
        public static PredictionSchemeEncoder Create(
            PredictionSchemeMethod predictionMethod,
            PredictionSchemeTransformType transform,
            MeshPredictionSchemeData meshData
            )
        {
            return new PredictionSchemeEncoder
            {
                method = predictionMethod,
                transformType = transform,
                m_MeshData = meshData,
                m_TransformOctahedron = OctahedronToolBox.CreateUninitialized(),
                m_PredictorOctahedron = OctahedronToolBox.CreateUninitialized()
            };
        }

        /// <summary>
        /// Configures the maximum quantized value of the octahedron transforms.
        /// </summary>
        /// <param name="maxQuantizedValue">Maximum quantized value, of the form 2^b-1.</param>
        /// <returns>False if the value has the wrong form.</returns>
        public bool SetMaxQuantizedValue(int maxQuantizedValue)
        {
            if (maxQuantizedValue % 2 == 0)
            {
                return false;
            }
            var q = BitUtils.MostSignificantBit((uint)maxQuantizedValue) + 1;
            return m_TransformOctahedron.SetQuantizationBits(q);
        }

        /// <summary>
        /// True when the scheme is usable with the data it was given.
        /// </summary>
        /// <remarks>
        /// The parent position attribute is deliberately not required here. It is resolved much
        /// later, in <c>SequentialAttributeEncoder.EncodeIntegerValues</c>, because only then
        /// does the portable form of the position attribute exist. This property is read right
        /// after <see cref="Create"/>, so requiring the parent would reject
        /// <see cref="PredictionSchemeMethod.MeshTexCoordsPortable"/> and
        /// <see cref="PredictionSchemeMethod.MeshGeometricNormal"/> unconditionally and
        /// silently degrade them to difference prediction. Failing to resolve the parent later
        /// is handled there.
        /// </remarks>
        public bool IsInitialized
        {
            get
            {
                switch (method)
                {
                    case PredictionSchemeMethod.Difference:
                        return true;
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

        void TransformInit(int* originalData, int size, int numComponents)
        {
            m_NumComponents = numComponents;
            if (transformType != PredictionSchemeTransformType.Wrap)
            {
                return;
            }
            if (!m_ClampedValue.IsCreated)
            {
                m_ClampedValue = new UnsafeList<int>(numComponents, Allocator.Persistent);
            }
            m_ClampedValue.Resize(numComponents, NativeArrayOptions.ClearMemory);
            if (size == 0)
            {
                return;
            }
            // The value range of the input is needed by the decoder to unwrap corrections.
            var minValue = originalData[0];
            var maxValue = minValue;
            for (var i = 1; i < size; ++i)
            {
                if (originalData[i] < minValue)
                {
                    minValue = originalData[i];
                }
                else if (originalData[i] > maxValue)
                {
                    maxValue = originalData[i];
                }
            }
            m_MinValue = minValue;
            m_MaxValue = maxValue;
            InitCorrectionBounds();
        }

        bool InitCorrectionBounds()
        {
            var dif = (long)m_MaxValue - m_MinValue;
            if (dif < 0 || dif >= int.MaxValue)
            {
                return false;
            }
            m_MaxDif = 1 + (int)dif;
            m_MaxCorrection = m_MaxDif / 2;
            m_MinCorrection = -m_MaxCorrection;
            if ((m_MaxDif & 1) == 0)
            {
                m_MaxCorrection -= 1;
            }
            return true;
        }

        /// <summary>
        /// Writes the transform specific data to the bit-stream.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False for an unsupported transform.</returns>
        bool EncodeTransformData(ref EncoderBuffer buffer)
        {
            switch (transformType)
            {
                case PredictionSchemeTransformType.Wrap:
                    buffer.Encode(m_MinValue);
                    buffer.Encode(m_MaxValue);
                    return true;
                case PredictionSchemeTransformType.NormalOctahedronCanonicalized:
                    buffer.Encode(m_TransformOctahedron.MaxQuantizedValue);
                    buffer.Encode(m_TransformOctahedron.CenterValue);
                    return true;
                default:
                    return false;
            }
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
        void ComputeCorrection(int* originalValues, int* predictedValues, int* outCorrections)
        {
            switch (transformType)
            {
                case PredictionSchemeTransformType.Wrap:
                {
                    var clamped = ClampPredictedValue(predictedValues);
                    for (var i = 0; i < m_NumComponents; ++i)
                    {
                        var correction = originalValues[i] - clamped[i];
                        // Wrap out of bound corrections around the value range.
                        if (correction < m_MinCorrection)
                        {
                            correction += m_MaxDif;
                        }
                        else if (correction > m_MaxCorrection)
                        {
                            correction -= m_MaxDif;
                        }
                        outCorrections[i] = correction;
                    }
                    break;
                }
                case PredictionSchemeTransformType.NormalOctahedronCanonicalized:
                {
                    ComputeCorrectionOctahedronCanonicalized(
                        originalValues[0], originalValues[1],
                        predictedValues[0], predictedValues[1],
                        out outCorrections[0], out outCorrections[1]);
                    break;
                }
            }
        }

        void ComputeCorrectionOctahedronCanonicalized(
            int origS, int origT, int predS, int predT, out int corrS, out int corrT)
        {
            var center = m_TransformOctahedron.CenterValue;
            var oS = origS - center;
            var oT = origT - center;
            var pS = predS - center;
            var pT = predT - center;

            if (!m_TransformOctahedron.IsInDiamond(pS, pT))
            {
                m_TransformOctahedron.InvertDiamond(ref oS, ref oT);
                m_TransformOctahedron.InvertDiamond(ref pS, ref pT);
            }
            if (!IsInBottomLeft(pS, pT))
            {
                var rotationCount = GetRotationCount(pS, pT);
                RotatePoint(oS, oT, rotationCount, out oS, out oT);
                RotatePoint(pS, pT, rotationCount, out pS, out pT);
            }

            corrS = m_TransformOctahedron.MakePositive(oS - pS);
            corrT = m_TransformOctahedron.MakePositive(oT - pT);
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

        #region Correction values

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

        /// <summary>
        /// Applies the prediction, turning attribute values into correction values.
        /// </summary>
        /// <param name="inData">Portable attribute values in encoding order.</param>
        /// <param name="outCorrections">Destination for the correction values.</param>
        /// <param name="size">Total number of components across all entries.</param>
        /// <param name="numComponents">Number of components per entry.</param>
        /// <param name="entryToPointIdMap">Maps entries onto point indices.</param>
        /// <returns>False when the prediction cannot be computed.</returns>
        public bool ComputeCorrectionValues(
            int* inData,
            int* outCorrections,
            int size,
            int numComponents,
            int* entryToPointIdMap
            )
        {
            switch (method)
            {
                case PredictionSchemeMethod.Difference:
                    return ComputeCorrectionValuesDelta(
                        inData, outCorrections, size, numComponents);
                case PredictionSchemeMethod.MeshParallelogram:
                    return ComputeCorrectionValuesParallelogram(
                        inData, outCorrections, size, numComponents);
                case PredictionSchemeMethod.MeshConstrainedMultiParallelogram:
                    return ComputeCorrectionValuesConstrainedMultiParallelogram(
                        inData, outCorrections, size, numComponents);
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                    return ComputeCorrectionValuesTexCoordsPortable(
                        inData, outCorrections, size, numComponents, entryToPointIdMap);
                case PredictionSchemeMethod.MeshGeometricNormal:
                    return ComputeCorrectionValuesGeometricNormal(
                        inData, outCorrections, size, numComponents, entryToPointIdMap);
                default:
                    return false;
            }
        }

        bool ComputeCorrectionValuesDelta(
            int* inData, int* outCorrections, int size, int numComponents)
        {
            TransformInit(inData, size, numComponents);
            // Encode from the back using D(i) = D(i) - D(i - 1).
            for (var i = size - numComponents; i > 0; i -= numComponents)
            {
                ComputeCorrection(inData + i, inData + i - numComponents, outCorrections + i);
            }
            // The first element has no predecessor and is predicted with zero.
            var zeroValues = GetScratch(numComponents);
            UnsafeUtility.MemClear(zeroValues, (long)numComponents * sizeof(int));
            ComputeCorrection(inData, zeroValues, outCorrections);
            return true;
        }

        bool ComputeCorrectionValuesParallelogram(
            int* inData, int* outCorrections, int size, int numComponents)
        {
            TransformInit(inData, size, numComponents);
            var predictedValues = GetScratch(numComponents);
            UnsafeUtility.MemClear(predictedValues, (long)numComponents * sizeof(int));

            var table = m_MeshData.cornerTable;
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;

            // Processing starts at the end because the prediction reads entries that would be
            // overwritten once they are processed.
            for (var p = m_MeshData.dataToCornerMap->Length - 1; p > 0; --p)
            {
                var cornerId = dataToCornerMap[p];
                var dstOffset = p * numComponents;
                if (!PredictionScheme.ComputeParallelogramPrediction(
                        p, cornerId, ref table, vertexToDataMap, inData,
                        numComponents, predictedValues))
                {
                    // The parallelogram is not available, for example because some of its
                    // vertices are not encoded yet. Fall back to delta coding.
                    var srcOffset = (p - 1) * numComponents;
                    ComputeCorrection(
                        inData + dstOffset, inData + srcOffset, outCorrections + dstOffset);
                }
                else
                {
                    ComputeCorrection(
                        inData + dstOffset, predictedValues, outCorrections + dstOffset);
                }
            }
            // The first element cannot be predicted.
            UnsafeUtility.MemClear(predictedValues, (long)numComponents * sizeof(int));
            ComputeCorrection(inData, predictedValues, outCorrections);
            return true;
        }

        #endregion

        #region Constrained multi-parallelogram

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

        /// <summary>
        /// Error of a candidate prediction, primarily measured in bits needed to store it.
        /// Mirrors the <c>Error</c> struct of the reference encoder.
        /// </summary>
        struct PredictionError
        {
            public long numBits;
            public int residualError;

            public bool IsBetterThan(PredictionError other)
            {
                if (numBits < other.numBits)
                {
                    return true;
                }
                if (numBits > other.numBits)
                {
                    return false;
                }
                return residualError < other.residualError;
            }
        }

        PredictionError ComputeError(
            int* predictedValues,
            int* actualValues,
            int* outResiduals,
            uint* entropySymbols,
            int numComponents
            )
        {
            var error = new PredictionError();
            for (var i = 0; i < numComponents; ++i)
            {
                var dif = predictedValues[i] - actualValues[i];
                error.residualError += (int)math.abs(dif);
                outResiduals[i] = dif;
                // Entropy coding needs unsigned symbols.
                entropySymbols[i] = BitUtils.ConvertSignedIntToSymbol(dif);
            }

            // Evaluate the entropy as if this configuration was chosen, without committing.
            var entropyData = m_EntropyTracker.Peek(entropySymbols, numComponents);
            error.numBits = ShannonEntropyTracker.GetNumberOfDataBits(entropyData)
                + ShannonEntropyTracker.GetNumberOfRAnsTableBits(entropyData);
            return error;
        }

        /// <summary>
        /// Number of bits needed to store which parallelograms were selected.
        /// Mirrors <c>ComputeOverheadBits</c>.
        /// </summary>
        static long ComputeOverheadBits(long totalUsedParallelograms, long totalParallelograms)
        {
            // rANS coding is assumed, where the size correlates with the binary entropy.
            var entropy = ShannonEntropyTracker.ComputeBinaryShannonEntropy(
                (uint)totalParallelograms, (uint)totalUsedParallelograms);
            return (long)math.ceil(totalParallelograms * entropy);
        }

        bool ComputeCorrectionValuesConstrainedMultiParallelogram(
            int* inData, int* outCorrections, int size, int numComponents)
        {
            TransformInit(inData, size, numComponents);
            if (!m_IsCreaseEdge0.IsCreated)
            {
                for (var i = 0; i < k_MaxNumParallelograms; ++i)
                {
                    SetCreaseEdgeList(i, new UnsafeList<bool>(16, Allocator.Persistent));
                }
                m_EntropyTracker.Init();
            }

            var table = m_MeshData.cornerTable;
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;

            // Predicted values of every simple parallelogram found at a vertex.
            var predValues = (int*)UnsafeUtility.Malloc(
                (long)k_MaxNumParallelograms * numComponents * sizeof(int), 4, Allocator.Temp);
            var multiPredValues = (int*)UnsafeUtility.Malloc(
                (long)numComponents * sizeof(int), 4, Allocator.Temp);
            var bestPredictedValue = (int*)UnsafeUtility.Malloc(
                (long)numComponents * sizeof(int), 4, Allocator.Temp);
            var currentResiduals = (int*)UnsafeUtility.Malloc(
                (long)numComponents * sizeof(int), 4, Allocator.Temp);
            var bestResiduals = (int*)UnsafeUtility.Malloc(
                (long)numComponents * sizeof(int), 4, Allocator.Temp);
            var entropySymbols = (uint*)UnsafeUtility.Malloc(
                (long)numComponents * sizeof(uint), 4, Allocator.Temp);
            var excludedParallelograms = stackalloc bool[k_MaxNumParallelograms];

            var totalUsedParallelograms = stackalloc long[k_MaxNumParallelograms];
            var totalParallelograms = stackalloc long[k_MaxNumParallelograms];
            for (var i = 0; i < k_MaxNumParallelograms; ++i)
            {
                totalUsedParallelograms[i] = 0;
                totalParallelograms[i] = 0;
            }

            // Processing starts at the end because the prediction reads entries that would be
            // overwritten once they are processed.
            for (var p = m_MeshData.dataToCornerMap->Length - 1; p > 0; --p)
            {
                var startCornerId = dataToCornerMap[p];

                // Collect the parallelograms defined by the faces opposite to the corners
                // attached to the vertex.
                var cornerId = startCornerId;
                var numParallelograms = 0;
                var firstPass = true;
                while (cornerId != DracoConstants.invalidCornerIndex)
                {
                    if (PredictionScheme.ComputeParallelogramPrediction(
                            p, cornerId, ref table, vertexToDataMap, inData, numComponents,
                            predValues + numParallelograms * numComponents))
                    {
                        ++numParallelograms;
                        if (numParallelograms == k_MaxNumParallelograms)
                        {
                            break;
                        }
                    }

                    // Advance to the next corner of the vertex, swinging left first and
                    // switching to swinging right once a boundary is reached.
                    cornerId = firstPass ? table.SwingLeft(cornerId) : table.SwingRight(cornerId);
                    if (cornerId == startCornerId)
                    {
                        break;
                    }
                    if (cornerId == DracoConstants.invalidCornerIndex && firstPass)
                    {
                        firstPass = false;
                        cornerId = table.SwingRight(startCornerId);
                    }
                }

                var dstOffset = p * numComponents;

                // Start with the delta coding configuration, where no parallelogram is used.
                var srcOffset = (p - 1) * numComponents;
                var error = ComputeError(
                    inData + srcOffset, inData + dstOffset, currentResiduals,
                    entropySymbols, numComponents);

                if (numParallelograms > 0)
                {
                    totalParallelograms[numParallelograms - 1] += numParallelograms;
                    error.numBits += ComputeOverheadBits(
                        totalUsedParallelograms[numParallelograms - 1],
                        totalParallelograms[numParallelograms - 1]);
                }

                var bestError = error;
                var bestConfiguration = 0;
                var bestNumUsedParallelograms = 0;
                UnsafeUtility.MemCpy(
                    bestPredictedValue, inData + srcOffset, (long)numComponents * sizeof(int));
                UnsafeUtility.MemCpy(
                    bestResiduals, currentResiduals, (long)numComponents * sizeof(int));

                // Evaluate every combination of the available parallelograms.
                for (var numUsedParallelograms = 1;
                    numUsedParallelograms <= numParallelograms;
                    ++numUsedParallelograms)
                {
                    for (var j = 0; j < numParallelograms; ++j)
                    {
                        excludedParallelograms[j] = true;
                    }
                    for (var j = 0; j < numUsedParallelograms; ++j)
                    {
                        excludedParallelograms[j] = false;
                    }
                    // Permute over the excluded parallelograms.
                    do
                    {
                        for (var j = 0; j < numComponents; ++j)
                        {
                            multiPredValues[j] = 0;
                        }
                        var configuration = 0;
                        for (var j = 0; j < numParallelograms; ++j)
                        {
                            if (excludedParallelograms[j])
                            {
                                continue;
                            }
                            for (var c = 0; c < numComponents; ++c)
                            {
                                multiPredValues[c] += predValues[j * numComponents + c];
                            }
                            configuration |= 1 << j;
                        }

                        for (var j = 0; j < numComponents; ++j)
                        {
                            multiPredValues[j] /= numUsedParallelograms;
                        }
                        error = ComputeError(
                            multiPredValues, inData + dstOffset, currentResiduals,
                            entropySymbols, numComponents);
                        error.numBits += ComputeOverheadBits(
                            totalUsedParallelograms[numParallelograms - 1] + numUsedParallelograms,
                            totalParallelograms[numParallelograms - 1]);

                        if (error.IsBetterThan(bestError))
                        {
                            bestError = error;
                            bestConfiguration = configuration;
                            bestNumUsedParallelograms = numUsedParallelograms;
                            UnsafeUtility.MemCpy(
                                bestPredictedValue, multiPredValues,
                                (long)numComponents * sizeof(int));
                            UnsafeUtility.MemCpy(
                                bestResiduals, currentResiduals,
                                (long)numComponents * sizeof(int));
                        }
                    } while (NextPermutation(excludedParallelograms, numParallelograms));
                }
                if (numParallelograms > 0)
                {
                    totalUsedParallelograms[numParallelograms - 1] += bestNumUsedParallelograms;
                }

                // Commit the residuals of the chosen configuration to the entropy stream.
                for (var i = 0; i < numComponents; ++i)
                {
                    entropySymbols[i] = BitUtils.ConvertSignedIntToSymbol(bestResiduals[i]);
                }
                m_EntropyTracker.Push(entropySymbols, numComponents);

                for (var i = 0; i < numParallelograms; ++i)
                {
                    // An unused parallelogram marks its edge as a crease.
                    var isCrease = (bestConfiguration & (1 << i)) == 0;
                    var list = GetCreaseEdgeList(numParallelograms - 1);
                    list.Add(isCrease);
                    SetCreaseEdgeList(numParallelograms - 1, list);
                }
                ComputeCorrection(
                    inData + dstOffset, bestPredictedValue, outCorrections + dstOffset);
            }
            // The first element cannot be predicted.
            for (var i = 0; i < numComponents; ++i)
            {
                predValues[i] = 0;
            }
            ComputeCorrection(inData, predValues, outCorrections);

            UnsafeUtility.Free(predValues, Allocator.Temp);
            UnsafeUtility.Free(multiPredValues, Allocator.Temp);
            UnsafeUtility.Free(bestPredictedValue, Allocator.Temp);
            UnsafeUtility.Free(currentResiduals, Allocator.Temp);
            UnsafeUtility.Free(bestResiduals, Allocator.Temp);
            UnsafeUtility.Free(entropySymbols, Allocator.Temp);
            return true;
        }

        /// <summary>
        /// Rearranges a range of flags into the next lexicographically larger permutation,
        /// treating false as smaller than true.
        /// Mirrors <c>std::next_permutation</c>, which the reference encoder relies on.
        /// </summary>
        /// <param name="values">Flags to permute.</param>
        /// <param name="count">Number of flags.</param>
        /// <returns>False once the permutations wrapped around to the smallest one.</returns>
        static bool NextPermutation(bool* values, int count)
        {
            if (count < 2)
            {
                return false;
            }
            var i = count - 1;
            while (true)
            {
                var j = i;
                --i;
                // Ordering of Boolean values: false is smaller than true.
                if (!values[i] && values[j])
                {
                    var k = count;
                    while (!(!values[i] && values[--k]))
                    {
                        // Search backwards for the first value larger than values[i].
                    }
                    var tmp = values[i];
                    values[i] = values[k];
                    values[k] = tmp;
                    Reverse(values, j, count);
                    return true;
                }
                if (i == 0)
                {
                    Reverse(values, 0, count);
                    return false;
                }
            }
        }

        static void Reverse(bool* values, int first, int last)
        {
            while (first < --last)
            {
                var tmp = values[first];
                values[first] = values[last];
                values[last] = tmp;
                ++first;
            }
        }

        #endregion

        #region Texture coordinate prediction

        void GetPositionForEntryId(int entryId, int* entryToPointIdMap, long* outPosition)
        {
            var pointId = entryToPointIdMap[entryId];
            var valueIndex = m_PositionAttribute->MappedIndex(pointId);
            AttributeValueConversion.ConvertToInt64(m_PositionAttribute, valueIndex, 3, outPosition);
        }

        bool ComputeCorrectionValuesTexCoordsPortable(
            int* inData, int* outCorrections, int size, int numComponents, int* entryToPointIdMap)
        {
            if (numComponents != 2)
            {
                return false;
            }
            TransformInit(inData, size, numComponents);
            if (!m_Orientations.IsCreated)
            {
                m_Orientations = new UnsafeList<bool>(64, Allocator.Persistent);
            }

            var predictedValue = stackalloc int[2];
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;

            // Processing starts at the end because the prediction reads entries that would be
            // overwritten once they are processed.
            for (var p = m_MeshData.dataToCornerMap->Length - 1; p >= 0; --p)
            {
                var cornerId = dataToCornerMap[p];
                if (!ComputePredictedTexCoord(
                        cornerId, inData, p, entryToPointIdMap, predictedValue))
                {
                    return false;
                }
                var dstOffset = p * numComponents;
                ComputeCorrection(
                    inData + dstOffset, predictedValue, outCorrections + dstOffset);
            }
            return true;
        }

        bool ComputePredictedTexCoord(
            int cornerId,
            int* data,
            int dataId,
            int* entryToPointIdMap,
            int* predictedValue
            )
        {
            var table = m_MeshData.cornerTable;
            var nextCornerId = CornerTable.Next(cornerId);
            var prevCornerId = CornerTable.Previous(cornerId);

            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var nextDataId = vertexToDataMap[table.Vertex(nextCornerId)];
            var prevDataId = vertexToDataMap[table.Vertex(prevCornerId)];

            if (prevDataId < dataId && nextDataId < dataId)
            {
                // Both neighbouring corners already have encoded UV coordinates.
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

                    var pnAbsMax = MathUtils.Max(
                        MathUtils.Max(MathUtils.Abs(pnX), MathUtils.Abs(pnY)), MathUtils.Abs(pnZ));
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

                    // Both orientations are evaluated and the better one is stored so the
                    // decoder can reproduce the choice.
                    var predicted0X = (xUvX + cxUvX) / (long)pnNorm2Squared;
                    var predicted0Y = (xUvY + cxUvY) / (long)pnNorm2Squared;
                    var predicted1X = (xUvX - cxUvX) / (long)pnNorm2Squared;
                    var predicted1Y = (xUvY - cxUvY) / (long)pnNorm2Squared;

                    var cUvX = (long)data[dataId * 2];
                    var cUvY = (long)data[dataId * 2 + 1];
                    var dif0X = cUvX - predicted0X;
                    var dif0Y = cUvY - predicted0Y;
                    var dif1X = cUvX - predicted1X;
                    var dif1Y = cUvY - predicted1Y;
                    var norm0 = dif0X * dif0X + dif0Y * dif0Y;
                    var norm1 = dif1X * dif1X + dif1Y * dif1Y;

                    if (norm0 < norm1)
                    {
                        predictedValue[0] = (int)predicted0X;
                        predictedValue[1] = (int)predicted0Y;
                        m_Orientations.Add(true);
                    }
                    else
                    {
                        predictedValue[0] = (int)predicted1X;
                        predictedValue[1] = (int)predicted1Y;
                        m_Orientations.Add(false);
                    }
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

        bool ComputeCorrectionValuesGeometricNormal(
            int* inData, int* outCorrections, int size, int numComponents, int* entryToPointIdMap)
        {
            if (numComponents != 2)
            {
                return false;
            }
            m_PredictorOctahedron.SetQuantizationBits(m_TransformOctahedron.QuantizationBits);
            TransformInit(inData, size, numComponents);

            var flipEncoder = (RAnsBitEncoder*)UnsafeUtility.AddressOf(ref m_FlipNormalBitEncoder);
            flipEncoder->StartEncoding();

            var cornerMapSize = m_MeshData.dataToCornerMap->Length;
            var dataToCornerMap = m_MeshData.dataToCornerMap->Ptr;

            var predNormal3d = stackalloc int[3];
            var posPredNormalOct = stackalloc int[2];
            var negPredNormalOct = stackalloc int[2];
            var posCorrection = stackalloc int[2];
            var negCorrection = stackalloc int[2];

            for (var dataId = 0; dataId < cornerMapSize; ++dataId)
            {
                var cornerId = dataToCornerMap[dataId];
                ComputePredictedNormal(cornerId, entryToPointIdMap, predNormal3d);

                m_PredictorOctahedron.CanonicalizeIntegerVector(predNormal3d);

                // Octahedral coordinates of both possible directions.
                m_PredictorOctahedron.IntegerVectorToQuantizedOctahedralCoords(
                    predNormal3d, out posPredNormalOct[0], out posPredNormalOct[1]);
                predNormal3d[0] = -predNormal3d[0];
                predNormal3d[1] = -predNormal3d[1];
                predNormal3d[2] = -predNormal3d[2];
                m_PredictorOctahedron.IntegerVectorToQuantizedOctahedralCoords(
                    predNormal3d, out negPredNormalOct[0], out negPredNormalOct[1]);

                // Keep the direction with the smaller correction.
                var dataOffset = dataId * 2;
                ComputeCorrection(inData + dataOffset, posPredNormalOct, posCorrection);
                ComputeCorrection(inData + dataOffset, negPredNormalOct, negCorrection);
                posCorrection[0] = m_PredictorOctahedron.ModMax(posCorrection[0]);
                posCorrection[1] = m_PredictorOctahedron.ModMax(posCorrection[1]);
                negCorrection[0] = m_PredictorOctahedron.ModMax(negCorrection[0]);
                negCorrection[1] = m_PredictorOctahedron.ModMax(negCorrection[1]);

                var posAbsSum = math.abs(posCorrection[0]) + math.abs(posCorrection[1]);
                var negAbsSum = math.abs(negCorrection[0]) + math.abs(negCorrection[1]);
                if (posAbsSum < negAbsSum)
                {
                    flipEncoder->EncodeBit(false);
                    outCorrections[dataOffset] = m_PredictorOctahedron.MakePositive(posCorrection[0]);
                    outCorrections[dataOffset + 1] = m_PredictorOctahedron.MakePositive(posCorrection[1]);
                }
                else
                {
                    flipEncoder->EncodeBit(true);
                    outCorrections[dataOffset] = m_PredictorOctahedron.MakePositive(negCorrection[0]);
                    outCorrections[dataOffset + 1] = m_PredictorOctahedron.MakePositive(negCorrection[1]);
                }
            }
            return true;
        }

        void GetPositionForCorner(int corner, int* entryToPointIdMap, long* outPosition)
        {
            var vertexToDataMap = m_MeshData.vertexToDataMap->Ptr;
            var dataId = vertexToDataMap[m_MeshData.cornerTable.Vertex(corner)];
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
                        break;
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

            // Scale the accumulated normal down so that it fits into 32 bit components.
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

        #region Prediction data

        /// <summary>
        /// Writes the prediction specific data to the bit-stream.
        /// </summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <returns>False on an unsupported configuration.</returns>
        public bool EncodePredictionData(ref EncoderBuffer buffer)
        {
            switch (method)
            {
                case PredictionSchemeMethod.MeshConstrainedMultiParallelogram:
                {
                    // Crease edges of every context, using a separate rANS coder each.
                    for (var i = 0; i < k_MaxNumParallelograms; ++i)
                    {
                        var list = GetCreaseEdgeList(i);
                        var count = list.IsCreated ? list.Length : 0;
                        buffer.EncodeVarintU32((uint)count);
                        if (count == 0)
                        {
                            continue;
                        }
                        // The context index plus one is the number of edges stored per vertex.
                        var numUsedParallelograms = i + 1;
                        var encoder = new RAnsBitEncoder();
                        encoder.StartEncoding();
                        // The decoder consumes the vertices in reverse order.
                        for (var j = count - numUsedParallelograms; j >= 0; j -= numUsedParallelograms)
                        {
                            for (var k = 0; k < numUsedParallelograms; ++k)
                            {
                                encoder.EncodeBit(list[j + k]);
                            }
                        }
                        encoder.EndEncoding(ref buffer);
                        encoder.Dispose();
                    }
                    return EncodeTransformData(ref buffer);
                }
                case PredictionSchemeMethod.MeshTexCoordsPortable:
                {
                    // Delta coded orientations, using arithmetic coding.
                    var numOrientations = m_Orientations.IsCreated ? m_Orientations.Length : 0;
                    buffer.Encode(numOrientations);
                    var lastOrientation = true;
                    var encoder = new RAnsBitEncoder();
                    encoder.StartEncoding();
                    for (var i = 0; i < numOrientations; ++i)
                    {
                        var orientation = m_Orientations[i];
                        encoder.EncodeBit(orientation == lastOrientation);
                        lastOrientation = orientation;
                    }
                    encoder.EndEncoding(ref buffer);
                    encoder.Dispose();
                    return EncodeTransformData(ref buffer);
                }
                case PredictionSchemeMethod.MeshGeometricNormal:
                {
                    // The geometric normal scheme writes the transform data first.
                    if (!EncodeTransformData(ref buffer))
                    {
                        return false;
                    }
                    var flipEncoder = (RAnsBitEncoder*)UnsafeUtility.AddressOf(ref m_FlipNormalBitEncoder);
                    flipEncoder->EndEncoding(ref buffer);
                    return true;
                }
                default:
                    return EncodeTransformData(ref buffer);
            }
        }

        #endregion

        /// <inheritdoc />
        public void Dispose()
        {
            if (m_ClampedValue.IsCreated)
            {
                m_ClampedValue.Dispose();
            }
            if (m_Scratch.IsCreated)
            {
                m_Scratch.Dispose();
            }
            if (m_Orientations.IsCreated)
            {
                m_Orientations.Dispose();
            }
            for (var i = 0; i < k_MaxNumParallelograms; ++i)
            {
                var list = GetCreaseEdgeList(i);
                if (list.IsCreated)
                {
                    list.Dispose();
                    SetCreaseEdgeList(i, default);
                }
            }
            m_EntropyTracker.Dispose();
            m_FlipNormalBitEncoder.Dispose();
        }
    }
}
