using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Com.Graywar.NoServerManager.Proto;
using Cysharp.Threading.Tasks;
using GW_server_plugin.Features.CommandUtils;
using GW_server_plugin.Features.CommandUtils.Commands;
using GW_server_plugin.Helpers;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace NO_GWNC;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("GrayWar.ServerPlugin")]
public class Plugin : BaseUnityPlugin
{
    private Harmony? _harmony;
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static ConfigEntry<float> SafeNukeRadiusMeters { get; private set; } = null!;
    internal static ConfigEntry<float> GambleNukeChancePercent { get; private set; } = null!;
    
    private void Awake()
    {
        Log = Logger;
        
        SafeNukeRadiusMeters = Config.Bind("Nuke", "Safe radius meters", 10000f,
            "In safe mode, other spawned players inside this radius don't observe the nuke and " +
            "thus don't receive its client sided shockwave.");
        
        GambleNukeChancePercent = Config.Bind("Gamble", "Safe nuke chance percent", 0f,
            new ConfigDescription(
                "Chance for a /gamble to additionally nuke (with safe mode) the gambler. Set to 0 to disable.",
                new AcceptableValueRange<float>(0f, 100f)));
        
        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();
        
        var command = new NukeCommand(Config);
        if (command.Enable)
        {
            if (CommandService.TryGetCommand(command.OutputName, out _))
            {
                Log.LogWarning(
                    $"Command \"{command.OutputName}\" is already registered, NO_GWNC will not register another.");
            }
            else
            {
                CommandService.AddCommand(command);
                Log.LogInfo($"Registered GW command \"{command.OutputName}\".");
            }
        }
        else
        {
            Log.LogInfo("Nuke command is disabled in config.");
        }
        
        Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }
    
    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}

public enum NukeMode
{
    Public,
    Stealth,
    Safe,
    Unsafe
}

internal sealed class NukeCommand : ConfigurableCommand, IGameCommand
{
    internal NukeCommand(ConfigFile config) : base(config)
    {
    }
    
    public override PermissionLevel DefaultPermissionLevel => PermissionLevel.Moderator;
    
    public override string OutputName => "nuke";
    
    public override string Description =>
        "Nuke a player (who's spawned in) with a 250kt nuke. Optional modes: stealth, safe, or unsafe.";
    
    public override string Usage => "nuke <player name / ID / SteamID> [stealth|safe|unsafe]";
    
    public UniTask<bool> Validate(Player player, string[] args) =>
        UniTask.FromResult(TryParseArguments(args, out _, out _));
    
    public UniTask<(bool success, string? response)> Execute(Player player, string[] args)
    {
        if (!TryParseArguments(args, out var targetText, out var mode))
            return Result(false, $"Usage: {Usage}");
        
        if (!PlayerUtils.TryFindPlayer(targetText, out var targetPlayer) || targetPlayer == null)
            return Result(false, $"Could not find player \"{targetText}\".");
        
        if (!NukeService.TrySmite(targetPlayer, mode, out var error))
            return Result(false, error);
        
        var modeText = mode switch
        {
            NukeMode.Stealth => " with a stealth nuke",
            NukeMode.Safe => " with a safe nuke",
            NukeMode.Unsafe => " with an unsafe nuke",
            _ => ""
        };
        
        return Result(true, $"Nuked {targetPlayer.GetDisplayName()}{modeText}.");
    }
    
    private static bool TryParseArguments(string[] args, out string targetText, out NukeMode mode)
    {
        targetText = string.Empty;
        mode = NukeMode.Public;
        if (args.Length == 0)
            return false;
        
        var targetArgCount = args.Length;
        
        // This supports spaces in player names as a potential stealth/safe argument is always the last one
        if (args.Length >= 2 && TryParseMode(args[args.Length - 1], out var parsedMode))
        {
            mode = parsedMode;
            targetArgCount--;
        }
        
        if (targetArgCount <= 0)
            return false;
        
        targetText = string.Join(" ", args, 0, targetArgCount).Trim();
        return targetText.Length > 0;
    }
    
    private static bool TryParseMode(string value, out NukeMode mode)
    {
        if (value.Equals("stealth", StringComparison.OrdinalIgnoreCase))
        {
            mode = NukeMode.Stealth;
            return true;
        }
        
        if (value.Equals("safe", StringComparison.OrdinalIgnoreCase))
        {
            mode = NukeMode.Safe;
            return true;
        }
        
        if (value.Equals("unsafe", StringComparison.OrdinalIgnoreCase))
        {
            mode = NukeMode.Unsafe;
            return true;
        }
        
        mode = NukeMode.Public;
        return false;
    }
    
    private static UniTask<(bool success, string? response)> Result(bool success, string response) =>
        UniTask.FromResult<(bool, string?)>((success, response));
}

public static class NukeService
{
    private const string StrategicNukeDefinitionKey = "nuclearBomb1_strategic";
    private const float SmiteImpactDamage = 1000f;
    
    public static bool TrySmite(Player targetPlayer, NukeMode mode, out string error)
    {
        var targetUnit = targetPlayer.Aircraft;
        if (targetUnit == null)
        {
            error = $"{targetPlayer.GetDisplayName()} is not currently spawned in an aircraft.";
            return false;
        }
        
        if (targetUnit.disabled)
        {
            error = $"{targetPlayer.GetDisplayName()}'s aircraft is already disabled.";
            return false;
        }
        
        return TrySmite(targetPlayer, targetUnit, mode, out error);
    }
    
    private static bool TrySmite(Player targetPlayer, Unit targetUnit, NukeMode mode, out string error)
    {
        error = string.Empty;
        var networkManager = NetworkManagerNuclearOption.i;
        if (networkManager == null || !networkManager.Server.Active)
        {
            error = "Server is not active.";
            return false;
        }
        
        if (targetPlayer.Owner == null)
        {
            error = "The target player does not have an active network connection.";
            return false;
        }
        
        if (!Encyclopedia.Lookup.TryGetValue(StrategicNukeDefinitionKey, out var definition)
            || definition is not MissileDefinition missileDefinition
            || missileDefinition.unitPrefab == null)
        {
            error = $"Could not find MissileDefinition \"{StrategicNukeDefinitionKey}\".";
            return false;
        }
        
        var globalPosition = targetUnit.transform.GlobalPosition();
        var localPosition = globalPosition.ToLocalPosition();
        
        GameObject? dummyObject = null;
        
        try
        {
            dummyObject = Object.Instantiate(missileDefinition.unitPrefab, localPosition, Quaternion.identity);
            var missile = dummyObject.GetComponent<Missile>();
            var identity = dummyObject.GetComponent<NetworkIdentity>();
            
            if (missile == null || identity == null)
            {
                Object.Destroy(dummyObject);
                error = "The strategic nuke prefab is missing Missile or NetworkIdentity.";
                return false;
            }
            
            // Match the important part of Spawner.SpawnSavedMissile / SpawnMissile, but use a special observer filter
            // before Mirage spawns it
            missile.NetworkstartPosition = globalPosition;
            missile.NetworkstartRotation = Quaternion.identity;
            missile.NetworkstartingVelocity = Vector3.zero;
            missile.NetworkunitName = missile.definition.unitName;
            missile.NetworkownerID = PersistentID.None;
            missile.NetworkHQ = targetUnit.NetworkHQ;
            if (missile.rb != null)
                missile.rb.velocity = Vector3.zero;
            
            NukeVisibilityService.Register(identity, targetPlayer.Owner, mode, globalPosition,
                Plugin.SafeNukeRadiusMeters.Value);
            networkManager.ServerObjectManager.Spawn(dummyObject);
            
            // Send a detonation RPC with armed set to true, with the server's client not being an observer to prevent
            // actual (direct) damage from the nuke as it won't happen on the server's end
            // Exception for unsafe mode where it also happens on server and thus causes actual damage
            missile.RpcDetonate(null, false, globalPosition.AsVector3(), true, false, false, Vector3.up);
            
            // Immediately get rid of the dummy missile after it sent its RPC
            missile.SetTangible(false);
            if (missile.rb != null)
            {
                missile.rb.velocity = Vector3.zero;
                missile.rb.angularVelocity = Vector3.zero;
                missile.rb.isKinematic = true;
            }
            
            missile.enabled = false;
            Object.Destroy(dummyObject, 2f);
            
            // Actually kill the target, since there was no direct damage being done by the nuke
            // This simulates as if they were the only ones that took direct damage from it
            KillTargetUnit(targetUnit);
            
            return true;
        }
        catch (Exception ex)
        {
            if (dummyObject != null)
                Object.Destroy(dummyObject);
            
            Plugin.Log.LogError($"Failed to nuke {targetPlayer.GetDisplayName()}: {ex}");
            error = $"Failed to spawn/detonate the nuke: {ex.Message}";
            return false;
        }
    }
    
    private static void KillTargetUnit(Unit unit)
    {
        if (unit == null || unit.disabled)
            return;
        
        var parts = unit.partLookup?.ToArray() ?? Array.Empty<UnitPart>();
        foreach (var part in parts)
        {
            if (part == null || !part.criticalPart)
                continue;
            
            part.TakeDamage(0f, 0f, 0f, 0f, SmiteImpactDamage, PersistentID.None);
            if (unit.disabled)
                return;
        }
        
        // Fallback in case the target is not dead yet, like an unusual modded plane without proper criticalPart setup
        var damageables = unit.damageables?.ToArray() ?? [];
        foreach (var entry in damageables)
        {
            if (entry.Removed || entry.Damageable == null)
                continue;
            
            entry.Damageable.TakeDamage(0f, 0f, 0f, 0f, SmiteImpactDamage, PersistentID.None);
            if (unit.disabled)
                return;
        }
        
        // Last resort kill method
        if (!unit.disabled)
        {
            unit.Networkdisabled = true;
            unit.ReportKilled();
        }
    }
}

internal static class NukeVisibilityService
{
    private static readonly ConditionalWeakTable<NetworkIdentity, NukeVisibility> Visibilities = new();
    
    internal static void Register(NetworkIdentity identity, INetworkPlayer targetOwner, NukeMode mode,
        GlobalPosition blastPosition, float safeRadiusMeters)
    {
        Visibilities.Remove(identity);
        Visibilities.Add(identity, new NukeVisibility(identity, targetOwner, mode, blastPosition, safeRadiusMeters));
    }
    
    internal static bool TryGetVisibility(NetworkIdentity identity, out INetworkVisibility visibility)
    {
        if (Visibilities.TryGetValue(identity, out var nukeVisibility))
        {
            visibility = nukeVisibility;
            return true;
        }
        
        visibility = null!;
        return false;
    }
}

internal sealed class NukeVisibility : INetworkVisibility
{
    private readonly GlobalPosition _blastPosition;
    private readonly NetworkIdentity _identity;
    private readonly NukeMode _mode;
    private readonly float _safeRadiusSquared;
    private readonly INetworkPlayer _targetOwner;
    
    internal NukeVisibility(NetworkIdentity identity, INetworkPlayer targetOwner, NukeMode mode,
        GlobalPosition blastPosition, float safeRadiusMeters)
    {
        _identity = identity;
        _targetOwner = targetOwner;
        _mode = mode;
        _blastPosition = blastPosition;
        _safeRadiusSquared = safeRadiusMeters * safeRadiusMeters;
    }
    
    public bool OnCheckObserver(INetworkPlayer? observer)
    {
        if (observer == null)
            return false;
        
        var server = _identity.Server;
        if (server == null)
            return false;
        
        // Server's client shouldn't be an observer, this is also the case for dedicated servers, so that the nuke doesn't
        // actually happen on server's end and thus won't cause direct damage as that's server sided
        // Exception is for unsafe mode where the server is included so it causes actual damage
        if (observer == server.LocalPlayer)
            return _mode == NukeMode.Unsafe;;
        
        if (observer == _targetOwner)
            return true;
        
        switch (_mode)
        {
            case NukeMode.Stealth:
                return false;
            
            case NukeMode.Safe:
                return IsOutsideSafeRadius(observer);
            
            default:
                return true;
        }
    }
    
    public void OnRebuildObservers(HashSet<INetworkPlayer> observers, bool initialise)
    {
        var server = _identity.Server;
        if (server == null)
            return;
        
        if (_mode == NukeMode.Unsafe && server.LocalPlayer != null)
            observers.Add(server.LocalPlayer);
        
        foreach (var observer in server.AuthenticatedPlayers)
            if (OnCheckObserver(observer))
                observers.Add(observer);
    }
    
    private bool IsOutsideSafeRadius(INetworkPlayer observer)
    {
        if (!observer.TryGetPlayer<Player>(out var observerPlayer) || observerPlayer == null)
            return false;
        
        Unit? observerUnit = observerPlayer.Aircraft;
        // This might be necessary? Not sure, don't think it really matters what happens to the dismounted pilot
        // observerUnit ??= observerPlayer.PilotDismounted;
        
        if (observerUnit == null)
            return true;
        
        return FastMath.SquareDistance(observerUnit.GlobalPosition(), _blastPosition) > _safeRadiusSquared;
    }
}

[HarmonyPatch]
internal static class HarmonyPatches
{
    private static readonly Random GambleNukeRandom = new();
    
    [HarmonyPatch(typeof(NetworkIdentity), nameof(NetworkIdentity.Visibility), MethodType.Getter)]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void NetworkIdentityVisibilityPostfix(NetworkIdentity __instance,
        // ReSharper disable once InconsistentNaming
        ref INetworkVisibility __result)
    {
        if (NukeVisibilityService.TryGetVisibility(__instance, out var visibility))
            __result = visibility;
    }
    
    [HarmonyPatch(typeof(GambleCommand), nameof(GambleCommand.Execute), typeof(Player), typeof(string[]))]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GambleExecutePostfix(Player player, ref UniTask<(bool success, string? response)> __result)
    {
        __result = ApplyGambleNukeChance(player, __result);
    }
    
    private static async UniTask<(bool success, string? response)> ApplyGambleNukeChance(Player player,
        UniTask<(bool success, string? response)> originalResult)
    {
        var result = await originalResult;
        if (!result.success)
            return result;
        
        var chance = Plugin.GambleNukeChancePercent.Value;
        if (chance <= 0f)
            return result;
        
        double roll;
        lock (GambleNukeRandom)
        {
            roll = GambleNukeRandom.NextDouble() * 100.0;
        }
        
        if (roll >= chance)
            return result;
        
        if (!NukeService.TrySmite(player, NukeMode.Safe, out var error))
            Plugin.Log.LogWarning($"Gamble nuke roll succeeded for {player.GetDisplayName()} ({player.SteamID}), " +
                                  $"but smite failed: {error}");
        
        return result;
    }
}