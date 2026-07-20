// Forge port: this manager used to back patron/sponsor state with its own DB tier
// and a custom Discord OAuth flow. After porting the Monolith _Forge Discord auth
// (run at connect time, see DiscordAuthManager) and SponsorManager (level-driven),
// tier flags now come straight from SponsorLevel. Per-user customization
// (ghost color, lobby message, marine/xeno shoutouts) and the patron list used by
// figurines/credits keep using the existing RMCPatron DB rows.
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Forge.Sponsor;
using Content.Server.Database;
using Content.Shared._Forge.Sponsor;
using Content.Shared._RMC14.LinkAccount;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._RMC14.LinkAccount;

public sealed class LinkAccountManager : IPostInjectInit
{
    [Dependency] private readonly ILogManager _log = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly UserDbDataManager _userDb = default!;
    [Dependency] private readonly SponsorManager _sponsors = default!;

    private ISawmill _sawmill = default!;
    private readonly Dictionary<NetUserId, SharedRMCPatronFull> _connected = new();
    private readonly Dictionary<NetUserId, SharedRMCPatron> _allPatrons = [];
    private readonly HashSet<Guid> _figurines = [];

    public event Action? PatronsReloaded;
    public event Action<(NetUserId Id, SharedRMCPatronFull Patron)>? PatronUpdated;

    private async Task LoadData(ICommonSession player, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        await RefreshConnected(player.UserId, cancel);
    }

    private void FinishLoad(ICommonSession player)
    {
        SendPatronStatus(player);
    }

    private void ClientDisconnected(ICommonSession player)
    {
        _connected.Remove(player.UserId);
    }

    private void SendPatronStatus(ICommonSession player)
    {
        var connected = _connected.GetValueOrDefault(player.UserId);
        _net.ServerSendMessage(new LinkAccountStatusMsg { Patron = connected }, player.Channel);
        SendPatrons(player);
    }

    private void SendPatronStatus(NetUserId user)
    {
        if (_player.TryGetSessionById(user, out var session))
            SendPatronStatus(session);
    }

    private async void OnClearGhostColor(RMCClearGhostColorMsg message)
    {
        await RunLogged(async () =>
        {
            await _db.SetGhostColor(message.MsgChannel.UserId.UserId, null);
            await RefreshAndSend(message.MsgChannel.UserId);
        });
    }

    private async void OnChangeGhostColor(RMCChangeGhostColorMsg message)
    {
        await RunLogged(async () =>
        {
            await _db.SetGhostColor(message.MsgChannel.UserId.UserId, System.Drawing.Color.FromArgb(message.Color.ToArgb()));
            await RefreshAndSend(message.MsgChannel.UserId);
        });
    }

    private async void OnChangeLobbyMessage(RMCChangeLobbyMessageMsg message)
    {
        await RunLogged(async () =>
        {
            var text = message.Text ?? string.Empty;
            if (text.Length > SharedRMCLobbyMessage.CharacterLimit)
                text = text[..SharedRMCLobbyMessage.CharacterLimit];

            await _db.SetLobbyMessage(message.MsgChannel.UserId.UserId, text);
            await RefreshAndSend(message.MsgChannel.UserId);
        });
    }

    private async void OnChangeMarineShoutout(RMCChangeMarineShoutoutMsg message)
    {
        await RunLogged(async () =>
        {
            var name = message.Name ?? string.Empty;
            if (name.Length > SharedRMCRoundEndShoutouts.CharacterLimit)
                name = name[..SharedRMCRoundEndShoutouts.CharacterLimit];

            await _db.SetMarineShoutout(message.MsgChannel.UserId.UserId, name);
            await RefreshAndSend(message.MsgChannel.UserId);
        });
    }

    private async void OnChangeXenoShoutout(RMCChangeXenoShoutoutMsg message)
    {
        await RunLogged(async () =>
        {
            var name = message.Name ?? string.Empty;
            if (name.Length > SharedRMCRoundEndShoutouts.CharacterLimit)
                name = name[..SharedRMCRoundEndShoutouts.CharacterLimit];

            await _db.SetXenoShoutout(message.MsgChannel.UserId.UserId, name);
            await RefreshAndSend(message.MsgChannel.UserId);
        });
    }

    public async Task RefreshAllPatrons()
    {
        _allPatrons.Clear();
        _figurines.Clear();
        var patrons = await _db.GetAllPatrons();
        foreach (var patron in patrons)
        {
            _allPatrons[new NetUserId(patron.PlayerId)] = ToSharedPatron(patron);
            if (patron.Tier.Figurines)
                _figurines.Add(patron.PlayerId);
        }

        PatronsReloaded?.Invoke();
    }

    public void SendPatronsToAll()
    {
        _net.ServerSendToAll(new RMCPatronListMsg { Patrons = [.. _allPatrons.Values] });
    }

    private void SendPatrons(ICommonSession player)
    {
        _net.ServerSendMessage(new RMCPatronListMsg { Patrons = [.. _allPatrons.Values] }, player.Channel);
    }

    public SharedRMCPatronFull? GetConnectedPatron(ICommonSession player)
    {
        return GetConnectedPatron(player.UserId);
    }

    public SharedRMCPatronFull? GetConnectedPatron(NetUserId userId)
    {
        return _connected.GetValueOrDefault(userId);
    }

    public bool TryGetPatron(NetUserId userId, out SharedRMCPatron? tier)
    {
        return _allPatrons.TryGetValue(userId, out tier);
    }

    public IReadOnlySet<Guid> GetFigurines()
    {
        return _figurines;
    }

    public string GetPatronOOCHexColor(NetUserId userId)
    {
        if (_sponsors.TryGetSponsor(userId, out var level) && SponsorData.SponsorColor.TryGetValue(level, out var color))
            return color;

        return "#FFFFFF";
    }

    private async Task RefreshAndSend(NetUserId userId)
    {
        await RefreshConnected(userId, CancellationToken.None);
        SendPatronStatus(userId);
    }

    private async Task RunLogged(Func<Task> task)
    {
        try
        {
            await task();
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to update patron data:\n{e}");
        }
    }

    private async Task RefreshConnected(NetUserId userId, CancellationToken cancel)
    {
        var patron = await _db.GetPatron(userId.UserId, cancel);
        var full = ToSharedPatronFull(userId, patron);
        _connected[userId] = full;
        PatronUpdated?.Invoke((userId, full));
    }

    private SharedRMCPatronFull ToSharedPatronFull(NetUserId userId, RMCPatron? patron)
    {
        // Tier flags come from Forge SponsorLevel; the DB patron row (if present)
        // only provides the displayed tier name and per-user customization storage.
        _sponsors.TryGetSponsor(userId, out var level);
        var linked = level != SponsorLevel.None;

        var dbTierName = patron?.Tier?.Name;
        var tier = BuildTierFromLevel(level, dbTierName);

        Color? ghostColor = patron?.GhostColor == null
            ? null
            : System.Drawing.Color.FromArgb(patron.GhostColor.Value);

        var lobbyMessage = patron?.LobbyMessage?.Message == null
            ? null
            : new SharedRMCLobbyMessage(patron.LobbyMessage.Message);

        SharedRMCRoundEndShoutouts? shoutouts = null;
        if (patron?.RoundEndMarineShoutout != null || patron?.RoundEndXenoShoutout != null)
        {
            shoutouts = new SharedRMCRoundEndShoutouts(
                patron.RoundEndMarineShoutout?.Name,
                patron.RoundEndXenoShoutout?.Name);
        }

        return new SharedRMCPatronFull(tier, linked, ghostColor, lobbyMessage, shoutouts);
    }

    /// <summary>
    ///     Maps a Forge sponsor level onto the patron feature flags consumed by client UI,
    ///     figurine/named-item systems and chat. Levels stack — each tier unlocks the
    ///     previous tier's perks plus its own.
    /// </summary>
    private static SharedRMCPatronTier? BuildTierFromLevel(SponsorLevel level, string? dbTierName)
    {
        if (level == SponsorLevel.None)
            return null;

        SponsorData.SponsorNames.TryGetValue(level, out var defaultName);
        var name = dbTierName ?? defaultName ?? level.ToString();

        var showOnCredits = true;
        var lobbyMessage = level >= SponsorLevel.Level1;
        var roundEndShoutout = level >= SponsorLevel.Level2;
        var ghostColor = level >= SponsorLevel.Level3;
        var namedItems = level >= SponsorLevel.Level4;
        var figurines = level >= SponsorLevel.Level5;

        return new SharedRMCPatronTier(
            showOnCredits,
            ghostColor,
            namedItems,
            figurines,
            lobbyMessage,
            roundEndShoutout,
            name);
    }

    private static SharedRMCPatron ToSharedPatron(RMCPatron patron)
    {
        return new SharedRMCPatron(patron.Player.LastSeenUserName, patron.Tier.Name);
    }

    private void OnSponsorChanged(NetUserId userId)
    {
        // Refresh tier flags when SponsorManager learns a new level (e.g. after Discord auth).
        _ = RefreshAndSend(userId);
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _log.GetSawmill("rmc.link_account");
        _net.RegisterNetMessage<LinkAccountStatusMsg>();
        _net.RegisterNetMessage<RMCPatronListMsg>();
        _net.RegisterNetMessage<RMCClearGhostColorMsg>(OnClearGhostColor);
        _net.RegisterNetMessage<RMCChangeGhostColorMsg>(OnChangeGhostColor);
        _net.RegisterNetMessage<RMCChangeLobbyMessageMsg>(OnChangeLobbyMessage);
        _net.RegisterNetMessage<RMCChangeMarineShoutoutMsg>(OnChangeMarineShoutout);
        _net.RegisterNetMessage<RMCChangeXenoShoutoutMsg>(OnChangeXenoShoutout);
        _userDb.AddOnLoadPlayer(LoadData);
        _userDb.AddOnFinishLoad(FinishLoad);
        _userDb.AddOnPlayerDisconnect(ClientDisconnected);

        _sponsors.SponsorChanged += OnSponsorChanged;
    }
}
