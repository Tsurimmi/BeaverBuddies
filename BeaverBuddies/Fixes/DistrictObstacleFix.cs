using HarmonyLib;
using Timberborn.Navigation;

namespace BeaverBuddies.Fixes
{
    // DistrictObstacleService.SetObstacle throws InvalidOperationException when
    // the target nodeId is already marked as an obstacle. That happens in
    // multiplayer when a DistrictCrossing (or similar DistrictObstacle) is
    // deconstructed and immediately replaced at the same tile: the block
    // object is gone but the navmesh node has not been released yet (the
    // release happens on a later tick via OnExitUnfinishedState).
    //
    // The original game can prevent this via its placement preview UI, but
    // when the mod replays a BuildingPlacedEvent from the network the
    // instantiation path goes straight through EntityComponent.Start ->
    // BlockObjectState.Initialize -> OnEnterUnfinishedState -> SetObstacle.
    // The exception then escapes to Unity and the game hard-crashes with no
    // way to catch it from Replay().
    //
    // Convert the throw into a silent no-op so the placement succeeds on the
    // navmesh side too. Worst case: the nodeId is marked as obstacle once
    // instead of twice, which matches the real state (a single building is
    // there now). The old UnsetObstacle call on the stale building clears it
    // later, so the map stays consistent.
    [HarmonyPatch(typeof(DistrictObstacleService), nameof(DistrictObstacleService.SetObstacle))]
    static class DistrictObstacleServiceSetObstaclePatcher
    {
        static bool Prefix(DistrictObstacleService __instance, int nodeId)
        {
            if (__instance.IsSetObstacle(nodeId))
            {
                Plugin.LogWarning($"[DistrictObstacleFix] Skipping SetObstacle at {nodeId} (already set)");
                return false;
            }
            return true;
        }
    }

    // Symmetric safety net: UnsetObstacle throws if the obstacle wasn't set,
    // which can happen in the opposite scenario (stale cleanup after forced
    // Place). Same treatment.
    [HarmonyPatch(typeof(DistrictObstacleService), nameof(DistrictObstacleService.UnsetObstacle))]
    static class DistrictObstacleServiceUnsetObstaclePatcher
    {
        static bool Prefix(DistrictObstacleService __instance, int nodeId)
        {
            if (!__instance.IsSetObstacle(nodeId))
            {
                Plugin.LogWarning($"[DistrictObstacleFix] Skipping UnsetObstacle at {nodeId} (not set)");
                return false;
            }
            return true;
        }
    }
}