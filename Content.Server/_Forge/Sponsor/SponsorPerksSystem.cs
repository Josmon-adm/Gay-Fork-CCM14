// Forge: replaces the former CCM bridge (Content.Server/_CCM/Sponsorship/CCMSponsorshipSystem).
// This thin EntitySystem only carries the client network protocol for the CCM sponsorship/
// customization UI and appends round-end sponsor credits. All tier resolution now lives in
// the _Forge SponsorManager, and the perks themselves live in CCMCustomizationManager /
// CCMCustomizationApplySystem (unchanged).
using System.Text;
using System.Threading.Tasks;
using Content.Server._CCM.Sponsorship;
using Content.Server.GameTicking;
using Content.Shared._CCM.Sponsorship;
using Content.Shared._Forge.Sponsor;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Forge.Sponsor;

public sealed class SponsorPerksSystem : EntitySystem
{
    [Dependency] private readonly SponsorManager _sponsor = default!;
    [Dependency] private readonly CCMCustomizationManager _customization = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override void Initialize()
    {
        SubscribeNetworkEvent<RequestCCMSponsorshipStatusEvent>(OnRequestStatus);
        SubscribeNetworkEvent<RequestCCMCustomizationEvent>(OnRequestCustomization);
        SubscribeNetworkEvent<SaveCCMCustomizationEvent>(OnSaveCustomization);

        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndTextAppend);

        _sponsor.SponsorChanged += OnSponsorChanged;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _sponsor.SponsorChanged -= OnSponsorChanged;
    }

    public void PushStatus(ICommonSession session)
    {
        RaiseNetworkEvent(new CCMSponsorshipStatusResponseEvent(_sponsor.GetStatus(session.UserId)),
            session.Channel);
    }

    public async Task PushCustomization(ICommonSession session)
    {
        var snapshot = await _customization.GetSnapshot(session.UserId);
        if (session.AttachedEntity is { Valid: true } attached)
            EntityManager.System<CCMCustomizationApplySystem>().ApplyCustomization(attached, snapshot);

        RaiseNetworkEvent(new CCMCustomizationResponseEvent(snapshot), session.Channel);
    }

    private void OnSponsorChanged(NetUserId userId)
    {
        if (!_players.TryGetSessionById(userId, out var session))
            return;

        PushStatus(session);
        // The customization cache may have been normalized at tier None before the Discord
        // auth resolved the real level; drop it so the next snapshot re-reads from the DB.
        _customization.InvalidateCache(userId);
        _ = PushCustomization(session);
    }

    private void OnRequestStatus(RequestCCMSponsorshipStatusEvent ev, EntitySessionEventArgs args)
    {
        PushStatus(args.SenderSession);
    }

    private async void OnRequestCustomization(RequestCCMCustomizationEvent ev, EntitySessionEventArgs args)
    {
        var snapshot = await _customization.GetSnapshot(args.SenderSession.UserId);
        RaiseNetworkEvent(new CCMCustomizationResponseEvent(snapshot), args.SenderSession.Channel);
    }

    private async void OnSaveCustomization(SaveCCMCustomizationEvent ev, EntitySessionEventArgs args)
    {
        var snapshot = await _customization.SaveSnapshot(
            args.SenderSession.UserId,
            new CCMCustomizationSnapshot(
                ev.Selections,
                ev.SelectedOocTagId,
                ev.CustomOocTagText,
                ev.SelectedOocColorId,
                ev.SelectedLoocColorId));

        if (args.SenderSession.AttachedEntity is { Valid: true } attached)
            EntityManager.System<CCMCustomizationApplySystem>().ApplyCustomization(attached, snapshot);

        RaiseNetworkEvent(new CCMCustomizationResponseEvent(snapshot), args.SenderSession.Channel);
    }

    private void OnRoundEndTextAppend(RoundEndTextAppendEvent ev)
    {
        var sponsors = _sponsor.GetConnectedSponsorsForCredits();
        if (sponsors.Count == 0)
            return;

        var builder = new StringBuilder();
        builder.Append("[bold][color=#D9DDE3]");
        builder.Append(Loc.GetString("ccm-sponsorship-endgame-header"));
        builder.Append("[/color][/bold]\n");

        foreach (var (ckey, level) in sponsors)
        {
            builder.Append("[color=");
            builder.Append(GetLevelColor(level));
            builder.Append(']');
            builder.Append(ckey);
            builder.Append("[/color] [color=#8E9AA8](");
            builder.Append(SponsorData.SponsorNames.GetValueOrDefault(level, level.ToString()));
            builder.Append(")[/color]\n");
        }

        foreach (var line in builder.ToString().TrimEnd().Split('\n'))
        {
            ev.AddLine(line);
        }
    }

    private static string GetLevelColor(SponsorLevel level)
    {
        // Цвет ника в кредитах = цвет роли из SponsorData, с откатом на пороговый цвет перков.
        if (SponsorData.SponsorColor.TryGetValue(level, out var roleColor))
            return roleColor;

        return level switch
        {
            >= SponsorLevel.Level3 => "#F6C453",
            SponsorLevel.Level2 => "#D96CFF",
            _ => "#61C9FF",
        };
    }
}
