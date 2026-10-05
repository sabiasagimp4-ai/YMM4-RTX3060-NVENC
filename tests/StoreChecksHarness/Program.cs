string path = Path.Combine(Path.GetTempPath(), "ymm-frame-store-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(path);
try { DynamicComputeChecks.Run(); FrameAdmissionChecks.Run(); TraceChecks.Run(path); StoreChecks.Run(path); CompressionChecks.Run(path); CacheBudgetChecks.Run(path); GpuBudgetChecks.Run(); NvencErrorChecks.Run(); IdleFramePlanChecks.Run(); IdleWorkerChecks.Run(); RenderModelComparisonChecks.Run(); TimeKeyChecks.Run(); ModelSplitChecks.Run(); PreviewRectsChecks.Run(); FrameDependencyChecks.Run(); CacheBarChecks.Run(); HostContractChecks.Run(); }
finally { Directory.Delete(path, recursive: true); }
