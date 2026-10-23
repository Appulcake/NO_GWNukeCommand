using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GW_server_plugin.Features.CommandUtils;
using HarmonyLib;

namespace NO_GWNC;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("GrayWar.ServerPlugin")]
public class Plugin : BaseUnityPlugin
{
    private Harmony? _harmony;
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static ConfigEntry<float> SafeNukeRadiusMeters { get; private set; } = null!;
    internal static ConfigEntry<float> GamblePunishmentChancePercent { get; private set; } = null!;
    internal static ConfigEntry<string> SabreMissileDefinitionKey { get; private set; } = null!;
    
    private void Awake()
    {
        Log = Logger;
        
        SafeNukeRadiusMeters = Config.Bind("Nuke", "Safe radius meters", 10000f,
            "In safe mode, other spawned players inside this radius don't observe the nuke and " +
            "thus don't receive its client sided shockwave.");
        
        GamblePunishmentChancePercent = Config.Bind("Gamble", "Safe nuke chance percent", 0f,
            new ConfigDescription(
                "Chance for a /gamble to additionally 50/50 either nuke (with safe mode) the gambler, or spawn " +
                "12 sabres with /sabre command on them. Set to 0 to disable.",
                new AcceptableValueRange<float>(0f, 100f)));
        
        SabreMissileDefinitionKey = Config.Bind("Sabre", "Default Missile Definition", "Aryx_Missile_AAM45",
            "Default MissileDefinition jsonKey used by the /sabre command (when none is specified as argument).");
        
        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();
        
        var nukeCommand = new NukeCommand(Config);
        if (nukeCommand.Enable)
        {
            if (CommandService.TryGetCommand(nukeCommand.OutputName, out _))
            {
                Log.LogWarning(
                    $"Command \"{nukeCommand.OutputName}\" is already registered, NO_GWNC will not register another.");
            }
            else
            {
                CommandService.AddCommand(nukeCommand);
                Log.LogInfo($"Registered GW command \"{nukeCommand.OutputName}\".");
            }
        }
        else
        {
            Log.LogInfo("Nuke command is disabled in config.");
        }
        
        var sabreCommand = new SabreCommand(Config);
        if (sabreCommand.Enable)
        {
            if (CommandService.TryGetCommand(sabreCommand.OutputName, out _))
            {
                Log.LogWarning(
                    $"Command \"{sabreCommand.OutputName}\" is already registered, NO_GWNC will not register another.");
            }
            else
            {
                CommandService.AddCommand(sabreCommand);
                Log.LogInfo($"Registered GW command \"{sabreCommand.OutputName}\".");
            }
        }
        
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }
    
    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}