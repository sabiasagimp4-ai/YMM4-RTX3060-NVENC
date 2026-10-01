string path = Path.Combine(Path.GetTempPath(), "ymm-frame-store-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(path);
try { StoreChecks.Run(path); CacheBudgetChecks.Run(path); IdleFramePlanChecks.Run(); TimeKeyChecks.Run(); ModelSplitChecks.Run(); PreviewRectsChecks.Run(); FrameDependencyChecks.Run(); CacheBarChecks.Run(); HostContractChecks.Run(); }
finally { Directory.Delete(path, recursive: true); }
