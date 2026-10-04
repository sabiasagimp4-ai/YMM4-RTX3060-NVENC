using System.Reflection;
using NVEncVideoWriterPlugin;

internal static class GpuRetentionChecks
{
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
