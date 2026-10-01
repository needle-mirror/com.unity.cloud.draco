// SPDX-FileCopyrightText: 2024 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if UNITY_6000_5_OR_NEWER
using Unity.Scripting.LifecycleManagement;
#endif
using UnityEngine;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Draco.Editor
{
    class BuildPreProcessor : IPreprocessBuildWithReport
    {
        public const string packagePath = "Packages/com.unity.cloud.draco/Runtime/Plugins/";

#if UNITY_6000_5_OR_NEWER
        // AutoStaticsCleanup does not work with C# 9.0, so we're not using it until Unity 7
        [NoAutoStaticsCleanup]
#endif
        internal static readonly Dictionary<GUID, int> webAssemblyLibraries = new()
        {
            // Database of WebAssembly library files within folder `Runtime/Plugins/WebGL`
            [new GUID("9ab284c4ad5904cf09339d3522f7b10d")] = 2023, // 2023/libdraco_unity.a
        };

        public int callbackOrder => 0;

        void IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
        {
            SetRuntimePluginCopyDelegate(report.summary.platform);
        }

        static void SetRuntimePluginCopyDelegate(BuildTarget platform)
        {
            var allPlugins = PluginImporter.GetImporters(platform);
            foreach (var plugin in allPlugins)
            {
                if (plugin.isNativePlugin
                    && plugin.assetPath.StartsWith(packagePath)
                   )
                {
                    switch (platform)
                    {
                        case BuildTarget.iOS:
                        case BuildTarget.tvOS:
                        case BuildTarget.VisionOS:
                            plugin.SetIncludeInBuildDelegate(IncludeAppleLibraryInBuild);
                            break;
                        case BuildTarget.WebGL:
                            if (webAssemblyLibraries.Keys.Any(libGuid => libGuid == AssetDatabase.GUIDFromAssetPath(plugin.assetPath)))
                            {
                                plugin.SetIncludeInBuildDelegate(IncludeWebLibraryInBuild);
                            }
                            break;
                    }
                }
            }
        }

        static bool IsSimulatorBuild(BuildTarget platformGroup)
        {
            switch (platformGroup)
            {
                case BuildTarget.iOS:
                    return PlayerSettings.iOS.sdkVersion == iOSSdkVersion.SimulatorSDK;
                case BuildTarget.tvOS:
                    return PlayerSettings.tvOS.sdkVersion == tvOSSdkVersion.Simulator;
                case BuildTarget.VisionOS:
                    return PlayerSettings.VisionOS.sdkVersion == VisionOSSdkVersion.Simulator;
            }

            return false;
        }

        static bool IncludeAppleLibraryInBuild(string path)
        {
            var isSimulatorLibrary = IsAppleSimulatorLibrary(path);
            var isSimulatorBuild = IsSimulatorBuild(EditorUserBuildSettings.activeBuildTarget);
            return isSimulatorLibrary == isSimulatorBuild;
        }

        static bool IncludeWebLibraryInBuild(string path)
        {
            return IsWebAssemblyCompatible(path);
        }

        public static bool IsAppleSimulatorLibrary(string assetPath)
        {
            var parent = new DirectoryInfo(assetPath).Parent;

            switch (parent?.Name)
            {
                case "Simulator":
                    return true;
                case "Device":
                    return false;
                default:
                    throw new InvalidDataException(
                        $@"Could not determine SDK type of library ""{assetPath}"". " +
                        @"Apple iOS/tvOS/visionOS native libraries have to be placed in a folder named ""Device"" " +
                        @"or ""Simulator"" for implicit SDK type detection."
                    );
            }
        }

        static bool IsWebAssemblyCompatible(string assetPath)
        {
            var unityVersion = new UnityVersion(Application.unityVersion);

            var pluginGuid = AssetDatabase.GUIDFromAssetPath(assetPath);

            return IsWebAssemblyCompatible(pluginGuid, unityVersion);
        }

        public static bool IsWebAssemblyCompatible(GUID pluginGuid, UnityVersion unityVersion)
        {
            var wasm2023 = new UnityVersion("2023.2.0a17");

            if (webAssemblyLibraries.TryGetValue(pluginGuid, out var majorVersion))
            {
                switch (majorVersion)
                {
                    case 2023:
                        return unityVersion >= wasm2023;
                }
            }

            throw new InvalidDataException($"Unknown WebAssembly library at {AssetDatabase.GUIDToAssetPath(pluginGuid)}.");
        }
    }
}
