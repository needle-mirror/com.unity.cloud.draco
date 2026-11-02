# Prerequisites

## Platform support

*Draco for Unity* supports the following platforms:

- Web
- iOS
- tvOS
- visionOS
- Android
  - Chrome OS (via Android)
  - Magic Leap 2 (via Android)
- Windows
- Universal Windows Platform
- macOS
- Linux (x86_64)
- Embedded Linux

## Compatibility

The *Draco for Unity* package is compatible with Unity version of 6000.0 or later.

## Visual C++ runtime on Windows

The native Windows library (`draco_unity.dll`) links the Microsoft Visual C++ runtime dynamically. On Windows (x86, x64, and ARM64) it depends on these DLLs of the latest [Microsoft Visual C++ v14 Redistributable][VCRedist]:

- `MSVCP140.dll`
- `VCRUNTIME140.dll`
- `VCRUNTIME140_1.dll` (x64 only)

If they're missing, `draco_unity.dll` fails to load and Draco meshes can't be decoded or encoded.

Unity 6.7 and newer ship these DLLs with Windows players, so no further action is required.

With earlier Unity versions, end users' machines might not have the redistributable installed. Make sure your application provides it, for example:

- Run the redistributable installer as part of your application's installer (for example `vc_redist.x64.exe /install /quiet /norestart`). Distribution platforms like Steam can install it as a common redistributable.
- Copy the DLLs next to your application's executable (app-local deployment). Refer to [Redistributing Visual C++ files][VCRedistFiles].

## Troubleshooting

Refer to the [troubleshooting](troubleshooting.md#native-library-on-windows) section for help with loading the native library on Windows.

[VCRedist]: https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist
[VCRedistFiles]: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
