using System;
using Cysharp.Threading.Tasks;
using GW_server_plugin.Features.CommandUtils.Commands;
using GW_server_plugin.Helpers;
using HarmonyLib;
using Mirage;
using NuclearOption.Networking;

namespace NO_GWNC;

[HarmonyPatch]
internal static class HarmonyPatches
{
    private static readonly Random GamblePunishmentRandom = new();
    
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
        __result = ApplyGamblePunishmentChance(player, __result);
    }
    
    private static async UniTask<(bool success, string? response)> ApplyGamblePunishmentChance(Player player,
        UniTask<(bool success, string? response)> originalResult)
    {
        var result = await originalResult;
        if (!result.success)
            return result;
        
        var chance = Plugin.GamblePunishmentChancePercent.Value;
        if (chance <= 0f)
            return result;
        
        double chanceRoll;
        bool useSabre;
        
        lock (GamblePunishmentRandom)
        {
            chanceRoll = GamblePunishmentRandom.NextDouble() * 100.0;
            useSabre = GamblePunishmentRandom.Next(2) == 0;
        }
        
        if (chanceRoll >= chance)
            return result;
        
        if (useSabre)
        {
            var options = new SabreOptions(12, 15000f, null);
            if (!SabreService.TryLaunch(player, options, out var spawned, out var missileKey, out var error))
                Plugin.Log.LogWarning(
                    $"Gamble Sabre roll succeeded for {player.GetDisplayName()} ({player.SteamID}), " +
                    $"but Sabre failed: {error}");
            else
                Plugin.Log.LogInfo($"Gamble Sabre triggered for {player.GetDisplayName()} ({player.SteamID}): " +
                                   $"{spawned}/12 {missileKey} missiles launched.");
        }
        else
        {
            if (!NukeService.TrySmite(player, NukeMode.Safe, out var error))
                Plugin.Log.LogWarning($"Gamble nuke roll succeeded for {player.GetDisplayName()} ({player.SteamID}), " +
                                      $"but smite failed: {error}");
        }
        
        return result;
    }
}