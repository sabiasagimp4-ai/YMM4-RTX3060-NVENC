# Host cache hook probe

Test-only .NET 10 console probe; no host binary is modified or copied. Harmony is
loaded only by this project. Supply your installed YMM4 directory:

```powershell
dotnet run --project tests/HostCacheProbe -c Release -- 'D:\YukkuriMovieMaker_v4_Lite' --gpu
```

The probe checks internal `TimelineSource.Update` and `Dispose(bool)` signatures,
patches both with Harmony 2.4.2, executes their prefix/postfix/skip paths, and
verifies unpatch restores original execution. `--gpu` additionally constructs
host graphics devices and an empty scene, executes the real renderer, substitutes
the source-owned closed command list, and checks `Output` and normal GPU cleanup.

Passing this proves the hook mechanism for the tested installation, not cache
pixel parity, arbitrary effects, UI geometry, asset invalidation or host GUI
integration. Unknown host versions must not silently assume this contract.
