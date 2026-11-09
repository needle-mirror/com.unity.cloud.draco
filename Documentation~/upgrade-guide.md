# Upgrade

These guides will help you upgrade your project to use the latest version of *Draco for Unity*. If you still encounter problems, help us improving this guide and *Draco for Unity* in general by reaching out or by raising an issue.

## Upgrading to *Draco for Unity* 6.0.0

Encoding and decoding are implemented in C# now. The package doesn't contain native plug-in binaries anymore.

### Namespace and assembly changes

The `Draco` namespace and all its sub-namespaces moved to `Unity.Cloud.Draco` (e.g. `Draco.Encode` to `Unity.Cloud.Draco.Encode`). Unity's API Updater converts your code automatically when you open the project.

Assembly definitions were renamed accordingly:

| Old name       | New name                   |
|----------------|----------------------------|
| `Draco`        | `Unity.Cloud.Draco`        |
| `Draco.Encode` | `Unity.Cloud.Draco.Encode` |
| `Draco.Editor` | `Unity.Cloud.Draco.Editor` |

If one of your assembly definitions references them by name, update the references manually.

### API changes

Previously obsolete API was removed. If your code still uses it, replace it as follows:

- `DracoMeshLoader`: Use the [DecodeMesh](xref:Unity.Cloud.Draco.DracoDecoder.DecodeMesh*) methods of [DracoDecoder](xref:Unity.Cloud.Draco.DracoDecoder) instead. Pass bone weight and joint attribute identifiers via an `attributeIdMap` (see [CreateAttributeIdMap](xref:Unity.Cloud.Draco.DracoDecoder.CreateAttributeIdMap*)).
- `DracoDecoder.DecodeMesh` overloads accepting `NativeSlice<byte>`: Pass the encoded data as `NativeArray<byte>.ReadOnly` (e.g. via `NativeArray<byte>.AsReadOnly()`) or `byte[]` instead.
- `DracoEncoder.EncodeMesh` overloads with individual speed and quantization parameters: Pass [QuantizationSettings](xref:Unity.Cloud.Draco.Encode.QuantizationSettings) and [SpeedSettings](xref:Unity.Cloud.Draco.Encode.SpeedSettings) instead. Use [QuantizationSettings.FromWorldSize](xref:Unity.Cloud.Draco.Encode.QuantizationSettings.FromWorldSize*) to replace the `worldScale`/`precision` variants.

Things to consider:

- Draco data encoded with bitstream versions older than 2.2 (meshes) or 2.3 (point clouds) can't be decoded. The previous native plug-in didn't support those either.
- WebGL sub-packages (`com.unity.cloud.draco.webgl-*`) installed by earlier versions (pre `5.1.0`) aren't automatically removed anymore. They don't conflict with this version, but you can safely remove them from your project via the Package Manager.

## Trademarks

*Unity* is a registered trademark of [*Unity Technologies*][unity].

*Draco&trade;* is a trademark of [*Google LLC*][GoogleLLC].

[GoogleLLC]: https://about.google/
[unity]: https://unity.com
