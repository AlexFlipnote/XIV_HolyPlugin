using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow
{
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

        ConfigCheckbox(
            "Disable idle movie##titlemovie",
            configuration.TitleMovieDisabled,
            v => configuration.TitleMovieDisabled = v,
            "Prevents the intro video from playing on the title screen");

        ConfigCheckbox(
            "Show congestion queue timer##queuetimer",
            configuration.QueueTimerEnabled,
            v => configuration.QueueTimerEnabled = v,
            "Adds a countdown to the \"server is currently congested\" dialog. The refresh rate is measured " +
            "while you wait, so it shows time since the queue started or last updated until it is known.");

        // ── No-kill ───────────────────────────────────────────────────────────
        SubsectionLabel("No-kill",
            "Intercepts lobby errors and converts them to a reconnect attempt instead of closing the game.");

        ConfigCheckbox(
            "Enable no-kill##nokill",
            configuration.NoKillEnabled,
            v =>
            {
                configuration.NoKillEnabled = v;
                noKillHandler.SetEnabled(v);
            });

        ImGui.BeginDisabled(!configuration.NoKillEnabled);
        ConfigCheckbox(
            "Disable popup on lobby error##nokillpopup",
            configuration.NoKillDisablePopup,
            v => configuration.NoKillDisablePopup = v);
        ImGui.EndDisabled();

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

    private bool loginOrderDirty;

    // A framed list: grip and name on the left drag the row to a new position, checkbox on the right
    private void DrawInfoOrderList()
    {
        var order = configuration.LoginInfoOrder;
        const float boxW = 260f;
        const float pad  = 6f;
        var frameH = ImGui.GetFrameHeight();
        var rowH   = frameH + 4f;
        var boxH   = order.Count * rowH + pad * 2;

        SectionRow();
        var origin = ImGui.GetCursorScreenPos();
        var dl     = ImGui.GetWindowDrawList();
        // Faded like the window's own backgrounds so the theme opacity setting applies here too
        var boxBg     = ImGui.GetColorU32(Theme.Fade(Theme.UseCustom ? Theme.ColHighlight : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]));
        var boxBorder = Theme.UseCustom ? ImGui.GetColorU32(Theme.ColGold)      : ImGui.GetColorU32(ImGuiCol.Border);
        dl.AddRectFilled(origin, origin + new Vector2(boxW, boxH), boxBg, 4f);
        dl.AddRect(origin, origin + new Vector2(boxW, boxH), boxBorder, 4f);

        int moveFrom = -1, moveTo = -1;

        for (int i = 0; i < order.Count; i++)
        {
            int slot   = order[i];
            var rowMin = origin + new Vector2(0, pad + i * rowH);
            var checkX = origin.X + boxW - pad - frameH;

            ImGui.SetCursorScreenPos(rowMin + new Vector2(pad, 0));
            ImGui.BeginGroup();

            // The id follows the slot, not the index, so the drag stays active as the row moves
            ImGui.InvisibleButton($"##logininfodrag{slot}", new Vector2(checkX - rowMin.X - pad * 2, rowH));
            var hovered = ImGui.IsItemHovered();
            var active  = ImGui.IsItemActive();

            if (hovered || active)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
                dl.AddRectFilled(rowMin + new Vector2(2, 0), rowMin + new Vector2(boxW - 2, rowH),
                    ImGui.GetColorU32(active ? Theme.ColGoldSub : Theme.ColGrey with { W = 0.5f }), 3f);
            }

            if (active)
            {
                var target = (int)MathF.Floor((ImGui.GetIO().MousePos.Y - origin.Y - pad) / rowH);
                target = Math.Clamp(target, 0, order.Count - 1);
                if (target != i) { moveFrom = i; moveTo = target; }
            }
            if (ImGui.IsItemDeactivated() && loginOrderDirty)
            {
                loginOrderDirty = false;
                configuration.Save();
            }

            // Grip: two columns of three dots
            var gripColor = ImGui.GetColorU32(hovered || active ? Theme.ColWhite : Theme.ColWhiteDim);
            var midY      = rowMin.Y + rowH / 2;
            for (int gx = 0; gx < 2; gx++)
                for (int gy = -1; gy <= 1; gy++)
                    dl.AddCircleFilled(new Vector2(rowMin.X + pad + 4 + gx * 5, midY + gy * 5), 1.6f, gripColor);

            bool enabled = slot switch
            {
                0 => configuration.ShowCharacterInfo,
                1 => configuration.AdventurePlateEnabled,
                2 => configuration.ShowPrivateHouseLocation,
                3 => configuration.InfoEnabled,
                4 => configuration.ShowFcHouseLocation,
                _ => false,
            };
            var label = InfoItemLabels[slot];
            dl.AddText(new Vector2(rowMin.X + pad + 18, midY - ImGui.GetTextLineHeight() / 2),
                ImGui.GetColorU32(enabled ? Theme.ColWhite : Theme.ColWhiteDim), label);

            ImGui.SetCursorScreenPos(new Vector2(checkX, rowMin.Y + (rowH - frameH) / 2));
            PushCheckbox();
            bool newEnabled = enabled;
            if (ImGui.Checkbox($"##logininfocheck{slot}", ref newEnabled) && newEnabled != enabled)
            {
                switch (slot)
                {
                    case 0: configuration.ShowCharacterInfo          = newEnabled; break;
                    case 1: configuration.AdventurePlateEnabled      = newEnabled; break;
                    case 2: configuration.ShowPrivateHouseLocation   = newEnabled; break;
                    case 3: configuration.InfoEnabled                = newEnabled; break;
                    case 4: configuration.ShowFcHouseLocation        = newEnabled; break;
                }
                configuration.Save();
            }
            PopCheckbox();

            ImGui.EndGroup();
            Anchor($"logininfo{slot}", $"Login info: {label}", "Shown when logging in with a character");
        }

        // Applied after the loop so the list is not mutated mid-draw; saved once the drag is released
        if (moveFrom >= 0)
        {
            var moved = order[moveFrom];
            order.RemoveAt(moveFrom);
            order.Insert(moveTo, moved);
            loginOrderDirty = true;
        }

        // Claim the box's full area in the layout
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(boxW, boxH));
        ImGui.Dummy(new Vector2(0, 4));
    }

    private void DrawRestrictionsSubsection(bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        RowGap(8);
        ConfigInputInt("Stop if N or fewer free inventory slots (0-140)##max", configuration.AccessoryInventory, 0, 140,
            v => configuration.AccessoryInventory = v, hint: "(0 = skip check)", padding: false,
            desc: "Accessory equip is skipped if you have this many free inventory slots or fewer");

        ConfigInputInt("Stop if N or more free inventory slots (0-140)##min", configuration.AccessoryInventoryMin, 0, 140,
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
            var key = Common.CharacterKey(player!);
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
