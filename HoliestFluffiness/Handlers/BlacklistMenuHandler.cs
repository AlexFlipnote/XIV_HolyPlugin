using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace HoliestFluffiness.Handlers;

// Adds "Add to Blacklist" to the native right-click menu on players in the world and on the
// target / focus target bars, where the game itself only offers it from chat.
public sealed class BlacklistMenuHandler : IDisposable
{
    private readonly Configuration   config;
    private readonly IContextMenu    contextMenu;
    private readonly IObjectTable    objectTable;
    private readonly ITargetManager  targetManager;

    public BlacklistMenuHandler(Configuration config, IContextMenu contextMenu, IObjectTable objectTable, ITargetManager targetManager)
    {
        this.config         = config;
        this.contextMenu    = contextMenu;
        this.objectTable    = objectTable;
        this.targetManager  = targetManager;

        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!config.BlacklistContextMenuEnabled) return;
        if (args.MenuType != ContextMenuType.Default) return;

        // null = right-clicking a character in the world
        if (args.AddonName is not (null or "_TargetInfo" or "_TargetInfoMainTarget" or "_FocusTargetInfo")) return;

        if (args.Target is not MenuTargetDefault target) return;
        if (objectTable.SearchById(target.TargetObjectId) is not IPlayerCharacter) return;
        if (Common.TryGetLocalPlayer(objectTable, out var self) && target.TargetObjectId == self.GameObjectId) return;

        var objectId = target.TargetObjectId;
        args.AddMenuItem(new MenuItem
        {
            Name        = "Add to Blacklist",
            PrefixChar  = 'H',
            PrefixColor = 548, // UIColor gold (#F1C600)
            OnClicked   = _ =>
            {
                // /blacklist add only accepts placeholders, not names
                if (objectTable.SearchById(objectId) is IPlayerCharacter pc)
                    Common.ExecuteCommandOnTarget(targetManager, pc, "/blacklist add <t>");
            },
        });
    }

    public void Dispose()
    {
        contextMenu.OnMenuOpened -= OnMenuOpened;
    }
}
