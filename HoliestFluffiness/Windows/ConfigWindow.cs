using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HoliestFluffiness.Handlers;

namespace HoliestFluffiness.Windows;

public partial class ConfigWindow : Window
{
    private readonly Configuration configuration;
    private readonly LoginInfoHandler loginInfoHandler;
    private readonly AccessoryHandler accessoryHandler;
    private readonly RepairHandler repairHandler;
    private readonly NoKillHandler noKillHandler;
    private readonly PhysicsHandler physicsHandler;
    private readonly AntiAfkHandler antiAfkHandler;
    private readonly ReadyCheckHandler readyCheckHandler;
    private readonly IObjectTable objectTable;
    private readonly CharacterDb characterDb;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IClientState clientState;
    private readonly Action<string, string> onSwitchCharacter;
    private readonly Action<CharacterRecord, HousingBidRecord> onGoToBid;
    private readonly Action onClientSettingsChanged;
    private readonly FileDialogManager fileDialogManager = new() { AddedWindowFlags = ImGuiWindowFlags.NoCollapse };

    private int selectedSection;

    private static readonly Vector4 ColBg       = new(30f / 255f,  30f / 255f,  30f / 255f,  1f);
    private static readonly Vector4 ColSidebar  = new(48f / 255f,  48f / 255f,  48f / 255f,  1f);
    private static readonly Vector4 ColContent  = new(24f / 255f,  24f / 255f,  24f / 255f,  1f);
    private static readonly Vector4 ColSection  = new(40f / 255f,  40f / 255f,  40f / 255f,  1f);
    private static readonly Vector4 ColBgDeep   = new(18f / 255f,  18f / 255f,  18f / 255f,  1f);
    private static readonly Vector4 ColWhite    = new(249f / 255f, 248f / 255f, 244f / 255f, 1f);
    private static readonly Vector4 ColWhiteDim = new(249f / 255f, 248f / 255f, 244f / 255f, 0.55f);
    private static readonly Vector4 ColGreen    = new( 80f / 255f, 200f / 255f,  80f / 255f, 1f);
    private static readonly Vector4 ColRed      = new(220f / 255f,  80f / 255f,  80f / 255f, 1f);
    private static readonly Vector4 ColGold     = new(235f / 255f, 230f / 255f, 114f / 255f, 1f);
    private static readonly Vector4 ColGoldSub  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.18f);
    private static readonly Vector4 ColGoldMid  = new(235f / 255f, 230f / 255f, 114f / 255f, 0.35f);
    private static readonly Vector4 ColNone     = new(0f, 0f, 0f, 0f);

    public ConfigWindow(Configuration configuration, LoginInfoHandler loginInfoHandler, AccessoryHandler accessoryHandler, RepairHandler repairHandler, NoKillHandler noKillHandler, PhysicsHandler physicsHandler, AntiAfkHandler antiAfkHandler, ReadyCheckHandler readyCheckHandler, IObjectTable objectTable, IDalamudPluginInterface pluginInterface, CharacterDb characterDb, IClientState clientState, Action<string, string> onSwitchCharacter, Action<CharacterRecord, HousingBidRecord> onGoToBid, Action onClientSettingsChanged)
        : base($"The Holiest Fluffiness##Config")
    {
        this.configuration = configuration;
        this.loginInfoHandler = loginInfoHandler;
        this.accessoryHandler = accessoryHandler;
        this.repairHandler = repairHandler;
        this.noKillHandler = noKillHandler;
        this.physicsHandler = physicsHandler;
        this.antiAfkHandler = antiAfkHandler;
        this.readyCheckHandler = readyCheckHandler;
        this.objectTable = objectTable;
        this.characterDb = characterDb;
        this.pluginInterface = pluginInterface;
        this.clientState = clientState;
        this.onSwitchCharacter = onSwitchCharacter;
        this.onGoToBid = onGoToBid;
        this.onClientSettingsChanged = onClientSettingsChanged;
        selectedSection = configuration.LastSelectedSection;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        Size          = new Vector2(600, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void NavigateTo(int section)
    {
        selectedSection = section;
        configuration.LastSelectedSection = section;
        configuration.Save();
        if (section == 5) LoadCharacters();
        if (section == 6) LoadBids();
        if (section == 9) LoadInventory();
    }

    public override void PreDraw()
    {
        SizeConstraints = (selectedSection == 5 || selectedSection == 6 || selectedSection == 9)
            ? new WindowSizeConstraints { MinimumSize = new Vector2(700, 380), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) }
            : new WindowSizeConstraints { MinimumSize = new Vector2(480, 250), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        PushGlobalStyle();
    }

    public override void PostDraw()
    {
        PopGlobalStyle();
        ImGui.PopStyleVar();
    }

    public override void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        const float sidebarWidth = 180f;

        DrawSidebar(sidebarWidth, avail.Y);
        ImGui.SameLine(0, 0);
        DrawMain(avail.Y);

        DrawResizeGrip();
        fileDialogManager.Draw();
    }

    public override void OnClose()
    {
        testAllCts?.Cancel();
        accessoryCts?.Cancel();
        bulkUpdateCts?.Cancel();
        repairHandler.TestPct = null;
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void DrawSidebar(float width, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              ColSidebar);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 6));
        ImGui.BeginChild("##sidebar", new Vector2(width, height), false);

        ImGui.Dummy(new Vector2(0, 4));
        SidebarItem("Client", 0);
        SidebarItem("Login", 1);
        SidebarItem("Indicators", 2);
        SidebarItem("Players", 8);

        ImGui.Dummy(new Vector2(0, 4));
        SidebarSeparator();
        SidebarItem("Database", 4);
        if (SidebarItem("Characters", 5))
            LoadCharacters();
        if (SidebarItem("Inventory", 9))
            LoadInventory();
        if (SidebarItem("House bids", 6))
            LoadBids();

        ImGui.Dummy(new Vector2(0, 4));
        SidebarSeparator();
        SidebarItem("About", 7);

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    private void SidebarSeparator()
    {
        var x     = ImGui.GetCursorScreenPos().X + 8f;
        var y     = ImGui.GetCursorScreenPos().Y;
        var width = ImGui.GetContentRegionAvail().X - 16f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, y), new Vector2(x + width, y), ImGui.GetColorU32(ColGoldMid), 1f);
        ImGui.Dummy(new Vector2(0, 4));
    }

    private bool SidebarItem(string label, int index)
    {
        bool active = selectedSection == index;

        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGold);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColBg);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        ColNone);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldSub);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGoldMid);
            ImGui.PushStyleColor(ImGuiCol.Text,          ColWhite);
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
        bool clicked = ImGui.Button(label, new Vector2(ImGui.GetContentRegionAvail().X - 6f, 30));
        if (clicked)
        {
            selectedSection = index;
            configuration.LastSelectedSection = index;
            configuration.Save();
        }
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    // ── Main content ──────────────────────────────────────────────────────────

    private void DrawMain(float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg,              ColContent);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg,          ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab,        ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ColGold);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive,  ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8, 0));
        ImGui.BeginChild("##main", new Vector2(0, height), false);

        switch (selectedSection)
        {
            case 0: DrawClientSection();      break;
            case 1: DrawLoginSection();       break;
            case 2: DrawIndicatorsSection();  break;
            case 4: DrawDatabaseSection();    break;
            case 5: DrawCharactersSection();  break;
            case 6: DrawBidsSection();        break;
            case 7: DrawAboutSection();       break;
            case 8: DrawNearbySection();      break;
            case 9: DrawInventorySection();   break;
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
    }

    // ── Resize grip ───────────────────────────────────────────────────────────

    private void DrawResizeGrip()
    {
        const float gripSize = 15f;
        var winPos  = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var corner  = winPos + winSize;
        var mouse   = ImGui.GetMousePos();

        bool hovered = mouse.X >= corner.X - gripSize && mouse.X <= corner.X &&
                       mouse.Y >= corner.Y - gripSize && mouse.Y <= corner.Y;
        var col = ImGui.GetColorU32(hovered ? ColGold : ColGoldMid);

        var p1 = corner;
        var p2 = corner with { X = corner.X - gripSize };
        var p3 = corner with { Y = corner.Y - gripSize };

        ImGui.GetForegroundDrawList().AddTriangleFilled(p1, p2, p3, col);
    }

    // ── Section helpers ───────────────────────────────────────────────────────

    private void BeginSection(string title, Action? afterTitle = null)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ColSection);
        ImGui.BeginChild(title + "##sec", new Vector2(0, 0), false);

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10f);
        ImGui.TextUnformatted(title);
        if (afterTitle != null) { ImGui.SameLine(); afterTitle(); }
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(0, 6));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
    }

    private void EndSection()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private static void SectionRow() =>
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);

    private void SubsectionLabel(string label)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8f);
        ImGui.PushStyleColor(ImGuiCol.Text, ColGold);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();
    }

    // ── Style helpers ─────────────────────────────────────────────────────────

    private void PushGlobalStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Text,                ColWhite);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,            ColBg);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,             ColBgDeep);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,      ColSidebar);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,       ColSidebar);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip,          ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered,   ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive,    ColGold);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,             ColBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,       ColBg);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(8, 6));
    }

    private void PopGlobalStyle()
    {
        ImGui.PopStyleColor(10);
        ImGui.PopStyleVar(2);
    }

    private void PushButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button,        ColGoldSub);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColGoldMid);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColGold);
        ImGui.PushStyleColor(ImGuiCol.Text,          ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
    }

    private void PopButton()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar();
    }

    private void PushCheckbox()
    {
        ImGui.PushStyleColor(ImGuiCol.CheckMark, ColGold);
        ImGui.PushStyleColor(ImGuiCol.Border,    ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopCheckbox()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar();
    }

    private void PushInput()
    {
        ImGui.PushStyleColor(ImGuiCol.Border, ColGold);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }

    private void PopInput()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }
}
