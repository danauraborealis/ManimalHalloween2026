using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace Manimal.Halloween2026.Server;

[UsedImplicitly]
public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = ModIdentity.Guid;
    public string Name { get; init; } = ModIdentity.Name;
    public string Author { get; init; } = ModIdentity.Author;
    public List<string>? Contributors { get; init; }
    public Version Version { get; init; } = new(ModIdentity.Version);
    public Range SptVersion { get; init; } = new(ModIdentity.SptRange);
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        [ModIdentity.MapVariantsGuid] = new($">={ModIdentity.MapVariantsVersion} <2.0.0"),
        [ModIdentity.CommonLibGuid] = new($"^{ModIdentity.CommonLibVersion}"),
        // the event's items are its, from this version on
        [ModIdentity.ContentBackportGuid] = new($">={ModIdentity.ContentBackportVersion}"),
    };
    public string? Url { get; init; } = string.IsNullOrEmpty(ModIdentity.SourceUrl) ? null : ModIdentity.SourceUrl;
    public string License { get; init; } = ModIdentity.License;
}
