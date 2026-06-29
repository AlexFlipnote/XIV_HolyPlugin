using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace HoliestFluffiness.Windows;

public abstract class HfOverlayWindow : Window
{
    protected HfOverlayWindow(string id) : base(id)
    {
        ForceMainWindow     = true;
        RespectCloseHotkey  = false;
        DisableWindowSounds = true;
        Flags = ImGuiWindowFlags.NoDecoration       | ImGuiWindowFlags.NoSavedSettings    |
                ImGuiWindowFlags.NoMove             | ImGuiWindowFlags.NoMouseInputs      |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground       | ImGuiWindowFlags.NoNav;
    }

    public override void PreDraw()
    {
        Size     = ImGuiHelpers.MainViewport.Size;
        Position = ImGuiHelpers.MainViewport.Pos;
    }
}
