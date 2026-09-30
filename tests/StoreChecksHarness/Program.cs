string path = Path.Combine(Path.GetTempPath(), "ymm-frame-store-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(path);
try { StoreChecks.Run(path); TimeKeyChecks.Run(); }
finally { Directory.Delete(path, recursive: true); }
