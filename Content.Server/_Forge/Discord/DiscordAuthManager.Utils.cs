using System.Text.Json.Serialization;
using Content.Shared._Forge.DiscordAuth;

namespace Content.Server._Forge.Discord;

public sealed partial class DiscordAuthManager
{
    private DiscordData CreateError(string localizationKey)
    {
        return new DiscordData(false, null, Loc.GetString(localizationKey));
    }

    private DiscordData UnauthorizedErrorData() => CreateError("st-not-authorized-error-text");
    private DiscordData NotInGuildErrorData() => CreateError("st-not-in-guild");
    private DiscordData EmptyResponseErrorData() => CreateError("st-service-response-empty");
    private DiscordData EmptyResponseErrorRoleData() => CreateError("st-guild-role-empty");
    private DiscordData ServiceUnreachableErrorData() => CreateError("st-service-unreachable");
    private DiscordData UnexpectedErrorData() => CreateError("st-unexpected-error");

    private sealed class DiscordUuidResponse
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = null!;

        [JsonPropertyName("discord_id")]
        public string DiscordId { get; set; } = null!;
    }

    private sealed class DiscordLinkResponse
    {
        [JsonPropertyName("link")]
        public string Link { get; set; } = default!;
    }

    private sealed class RolesResponse
    {
        [JsonPropertyName("roles")]
        public string[] Roles { get; set; } = [];
    }
}
