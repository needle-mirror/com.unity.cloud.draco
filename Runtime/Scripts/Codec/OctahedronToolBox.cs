// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Converts unit vectors to octahedral coordinates and back.
    /// Mirrors <c>draco::OctahedronToolBox</c>.
    /// </summary>
    struct OctahedronToolBox
    {
        int m_QuantizationBits;
        int m_MaxQuantizedValue;
        int m_MaxValue;
        float m_DequantizationScale;
        int m_CenterValue;

        /// <summary>Number of quantization bits, or -1 if uninitialized.</summary>
        public int QuantizationBits => m_QuantizationBits;
        /// <summary>Largest value representable with the configured bit count.</summary>
        public int MaxQuantizedValue => m_MaxQuantizedValue;
        /// <summary>Coordinate of the diamond center after the shift.</summary>
        public int CenterValue => m_CenterValue;

        /// <summary>
        /// Configures the quantization precision.
        /// </summary>
        /// <param name="q">Number of quantization bits, between 2 and 30.</param>
        /// <returns>False if <paramref name="q"/> is out of range.</returns>
        public bool SetQuantizationBits(int q)
        {
            if (q < 2 || q > 30)
            {
                return false;
            }
            m_QuantizationBits = q;
            m_MaxQuantizedValue = (int)((1u << q) - 1);
            m_MaxValue = m_MaxQuantizedValue - 1;
            m_DequantizationScale = 2f / m_MaxValue;
            m_CenterValue = m_MaxValue / 2;
            return true;
        }

        /// <summary>
        /// Creates an uninitialized instance.
        /// </summary>
        /// <returns>An uninitialized tool box.</returns>
        public static OctahedronToolBox CreateUninitialized()
        {
            return new OctahedronToolBox
            {
                m_QuantizationBits = -1,
                m_MaxQuantizedValue = -1,
                m_MaxValue = -1,
                m_DequantizationScale = 1f,
                m_CenterValue = -1
            };
        }

        /// <summary>
        /// Maps edge points of the top left and bottom right quadrants onto their
        /// counterparts in the bottom left and top right quadrants.
        /// </summary>
        /// <param name="s">Input s coordinate.</param>
        /// <param name="t">Input t coordinate.</param>
        /// <param name="outS">Canonicalized s coordinate.</param>
        /// <param name="outT">Canonicalized t coordinate.</param>
        public void CanonicalizeOctahedralCoords(int s, int t, out int outS, out int outT)
        {
            if ((s == 0 && t == 0) || (s == 0 && t == m_MaxValue) || (s == m_MaxValue && t == 0))
            {
                s = m_MaxValue;
                t = m_MaxValue;
            }
            else if (s == 0 && t > m_CenterValue)
            {
                t = m_CenterValue - (t - m_CenterValue);
            }
            else if (s == m_MaxValue && t < m_CenterValue)
            {
                t = m_CenterValue + (m_CenterValue - t);
            }
            else if (t == m_MaxValue && s < m_CenterValue)
            {
                s = m_CenterValue + (m_CenterValue - s);
            }
            else if (t == 0 && s > m_CenterValue)
            {
                s = m_CenterValue - (s - m_CenterValue);
            }

            outS = s;
            outT = t;
        }

        /// <summary>
        /// Converts an integer vector whose absolute component sum equals the center value
        /// into quantized octahedral coordinates.
        /// </summary>
        /// <param name="intVec">Input vector with three components.</param>
        /// <param name="outS">Resulting s coordinate.</param>
        /// <param name="outT">Resulting t coordinate.</param>
        public unsafe void IntegerVectorToQuantizedOctahedralCoords(int* intVec, out int outS, out int outT)
        {
            int s, t;
            if (intVec[0] >= 0)
            {
                // Right hemisphere.
                s = intVec[1] + m_CenterValue;
                t = intVec[2] + m_CenterValue;
            }
            else
            {
                // Left hemisphere.
                s = intVec[1] < 0 ? math.abs(intVec[2]) : m_MaxValue - math.abs(intVec[2]);
                t = intVec[2] < 0 ? math.abs(intVec[1]) : m_MaxValue - math.abs(intVec[1]);
            }
            CanonicalizeOctahedralCoords(s, t, out outS, out outT);
        }

        /// <summary>
        /// Projects a floating point vector onto the octahedron and quantizes it.
        /// </summary>
        /// <param name="vector">Input vector with three components.</param>
        /// <param name="outS">Resulting s coordinate.</param>
        /// <param name="outT">Resulting t coordinate.</param>
        public unsafe void FloatVectorToQuantizedOctahedralCoords(float* vector, out int outS, out int outT)
        {
            var absSum = math.abs((double)vector[0]) + math.abs((double)vector[1]) + math.abs((double)vector[2]);

            double3 scaledVector;
            if (absSum > 1e-6)
            {
                var scale = 1.0 / absSum;
                scaledVector = new double3(vector[0] * scale, vector[1] * scale, vector[2] * scale);
            }
            else
            {
                scaledVector = new double3(1.0, 0.0, 0.0);
            }

            var intVec = stackalloc int[3];
            intVec[0] = (int)math.floor(scaledVector.x * m_CenterValue + 0.5);
            intVec[1] = (int)math.floor(scaledVector.y * m_CenterValue + 0.5);
            // Make sure the absolute sum is exactly the center value.
            intVec[2] = m_CenterValue - math.abs(intVec[0]) - math.abs(intVec[1]);
            if (intVec[2] < 0)
            {
                // The first two coordinates are too large, shorten one of them.
                if (intVec[1] > 0)
                {
                    intVec[1] += intVec[2];
                }
                else
                {
                    intVec[1] -= intVec[2];
                }
                intVec[2] = 0;
            }
            if (scaledVector.z < 0)
            {
                intVec[2] *= -1;
            }

            IntegerVectorToQuantizedOctahedralCoords(intVec, out outS, out outT);
        }

        /// <summary>
        /// Scales an integer vector so its absolute component sum equals the center value.
        /// </summary>
        /// <param name="vec">Vector with three components, modified in place.</param>
        public unsafe void CanonicalizeIntegerVector(int* vec)
        {
            var absSum = (long)math.abs(vec[0]) + math.abs(vec[1]) + math.abs(vec[2]);

            if (absSum == 0)
            {
                vec[0] = m_CenterValue; // vec[1] == vec[2] == 0
            }
            else
            {
                vec[0] = (int)((long)vec[0] * m_CenterValue / absSum);
                vec[1] = (int)((long)vec[1] * m_CenterValue / absSum);
                if (vec[2] >= 0)
                {
                    vec[2] = m_CenterValue - math.abs(vec[0]) - math.abs(vec[1]);
                }
                else
                {
                    vec[2] = -(m_CenterValue - math.abs(vec[0]) - math.abs(vec[1]));
                }
            }
        }

        /// <summary>
        /// Converts quantized octahedral coordinates back into a unit vector.
        /// </summary>
        /// <param name="inS">Quantized s coordinate.</param>
        /// <param name="inT">Quantized t coordinate.</param>
        /// <param name="outVector">Destination for the three vector components.</param>
        public unsafe void QuantizedOctahedralCoordsToUnitVector(int inS, int inT, float* outVector)
        {
            OctahedralCoordsToUnitVector(
                inS * m_DequantizationScale - 1f,
                inT * m_DequantizationScale - 1f,
                outVector);
        }

        /// <summary>
        /// Tests whether signed coordinates lie inside the central diamond.
        /// </summary>
        /// <param name="s">Signed s coordinate.</param>
        /// <param name="t">Signed t coordinate.</param>
        /// <returns>True if the point is inside the diamond.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInDiamond(int s, int t)
        {
            var st = (uint)math.abs(s) + (uint)math.abs(t);
            return st <= m_CenterValue;
        }

        /// <summary>
        /// Mirrors signed coordinates across the nearest diagonal edge of the diamond.
        /// </summary>
        /// <param name="s">Signed s coordinate, modified in place.</param>
        /// <param name="t">Signed t coordinate, modified in place.</param>
        public void InvertDiamond(ref int s, ref int t)
        {
            int signS;
            int signT;
            if (s >= 0 && t >= 0)
            {
                signS = 1;
                signT = 1;
            }
            else if (s <= 0 && t <= 0)
            {
                signS = -1;
                signT = -1;
            }
            else
            {
                signS = s > 0 ? 1 : -1;
                signT = t > 0 ? 1 : -1;
            }

            // Unsigned arithmetic mirrors the reference implementation and avoids
            // signed overflow on malformed input.
            var cornerPointS = (uint)(signS * m_CenterValue);
            var cornerPointT = (uint)(signT * m_CenterValue);
            var us = (uint)s;
            var ut = (uint)t;
            us = us + us - cornerPointS;
            ut = ut + ut - cornerPointT;
            if (signS * signT >= 0)
            {
                var temp = us;
                us = (uint)-(int)ut;
                ut = (uint)-(int)temp;
            }
            else
            {
                (us, ut) = (ut, us);
            }
            us += cornerPointS;
            ut += cornerPointT;

            s = (int)us / 2;
            t = (int)ut / 2;
        }

        /// <summary>
        /// Wraps a correction value into the valid signed range.
        /// </summary>
        /// <param name="x">Correction value.</param>
        /// <returns>Wrapped value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ModMax(int x)
        {
            if (x > m_CenterValue)
            {
                return x - m_MaxQuantizedValue;
            }
            if (x < -m_CenterValue)
            {
                return x + m_MaxQuantizedValue;
            }
            return x;
        }

        /// <summary>
        /// Maps a correction value into the non-negative range.
        /// </summary>
        /// <param name="x">Correction value.</param>
        /// <returns>Non-negative value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MakePositive(int x)
        {
            if (x < 0)
            {
                return x + m_MaxQuantizedValue;
            }
            return x;
        }

        unsafe void OctahedralCoordsToUnitVector(float inSScaled, float inTScaled, float* outVector)
        {
            var y = inSScaled;
            var z = inTScaled;

            // Project (y, z) onto the surface of the octahedron.
            var x = 1f - math.abs(y) - math.abs(z);

            // |x| is a signed distance from the diagonal edges of the diamond: positive
            // inside (right hemisphere), negative outside (left hemisphere). Points on the
            // left hemisphere are mirrored along the nearest diagonal edge.
            var xOffset = -x;
            xOffset = xOffset < 0 ? 0 : xOffset;

            y += y < 0 ? xOffset : -xOffset;
            z += z < 0 ? xOffset : -xOffset;

            var normSquared = x * x + y * y + z * z;
            if (normSquared < 1e-6)
            {
                outVector[0] = 0;
                outVector[1] = 0;
                outVector[2] = 0;
            }
            else
            {
                var d = 1.0f / math.sqrt(normSquared);
                outVector[0] = x * d;
                outVector[1] = y * d;
                outVector[2] = z * d;
            }
        }
    }
}
