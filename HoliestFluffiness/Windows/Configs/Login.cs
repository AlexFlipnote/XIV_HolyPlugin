using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
    private LoginEnhancementHandler loginEnhancementHandler = null!;
    internal void SetLoginEnhancementHandler(LoginEnhancementHandler handler) => loginEnhancementHandler = handler;

    private CancellationTokenSource? testAllCts;
    private CancellationTokenSource? accessoryCts;

    private static readonly string[] InfoItemLabels = ["Character", "Search info", "Private house", "Free Company", "FC house"];

    private void DrawLoginSection()
    {
        BeginSection("Login", "Settings for what happens when you log in with a character.");

        ConfigCheckbox(
            "Show on character select##charPicker",
            configuration.CharacterPickerOnMainMenu,
            v => configuration.CharacterPickerOnMainMenu = v,
            "Shows a character picker popup when you enter the main menu");

        // ── Login info ────────────────────────────────────────────────────────
        SubsectionLabel(
            "Login info",
            "Information that will be shown when you login to a character.");

        DrawInfoOrderList();

        bool anyEnabled = configuration.ShowCharacterInfo
            || configuration.InfoEnabled
            || configuration.AdventurePlateEnabled
            || configuration.ShowPrivateHouseLocation
            || configuration.ShowFcHouseLocation;

        ImGui.BeginDisabled(!anyEnabled);

        SectionRow();
        Common.DimmedText("Show as:");
        ImGui.SameLine();
        var loginDisplayModes = new[] { "Echo text", "Popup", "Toast" };
        ConfigCombo("##logininfodisplay", (int)configuration.LoginInfoDisplay, loginDisplayModes,
            v => configuration.LoginInfoDisplay = (LoginInfoDisplay)v, width: 120, padding: false,
            title: "Login info display mode", desc: "Echo text, popup, or toast");

        SectionRow();
        PushButton();
        if (ImGui.Button("Reset order##loginorder"))
        {
            configuration.LoginInfoOrder = [0, 1, 2, 3, 4];
            configuration.Save();
        }
        PopButton();

        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Test##loginall"))
        {
            testAllCts?.Cancel();
            testAllCts?.Dispose();
            testAllCts = new CancellationTokenSource();
            Task.Run(() => loginInfoHandler.RunAsync(testAllCts.Token, instant: true));
        }
        PopButton();
        ImGui.EndDisabled();

        // ── Login enhancements ────────────────────────────────────────────────
        SubsectionLabel("Login enhancements");

        ConfigCheckbox(
            "Skip logo on launch##skiplogo",
            configuration.LoginSkipLogo,
            v => configuration.LoginSkipLogo = v,
            "Jumps straight to the title screen, skipping the intro movie");

        ConfigCheckbox(
            "Preload territory on character select##preloadterritory",
            configuration.PreloadTerritory,
            v => configuration.PreloadTerritory = v,
            "Starts loading the destination zone in the background while in the login queue");

        // ── Fashion accessory ─────────────────────────────────────────────────
        SubsectionLabel("Fashion accessory");

        ConfigCheckbox("Equip accessory on login##accessoryenabled", configuration.AccessoryEnabled,
            v => configuration.AccessoryEnabled = v);
        var accessoryEnabled = configuration.AccessoryEnabled;

        ImGui.BeginDisabled(!accessoryEnabled);

        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Test##accessory"))
        {
            accessoryCts?.Cancel();
            accessoryCts?.Dispose();
            accessoryCts = new CancellationTokenSource();
            Task.Run(() => accessoryHandler.RunAsync(accessoryCts.Token));
        }
        PopButton();

        ConfigInputText("Accessory name##accessoryname", configuration.AccessoryName,
            v => configuration.AccessoryName = v, width: 200);

        ImGui.EndDisabled();

        DrawRestrictionsSubsection(accessoryEnabled);

        EndSection(10);
    }

    private void DrawInfoOrderList()
    {
        var order = configuration.LoginInfoOrder;
        const float btnW = 22f;

        for (int i = 0; i < order.Count; i++)
        {
            int slot = order[i];

            SectionRow();
            ImGui.BeginGroup();

            PushButton();
            ImGui.BeginDisabled(i == 0);
            if (ImGui.Button($"^##{slot}u", new Vector2(btnW, 0)))
            {
                (order[i - 1], order[i]) = (order[i], order[i - 1]);
                configuration.Save();
            }
            ImGui.EndDisabled();
            ImGui.SameLine(0, 2);
            ImGui.BeginDisabled(i == order.Count - 1);
            if (ImGui.Button($"v##{slot}d", new Vector2(btnW, 0)))
            {
                (order[i + 1], order[i]) = (order[i], order[i + 1]);
                configuration.Save();
            }
            ImGui.EndDisabled();
            PopButton();

            ImGui.SameLine(0, 6);

            bool enabled = slot switch
            {
                0 => configuration.ShowCharacterInfo,
                1 => configuration.AdventurePlateEnabled,
                2 => configuration.ShowPrivateHouseLocation,
                3 => configuration.InfoEnabled,
                4 => configuration.ShowFcHouseLocation,
                _ => false,
            };
            PushCheckbox();
            bool newEnabled = enabled;
            if (ImGui.Checkbox($"{InfoItemLabels[slot]}##{slot}", ref newEnabled) && newEnabled != enabled)
            {
                switch (slot)
                {
                    case 0: configuration.ShowCharacterInfo          = newEnabled; break;
                    case 1: configuration.AdventurePlateEnabled      = newEnabled; break;
                    case 2: configuration.ShowPrivateHouseLocation   = newEnabled; break;
                    case 3: configuration.InfoEnabled                = newEnabled; break;
                    case 4: configuration.ShowFcHouseLocation        = newEnabled; break;
                }
                configuration.Save();ImGui.Dummy(new Vector2(0, 6));
            }
            PopCheckbox();

            ImGui.EndGroup();
            Anchor($"logininfo{slot}", $"Login info: {InfoItemLabels[slot]}", "Shown when logging in with a character");
        }

        ImGui.Dummy(new Vector2(0, 4));
    }

    private void DrawRestrictionsSubsection(bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        RowGap(8);
        ConfigInputInt("Stop if N or fewer free inventory slots (0–140)##max", configuration.AccessoryInventory, 0, 140,
            v => configuration.AccessoryInventory = v, hint: "(0 = skip check)", padding: false,
            desc: "Accessory equip is skipped if you have this many free inventory slots or fewer");

        ConfigInputInt("Stop if N or more free inventory slots (0–140)##min", configuration.AccessoryInventoryMin, 0, 140,
            v => configuration.AccessoryInventoryMin = v, hint: "(0 = skip check)",
            desc: "Accessory equip is skipped if you have this many free inventory slots or more");

        RowGap(4);
        Common.DimmedText("Characters listed here will bypass all restrictions.");
        int removeIdx = -1;
        for (int i = 0; i < configuration.AccessoryWhitelist.Count; i++)
        {
            SectionRow();
            PushButton();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.ColRed);
            if (ImGui.Button($"X##{i}", new Vector2(22, 0)))
                removeIdx = i;
            ImGui.PopStyleColor();
            PopButton();
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted(configuration.AccessoryWhitelist[i]);
        }
        if (removeIdx >= 0)
        {
            configuration.AccessoryWhitelist.RemoveAt(removeIdx);
            configuration.Save();
        }

        SectionRow();
        var player = objectTable[0] as IPlayerCharacter;
        ImGui.BeginDisabled(player == null);
        PushButton();
        if (ImGui.Button("+ Add current character"))
        {
            var key = $"{player!.Name.TextValue}@{player.HomeWorld.ValueNullable?.Name.ExtractText()}";
            if (!configuration.AccessoryWhitelist.Contains(key))
            {
                configuration.AccessoryWhitelist.Add(key);
                configuration.Save();
            }
        }
        PopButton();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
    }
}
