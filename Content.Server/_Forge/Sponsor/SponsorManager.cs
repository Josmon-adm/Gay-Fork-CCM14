using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared._CCM.Sponsorship;
using Content.Shared._Forge.Sponsor;
using JetBrains.Annotations;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Forge.Sponsor;

[UsedImplicitly]
public sealed class SponsorManager : ISharedSponsorManager
{
    // Forge: the sole source of sponsorship is the Discord-role driven level resolved by
    // DiscordAuthManager. The former CCM bridge (CCMSponsorshipManager) has been folded in
    // here so the CCM perk stack (chat colors/tags, armor/xeno/ghost skins, round-end
    // credits, role-timer bypass) keeps working while there is exactly one source of truth.
    private const string DonateUrl = "https://boosty.to/corvaxforge";

    public readonly Dictionary<NetUserId, SponsorLevel> Sponsors = new();

    [Dependency] private readonly IPlayerManager _players = default!;

    public event Action<NetUserId>? SponsorChanged;

    public void Initialize() { }

    public bool TryGetSponsor(NetUserId user, [NotNullWhen(true)] out SponsorLevel level)
    {
        return Sponsors.TryGetValue(user, out level);
    }

    public bool TryGetSponsorColor(SponsorLevel level, [NotNullWhen(true)] out string? color)
    {
        return SponsorData.SponsorColor.TryGetValue(level, out color);
    }

    public bool TryGetSponsorGhost(SponsorLevel level, [NotNullWhen(true)] out string? ghost)
    {
        return SponsorData.SponsorGhost.TryGetValue(level, out ghost);
    }

    public void SetSponsor(NetUserId user, SponsorLevel level)
    {
        if (level == SponsorLevel.None)
        {
            if (Sponsors.Remove(user))
                SponsorChanged?.Invoke(user);
            return;
        }

        Sponsors[user] = level;
        SponsorChanged?.Invoke(user);
    }

    public void RemoveSponsor(NetUserId user)
    {
        if (Sponsors.Remove(user))
            SponsorChanged?.Invoke(user);
    }

    // --- CCM perk resolution (folded in from the former CCMSponsorshipManager) ---
    // Perk thresholds expressed directly in SponsorLevel terms:
    //   Level1+ : приоритетный вход, цвет OOC, ckey в конце раунда
    //   Level2+ : цвет LOOC, готовый OOC-тег, базовая кастомизация (обход таймеров ролей)
    //   Level3+ : скин призрака, скины ксено, свой OOC-тег, расширенная кастомизация

    public CCMSponsorshipStatusSnapshot GetStatus(NetUserId userId)
    {
        if (!TryGetSponsor(userId, out var level) || level == SponsorLevel.None)
            return EmptySnapshot();

        var oocColor = SponsorData.SponsorColor.GetValueOrDefault(level, GetDefaultColor(level, false));

        return new CCMSponsorshipStatusSnapshot(
            level,
            DonateUrl,
            // Discord-role sponsorship is re-checked on every connect, so there is no
            // meaningful expiration to show; 0 renders as "permanent" on the client.
            0,
            oocColor,
            GetDefaultColor(level, true),
            level >= SponsorLevel.Level2);
    }

    public bool HasRoleTimerBypass(NetUserId userId)
    {
        return TryGetSponsor(userId, out var level) && level >= SponsorLevel.Level2;
    }

    public bool HasRoleTimerBypass(ICommonSession session)
    {
        return HasRoleTimerBypass(session.UserId);
    }

    public bool TryGetChatColorHex(NetUserId userId, bool looc, out string colorHex)
    {
        var status = GetStatus(userId);
        colorHex = looc ? status.LoocColorHex : status.OocColorHex;
        return !string.IsNullOrWhiteSpace(colorHex);
    }

    public IReadOnlyList<(string Ckey, SponsorLevel Level)> GetConnectedSponsorsForCredits()
    {
        return _players.Sessions
            .Select(session => (session.Name, Level: TryGetSponsor(session.UserId, out var level) ? level : SponsorLevel.None))
            .Where(entry => entry.Level != SponsorLevel.None)
            .OrderByDescending(entry => entry.Level)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (entry.Name, entry.Level))
            .ToList();
    }

    private static CCMSponsorshipStatusSnapshot EmptySnapshot()
    {
        return new CCMSponsorshipStatusSnapshot(
            SponsorLevel.None,
            DonateUrl,
            0,
            string.Empty,
            string.Empty,
            false);
    }

    private static string GetDefaultColor(SponsorLevel level, bool looc)
    {
        return level switch
        {
            SponsorLevel.Level1 => looc ? "#7FD7FF" : "#61C9FF",
            SponsorLevel.Level2 => looc ? "#F2A7FF" : "#D96CFF",
            >= SponsorLevel.Level3 => looc ? "#FFE082" : "#F6C453",
            _ => string.Empty,
        };
    }
}
