// SPDX-FileCopyrightText: 2023 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

#if UNITY_STANDALONE || UNITY_WEBGL || UNITY_IOS || UNITY_TVOS || UNITY_VISIONOS || UNITY_ANDROID || UNITY_WSA || UNITY_LUMIN || PLATFORM_EMBEDDED_LINUX
#define DRACO_PLATFORM_SUPPORTED
#else
#define DRACO_PLATFORM_NOT_SUPPORTED
#endif

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

[assembly: InternalsVisibleTo("Draco.Editor")]

namespace Draco
{
    /// <summary>
    /// Provides Draco mesh decoding.
    /// </summary>
    public static class DracoDecoder
    {
        /// <summary>
        /// These <see cref="MeshUpdateFlags"/> ensure the best performance when using DecodeMesh variants that use
        /// <see cref="Mesh.MeshData"/> as parameter. Pass them to the subsequent
        /// <see cref="UnityEngine.Mesh.ApplyAndDisposeWritableMeshData(Mesh.MeshDataArray,Mesh,MeshUpdateFlags)"/>
        /// method. They're used internally for DecodeMesh variants returning a <see cref="Mesh"/> directly.
        /// </summary>
        public const MeshUpdateFlags defaultMeshUpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;


        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeArray<byte>.ReadOnly encodedData
        )
        {
            return await DecodeMesh(meshData, encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData
        )
        {
            return await DecodeMesh(meshData, encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>A DecodeResult</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeSlice<byte> encodedData
        )
        {
            return await DecodeMesh(meshData, encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(meshData, encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(meshData, encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>A DecodeResult</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeSlice<byte> encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(meshData, encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            var result = await DecodeMeshInternal(
                meshData,
                encodedData,
                decodeSettings,
                attributeIdMap
            );
            return result;
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMaps">Attribute type to index maps</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMaps
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            var result = await DecodeMeshInternal(
                meshData,
                encodedData,
                decodeSettings,
                attributeIdMaps
            );
            return result;
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>A DecodeResult</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            NativeSlice<byte> encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            var result = await DecodeMeshInternal(
                meshData,
                encodedData.AsNativeArray().AsReadOnly(),
                decodeSettings,
                attributeIdMap
            );
            return result;
        }

        /// <inheritdoc cref="DecodeMesh(Mesh.MeshData,NativeSlice{byte})"/>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            byte[] encodedData
        )
        {
            return await DecodeMesh(meshData, encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            byte[] encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(meshData, encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <param name="meshData">MeshData used to create the mesh</param>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>A DecodeResult</returns>
        public static async Task<DecodeResult> DecodeMesh(
            Mesh.MeshData meshData,
            byte[] encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
            )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            using var nativeArray = new ManagedNativeArray(encodedData);
            var result = await DecodeMeshInternal(
                meshData,
                nativeArray.nativeArray.AsReadOnly(),
                decodeSettings,
                attributeIdMap
                );
            return result;
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(Mesh.MeshData,NativeArray{byte}.ReadOnly)"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            NativeArray<byte>.ReadOnly encodedData
        )
        {
            return await DecodeMesh(encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,IReadOnlyList{Unity.Collections.NativeArray{byte}.ReadOnly})"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData
        )
        {
            return await DecodeMesh(encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly)"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<Mesh> DecodeMesh(
            NativeSlice<byte> encodedData
        )
        {
            return await DecodeMesh(encodedData, DecodeSettings.Default, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly,DecodeSettings)"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,IReadOnlyList{Unity.Collections.NativeArray{byte}.ReadOnly},DecodeSettings)"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly,DecodeSettings)"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<Mesh> DecodeMesh(
            NativeSlice<byte> encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly,DecodeSettings,Dictionary{VertexAttribute,int})"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            return await DecodeMeshInternal(
                encodedData,
                decodeSettings,
                attributeIdMap
            );
        }

        /// <summary>
        /// Decodes multiple Draco meshes into one Unity mesh with sub-meshes.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,IReadOnlyList{Unity.Collections.NativeArray{byte}.ReadOnly},DecodeSettings,IReadOnlyList{Dictionary{VertexAttribute,int}})"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMaps">Attribute type to index maps</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMaps
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            return await DecodeMeshInternal(
                encodedData,
                decodeSettings,
                attributeIdMaps
            );
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly,DecodeSettings,Dictionary{VertexAttribute,int})"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        [Obsolete("Use the overload that accepts encodedData as NativeArray<byte>.ReadOnly.")]
        public static async Task<Mesh> DecodeMesh(
            NativeSlice<byte> encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
        )
        {
            return await DecodeMesh(encodedData.AsNativeArray().AsReadOnly(), decodeSettings, attributeIdMap);
        }

        /// <inheritdoc cref="DecodeMesh(NativeSlice{byte})"/>
        public static async Task<Mesh> DecodeMesh(
            byte[] encodedData
        )
        {
            return await DecodeMesh(encodedData, DecodeSettings.Default, null);
        }

        /// <inheritdoc cref="DecodeMesh(NativeSlice{byte},DecodeSettings)"/>
        public static async Task<Mesh> DecodeMesh(
            byte[] encodedData,
            DecodeSettings decodeSettings
        )
        {
            return await DecodeMesh(encodedData, decodeSettings, null);
        }

        /// <summary>
        /// Decodes a Draco mesh.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="DecodeMesh(UnityEngine.Mesh.MeshData,Unity.Collections.NativeArray{byte}.ReadOnly,DecodeSettings,Dictionary{VertexAttribute,int})"/>
        /// for increased performance.
        /// </remarks>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <param name="decodeSettings">Decode setting flags</param>
        /// <param name="attributeIdMap">Attribute type to index map</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public static async Task<Mesh> DecodeMesh(
            byte[] encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
        )
        {
            CertifySupportedPlatform(
#if UNITY_EDITOR
                false
#endif
            );
            using var nativeArray = new ManagedNativeArray(encodedData);
            return await DecodeMesh(
                nativeArray.nativeArray.AsReadOnly(),
                decodeSettings,
                attributeIdMap
            );
        }

        /// <summary>
        /// Creates an attribute type to index map from indices for bone weights and joints.
        /// </summary>
        /// <param name="weightsAttributeId">Bone weights attribute index.</param>
        /// <param name="jointsAttributeId">Bone joints attribute index.</param>
        /// <returns>Attribute type to index map.</returns>
        public static Dictionary<VertexAttribute, int> CreateAttributeIdMap(
            int weightsAttributeId,
            int jointsAttributeId
            )
        {
            Dictionary<VertexAttribute, int> result = null;
            if (weightsAttributeId >= 0)
            {
                result = new Dictionary<VertexAttribute, int>
                {
                    [VertexAttribute.BlendWeight] = weightsAttributeId
                };
            }

            if (jointsAttributeId >= 0)
            {
                result ??= new Dictionary<VertexAttribute, int>();
                result[VertexAttribute.BlendIndices] = jointsAttributeId;
            }

            return result;
        }

        static void ApplyAndDisposeDecodeResult(Mesh unityMesh, DecodeResult result, DecodeSettings decodeSettings)
        {
            unityMesh.bounds = result.bounds;
            if (result.boneWeightData != null)
            {
                result.boneWeightData.ApplyOnMesh(unityMesh);
                result.boneWeightData.Dispose();
            }
            if (unityMesh.GetTopology(0) == MeshTopology.Triangles)
            {
                if (result.calculateNormals)
                {
                    unityMesh.RecalculateNormals();
                }
                if ((decodeSettings & DecodeSettings.RequireTangents) != 0)
                {
                    unityMesh.RecalculateTangents();
                }
            }
        }

        internal static async Task<Mesh> DecodeMeshInternal(
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var mesh = meshDataArray[0];
            var result = await DecodeMeshInternal(
                mesh,
                encodedData,
                decodeSettings,
                attributeIdMap
#if UNITY_EDITOR
                ,sync
#endif
            );
            if (!result.success)
            {
                meshDataArray.Dispose();
                return null;
            }
            var unityMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, unityMesh, defaultMeshUpdateFlags);
            ApplyAndDisposeDecodeResult(unityMesh, result, decodeSettings);
            return unityMesh;
        }

        internal static async Task<Mesh> DecodeMeshInternal(
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMaps
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            var mesh = meshDataArray[0];
            var result = await DecodeMeshInternal(
                mesh,
                encodedData,
                decodeSettings,
                attributeIdMaps
#if UNITY_EDITOR
                ,sync
#endif
            );
            if (!result.success)
            {
                meshDataArray.Dispose();
                return null;
            }
            var unityMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, unityMesh, defaultMeshUpdateFlags);
            ApplyAndDisposeDecodeResult(unityMesh, result, decodeSettings);
            return unityMesh;
        }

        internal static async Task<DecodeResult> DecodeMeshInternal(
            Mesh.MeshData meshData,
            NativeArray<byte>.ReadOnly encodedData,
            DecodeSettings decodeSettings,
            Dictionary<VertexAttribute, int> attributeIdMap
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var mesh = new DracoMesh(decodeSettings, meshData);
            return await mesh.DecodeAsync(
                new[] { encodedData },
                new[] { attributeIdMap }
#if UNITY_EDITOR
                ,sync
#endif
            );
        }

        internal static async Task<DecodeResult> DecodeMeshInternal(
            Mesh.MeshData meshData,
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            DecodeSettings decodeSettings,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMap
#if UNITY_EDITOR
            ,bool sync = false
#endif
        )
        {
            var mesh = new DracoMesh(decodeSettings, meshData);
            return await mesh.DecodeAsync(encodedData, attributeIdMap
#if UNITY_EDITOR
                ,sync
#endif
            );
        }

#if !UNITY_EDITOR && DRACO_PLATFORM_SUPPORTED
        [System.Diagnostics.Conditional("FALSE")]
#endif
        internal static void CertifySupportedPlatform(
#if UNITY_EDITOR
            bool editorImport
#endif
        )
        {
#if DRACO_PLATFORM_NOT_SUPPORTED
#if UNITY_EDITOR
#if !DRACO_IGNORE_PLATFORM_NOT_SUPPORTED
            if (!editorImport)
            {
                throw new NotSupportedException("Draco for Unity is not supported on the active build target. This will not work in a build, please switch to a supported platform in the build settings. You can bypass this exception in the Editor by setting the scripting define `DRACO_IGNORE_PLATFORM_NOT_SUPPORTED`.");
            }
#endif // !DRACO_IGNORE_PLATFORM_NOT_SUPPORTED
#else
            // In a build, always throw the exception.
            throw new NotSupportedException("Draco for Unity is not supported on this platform.");
#endif
#endif // DRACO_PLATFORM_NOT_SUPPORTED
        }
    }
}
