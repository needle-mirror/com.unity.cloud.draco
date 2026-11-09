// SPDX-FileCopyrightText: 2025 Unity Technologies and the Draco for Unity authors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("Unity.Cloud.Draco.Tests")]
// The encoder assembly drives the codec directly, just like the decoder does.
[assembly: InternalsVisibleTo("Unity.Cloud.Draco.Encode")]
