# Troubleshooting

## Native Library on Windows

If you run into an error message similar to this one:

```log
Failed to load 'draco_unity.dll' because one or more of its dependencies could not be loaded
```

The Microsoft Visual C++ runtime is likely missing on this machine. Refer to [Visual C++ runtime on Windows](prerequisites.md#visual-c-runtime-on-windows) for how to install it or ship it with your application.
