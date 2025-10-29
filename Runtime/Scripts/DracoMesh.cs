// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Draco
{
    class DracoMesh
    {
        public const int maxStreamCount = 4;

        /// <summary>
        /// If Draco mesh has more vertices than this value, memory is allocated persistent,
        /// which is slower, but safe when spanning multiple frames.
        /// </summary>
        const int k_PersistentDataThreshold = 5_000;

        readonly DecodeSettings m_DecodeSettings;
        Mesh.MeshData m_Mesh;
        Allocator m_Allocator;

        int[] m_StreamStrides;
        int[] m_StreamMemberCount;

        DracoSubMesh[] m_SubMeshes;
        List<ExtendedVertexAttributeDescriptor> m_AttributeDescriptors;

        // START BLEND-HACK
        // TODO: Unity does not support setting bone weights and indices via new Mesh API
        // https://fogbugz.unity3d.com/default.asp?1320869_7g7qeq40va98n6h6
        // As a workaround we extract those attributes separately so they can be fed into
        // Mesh.SetBoneWeights after the Mesh was created.
        bool m_HasBoneWeightData;
        NativeArray<byte> m_BonesPerVertex;
        NativeArray<BoneWeight1> m_BoneWeights;
        // END BLEND-HACK

        public DracoMesh(DecodeSettings decodeSettings, Mesh.MeshData mesh)
        {
            m_DecodeSettings = decodeSettings;
            m_Mesh = mesh;
        }

        public async Task<DecodeResult> DecodeAsync(
            IReadOnlyList<NativeArray<byte>.ReadOnly> encodedData,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMap
#if UNITY_EDITOR
            ,bool sync = false
#endif
            )
        {
            var tasks = new Task<DracoInstance>[encodedData.Count];
            for (var i = 0; i < encodedData.Count; i++)
            {
                tasks[i] = DracoInstance.Init(
                    encodedData[i]
#if UNITY_EDITOR
                    , sync
#endif
                );
            }
            var dracoInstances = await Task.WhenAll(tasks);

            foreach (var dracoInstance in dracoInstances)
            {
                if (dracoInstance == null)
                {
                    foreach (var disposable in dracoInstances)
                    {
                        disposable?.DisposeDracoMesh();
                    }
                    return new DecodeResult();
                }
            }

            CreateMesh(
                dracoInstances,
                attributeIdMap
            );

            var decodeTasks = new Task[encodedData.Count];
            var indexOffset = 0;
            for (var subMeshIndex = 0; subMeshIndex < encodedData.Count; subMeshIndex++)
            {
                decodeTasks[subMeshIndex] = DecodeVertexData(
                    subMeshIndex,
                    dracoInstances[subMeshIndex],
                    indexOffset
#if UNITY_EDITOR
                    ,sync
#endif
                );
                indexOffset += m_SubMeshes[subMeshIndex].indicesCount;
            }
            await Task.WhenAll(decodeTasks);

            var error = false;

            foreach (var subMesh in m_SubMeshes)
            {
                subMesh.DisposeAttributes();
            }
            for (var i = 0; i < encodedData.Count; i++)
            {
                error |= dracoInstances[i].ErrorOccured();
                dracoInstances[i].DisposeDracoMesh();
            }

            if (error)
            {
                return new DecodeResult();
            }

            var success = PopulateMeshData(out var bounds);
            BoneWeightData boneWeightData = null;
            if (success && m_HasBoneWeightData)
            {
                boneWeightData = new BoneWeightData(m_BonesPerVertex, m_BoneWeights);
            }

            var calculateNormals = false;
            foreach (var submesh in m_SubMeshes)
            {
                calculateNormals |= submesh.calculateNormals;
                submesh.Dispose();
            }

            return new DecodeResult(
                success,
                bounds,
                calculateNormals,
                boneWeightData
            );
        }

        void CreateMesh(
            DracoInstance[] dracoInstances,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMap
        )
        {
            Profiler.BeginSample("CreateMesh");

            var totalNumVertices = 0;
            var indexFormat = IndexFormat.UInt16;
            foreach (var submesh in dracoInstances)
            {
                totalNumVertices += submesh.numVertices;
                if (submesh.numVertices > ushort.MaxValue + 1)
                    indexFormat = IndexFormat.UInt32;
            }

            m_Allocator = totalNumVertices > k_PersistentDataThreshold
                ? Allocator.Persistent
                : Allocator.TempJob;

            CalculateVertexParams(
                dracoInstances,
                attributeIdMap
            );

            Profiler.BeginSample("SetParameters");

            var indicesCount = 0;
            var vertexOffset = 0;

            for (var subMeshIndex = 0; subMeshIndex < m_SubMeshes.Length; subMeshIndex++)
            {
                var subMesh = m_SubMeshes[subMeshIndex];
                subMesh.Init((m_DecodeSettings & DecodeSettings.DontCalculateBounds) == 0, m_Allocator);
                var draco = dracoInstances[subMeshIndex];
                if (draco.isPointCloud)
                {
                    subMesh.topology = MeshTopology.Points;
                    subMesh.indicesCount = draco.numVertices;
                }
                else
                {
                    subMesh.topology = MeshTopology.Triangles;
                    subMesh.indicesCount = draco.numFaces * 3;
                }

                indicesCount += subMesh.indicesCount;

                subMesh.baseVertex = vertexOffset;
                vertexOffset += draco.numVertices;
            }

            m_Mesh.SetIndexBufferParams(indicesCount, indexFormat);

            var vertexParams = new VertexAttributeDescriptor[m_AttributeDescriptors.Count];
            for (var i = 0; i < m_AttributeDescriptors.Count; i++)
            {
                vertexParams[i] = m_AttributeDescriptors[i].attribute;
            }

            m_Mesh.SetVertexBufferParams(totalNumVertices, vertexParams);

            if (m_HasBoneWeightData)
            {
                var boneCount = m_SubMeshes[0].bonesPerVertex;
                for (var i = 1; i < m_SubMeshes.Length; i++)
                {
                    if (m_SubMeshes[i].boneIndexAttribute.numComponents != boneCount)
                        throw new InvalidDataException("Inconsistent number of bone influences in submeshes!");
                }

                m_BonesPerVertex = new NativeArray<byte>(totalNumVertices, m_Allocator);
                m_BoneWeights = new NativeArray<BoneWeight1>(totalNumVertices * boneCount, m_Allocator);
            }
            Profiler.EndSample(); // SetParameters
            Profiler.EndSample(); // CreateMesh
        }

        void CalculateVertexParams(
            DracoInstance[] dracoMesh,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMap
            )
        {
            Profiler.BeginSample("CalculateVertexParams");

            m_SubMeshes = CreateDracoSubMeshes(
                dracoMesh,
                attributeIdMap,
                out var hasTexCoordOrColor
            );

            var attributeDescriptorDict = new Dictionary<VertexAttribute, ExtendedVertexAttributeDescriptor>();
            foreach (var attribute in m_SubMeshes[0].attributes)
            {
                var meshVertexAttribute =
                    new ExtendedVertexAttributeDescriptor(attribute.GetVertexAttributeDescriptor());
                attribute.attributeDescriptor = meshVertexAttribute;
                attributeDescriptorDict[attribute.attribute] = meshVertexAttribute;
            }

            m_HasBoneWeightData = m_SubMeshes[0].boneIndexAttribute != null
                && m_SubMeshes[0].boneWeightAttribute != null;

            for (var i = 1; i < m_SubMeshes.Length; i++)
            {
                var info = m_SubMeshes[i];
                var alignedAttributeCount = 0;
                foreach (var attribute in info.attributes)
                {
                    if (attributeDescriptorDict.TryGetValue(attribute.attribute, out var meshVertexAttribute))
                    {
                        if (meshVertexAttribute.attribute.format != attribute.format)
                        {
                            Debug.LogError($"Inconsistent {attribute.attribute} attribute format in submeshes!");
                        }

                        alignedAttributeCount++;
                    }
                    else
                    {
                        Debug.LogError($"Vertex attribute {attribute.attribute} not found on all submeshes!");
                        meshVertexAttribute =
                            new ExtendedVertexAttributeDescriptor(attribute.GetVertexAttributeDescriptor());
                        attributeDescriptorDict[attribute.attribute] = meshVertexAttribute;
                    }
                    attribute.attributeDescriptor = meshVertexAttribute;
                }

                if (alignedAttributeCount != attributeDescriptorDict.Count)
                {
                    Debug.LogError("Inconsistent set of vertex attributes across submeshes!");
                }

                var hasBoneWeightData = info.boneIndexAttribute != null
                    && info.boneWeightAttribute != null;
                if (hasBoneWeightData != m_HasBoneWeightData)
                {
                    Debug.LogError("Inconsistent presence of bone weight attributes in submeshes! Discarding bone weight data.");
                    m_HasBoneWeightData = false;
                }
            }

            m_StreamStrides = new int[maxStreamCount];
            m_StreamMemberCount = new int[maxStreamCount];
            var streamIndex = 0;

            var forceUnityVertexLayout = (m_DecodeSettings & DecodeSettings.ForceUnityVertexLayout) != 0;
            // skinning requires SkinnedMeshRenderer layout
            forceUnityVertexLayout |= m_HasBoneWeightData;

            // On scenes with lots of small meshes the overhead of lots
            // of dedicated vertex buffers can have severe negative impact
            // on performance. Therefore, we stick to Unity's layout (which
            // combines pos+normal+tangent in one stream) for smaller meshes.
            // See: https://github.com/atteneder/glTFast/issues/197
            foreach (var submesh in dracoMesh)
            {
                forceUnityVertexLayout |= submesh.numVertices <= ushort.MaxValue;
            }

            foreach (var attribute in attributeDescriptorDict)
            {
                // Stream assignment:
                // Positions get a dedicated stream (0)
                // The rest lands on stream 1

                // If blend weights or blend indices are present, they land on stream 1
                // while the rest is combined in stream 0

                // Mesh layout SkinnedMeshRenderer (used for skinning and blend shapes)
                // requires:
                // stream 0: position,normal,tangent
                // stream 1: UVs,colors
                // stream 2: blend weights/indices

                switch (attribute.Key)
                {
                    case VertexAttribute.Position:
                        // Attributes that define/change the position go to stream 0
                        streamIndex = 0;
                        break;
                    case VertexAttribute.Normal:
                    case VertexAttribute.Tangent:
                        streamIndex = forceUnityVertexLayout ? 0 : 1;
                        break;
                    case VertexAttribute.TexCoord0:
                    case VertexAttribute.TexCoord1:
                    case VertexAttribute.TexCoord2:
                    case VertexAttribute.TexCoord3:
                    case VertexAttribute.TexCoord4:
                    case VertexAttribute.TexCoord5:
                    case VertexAttribute.TexCoord6:
                    case VertexAttribute.TexCoord7:
                    case VertexAttribute.Color:
                        streamIndex = 1;
                        break;
                    case VertexAttribute.BlendWeight:
                    case VertexAttribute.BlendIndices:
                        // Special case: blend weights/joints always have a special stream
                        streamIndex = hasTexCoordOrColor ? 2 : 1;
                        break;
                }

                var elementSize = attribute.Value.elementSize;
                attribute.Value.offset = m_StreamStrides[streamIndex];
                attribute.Value.attribute.stream = streamIndex;
                m_StreamStrides[streamIndex] += elementSize;
                m_StreamMemberCount[streamIndex]++;
            }

            m_AttributeDescriptors = new List<ExtendedVertexAttributeDescriptor>(attributeDescriptorDict.Values);
            m_AttributeDescriptors.Sort(ExtendedVertexAttributeDescriptor.CompareByStreamAndOffset);
            Profiler.EndSample(); // CalculateVertexParams
        }

        async Task DecodeVertexData(
            int subMeshIndex,
            DracoInstance dracoInstance,
            int indexOffset
#if UNITY_EDITOR
            ,bool sync = false
#endif
            )
        {
            var decodeVerticesJobHandle = dracoInstance.DecodeVertices();
#if UNITY_EDITOR
            if (sync) {
                decodeVerticesJobHandle.Complete();
            }
#endif

            JobHandle indicesJob;
            var dracoMeshJobCount = m_AttributeDescriptors.Count;

            var submesh = m_SubMeshes[subMeshIndex];

            var isPointCloud = dracoInstance.isPointCloud;
            if (isPointCloud)
            {
                indicesJob = new GeneratePointCloudIndicesJob
                {
                    mesh = m_Mesh
                }.Schedule();
            }
            else
            {
                indicesJob = dracoInstance.DecodeIndices(
                    m_Mesh,
                    (m_DecodeSettings & DecodeSettings.ConvertSpace) != 0,
                    indexOffset,
                    submesh.indicesCount,
                    decodeVerticesJobHandle
                    );
                dracoMeshJobCount++;
            }

            if (m_HasBoneWeightData) dracoMeshJobCount++;

            var jobIndex = 0;
            var jobHandles = new NativeArray<JobHandle>(dracoMeshJobCount, m_Allocator);

            if (!isPointCloud)
            {
                jobHandles[jobIndex] = indicesJob;
                jobIndex++;
            }

#if UNITY_EDITOR
            if (sync) {
                indicesJob.Complete();
            }
#endif

            foreach (var attribute in submesh.attributes)
            {
                if (attribute is not DracoAttribute dracoAttribute) continue;

                // BLEND-HACK: skip blend indices here (done below)
                // weights were removed from attributes before
                if (dracoAttribute.attribute == VertexAttribute.BlendIndices) continue; // Blend

                var calculateBound = dracoAttribute.attribute == VertexAttribute.Position;

                var stream = dracoAttribute.attributeDescriptor.attribute.stream;

                jobHandles[jobIndex] = dracoAttribute.GetDracoData(
                    m_Mesh,
                    submesh.baseVertex,
                    dracoInstance,
                    stream,
                    dracoAttribute.attributeDescriptor.offset,
                    m_StreamMemberCount[stream] > 1 ? m_StreamStrides[stream] : -1,
                    calculateBound ? submesh.positionMinMax : default,
                    decodeVerticesJobHandle
                    );
#if UNITY_EDITOR
                if (sync) {
                    jobHandles[jobIndex].Complete();
                }
#endif
                jobIndex++;
            }

            if (m_HasBoneWeightData)
            {
                // TODO: BLEND-HACK
                jobHandles[jobIndex] = dracoInstance.GetDracoBones(
                    submesh.boneIndexAttribute.DracoAttributeInstance,
                    submesh.boneWeightAttribute.DracoAttributeInstance,
                    m_BonesPerVertex.GetSubArray(submesh.baseVertex, submesh.vertexCount),
                    m_BoneWeights.GetSubArray(submesh.baseVertex * submesh.bonesPerVertex, submesh.vertexCount * submesh.bonesPerVertex),
                    submesh.boneIndexAttribute.format,
                    decodeVerticesJobHandle
                    );
            }

            var jobHandle = JobHandle.CombineDependencies(jobHandles);
            jobHandles.Dispose();

            var releaseDracoMeshJobHandle = dracoInstance.Release(jobHandle);

#if UNITY_EDITOR
            if (sync) {
                releaseDracoMeshJobHandle.Complete();
            }
#endif
            if (isPointCloud)
            {
                var pointCloudJobHandles = new NativeArray<JobHandle>(2, m_Allocator);
                pointCloudJobHandles[0] = indicesJob;
                pointCloudJobHandles[1] = releaseDracoMeshJobHandle;
                var pointCloudJobHandle = JobHandle.CombineDependencies(pointCloudJobHandles);
                pointCloudJobHandles.Dispose();
                await DracoInstance.WaitForJobHandle(pointCloudJobHandle);
            }

            await DracoInstance.WaitForJobHandle(releaseDracoMeshJobHandle);
        }

        bool PopulateMeshData(out Bounds bounds)
        {
            Profiler.BeginSample("PopulateMeshData");
            Profiler.BeginSample("MeshAssign");

            const MeshUpdateFlags flags = DracoDecoder.defaultMeshUpdateFlags;

            m_Mesh.subMeshCount = m_SubMeshes.Length;
            var indexStart = 0;
            var baseVertex = 0;
            bounds = new Bounds();
            var calculateBounds = (m_DecodeSettings & DecodeSettings.DontCalculateBounds) == 0;
            for (var i = 0; i < m_SubMeshes.Length; i++)
            {
                var subMesh = m_SubMeshes[i];
                var subMeshDescriptor = new SubMeshDescriptor(
                    indexStart,
                    subMesh.indicesCount,
                    subMesh.topology
                )
                {
                    firstVertex = baseVertex,
                    vertexCount = subMesh.vertexCount,
                    baseVertex = baseVertex
                };
                if (calculateBounds)
                {
                    subMeshDescriptor.bounds = subMesh.GetBounds();
                    if (i == 0)
                    {
                        bounds = subMeshDescriptor.bounds;
                    }
                    else
                    {
                        bounds.Encapsulate(subMeshDescriptor.bounds);
                    }
                }
                m_Mesh.SetSubMesh(i, subMeshDescriptor, flags);
                indexStart += subMesh.indicesCount;
                baseVertex += subMesh.vertexCount;
            }

            Profiler.EndSample(); // CreateUnityMesh.CreateMesh
            Profiler.EndSample();

            return true;
        }

        DracoSubMesh[] CreateDracoSubMeshes(
            DracoInstance[] dracoInstances,
            IReadOnlyList<Dictionary<VertexAttribute, int>> attributeIdMaps,
            out bool hasTexCoordOrColor
        )
        {
            // Vertex attributes are added in the order defined here:
            // https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rendering.VertexAttributeDescriptor.html

            var result = new DracoSubMesh[dracoInstances.Length];
            hasTexCoordOrColor = false;

            for (var submesh = 0; submesh < dracoInstances.Length; submesh++)
            {
                result[submesh] = new DracoSubMesh { vertexCount = dracoInstances[submesh].numVertices };
                var attributeIdMap = attributeIdMaps?[submesh];
                CreateAttribute(
                    dracoInstances[submesh],
                    attributeIdMap,
                    result[submesh].attributes,
                    AttributeType.Position,
                    VertexAttribute.Position
                    );

                var hasNormals = CreateAttribute(
                    dracoInstances[submesh],
                    attributeIdMap,
                    result[submesh].attributes,
                    AttributeType.Normal,
                    VertexAttribute.Normal,
                    true
                    );

                result[submesh].calculateNormals = !hasNormals
                    && (m_DecodeSettings & DecodeSettings.RequireNormalsAndTangents) != 0;
                if (result[submesh].calculateNormals)
                {
                    result[submesh].attributes.Add(new StubAttribute(
                        VertexAttribute.Normal,
                        VertexAttributeFormat.Float32,
                        3
                        ));
                }

                if (TryCreateAttributeById(
                        dracoInstances[submesh],
                        attributeIdMap,
                        VertexAttribute.Tangent,
                        out var tangentAttribute
                        ))
                {
                    result[submesh].attributes.Add(tangentAttribute);
                }
                else if ((m_DecodeSettings & DecodeSettings.RequireTangents) != 0)
                {
                    result[submesh].attributes.Add(
                        new StubAttribute(
                            VertexAttribute.Tangent,
                            VertexAttributeFormat.Float32,
                            4
                        ));
                }

                hasTexCoordOrColor = CreateAttributeByType(
                    dracoInstances[submesh],
                    AttributeType.Color,
                    result[submesh].attributes,
                    1,
                    true
                    ) || hasTexCoordOrColor;

                if (TryCreateAttributeById(
                        dracoInstances[submesh],
                        attributeIdMap,
                        VertexAttribute.TexCoord0,
                        out var uvAttribute
                        ))
                {
                    hasTexCoordOrColor = true;
                    result[submesh].attributes.Add(uvAttribute);
                    for (var i = 1; i < 8; i++)
                    {
                        var att = VertexAttribute.TexCoord0 + i;
                        if (TryCreateAttributeById(
                                dracoInstances[submesh],
                                attributeIdMap,
                                att,
                                out uvAttribute
                                ))
                        {
                            result[submesh].attributes.Add(uvAttribute);
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    hasTexCoordOrColor = CreateAttributeByType(
                        dracoInstances[submesh],
                        AttributeType.TextureCoordinate,
                        result[submesh].attributes,
                        8,
                        true
                        ) || hasTexCoordOrColor;
                }

                TryCreateAttributeById(
                    dracoInstances[submesh],
                    attributeIdMap,
                    VertexAttribute.BlendWeight,
                    out result[submesh].boneWeightAttribute
                    );
                TryCreateAttributeById(
                    dracoInstances[submesh],
                    attributeIdMap,
                    VertexAttribute.BlendIndices,
                    out result[submesh].boneIndexAttribute
                    );

                // BLEND-HACK: Notice that boneWeightAttribute and boneIndexAttribute are not added to the attributes as they'd get
                // deleted upon calling Mesh.SetBoneWeights
            }


            return result;
        }

        bool TryCreateAttributeById(
            DracoInstance draco,
            Dictionary<VertexAttribute, int> attributeIdMap,
            VertexAttribute vertexAttribute,
            out DracoAttribute dracoAttribute
            )
        {
            dracoAttribute = null;
            if (attributeIdMap != null && attributeIdMap.TryGetValue(vertexAttribute, out var attributeId)
                && CreateAttributeById(draco, vertexAttribute, attributeId, out dracoAttribute))
            {
                return true;
            }

            return false;
        }

        bool CreateAttribute(
            DracoInstance draco,
            Dictionary<VertexAttribute, int> attributeIdMap,
            IList<AttributeBase> results,
            AttributeType attributeType,
            VertexAttribute vertexAttribute,
            bool normalized = false
            )
        {
            if (TryCreateAttributeById(
                    draco,
                    attributeIdMap,
                    vertexAttribute,
                    out var attribute
                    ))
            {
                results.Add(attribute);
                return true;
            }

            return CreateAttributeByType(
                draco,
                attributeType,
                results,
                1,
                normalized
                );
        }

        bool CreateAttributeByType(
            DracoInstance draco,
            AttributeType attributeType,
            IList<AttributeBase> results,
            int count,
            bool normalized = false
        )
        {
            var foundAttribute = false;
            for (var i = 0; i < count; i++)
            {
                var type = DracoInstance.GetVertexAttribute(attributeType, i);
                if (!type.HasValue)
                {
#if UNITY_EDITOR
                    // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
                    Debug.LogWarning($"Unknown attribute {attributeType}!");
#endif
                    continue;
                }

                if (draco.TryGetAttributeByType(attributeType, i, out var attributeInstance))
                {
                    var format = DracoInstance.GetVertexAttributeFormat(
                        attributeInstance.Value.dataType, normalized);
                    if (!format.HasValue) { continue; }
                    var attribute = new DracoAttribute(
                        attributeInstance,
                        type.Value,
                        format.Value,
                        DracoInstance.ConvertSpace(type.Value) && (m_DecodeSettings & DecodeSettings.ConvertSpace) != 0
                    );
                    results.Add(attribute);
                    foundAttribute = true;
                }
                else
                {
                    // attributeType was not found
                    break;
                }
            }
            return foundAttribute;
        }

        bool CreateAttributeById(
            DracoInstance draco,
            VertexAttribute type,
            int id,
            out DracoAttribute attribute,
            bool normalized = false
            )
        {
            attribute = null;

            if (draco.TryGetAttributeByUniqueId(id, out var attributeInstance))
            {
                var format = attributeInstance.GetVertexAttributeFormat(normalized);
                if (!format.HasValue) { return false; }

                attribute = new DracoAttribute(
                    attributeInstance,
                    type,
                    format.Value,
                    DracoInstance.ConvertSpace(type) && (m_DecodeSettings & DecodeSettings.ConvertSpace) != 0
                );
                return true;
            }
            return false;
        }
    }
}
