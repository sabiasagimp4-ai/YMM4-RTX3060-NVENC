using Newtonsoft.Json;

// The portable check exercises the production converter and witnesses with ordinary Json.NET. Real-host
// settings and pixel parity are checked independently on Windows by CacheChecks and HostCacheProbe.
namespace YukkuriMovieMaker.Json;
public static class Json
{
    private static readonly JsonSerializerSettings settings = new() { TypeNameHandling = TypeNameHandling.Auto };
    public static string GetJsonText<T>(T value, JsonSerializerSettings? options = null) =>
        JsonConvert.SerializeObject(value, options ?? settings);
    public static T? LoadFromText<T>(string text) => JsonConvert.DeserializeObject<T>(text, settings);
}
