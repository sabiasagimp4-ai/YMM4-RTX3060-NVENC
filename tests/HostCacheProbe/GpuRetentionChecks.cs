using System.Reflection;
using NVEncVideoWriterPlugin;

internal static class GpuRetentionChecks
{
    private static void CheckMigration()
    {
        foreach (var test in new[] { (Auto: true, Limit: 2048, Expected: -1), (Auto: false, Limit: 2048, Expected: 2048),
            (Auto: true, Limit: 1024, Expected: 1024), (Auto: false, Limit: 0, Expected: 0), (Auto: true, Limit: -1, Expected: -1) })
        {
            string json = "{\"SettingsVersion\":1,\"PreviewCache\":true,\"ExportCache\":false,\"AutomaticGpuBudget\":"
                + test.Auto.ToString().ToLowerInvariant() + ",\"GpuLimitMiB\":" + test.Limit + "}";
            var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<FrameCacheToolSettings>(json)!;
            settings.Initialize();
            if (settings.GpuLimitMiB != test.Expected || settings.AutomaticGpuBudget != test.Auto || settings.GpuBudgetMigrationVersion != 1
                || !settings.PreviewCache || settings.ExportCache) throw new InvalidOperationException("GPU old default migration changed user settings");
            string saved = YukkuriMovieMaker.Json.Json.GetJsonText(settings);
            var loaded = YukkuriMovieMaker.Json.Json.LoadFromText<FrameCacheToolSettings>(saved)!;
            loaded.Initialize();
            if (loaded.GpuLimitMiB != test.Expected || loaded.GpuBudgetMigrationVersion != 1) throw new InvalidOperationException("GPU migration marker was not saved");
            loaded.GpuLimitMiB = 2048; loaded.AutomaticGpuBudget = true; loaded.Initialize();
            if (loaded.GpuLimitMiB != 2048) throw new InvalidOperationException("GPU migration repeated after later user choice");
        }
        Console.WriteLine("GPU_SETTINGS_MIGRATION: old Auto/2048 only, manual/other limits preserved, serialized marker prevents repeats");
    }

    internal static void Run(Assembly host)
    {
        var fresh = new FrameCacheToolSettings();
        if (!fresh.AutomaticGpuBudget || fresh.GpuLimitMiB != -1) throw new InvalidOperationException("New GPU settings do not use Auto");
        fresh.AutomaticGpuBudget = false;
        if (fresh.GpuLimitMiB != 2048) throw new InvalidOperationException("Disabling Auto did not choose the conservative manual limit");
        fresh.GpuLimitMiB = -1;
        if (!fresh.AutomaticGpuBudget) throw new InvalidOperationException("Choosing Auto did not enable automatic allocation");
        fresh.GpuLimitMiB = int.MaxValue;
        if (fresh.GpuLimitMiB != 8192) throw new InvalidOperationException("GPU upper bound changed");
        var old = Newtonsoft.Json.JsonConvert.DeserializeObject<FrameCacheToolSettings>("{\"AutomaticGpuBudget\":false,\"GpuLimitMiB\":1024}")!;
        if (old.AutomaticGpuBudget || old.GpuLimitMiB != 1024) throw new InvalidOperationException("Existing manual GPU settings changed on load");
        CheckMigration();
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try { GpuFirstRevisitMeasurements.Run(host, regression: true); }
            catch (Exception error) { failure = error; }
            finally { finished.Set(); }
        }) { IsBackground = true, Name = "GPU cold retention and recovery checks" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!finished.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("GPU extension checks exceeded 30 seconds");
        if (failure is not null) throw new InvalidOperationException("GPU extension checks", failure);
    }
}
