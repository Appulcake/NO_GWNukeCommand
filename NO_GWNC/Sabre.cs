using System;
using System.Globalization;
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

internal sealed class SabreCommand : ConfigurableCommand, IGameCommand
{
    internal SabreCommand(ConfigFile config) : base(config)
    {
    }
    
    public override PermissionLevel DefaultPermissionLevel => PermissionLevel.Moderator;
    
    public override string OutputName => "sabre";
    
    public override string Description =>
        "Surround a spawned airborne player with hostile ARH missiles. Use !all to target every spawned player.";
    
    public override string Usage => "sabre <player name / ID / SteamID | !all> [count] [#km] [@missileKey]";
    
    public UniTask<bool> Validate(Player player, string[] args) =>
        UniTask.FromResult(TryParseArguments(args, out _, out _));
    
    public UniTask<(bool success, string? response)> Execute(Player player, string[] args)
    {
        if (!TryParseArguments(args, out var targetText, out var options))
            return Result(false, $"Usage: {Usage}");
        
        if (targetText.Equals("!all", StringComparison.OrdinalIgnoreCase))
            return ExecuteSabreAll(options);
        
        if (!PlayerUtils.TryFindPlayer(targetText, out var targetPlayer) || targetPlayer == null)
            return Result(false, $"Could not find player \"{targetText}\".");
        
        if (!SabreService.TryLaunch(targetPlayer, options, out var spawned, out var missileKey, out var error))
            return Result(false, error);
        
        return Result(true, $"{spawned}/{options.Count} {missileKey} missile{(spawned == 1 ? "" : "s")} " +
                            $"launched at {targetPlayer.GetDisplayName()} from {options.DistanceMeters / 1000f:0.##}km.");
    }
    
    private static UniTask<(bool success, string? response)> ExecuteSabreAll(SabreOptions options)
    {
        var targetedPlayers = 0;
        var succeeded = 0;
        var failed = 0;
        var totalMissilesSpawned = 0;
        foreach (var connection in Globals.AuthenticatedPlayers)
        {
            if (!connection.TryGetPlayer<Player>(out var targetPlayer) || targetPlayer == null)
                continue;
            
            if (targetPlayer.Aircraft == null || targetPlayer.Aircraft.disabled)
                continue;
            
            targetedPlayers++;
            if (SabreService.TryLaunch(targetPlayer, options, out var spawned, out _, out var error))
            {
                succeeded++;
                totalMissilesSpawned += spawned;
            }
            else
            {
                failed++;
                Plugin.Log.LogWarning($"Failed to Sabre {targetPlayer.GetDisplayName()} ({targetPlayer.SteamID}) " +
                                      "during !all: {error}");
            }
        }
        
        if (targetedPlayers == 0)
            return Result(false, "There are no spawned players to Sabre.");
        
        return Result(
            succeeded > 0,
            failed == 0
                ? $"Sabred all {succeeded} spawned players with {totalMissilesSpawned} missiles."
                : $"Sabred {succeeded}/{targetedPlayers} spawned players with {totalMissilesSpawned} missiles; " +
                  $"{failed} failed.");
    }
    
    private static bool TryParseArguments(string[] args, out string targetText, out SabreOptions options)
    {
        targetText = string.Empty;
        var count = 1;
        var distanceMeters = SabreService.DefaultSpawnDistance;
        string? missileKeyOverride = null;
        var hasCount = false;
        var hasDistance = false;
        var hasMissileKey = false;
        if (args.Length == 0)
        {
            options = default;
            return false;
        }
        
        var targetArgCount = args.Length;
        while (targetArgCount > 1)
        {
            var token = args[targetArgCount - 1];
            if (!hasDistance && TryParseDistance(token, out var parsedDistance))
            {
                distanceMeters = parsedDistance;
                hasDistance = true;
                targetArgCount--;
                continue;
            }
            
            if (!hasMissileKey && TryParseMissileKey(token, out var parsedMissileKey))
            {
                missileKeyOverride = parsedMissileKey;
                hasMissileKey = true;
                targetArgCount--;
                continue;
            }
            
            if (!hasCount &&
                int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount))
            {
                count = Mathf.Clamp(parsedCount, 1, SabreService.MaxMissileCount);
                hasCount = true;
                targetArgCount--;
                continue;
            }
            
            break;
        }
        
        targetText = string.Join(" ", args, 0, targetArgCount).Trim();
        if (targetText.Length == 0)
        {
            options = default;
            return false;
        }
        
        options = new SabreOptions(count, distanceMeters, missileKeyOverride);
        return true;
    }
    
    private static bool TryParseDistance(string value, out float distanceMeters)
    {
        distanceMeters = 0f;
        if (!value.EndsWith("km", StringComparison.OrdinalIgnoreCase))
            return false;
        
        var numericPart = value.Substring(0, value.Length - 2);
        if (!float.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var kilometres))
            return false;
        
        if (float.IsNaN(kilometres) || float.IsInfinity(kilometres))
            return false;
        
        kilometres = Mathf.Clamp(kilometres, SabreService.MinSpawnDistanceKm, SabreService.MaxSpawnDistanceKm);
        distanceMeters = kilometres * 1000f;
        return true;
    }
    
    private static bool TryParseMissileKey(string value, out string missileKey)
    {
        missileKey = string.Empty;
        if (value.Length <= 1 || value[0] != '@')
            return false;
        
        missileKey = value.Substring(1).Trim();
        return missileKey.Length > 0;
    }
    
    private static UniTask<(bool success, string? response)> Result(bool success, string response) =>
        UniTask.FromResult<(bool, string?)>((success, response));
}

public readonly struct SabreOptions
{
    internal SabreOptions(int count, float distanceMeters, string? missileKeyOverride)
    {
        Count = count;
        DistanceMeters = distanceMeters;
        MissileKeyOverride = missileKeyOverride;
    }
    
    internal int Count { get; }
    internal float DistanceMeters { get; }
    internal string? MissileKeyOverride { get; }
}

public static class SabreService
{
    internal const int MaxMissileCount = 48;
    internal const float DefaultSpawnDistance = 15000f;
    internal const float MinSpawnDistanceKm = 0.1f;
    internal const float MaxSpawnDistanceKm = 1000f;
    private const float MinRadarAltitude = 3f;
    private const float InitialRelativeSpeed = 2500f;
    private const float MinimumInitialSpeed = 500f;
    
    public static bool TryLaunch(Player targetPlayer, SabreOptions options, out int spawned, out string missileKey,
        out string error)
    {
        spawned = 0;
        missileKey = string.Empty;
        error = string.Empty;
        
        var requestedMissileKey = !string.IsNullOrWhiteSpace(options.MissileKeyOverride)
            ? options.MissileKeyOverride
            : Plugin.SabreMissileDefinitionKey.Value;
        if (requestedMissileKey == null || requestedMissileKey.Trim().Length == 0)
        {
            error = "No Sabre missile definition key was specified.";
            return false;
        }
        
        missileKey = requestedMissileKey;
        var target = targetPlayer.Aircraft;
        if (target == null)
        {
            error = $"{targetPlayer.GetDisplayName()} is not currently spawned in an aircraft.";
            return false;
        }
        
        if (target.disabled)
        {
            error = $"{targetPlayer.GetDisplayName()}'s aircraft is already disabled.";
            return false;
        }
        
        target.CheckRadarAlt();
        if (target.radarAlt < MinRadarAltitude)
        {
            error = $"{targetPlayer.GetDisplayName()} is too close to the ground " +
                    $"({target.radarAlt:F0}m, minimum required is {MinRadarAltitude}m).";
            
            return false;
        }
        
        var networkManager = NetworkManagerNuclearOption.i;
        if (networkManager == null || !networkManager.Server.Active)
        {
            error = "Server is not active.";
            return false;
        }
        
        var targetHq = target.NetworkHQ;
        if (targetHq == null)
        {
            error = "Target aircraft has no faction.";
            return false;
        }
        
        var hostileHq = FindOpposingHq(targetHq);
        if (hostileHq == null)
        {
            error = "Could not find target's opposing faction.";
            return false;
        }
        
        if (!Encyclopedia.Lookup.TryGetValue(missileKey, out var definition) ||
            definition is not MissileDefinition missileDefinition || missileDefinition.unitPrefab == null)
        {
            error = $"Could not find MissileDefinition \"{missileKey}\".";
            return false;
        }
        
        if (missileDefinition.unitPrefab.GetComponent<ARHSeeker>() == null)
        {
            error = $"Missile \"{missileKey}\" does not seem to use an ARH seeker.";
            return false;
        }
        
        var count = Mathf.Clamp(options.Count, 1, MaxMissileCount);
        var spawnDistance = Mathf.Clamp(options.DistanceMeters, MinSpawnDistanceKm * 1000f, MaxSpawnDistanceKm * 1000f);
        hostileHq.RpcUpdateTrackingInfo(target.persistentID);
        spawned = SpawnRing(networkManager, missileDefinition, hostileHq, target, count, spawnDistance);
        if (spawned > 0)
            return true;
        
        error = "Failed to spawn any ARH missiles.";
        return false;
    }
    
    private static int SpawnRing(NetworkManagerNuclearOption networkManager, MissileDefinition missileDefinition,
        FactionHQ hostileHq, Aircraft target, int count, float spawnDistance)
    {
        var spawned = 0;
        var targetPosition = target.GlobalPosition();
        var targetVelocity = target.rb != null
            ? target.rb.velocity
            : Vector3.zero;
        var front = Vector3.ProjectOnPlane(target.transform.forward, Vector3.up);
        if (front.sqrMagnitude < 0.001f)
            front = Vector3.forward;
        else
            front.Normalize();
        var angleStep = 360f / count;
        for (var i = 0; i < count; i++)
        {
            var radialDirection = Quaternion.AngleAxis(angleStep * i, Vector3.up) * front;
            var spawnPosition = new GlobalPosition(targetPosition.x + radialDirection.x * spawnDistance,
                targetPosition.y + radialDirection.y * spawnDistance,
                targetPosition.z + radialDirection.z * spawnDistance);
            var directionToTarget = FastMath.NormalizedDirection(spawnPosition, targetPosition);
            var rotation = Quaternion.LookRotation(directionToTarget, Vector3.up);
            var missileSpeed = Mathf.Max(MinimumInitialSpeed,
                InitialRelativeSpeed + Vector3.Dot(targetVelocity, directionToTarget));
            var velocity = directionToTarget * missileSpeed;
            if (TrySpawnMissile(networkManager, missileDefinition, hostileHq, target, spawnPosition, rotation,
                    velocity))
                spawned++;
        }
        
        return spawned;
    }
    
    private static bool TrySpawnMissile(NetworkManagerNuclearOption networkManager, MissileDefinition definition,
        FactionHQ hostileHq, Unit target, GlobalPosition position, Quaternion rotation, Vector3 velocity)
    {
        GameObject? gameObject = null;
        try
        {
            var localPosition = position.ToLocalPosition();
            gameObject = Object.Instantiate(definition.unitPrefab, localPosition, rotation);
            var missile = gameObject.GetComponent<Missile>();
            var identity = gameObject.GetComponent<NetworkIdentity>();
            var rb = gameObject.GetComponent<Rigidbody>();
            if (missile == null || identity == null || rb == null)
            {
                Object.Destroy(gameObject);
                return false;
            }
            
            missile.NetworkHQ = hostileHq;
            missile.NetworkunitName = missile.definition.unitName;
            missile.NetworkownerID = PersistentID.None;
            missile.SetTarget(target);
            missile.NetworkstartPosition = position;
            missile.NetworkstartRotation = rotation;
            missile.NetworkstartingVelocity = velocity;
            rb.velocity = velocity;
            networkManager.ServerObjectManager.Spawn(identity);
            return true;
        }
        catch (Exception ex)
        {
            if (gameObject != null)
                Object.Destroy(gameObject);
            
            Plugin.Log.LogError($"Failed to spawn ARH missile: {ex}");
            return false;
        }
    }
    
    private static FactionHQ? FindOpposingHq(FactionHQ targetHq)
    {
        foreach (var hq in FactionRegistry.GetAllHQs())
            if (hq != null && hq != targetHq)
                return hq;
        return null;
    }
}