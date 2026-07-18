using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HoliestFluffiness.Handlers;

public sealed unsafe class ClientTweaksHandler : IDisposable
{
    private readonly Configuration   config;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework      framework;
    private readonly IntPtr          windowHandle;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);
    private const int WM_CLOSE = 0x10;

    // Confirmation dialogs whose default cursor/focus "Always Yes" moves onto the yes button.
    private static readonly string[] AlwaysYesAddons =
    {
        "SelectYesno", "ContentsFinderConfirm", "ShopCardDialog", "RetainerTaskAsk",
        "RetainerItemTransferList", "RetainerTaskResult", "MateriaAttachDialog",
        "MaterializeDialog", "MateriaRetrieveDialog", "MiragePrismRemove",
        "MiragePrismMiragePlateConfirm", "SalvageDialog", "PurifyResult",
        "LobbyWKTCheck", "LobbyDKTWorldList", "LobbyDKTCheckExec",
        "ShopExchangeItemDialog", "FGSExitDialog",
    };

    public ClientTweaksHandler(Configuration config, IAddonLifecycle addonLifecycle, IFramework framework, IntPtr windowHandle)
    {
        this.config         = config;
        this.addonLifecycle = addonLifecycle;
        this.framework      = framework;
        this.windowHandle   = windowHandle;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup,           "_ActionBar", OnActionBarUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_ActionBar", OnActionBarUpdate);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, AlwaysYesAddons, OnAlwaysYesSetup);
        framework.Update += OnFrameworkUpdate;

        if (config.HotbarLockHidden)
            ApplyHotbarLockHide();
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        // Alt + F4 closes the game safely (ported from SimpleTweaks). WM_CLOSE runs the
        // normal shutdown path rather than killing the process.
        if (config.AltF4ExitEnabled)
        {
            var input = UIInputData.Instance();
            if (input != null && input->IsKeyDown(SeVirtualKey.MENU) && input->IsKeyPressed(SeVirtualKey.F4))
                SendMessage(windowHandle, WM_CLOSE, 0, 0);
        }

        if (config.TitleMovieDisabled)
        {
            var agent = AgentLobby.Instance();
            if (agent != null) agent->IdleTime = 0;
        }
    }

    // "Always Yes": ported from SimpleTweaks. Moves the default cursor/focus onto the yes
    // button (or the checkbox, when one exists and yes is disabled) of confirmation dialogs,
    // so pressing confirm (num 0) accepts without arrowing over first.
    private void OnAlwaysYesSetup(AddonEvent type, AddonArgs args)
    {
        if (!config.AlwaysYesEnabled) return;

        var addon = args.Addon.Address;
        switch (args.AddonName)
        {
            case "SelectYesno":                   DelayedSetFocusYes(args.AddonName, 8, 9, 4, 1); break;
            case "ContentsFinderConfirm":         SetFocusYes(addon, 63); break;
            case "ShopCardDialog":                SetFocusYes(addon, 16); break;
            case "RetainerTaskAsk":               SetFocusYes(addon, 40); break;
            case "RetainerItemTransferList":      SetFocusYes(addon, 7); break;
            case "RetainerTaskResult":            SetFocusYes(addon, 20); break;
            case "MateriaAttachDialog":           SetFocusYes(addon, 35, null, 39); break;
            case "MaterializeDialog":             SetFocusYes(addon, 13); break;
            case "MateriaRetrieveDialog":         SetFocusYes(addon, 17); break;
            case "MiragePrismRemove":             SetFocusYes(addon, 15); break;
            case "MiragePrismMiragePlateConfirm": SetFocusYes(addon, 6); break;
            case "SalvageDialog":                 DelayedSetFocusYes(args.AddonName, 25, null, 24); break;
            case "PurifyResult":                  SetFocusYes(addon, 19); break;
            case "LobbyWKTCheck":                 SetFocusYes(addon, 4); break;
            case "LobbyDKTWorldList":             DelayedSetFocusYes(args.AddonName, 25); break;
            case "LobbyDKTCheckExec":             DelayedSetFocusYes(args.AddonName, 3); break;
            case "ShopExchangeItemDialog":        SetFocusYes(addon, 18); break;
            case "FGSExitDialog":                 SetSpecialFocus(addon, 10, 6); break;
        }
    }

    private void DelayedSetFocusYes(string addon, uint yesButtonId, uint? yesHoldButtonId = null, uint? checkBoxId = null, int delay = 0)
    {
        framework.RunOnTick(() =>
        {
            var unitBase = (AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(addon);
            if (unitBase != null) SetFocusYes((nint)unitBase, yesButtonId, yesHoldButtonId, checkBoxId);
        }, delayTicks: delay);
    }

    private static void SetFocusYes(nint unitBaseAddress, uint yesButtonId, uint? yesHoldButtonId = null, uint? checkBoxId = null)
    {
        var unitBase = (AtkUnitBase*)unitBaseAddress;
        if (unitBase == null || unitBase->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var yesButton = unitBase->GetComponentNodeById(yesButtonId);
        if (yesButton == null) return;

        var yesButtonBase = yesButton->Component;
        if (yesButtonBase == null || yesButtonBase->GetComponentType() != ComponentType.Button) return;

        var isYesButtonEnabled = ((AtkComponentButton*)yesButtonBase)->IsEnabled;

        var checkBox     = checkBoxId != null ? unitBase->GetComponentNodeById(checkBoxId.Value) : null;
        var checkBoxBase = checkBox != null ? checkBox->Component : null;
        if (checkBoxBase != null && checkBoxBase->GetComponentType() != ComponentType.CheckBox) return;

        var isCheckBoxVisible = checkBox != null && checkBox->IsVisible();
        var isCheckBoxTicked  = checkBoxBase != null && ((AtkComponentCheckBox*)checkBoxBase)->IsChecked;

        uint collisionId;
        AtkComponentNode* targetNode;
        // Default onto the checkbox when the yes button is disabled and the (unticked) checkbox is present.
        if (!isYesButtonEnabled && isCheckBoxVisible && !isCheckBoxTicked)
        {
            collisionId = 5;
            targetNode  = checkBox;
        }
        else
        {
            var holdButton = yesHoldButtonId != null ? unitBase->GetComponentNodeById(yesHoldButtonId.Value) : null;
            if (holdButton != null && !yesButton->IsVisible())
            {
                if (holdButton->Component->GetComponentType() != ComponentType.HoldButton) return;
                collisionId = 7;
                targetNode  = holdButton;
            }
            else
            {
                collisionId = 4;
                targetNode  = yesButton;
            }
        }

        var targetComponent = targetNode->Component;
        if (targetComponent == null || targetComponent->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var yesCollision = targetComponent->GetNodeById(collisionId);
        if (yesCollision == null || yesCollision->GetNodeType() != NodeType.Collision) return;

        unitBase->SetFocusNode(yesCollision, true);
    }

    private static void SetSpecialFocus(nint unitBaseAddress, uint buttonId, uint collisionId)
    {
        var unitBase = (AtkUnitBase*)unitBaseAddress;
        if (unitBase == null) return;

        var button = unitBase->UldManager.SearchNodeById(buttonId);
        if (button == null) return;

        var collision = ((AtkComponentNode*)button)->Component->UldManager.SearchNodeById(collisionId);
        if (collision == null) return;

        unitBase->SetFocusNode(collision);
        unitBase->CursorTarget = collision;
    }

    private void OnActionBarUpdate(AddonEvent type, AddonArgs args)
    {
        if (!config.HotbarLockHidden) return;
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady) return;
        var node = addon->GetNodeById(21);
        if (node != null) node->ToggleVisibility(false);
    }

    private void ApplyHotbarLockHide()
    {
        var addon = (AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_ActionBar");
        if (addon == null || !addon->IsReady) return;
        var node = addon->GetNodeById(21);
        if (node != null) node->ToggleVisibility(false);
    }

    public void RestoreHotbarLock()
    {
        var addon = (AtkUnitBase*)AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName("_ActionBar");
        if (addon == null || !addon->IsReady) return;
        var node = addon->GetNodeById(21);
        if (node != null) node->ToggleVisibility(true);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup,           "_ActionBar", OnActionBarUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "_ActionBar", OnActionBarUpdate);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, AlwaysYesAddons, OnAlwaysYesSetup);

        if (config.HotbarLockHidden)
            RestoreHotbarLock();
    }
}
