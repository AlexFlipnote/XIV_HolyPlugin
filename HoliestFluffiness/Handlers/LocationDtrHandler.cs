using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace HoliestFluffiness.Handlers;

// Where you are, as a server info bar entry. The bar shows the user's template, the tooltip shows everything
public sealed class LocationDtrHandler : IDisposable
{
    private readonly Configuration config;
    private readonly IFramework    framework;
    private readonly IClientState  clientState;
    private readonly IDataManager  data;
    private readonly IDtrBarEntry  entry;

    // Everything the text depends on; nothing is looked up or rebuilt until one of these changes
    private (uint Territory, uint Area, uint SubArea, uint Instance, sbyte Ward, sbyte Plot, short Room, byte Division, string Format) lastKey;
    private bool dirty = true;

    public LocationInfo Current { get; private set; } = new();

    public LocationDtrHandler(Configuration config, IDtrBar dtrBar, IFramework framework, IClientState clientState, IDataManager data)
    {
        this.config      = config;
        this.framework   = framework;
        this.clientState = clientState;
        this.data        = data;

        entry       = dtrBar.Get("[HF] Location");
        entry.Shown = false;
        entry.OnClick = OnClick;

        framework.Update += OnUpdate;
    }

    private unsafe void OnUpdate(IFramework fw)
    {
        var shown = config.LocationDtrEnabled && clientState.IsLoggedIn;
        if (!shown)
        {
            if (entry.Shown) entry.Shown = false;
            dirty = true;
            return;
        }

        var area    = TerritoryInfo.Instance();
        var housing = HousingManager.Instance();
        var ui      = UIState.Instance();
        if (area == null || ui == null) return;

        var inHousing = housing != null && housing->GetCurrentWard() >= 0;
        var key = (
            Territory: clientState.TerritoryType,
            Area:      area->AreaPlaceNameId,
            SubArea:   area->SubAreaPlaceNameId,
            Instance:  ui->PublicInstance.InstanceId,
            Ward:      inHousing ? housing->GetCurrentWard()     : (sbyte)-1,
            Plot:      inHousing ? housing->GetCurrentPlot()     : (sbyte)-1,
            Room:      inHousing ? housing->GetCurrentRoom()     : (short)0,
            Division:  inHousing ? housing->GetCurrentDivision() : (byte)0,
            Format:    config.LocationDtrFormat);

        if (!dirty && key == lastKey) return;
        lastKey = key;
        dirty   = false;

        Current = Resolve(key.Territory, key.Area, key.SubArea, key.Instance, key.Ward, key.Plot, key.Room, key.Division,
            housing != null && housing->IsInside());

        var text = Format(config.LocationDtrFormat, Current);
        if (text.Length == 0)
        {
            if (entry.Shown) entry.Shown = false;
            return;
        }

        entry.Text    = text;
        entry.Tooltip = Current.Tooltip + "\nClick to copy";
        if (!entry.Shown) entry.Shown = true;
    }

    private unsafe LocationInfo Resolve(uint territoryId, uint areaId, uint subAreaId, uint instance,
        sbyte ward, sbyte plot, short room, byte division, bool inside)
    {
        // Inside a house the territory is the interior; the district it belongs to reads better
        if (inside)
        {
            var original = HousingManager.GetOriginalHouseTerritoryTypeId();
            if (original != 0) territoryId = original;
        }

        var info = new LocationInfo();
        if (data.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var territory))
        {
            info.Territory = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
            info.Region    = territory.PlaceNameRegion.ValueNullable?.Name.ExtractText() ?? "";
        }
        info.Area    = PlaceName(areaId);
        info.SubArea = PlaceName(subAreaId);

        if (instance > 0)
            info.Instance = ((SeIconChar)((int)SeIconChar.Instance1 + (int)instance - 1)).ToIconString();
        info.InstanceNumber = instance;

        if (ward >= 0)
        {
            info.Ward = division == 2 ? $"Ward {ward + 1} Subdivision" : $"Ward {ward + 1}";
            // Apartment buildings report a negative plot; room 0 there is the lobby
            if (plot < -1)
                info.Plot = room > 0 ? $"Apartment {room}" : "Apartment Lobby";
            else if (plot >= 0)
                info.Plot = room > 0 ? $"Plot {plot + 1}, Room {room}" : $"Plot {plot + 1}";
        }

        return info;
    }

    private string PlaceName(uint id)
        => id != 0 && data.GetExcelSheet<PlaceName>().TryGetRow(id, out var row) ? row.Name.ExtractText() : "";

    private void OnClick(DtrInteractionEvent e)
    {
        var text = Current.CopyText;
        if (text.Length == 0) return;
        ImGui.SetClipboardText(text);
        Common.ShowToast("Location copied", text, 3f);
    }

    // Fills the template. A token that comes out empty, or repeats one already shown (the area is often
    // named after the zone), drops out along with the separator in front of it, so "<a>, <b>" never
    // leaves a dangling comma. Text before the first token and after the last one is always kept.
    internal static string Format(string template, LocationInfo info)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Configuration.DefaultLocationDtrFormat;

        var sb      = new StringBuilder();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Literal text since the last tag: the prefix until the first tag, a separator after that
        var pending = new StringBuilder();
        var prefix  = "";
        var emitted = false;
        var any     = false;
        var i       = 0;

        while (i < template.Length)
        {
            var open  = template.IndexOf('<', i);
            var close = open >= 0 ? template.IndexOf('>', open) : -1;
            if (open < 0 || close < 0)
            {
                pending.Append(template, i, template.Length - i);
                break;
            }

            pending.Append(template, i, open - i);
            var token = template.Substring(open + 1, close - open - 1);
            i = close + 1;

            var value = info.Get(token);
            if (value == null)
            {
                // Not a known tag, keep it as written
                pending.Append('<').Append(token).Append('>');
                continue;
            }

            if (!any)
            {
                prefix = pending.ToString();
                pending.Clear();
                any = true;
            }

            // A dropped tag takes the separator in front of it along
            if (value.Length == 0 || !seen.Add(value))
            {
                pending.Clear();
                continue;
            }

            // The first shown tag never gets a separator, even when earlier tags dropped out
            if (emitted) sb.Append(pending);
            pending.Clear();
            sb.Append(value);
            emitted = true;
        }

        // Without any tags the whole template is plain text, and with tags but nothing to show, show nothing
        if (!any) return template.Trim();
        if (!emitted) return "";
        return (prefix + sb + pending).Trim();
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        entry.OnClick = null;
        entry.Remove();
    }
}

public sealed class LocationInfo
{
    public string Region    { get; set; } = "";
    public string Territory { get; set; } = "";
    public string Area      { get; set; } = "";
    public string SubArea   { get; set; } = "";
    public string Ward      { get; set; } = "";
    public string Plot      { get; set; } = "";
    public string Instance  { get; set; } = "";
    public uint   InstanceNumber { get; set; }

    public const string Tags = "<subarea> | <area> | <zone> | <region> | <ward> | <plot> | <instance>";

    // null means the tag isn't one of ours
    public string? Get(string tag) => tag.ToLowerInvariant() switch
    {
        "subarea"  => SubArea,
        "area"     => Area,
        "zone"     => Territory,
        "region"   => Region,
        "ward"     => Ward,
        "plot"     => Plot,
        "instance" => Instance,
        _          => null,
    };

    // Most specific first, one line each, repeats skipped
    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            void Add(string s) { if (s.Length > 0 && !lines.Contains(s)) lines.Add(s); }
            Add(SubArea);
            Add(Area);
            Add(InstanceNumber > 0 ? $"{Territory} (instance {InstanceNumber})" : Territory);
            Add(Region);
            Add(Plot.Length > 0 ? $"{Ward}, {Plot}" : Ward);
            return string.Join("\n", lines);
        }
    }

    // One line for pasting into chat or Discord
    public string CopyText
    {
        get
        {
            var parts = new List<string>();
            void Add(string s) { if (s.Length > 0 && !parts.Contains(s)) parts.Add(s); }
            Add(SubArea);
            Add(Area);
            Add(InstanceNumber > 0 ? $"{Territory} (instance {InstanceNumber})" : Territory);
            Add(Ward);
            Add(Plot);
            return string.Join(", ", parts);
        }
    }
}
