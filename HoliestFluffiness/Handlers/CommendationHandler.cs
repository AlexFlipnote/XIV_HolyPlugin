using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HoliestFluffiness.Handlers;

public sealed class CommendationHandler : IDisposable
{
    private readonly Configuration configuration;
    private readonly IClientState  clientState;
    private readonly IFramework    framework;
    private readonly IPartyList    partyList;

    private short lastCommendationCount;
    private int   currentPartySize;
    private int   largestPartySize;
    private int   lastAreaPartySize;
    private bool  justLoggedIn;

    public event Action<int, int>? OnCommendation; // commendCount, matchmadePlayers

    public CommendationHandler(Configuration configuration, IClientState clientState, IFramework framework, IPartyList partyList)
    {
        this.configuration = configuration;
        this.clientState   = clientState;
        this.framework     = framework;
        this.partyList     = partyList;

        clientState.Login            += OnLogin;
        clientState.TerritoryChanged += OnTerritoryChanged;
        framework.Update             += OnUpdate;

        if (clientState.IsLoggedIn) OnLogin();
    }

    private void OnLogin()
    {
        lastCommendationCount = GetCommendations();
        currentPartySize = largestPartySize = lastAreaPartySize = Math.Max(partyList.Length, 1);
        justLoggedIn = true;
    }

    private void OnUpdate(IFramework fw)
    {
        if (!clientState.IsLoggedIn) return;
        currentPartySize = Math.Max(partyList.Length, 1);
        if (currentPartySize > largestPartySize) largestPartySize = currentPartySize;
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (!clientState.IsLoggedIn) return;
        var current = GetCommendations();
        if (!justLoggedIn && current > lastCommendationCount && configuration.CommendationEnabled)
            OnCommendation?.Invoke(current - lastCommendationCount, Math.Max(largestPartySize - lastAreaPartySize, 1));
        justLoggedIn          = false;
        lastCommendationCount = current;
        lastAreaPartySize     = currentPartySize;
        currentPartySize      = Math.Max(partyList.Length, 1);
        largestPartySize      = currentPartySize;
    }

    private static unsafe short GetCommendations() => PlayerState.Instance()->PlayerCommendations;

    public void Dispose()
    {
        clientState.Login            -= OnLogin;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        framework.Update             -= OnUpdate;
    }
}
