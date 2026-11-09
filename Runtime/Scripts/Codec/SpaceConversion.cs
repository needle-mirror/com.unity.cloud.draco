// SPDX-FileCopyrightText: 2026 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Cloud.Draco.Codec
{
    /// <summary>
    /// Converts attribute values between Unity's left handed and Draco's right handed
    /// coordinate system. The conversion is its own inverse.
    /// </summary>
    /// <remarks>
    /// Two component values are texture coordinates (flip V), three component values are
    /// positions or normals (flip X) and four component values are tangents (flip Y and Z).
    /// Values with any other component count are left untouched. Integer results wrap
    /// around like the casts of the native plug-in.
    /// </remarks>
    static unsafe class SpaceConversion
    {
        /// <summary>
        /// Converts values in place.
        /// </summary>
        /// <param name="data">First value.</param>
        /// <param name="count">Number of values.</param>
        /// <param name="stride">Distance in bytes between two values.</param>
        /// <param name="componentCount">Number of components per value.</param>
        /// <param name="dataType">Component data type. Other types than 8 to 32 bit integers
        /// and 32 bit floats are left untouched.</param>
        public static void Convert(byte* data, int count, int stride, int componentCount, DataType dataType)
        {
            switch (dataType)
            {
                case DataType.Int8:
                    Convert<sbyte>(data, count, stride, componentCount);
                    break;
                case DataType.UInt8:
                    Convert<byte>(data, count, stride, componentCount);
                    break;
                case DataType.Int16:
                    Convert<short>(data, count, stride, componentCount);
                    break;
                case DataType.UInt16:
                    Convert<ushort>(data, count, stride, componentCount);
                    break;
                case DataType.Int32:
                    Convert<int>(data, count, stride, componentCount);
                    break;
                case DataType.UInt32:
                    Convert<uint>(data, count, stride, componentCount);
                    break;
                case DataType.Float32:
                    Convert<float>(data, count, stride, componentCount);
                    break;
            }
        }

        static void Convert<T>(byte* data, int count, int stride, int componentCount) where T : unmanaged
        {
            for (var i = 0; i < count; ++i)
            {
                var value = (T*)(data + (long)i * stride);
                switch (componentCount)
                {
                    case 2:
                        value[1] = OneMinus(value[1]);
                        break;
                    case 3:
                        value[0] = Negate(value[0]);
                        break;
                    case 4:
                        value[1] = Negate(value[1]);
                        value[2] = Negate(value[2]);
                        break;
                }
            }
        }

        static T Negate<T>(T value) where T : unmanaged
        {
            if (typeof(T) == typeof(float))
            {
                var result = -UnsafeUtility.As<T, float>(ref value);
                return UnsafeUtility.As<float, T>(ref result);
            }
            return Truncate<T>(-ToLong(value));
        }

        static T OneMinus<T>(T value) where T : unmanaged
        {
            if (typeof(T) == typeof(float))
            {
                var result = 1f - UnsafeUtility.As<T, float>(ref value);
                return UnsafeUtility.As<float, T>(ref result);
            }
            return Truncate<T>(1 - ToLong(value));
        }

        static long ToLong<T>(T value) where T : unmanaged
        {
            if (typeof(T) == typeof(sbyte))
            {
                return UnsafeUtility.As<T, sbyte>(ref value);
            }
            if (typeof(T) == typeof(byte))
            {
                return UnsafeUtility.As<T, byte>(ref value);
            }
            if (typeof(T) == typeof(short))
            {
                return UnsafeUtility.As<T, short>(ref value);
            }
            if (typeof(T) == typeof(ushort))
            {
                return UnsafeUtility.As<T, ushort>(ref value);
            }
            if (typeof(T) == typeof(int))
            {
                return UnsafeUtility.As<T, int>(ref value);
            }
            return UnsafeUtility.As<T, uint>(ref value);
        }

        /// <summary>
        /// Keeps the low bytes of <paramref name="value"/>, which is a narrowing cast on the
        /// little endian platforms Unity supports.
        /// </summary>
        static T Truncate<T>(long value) where T : unmanaged
        {
            return UnsafeUtility.As<long, T>(ref value);
        }
    }
}
