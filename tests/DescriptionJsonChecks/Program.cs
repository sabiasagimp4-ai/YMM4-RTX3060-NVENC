using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NVEncVideoWriterPlugin;

var random = new Random(1004);
for (int i = 0; i < 200; ++i)
{
    var model = new Model
    {
        Short = new byte[4095], Long = new byte[4096 + i],
        Parts = new() { ["key.with['punctuation']"] = new byte[5000] },
        Voice = new() { VoiceCache = new byte[5000] },
    };
    random.NextBytes(model.Long); random.NextBytes(model.Voice.VoiceCache);
    string json = FrameDescriptionJson.Serialize(model, [model.Voice.VoiceCache]);
    var token = JObject.Parse(json);
    Check(Convert.FromBase64String((string)token["Long"]!).SequenceEqual(SHA256.HashData(model.Long)), "Wrong content digest");
    Check(Convert.FromBase64String((string)token["Short"]!).Length == 4095, "Threshold changed small payload");
    var clone = FrameDescriptionJson.Load<Model>(json)!;
    Check(clone.Long.SequenceEqual(model.Long) && !ReferenceEquals(clone.Long, model.Long), "Mutable parameter was lost or shared");
    Check(ReferenceEquals(clone.Voice.VoiceCache, model.Voice.VoiceCache), "Verified voice was not shared");
    Check(clone.Parts.Single().Value.SequenceEqual(model.Parts.Single().Value), "Quoted property path lost its payload");
    model.Long[100] ^= 1;
    string changed = FrameDescriptionJson.Serialize(model, [model.Voice.VoiceCache]);
    Check(json != changed, "An in-place byte edit was missed");
    bool rejected = false;
    try { FrameDescriptionJson.Load<Model>(json); }
    catch (Exception) { rejected = true; }
    Check(rejected, "A payload changed after description was adopted");
    rejected = false;
    try { FrameDescriptionJson.Load<Model>(new string(changed.ToCharArray())); }
    catch (Exception) { rejected = true; }
    Check(rejected, "A detached hash description was rendered without its witness");
}
Console.WriteLine("Embedded JSON: 200 models, threshold, SHA-256, quoted paths, verified sharing, mutable copies, edits and detached witness rejection passed.");
static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
internal sealed class Model
{
    public int Format { get; set; } = 3;
    public byte[] Short { get; set; } = [];
    public byte[] Long { get; set; } = [];
    public Dictionary<string, byte[]> Parts { get; set; } = [];
    public Voice Voice { get; set; } = new();
}
internal sealed class Voice { public byte[] VoiceCache { get; set; } = []; }
