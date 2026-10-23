using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Com.Graywar.NoServerManager.Proto;
using Cysharp.Threading.Tasks;
using GW_server_plugin.Features.CommandUtils;
using GW_server_plugin.Helpers;
using Mirage;
using NuclearOption.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NO_GWNC;

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
        "Nuke a player (who's spawned in) with a 250kt nuke. Optional modes: stealth, safe, or unsafe. Use !all to nuke everyone.";
    
    public override string Usage => "nuke <player name / ID / SteamID> [stealth|safe|unsafe] | nuke !all";
    
    public UniTask<bool> Validate(Player player, string[] args) =>
        UniTask.FromResult(TryParseArguments(args, out _, out _));
    
    public UniTask<(bool success, string? response)> Execute(Player player, string[] args)
    {
        if (args.Length == 1 && args[0].Equals("!all", StringComparison.OrdinalIgnoreCase))
            return ExecuteNukeAll();
        
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
    
    private static UniTask<(bool success, string? response)> ExecuteNukeAll()
    {
        var targets = new List<Player>();
        foreach (var connection in Globals.AuthenticatedPlayers)
        {
            if (!connection.TryGetPlayer<Player>(out var targetPlayer) || targetPlayer == null)
                continue;
            
            if (targetPlayer.Aircraft == null || targetPlayer.Aircraft.disabled)
                continue;
            
            targets.Add(targetPlayer);
        }
        
        if (targets.Count == 0)
            return Result(false, "There are no spawned players to nuke.");
        
        var nuked = 0;
        var failed = 0;
        
        foreach (var targetPlayer in targets)
            if (NukeService.TrySmite(targetPlayer, NukeMode.Public, out var error))
            {
                nuked++;
            }
            else
            {
                failed++;
                Plugin.Log.LogWarning(
                    $"Failed to nuke {targetPlayer.GetDisplayName()} ({targetPlayer.SteamID}) during !all: {error}");
            }
        
        return Result(nuked > 0, failed == 0
            ? $"Nuked all {nuked} spawned players."
            : $"Nuked {nuked} spawned players; {failed} failed.");
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
            return _mode == NukeMode.Unsafe;
        
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