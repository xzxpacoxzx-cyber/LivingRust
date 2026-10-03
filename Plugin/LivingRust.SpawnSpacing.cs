using System;
using LivingRust.Models;
using UnityEngine;

namespace Carbon.Plugins;

/// <summary>
/// Spawn spacing (2026-10-03, Lucas's own explicit spec): bots shouldn't pile onto the exact same
/// spot. When the first world/beach spawn point Rust hands back already has a living survivor on
/// it, pick another - usually another beach spawn, occasionally (20%) a random inland site
/// instead. Only applies to world spawns (fresh batch spawns and death-respawns that aren't going
/// to an owned sleeping bag); a bag/base respawn is deliberately NOT redirected, and explicit
/// positions (aimed spawns, persistence restores) are never touched. The 1-3s buffer between
/// batch spawns lives in the SpawnMany* timing constants (LivingRust.Debug.cs).
/// </summary>
public partial class LivingRust
{
    // How close a living survivor has to be to a candidate spawn point to count it as occupied.
    private const float SpawnOccupiedRadius = 5f;

    // Re-rolls before giving up and just using whatever the last candidate was - an unlucky,
    // crowded map should still spawn the bot rather than stall it.
    private const int SpawnRedirectMaxAttempts = 10;

    // Chance each re-roll goes to a random inland site instead of another beach spawn point.
    private const int SpawnRedirectInlandPercent = 20;

    private bool IsSpawnPointOccupied(Vector3 position, Guid selfCharacterId)
    {
        if (_engine == null)
        {
            return false;
        }

        foreach (Survivor other in _engine.SurvivorManager.GetAll())
        {
            BasePlayer otherNpc = other.Player;

            if (other.Character.Id == selfCharacterId || otherNpc == null || otherNpc.IsDestroyed || !otherNpc.IsAlive())
            {
                continue;
            }

            if (Vector3.Distance(otherNpc.transform.position, position) <= SpawnOccupiedRadius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rust's own spawn-point lookup, but re-rolled (beach, or random inland 20% of the time)
    /// while the chosen point already has a survivor standing on it.
    /// </summary>
    private void FindUnoccupiedSpawnPosition(BasePlayer npc, Guid selfCharacterId, out Vector3 position, out Quaternion rotation)
    {
        BasePlayer.SpawnPoint spawnPoint = ServerMgr.FindSpawnPoint(npc);
        position = spawnPoint.pos;
        rotation = spawnPoint.rot;

        for (int attempt = 0; attempt < SpawnRedirectMaxAttempts && IsSpawnPointOccupied(position, selfCharacterId); attempt++)
        {
            if (UnityEngine.Random.Range(0, 100) < SpawnRedirectInlandPercent && TryFindRandomInlandSite(out Vector3 inlandSite))
            {
                position = inlandSite;
                rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            }
            else
            {
                spawnPoint = ServerMgr.FindSpawnPoint(npc);
                position = spawnPoint.pos;
                rotation = spawnPoint.rot;
            }
        }
    }
}
