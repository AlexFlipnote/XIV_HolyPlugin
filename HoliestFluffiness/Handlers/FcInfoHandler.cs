using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace HoliestFluffiness;

public class FcInfoHandler(Configuration configuration, IChatGui chatGui, IFramework framework, IObjectTable objectTable)
{
    public async Task RunAsync(CancellationToken token)
    {
        if (!configuration.InfoEnabled) return;

        token.ThrowIfCancellationRequested();

        string companyTag = string.Empty;
        string companyName = string.Empty;

        await framework.RunOnFrameworkThread(() =>
        {
            if (objectTable[0] is IPlayerCharacter pc)
                companyTag = pc.CompanyTag.ToString();

            unsafe
            {
                var fc = InfoProxyFreeCompany.Instance();
                if (fc != null)
                    companyName = fc->NameString;
            }
        });

        if (string.IsNullOrEmpty(companyTag))
            return;

        await framework.RunOnFrameworkThread(() =>
            chatGui.Print($"FC Information: «{companyTag}» {companyName}"));
    }
}
