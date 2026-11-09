# What's new

## Version 6.0

### C# Implementation

Encoding and decoding are implemented in C# now, replacing the native Draco plug-in binaries. As a result:

- Every platform Unity supports is expected to work, without platform or Unity version specific binaries.

#### Performance

Decoding performance is on par with the native plug-in of version 5, or better. Decoding runs in [Burst](https://docs.unity3d.com/Packages/com.unity.burst@latest) compiled jobs, so the performance hardly depends on the scripting backend (IL2CPP or CoreCLR).

Measurements in a macOS standalone player (Apple M4 Max), comparing version 6 to version 5:

- Decoding meshes with ten thousand vertices or more takes about 10% less time (4% to 18% less, depending on the mesh and settings), with both IL2CPP (Unity 6.3) and CoreCLR (Unity 7.0).
- Decoding small meshes (under 1 ms) takes as long as before. The frame overhead of the asynchronous API dominates their decoding time.
- Decoding many small meshes in parallel can be slower. Decoding 8 to 32 small meshes at once took up to 1.7 times as long with IL2CPP. With CoreCLR, the difference was small.

Encoding performance was not measured. Other CPU architectures and platforms (for example, mobile or Web) may show different results.
