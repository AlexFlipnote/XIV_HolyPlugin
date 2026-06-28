using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class DynamicTravelerHandler : IDisposable
{
    private readonly Configuration config;
    private readonly INamePlateGui  namePlateGui;
    private readonly IDataManager   dataManager;

    public DynamicTravelerHandler(Configuration config, INamePlateGui namePlateGui, IDataManager dataManager)
    {
        this.config       = config;
        this.namePlateGui = namePlateGui;
        this.dataManager  = dataManager;
        namePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
    }

    private void OnNamePlateDataUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!config.DynamicTravelerEnabled) return;

        var worldSheet = dataManager.GetExcelSheet<World>();

        foreach (var handler in handlers)
        {
            if (handler.NamePlateKind != NamePlateKind.PlayerCharacter) continue;

            var player = handler.PlayerCharacter;
            if (player == null) continue;

            var chara = (BattleChara*)player.Address;
            if (chara == null) continue;

            var homeWorld    = chara->Character.HomeWorld;
            var currentWorld = chara->Character.CurrentWorld;
            if (homeWorld == currentWorld) continue;

            var homeRow = worldSheet?.GetRowOrDefault(homeWorld);
            if (homeRow == null) continue;

            var worldName = homeRow.Value.Name.ExtractText();

            var tag = new SeStringBuilder()
                .AddText(" «")
                .AddIcon(BitmapFontIcon.CrossWorld)
                .AddText(worldName)
                .AddText("» ")
                .Build();

            handler.FreeCompanyTag = tag;
        }
    }

    public void Dispose()
    {
        namePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;
    }
}
