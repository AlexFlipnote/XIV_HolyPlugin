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
        BeginSection("Login");

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Settings for what happens when you log in with a character.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 8));

        // ── Login info ────────────────────────────────────────────────────────
        SubsectionLabel("Login info");
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        bool anyEnabled = configuration.ShowCharacterInfo || configuration.InfoEnabled || configuration.AdventurePlateEnabled || configuration.ShowPrivateHouseLocation || configuration.ShowFcHouseLocation;
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulate login:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.BeginDisabled(!anyEnabled);
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

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Show as:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        foreach (var (label, val) in new (string, LoginInfoDisplay)[] { ("Echo text", LoginInfoDisplay.Echo), ("Popup", LoginInfoDisplay.Popup), ("Toast", LoginInfoDisplay.Toast) })
        {
            if (ImGui.RadioButton(label, configuration.LoginInfoDisplay == val))
            {
                configuration.LoginInfoDisplay = val;
                configuration.Save();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.Dummy(new Vector2(0, 8));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted("What to show");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        PushButton();
        if (ImGui.Button("Reset order##loginorder"))
        {
            configuration.LoginInfoOrder = [0, 1, 2, 3, 4];
            configuration.Save();
        }
        PopButton();

        DrawInfoOrderList();

        ImGui.Dummy(new Vector2(0, 8));

        // ── Fashion accessory ─────────────────────────────────────────────────
        SubsectionLabel("Fashion accessory");
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Simulate login:");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.BeginDisabled(!configuration.AccessoryEnabled);
        PushButton();
        if (ImGui.Button("Test##accessory"))
        {
            accessoryCts?.Cancel();
            accessoryCts?.Dispose();
            accessoryCts = new CancellationTokenSource();
            Task.Run(() => accessoryHandler.RunAsync(accessoryCts.Token));
        }
        PopButton();
        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 6));
        SectionRow();

        PushCheckbox();
        var accessoryEnabled = configuration.AccessoryEnabled;
        bool accessoryChanged = ImGui.Checkbox("Equip accessory on login", ref accessoryEnabled);
        PopCheckbox();
        if (accessoryChanged)
        {
            configuration.AccessoryEnabled = accessoryEnabled;
            configuration.Save();
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.BeginDisabled(!accessoryEnabled);

        SectionRow();
        var accessoryName = configuration.AccessoryName;
        ImGui.SetNextItemWidth(200);
        PushInput();
        if (ImGui.InputText("Accessory name", ref accessoryName, 128))
        {
            configuration.AccessoryName = accessoryName;
            configuration.Save();
        }
        PopInput();

        ImGui.EndDisabled();

        DrawRestrictionsSubsection(accessoryEnabled);

        ImGui.Dummy(new Vector2(0, 8));

        // ── Character picker ──────────────────────────────────────────────────
        SubsectionLabel("Character picker");
        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Show a character picker popup when you enter the main menu");
        ImGui.PopStyleColor();

        ImGui.Dummy(new Vector2(0, 4));
        SectionRow();

        PushCheckbox();
        var pickerEnabled = configuration.CharacterPickerOnMainMenu;
        if (ImGui.Checkbox("Show on character select##charPicker", ref pickerEnabled))
        {
            configuration.CharacterPickerOnMainMenu = pickerEnabled;
            configuration.Save();
        }
        PopCheckbox();

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
                configuration.Save();
            }
            PopCheckbox();
        }

        ImGui.Dummy(new Vector2(0, 4));
    }

    private void DrawRestrictionsSubsection(bool enabled)
    {
        ImGui.Dummy(new Vector2(0, 8f));
        SubsectionLabel("Restrictions");

        ImGui.BeginDisabled(!enabled);

        SectionRow();
        var maxFreeSlots = configuration.AccessoryInventory;
        ImGui.SetNextItemWidth(90);
        PushInput();
        if (ImGui.InputInt("Stop if N or fewer free inventory slots (0–140)##max", ref maxFreeSlots, 1, 10))
        {
            configuration.AccessoryInventory = Math.Clamp(maxFreeSlots, 0, 140);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(0 = skip check)");
        ImGui.PopStyleColor();

        SectionRow();
        var minFreeSlots = configuration.AccessoryInventoryMin;
        ImGui.SetNextItemWidth(90);
        PushInput();
        if (ImGui.InputInt("Stop if N or more free inventory slots (0–140)##min", ref minFreeSlots, 1, 10))
        {
            configuration.AccessoryInventoryMin = Math.Clamp(minFreeSlots, 0, 140);
            configuration.Save();
        }
        PopInput();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("(0 = skip check)");
        ImGui.PopStyleColor();

        ImGui.EndDisabled();

        ImGui.Dummy(new Vector2(0, 8f));
        SubsectionLabel("Whitelist");

        SectionRow();
        ImGui.PushStyleColor(ImGuiCol.Text, ColWhiteDim);
        ImGui.TextUnformatted("Characters listed here will bypass all restrictions.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 2f));

        ImGui.BeginDisabled(!enabled);

        int removeIdx = -1;
        for (int i = 0; i < configuration.AccessoryWhitelist.Count; i++)
        {
            SectionRow();
            PushButton();
            ImGui.PushStyleColor(ImGuiCol.Text, ColRed);
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
