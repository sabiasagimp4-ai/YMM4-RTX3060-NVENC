namespace NVEncVideoWriterPlugin;

// Exact host builds whose hooked internals were inspected. 4.56.1.0 was read with ILSpy from the official Lite zip
// (see docs/HOST_CONTRACTS.md); other builds use per-feature contract evaluation for optional cache support.
// Shared with tools/HostFingerprint (the version compatibility scan), so it holds data only.
internal static class HostKnownBuilds
{
    internal sealed record Binary(string File, Guid Mvid, string Sha256);
    internal sealed record Build(string Version, Binary Host, Binary Plugin, Binary Settings);

    internal static readonly Build[] All =
    [
        new("4.55.1.1",
            new("YukkuriMovieMaker.dll", Guid.Parse("5c07056d-022e-4d0f-a83d-ae0fa3b393f5"), "30E0B5E81FAA292F7968F3702446E54B3F4A5D319E33CC2EB38F2EF843606B9C"),
            new("YukkuriMovieMaker.Plugin.dll", Guid.Parse("78fce2a0-1106-4489-a080-938ae746bf7e"), "4713BBE55855A39D3BB340A7B8AB204711529929C2C12A69F1BEE54A9A7F60FF"),
            new("YukkuriMovieMaker.Settings.dll", Guid.Parse("5ddcb7f0-06f1-449a-bbc2-ecd8b3257b3c"), "F9339259B6C28C987DA0CBD416D983715A84A149F9D99D2ACA76F37879E0D0DF")),
        new("4.56.1.0",
            new("YukkuriMovieMaker.dll", Guid.Parse("23e5b5b5-adcf-43b7-b976-b6b63f8dadea"), "90D5022E2F4B46631254FAF788A1DF8A420071D9A190644FCC29892EC44F439D"),
            new("YukkuriMovieMaker.Plugin.dll", Guid.Parse("ddaa2ae6-046b-450c-9e9d-0c4e30ba9251"), "B99938260AA96A54DD2665D38A5A26889E70988CF8CC6293CD0EFDC88A81C0DF"),
            new("YukkuriMovieMaker.Settings.dll", Guid.Parse("88a1cec9-69bc-43bd-82ad-c9f3417cd271"), "8CB040510EF5E49289B47D60CF08E337CD9235D9577E49728D18404384CB22DA")),
    ];

    internal static string Versions => string.Join(" / ", All.Select(known => known.Version));
}
