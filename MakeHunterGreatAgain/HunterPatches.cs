// File: HunterPatches.cs
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using System.Reflection;
using System.Collections.Generic;
using Photon.Pun; // Required for PhotonView, RpcTarget etc.

namespace HunterMod
{
    /// <summary>
    /// Contains Harmony patches for the EnemyHunter class to integrate mod behaviors.
    /// </summary>
    [HarmonyPatch(typeof(EnemyHunter))]
    internal static class HunterPatches
    {
        // Dictionary to track failures finding retreat points specifically during reload (MasterClient only)
        private static Dictionary<int, int> stateLeaveFailCounters = new Dictionary<int, int>();
        private const int MAX_LEAVE_FAILURES_WHILE_RELOADING = 5; // Max retries before despawning

        /// <summary>
        /// Helper method to safely get the EnemyNavMeshAgent component using reflection.
        /// </summary>
        private static EnemyNavMeshAgent GetEnemyNavMeshAgent(EnemyHunter instance)
        {
            if (instance?.enemy == null)
            {
                Plugin.LogErrorF("GetEnemyNavMeshAgent: Instance or instance.enemy is null!");
                return null;
            }
            try
            {
                FieldInfo navAgentField = AccessTools.Field(typeof(Enemy), "NavMeshAgent");
                if (navAgentField != null)
                {
                    object navAgentObject = navAgentField.GetValue(instance.enemy);
                    return navAgentObject as EnemyNavMeshAgent;
                }
                else
                {
                    Plugin.LogErrorF($"GetEnemyNavMeshAgent: Could not find Field 'NavMeshAgent' on type 'Enemy' for {instance.gameObject.name}!");
                    return null;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.LogErrorF($"GetEnemyNavMeshAgent: Error accessing 'NavMeshAgent' field on {instance.gameObject.name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets or adds the HunterAmmoTracker component to the EnemyHunter instance.
        /// Populates the tracker with configuration values upon creation.
        /// </summary>
        private static HunterAmmoTracker GetOrAddTracker(EnemyHunter instance)
        {
            if (instance == null) return null;

            var tracker = instance.GetComponent<HunterAmmoTracker>();
            if (tracker == null)
            {
                tracker = instance.gameObject.AddComponent<HunterAmmoTracker>();
                Plugin.LogInfoF($"Added HunterAmmoTracker to {instance.gameObject.name}. Initializing config...");
                tracker.ConfiguredFastReloadTime = Plugin.FastReloadTimeConfig.Value;
                tracker.ConfiguredMediumReloadTime = Plugin.MediumReloadTimeConfig.Value;
                tracker.ConfiguredSlowReloadTime = Plugin.SlowReloadTimeConfig.Value;
                tracker.ConfigEnableDamageInterrupt = Plugin.EnableDamageInterruptConfig.Value;
                tracker.ConfigDamageInterruptDelay = Plugin.DamageInterruptDelayConfig.Value;
                tracker.ConfigMinigunShots = Plugin.MinigunShots.Value;
                tracker.ConfigMinigunShotDelay = Plugin.MinigunShotDelay.Value;
                tracker.ConfigEnableTotalAmmoLimit = Plugin.EnableTotalAmmoLimitConfig.Value;
                tracker.ConfigTotalAmmoCount = Plugin.TotalAmmoCountConfig.Value;
                tracker.ConfigRunAwayWhileReloading = Plugin.RunAwayWhileReloadingConfig.Value;
            }
            return tracker;
        }

        /// <summary>
        /// Postfix patch for OnSpawn. Initializes the HunterAmmoTracker state on the MasterClient
        /// and sends a buffered RPC to sync initial state to other clients.
        /// </summary>
        [HarmonyPatch(nameof(EnemyHunter.OnSpawn))]
        [HarmonyPostfix]
        static void OnSpawnPostfix(EnemyHunter __instance)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null || !pv.IsMine) return; // Only MasterClient initializes

            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker != null)
            {
                // MasterClient determines skill based on config weights
                ReloadSkill assignedSkill;
                int wF = Plugin.FastSkillWeight.Value, wM = Plugin.MediumSkillWeight.Value, wS = Plugin.SlowSkillWeight.Value;
                int tW = wF + wM + wS;
                if (tW <= 0) assignedSkill = ReloadSkill.Medium;
                else { int r = Random.Range(0, tW); if (r < wF) assignedSkill = ReloadSkill.Fast; else if (r < wF + wM) assignedSkill = ReloadSkill.Medium; else assignedSkill = ReloadSkill.Slow; }

                // MasterClient determines initial ammo based on config
                int initialAmmo = tracker.ConfigEnableTotalAmmoLimit ? tracker.ConfigTotalAmmoCount : 9999;

                // MasterClient calls NetworkInitialize on the tracker, which sets local state and sends RPC
                tracker.NetworkInitialize(assignedSkill, initialAmmo);

                stateLeaveFailCounters.Remove(__instance.GetInstanceID()); // Reset Master's local counter
            }
            else
            {
                Plugin.LogErrorF($"Failed GetOrAddTracker for {__instance?.gameObject?.name} in OnSpawnPostfix (Master).");
            }
        }

        /// <summary>
        /// Prefix patch for StateIdle. Checks synced state and potentially overrides behavior.
        /// Only MasterClient controls movement or state changes.
        /// </summary>
        [HarmonyPatch("StateIdle")]
        [HarmonyPrefix]
        static bool StateIdlePrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker == null) return true; // Allow original if no tracker

            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            // Check synced state: Out of Ammo
            if (tracker.isOutOfAmmoPermanently)
            {
                Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave.");
                if (pv.IsMine) // Only Master changes state
                {
                    Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave);
                    stateLeaveFailCounters.Remove(__instance.GetInstanceID());
                }
                return false; // Block original Idle logic on all clients
            }

            // Check synced state: Run Away While Reloading
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading)
            {
                Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} reloading (Synced). Moving away.");
                if (pv.IsMine) // Only Master controls NavMeshAgent
                {
                    EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance);
                    if (enemyNavAgent != null)
                    {
                        // Warping might cause issues if base game syncs position well, consider removing if jittery
                        // enemyNavAgent.Warp(__instance.transform.position);
                        enemyNavAgent.ResetPath();
                        if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) { enemyNavAgent.SetDestination(p); }
                    }
                    ___stateImpulse = false; // Prevent original idle logic timer reset
                }
                return false; // Block original Idle logic on all clients
            }
            return true; // Allow original Idle logic if no mod conditions met
        }

        /// <summary>
        /// Prefix patch for StateRoam. Checks synced state and potentially overrides behavior.
        /// Only MasterClient controls movement or state changes.
        /// </summary>
        [HarmonyPatch("StateRoam")]
        [HarmonyPrefix]
        static bool StateRoamPrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker == null) return true;

            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            // Check synced state: Out of Ammo
            if (tracker.isOutOfAmmoPermanently)
            {
                if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); }
                return false; // Block original Roam logic
            }

            // Check synced state: Run Away While Reloading
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading)
            {
                Plugin.LogDebugF($"StateRoamPrefix: Hunter {__instance.gameObject.name} reloading (Synced). Moving away.");
                if (pv.IsMine) // Only Master controls NavMeshAgent
                {
                    EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance);
                    if (enemyNavAgent != null)
                    {
                        enemyNavAgent.ResetPath();
                        if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) enemyNavAgent.SetDestination(p);
                    }
                    ___stateImpulse = false; // Prevent original roam logic timer reset
                }
                return false; // Block original Roam logic
            }
            return true; // Allow original Roam logic
        }

        /// <summary>
        /// Prefix patch for StateAim. Checks synced state to potentially block aiming or trigger retreat.
        /// Lets clients update visuals but blocks progression to Shoot state if needed.
        /// Only MasterClient controls actual movement or state changes.
        /// </summary>
        [HarmonyPatch("StateAim")]
        [HarmonyPrefix]
        static bool StateAimPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker == null) return true;

            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            // Check synced state: Out of Ammo
            if (tracker.isOutOfAmmoPermanently)
            {
                Plugin.LogDebugF($"StateAimPrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave.");
                if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); }
                return false; // Block aiming state
            }

            // Check synced states: Reloading, Interrupted, or No Ammo (but not permanent OOA yet)
            bool blockAimProgression = tracker.IsReloading || tracker.IsInterrupted || !tracker.HasTotalAmmo;
            if (blockAimProgression)
            {
                string reason = !tracker.HasTotalAmmo ? "OOA" : (tracker.IsReloading ? $"reloading" : $"interrupted");
                Plugin.LogDebugF($"StateAimPrefix BLOCKED: {__instance.gameObject.name} is {reason} (Synced).");

                if (pv.IsMine) // Master handles movement/state
                {
                    if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading)
                    {
                        Plugin.LogDebugF($"StateAimPrefix (Master): {__instance.gameObject.name} running away.");
                        EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance);
                        if (enemyNavAgent != null) { enemyNavAgent.ResetPath(); if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) enemyNavAgent.SetDestination(p); }
                        else Plugin.LogErrorF($"StateAimPrefix (Master): EnemyNavMeshAgent missing!");
                    }
                    else
                    {
                        // Keep calling AimLogic on Master to potentially update target, but shoot is blocked later
                        Plugin.CallAimLogic(__instance);
                    }
                    // Prevent immediate transition if timer was short
                    if (___stateTimer <= Time.deltaTime) ___stateTimer = 0.1f;
                }

                // Clients ALSO call AimLogic to keep visuals aimed at the (potentially moving) target
                Plugin.CallAimLogic(__instance);
                if (___stateTimer <= Time.deltaTime) ___stateTimer = 0.1f; // Reset timer on clients too

                return false; // Block original StateAim timer logic / transition to Shoot state
            }
            return true; // Allow original Aim state logic if not blocked
        }

        /// <summary>
        /// Prefix patch for StateShoot. Only MasterClient executes shooting logic.
        /// MasterClient checks ammo, calls ShootRPC, handles minigun logic, and transitions state.
        /// Clients are blocked from executing original shoot logic and rely on ShootRPC for effects.
        /// </summary>
        [HarmonyPatch("StateShoot")]
        [HarmonyPrefix]
        static bool StateShootPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker == null) return true;

            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            // --- Client Logic ---
            if (!pv.IsMine)
            {
                // Clients simply keep their visuals aiming; they don't shoot or change state here.
                Plugin.CallAimLogic(__instance);
                return false; // Block clients from running original shoot code
            }

            // --- MasterClient Authoritative Logic ---

            // Double-check ammo state before shooting
            if (!tracker.HasTotalAmmo)
            {
                Plugin.LogErrorF($"StateShootPrefix (Master): {__instance.gameObject.name} OOA! Forcing Leave.");
                if (!tracker.isOutOfAmmoPermanently && !Plugin.IsRepoLastStandActive()) // Set state if needed
                {
                    tracker.isOutOfAmmoPermanently = true;
                }
                Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave);
                stateLeaveFailCounters.Remove(__instance.GetInstanceID());
                return false; // Block shooting
            }

            // --- Standard Shoot Logic (Minigun OFF) ---
            if (!Plugin.IsMinigunModeEnabled())
            {
                if (!tracker.TryDecrementTotalAmmo()) // Master decrements authoritative ammo
                {
                    // OOA state handled within TryDecrementTotalAmmo
                    Plugin.LogInfoF($"StateShootPrefix(N) (Master): {__instance.gameObject.name} OOA this shot. Forcing Leave.");
                    Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave);
                    stateLeaveFailCounters.Remove(__instance.GetInstanceID());
                    return false; // Block shot
                }
                // Ammo OK, trigger the shot effect via RPC
                Plugin.LogDebugF($"StateShootPrefix (Master): Standard Shot Allowed. Calling ShootRPC.");
                Plugin.CallShootRPC(__instance); // Master sends RPC to all

                // We've handled the shot, now block original StateShoot and let StateShootEnd handle the transition
                ___stateTimer = 0.05f; // Set a very short timer to quickly move to ShootEnd
                return false;
            }
            // --- Minigun Shoot Logic (Minigun ON) ---
            else
            {
                // Check Master state for reload/interrupt
                if (tracker.IsReloading || tracker.IsInterrupted)
                {
                    Plugin.LogDebugF($"StateShootPrefix(M) (Master): Blocked by Reload/Interrupt.");
                    Plugin.CallAimLogic(__instance); // Keep aiming visuals
                    return false; // Block shooting
                }

                // Initialize burst if it hasn't started (Master only)
                if (!tracker.isMinigunBurstActive)
                {
                    tracker.InitializeMinigunBurst(); // Master sets state (synced via IPunObservable)
                    // Set timer for the *entire* burst duration + a buffer
                    ___stateTimer = tracker.ConfigMinigunShots * tracker.ConfigMinigunShotDelay + 1.0f;
                }

                // Handle firing shots based on timer (Master only)
                tracker.minigunShotTimer -= Time.deltaTime;
                if (tracker.minigunShotTimer <= 0f && tracker.minigunShotsRemaining > 0)
                {
                    if (!tracker.TryDecrementTotalAmmo()) // Master decrements ammo
                    {
                        Plugin.LogInfoF($"StateShootPrefix(M) (Master): {__instance.gameObject.name} OOA mid-burst. End & Leave.");
                        tracker.EndMinigunBurst(); // Master ends burst state
                        Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave);
                        stateLeaveFailCounters.Remove(__instance.GetInstanceID());
                        return false; // Block rest of burst
                    }
                    // Fire shot via RPC
                    Plugin.LogDebugF($"StateShootPrefix (Master): Fire minigun {tracker.ConfigMinigunShots - tracker.minigunShotsRemaining + 1}/{tracker.ConfigMinigunShots}. Ammo: {tracker.currentTotalAmmo}");
                    Plugin.CallShootRPC(__instance);
                    tracker.minigunShotsRemaining--; // Master tracks count
                    tracker.minigunShotTimer = tracker.ConfigMinigunShotDelay; // Reset timer for next shot
                }

                // Check if burst is finished (Master only)
                // *** MODIFIED: Transition to ShootEnd when burst finishes ***
                if (tracker.minigunShotsRemaining <= 0 && tracker.isMinigunBurstActive)
                {
                    Plugin.LogInfoF($"StateShootPrefix (Master): Minigun burst done. Transitioning to ShootEnd & Starting Reload.");
                    tracker.EndMinigunBurst(); // Master ends burst state
                    // ---> CHANGE: Transition to ShootEnd instead of LeaveStart
                    Plugin.CallUpdateState(__instance, EnemyHunter.State.ShootEnd);
                    tracker.StartReload(); // Master initiates reload immediately (sends RPC)
                    // StateShootEnd will now handle the rest of the animation/timer before its own transition
                    return false; // Block original StateShoot
                }

                Plugin.CallAimLogic(__instance); // Keep aiming during burst (Master)
                return false; // Block original StateShoot logic while burst is active
            }
        }


        /// <summary>
        /// Postfix patch for OnHurt. Only MasterClient applies the damage interrupt logic.
        /// </summary>
        [HarmonyPatch(nameof(EnemyHunter.OnHurt))]
        [HarmonyPostfix]
        static void OnHurtPostfix(EnemyHunter __instance)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null || !pv.IsMine) return; // Only MasterClient handles interrupts

            HunterAmmoTracker t = GetOrAddTracker(__instance);
            if (t != null)
            {
                t.ApplyDamageInterrupt(); // Master applies interrupt (sends RPC if needed)
            }
            else Plugin.LogErrorF($"Failed GetOrAddTracker in OnHurtPostfix (Master).");
        }

        /// <summary>
        /// Prefix patch for StateShootEnd. Handles transitions after shots.
        /// If Minigun mode was active, allows the original method to run for animation timing.
        /// Otherwise (standard shot), only MasterClient handles the transition to LeaveStart and starting reload.
        /// </summary>
        [HarmonyPatch("StateShootEnd")]
        [HarmonyPrefix]
        static bool StateShootEndPrefix(EnemyHunter __instance, ref float ___stateTimer, ref int ___shotsFired, int ___shotsFiredMax)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            // *** ADDED: Check if minigun mode IS enabled ***
            if (Plugin.IsMinigunModeEnabled())
            {
                // If we entered this state *after* a minigun burst (via the modified StateShootPrefix),
                // we just want the original StateShootEnd to run its course for animation/timing.
                // Reload was already started in StateShootPrefix.
                Plugin.LogDebugF("StateShootEndPrefix(M): Minigun mode was active, allowing original StateShootEnd to run.");
                // Master will run original, Clients will observe (original method likely checks IsMine anyway)
                return true; // <<< ALLOW original method to run
            }

            // --- Original Logic for Standard Shots (Run only on Master) ---
            if (!pv.IsMine) return false; // Clients don't run state transitions

            HunterAmmoTracker t = GetOrAddTracker(__instance);
            if (t == null) return true;

            // Check Master state for OOA
            if (t.isOutOfAmmoPermanently)
            {
                Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave);
                stateLeaveFailCounters.Remove(__instance.GetInstanceID());
                return false;
            }

            // Standard shot finished, transition to LeaveStart and trigger reload
            if (___stateTimer <= Time.deltaTime) // Use original timer condition
            {
                Plugin.LogInfoF($"StateShootEndPrefix(N) (Master): Shot done. LeaveStart & Reload.");
                Plugin.CallUpdateState(__instance, EnemyHunter.State.LeaveStart); // Master transitions state
                t.StartReload(); // Master initiates reload (sends RPC)
                return false; // Block original transition logic
            }

            return true; // Allow original timer to continue if standard shot not ready
        }


        /// <summary>
        /// Helper prefix check used by various interaction patches (OnInvestigate, OnTouchPlayer, etc.).
        /// Checks the synced 'isOutOfAmmoPermanently' state to prevent re-engagement if OOA. Runs on all clients.
        /// </summary>
        static bool PreventReEngagePrefix(EnemyHunter __instance)
        {
            HunterAmmoTracker t = __instance?.GetComponent<HunterAmmoTracker>(); // Check local component
            if (t != null && t.isOutOfAmmoPermanently) // Check the synced state
            {
                Plugin.LogDebugF($"PreventReEngagePrefix: {__instance.gameObject.name} OOA (Synced). Prevent engage.");
                return false; // Block engagement on this client
            }
            return true; // Allow engagement
        }
        // Patches using the PreventReEngagePrefix check:
        [HarmonyPatch(nameof(EnemyHunter.OnInvestigate))][HarmonyPrefix] static bool OnInvestigatePrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayer))][HarmonyPrefix] static bool OnTouchPlayerPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayerGrabbedObject))][HarmonyPrefix] static bool OnTouchPlayerGrabbedObjectPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnGrabbed))][HarmonyPrefix] static bool OnGrabbedPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);


        /// <summary>
        /// Prefix patch for StateLeave. MasterClient handles movement, finding new retreat points,
        /// failure counting, and state transitions (Leave -> Despawn).
        /// Clients are blocked from executing movement/state logic.
        /// </summary>
        [HarmonyPatch("StateLeave")]
        [HarmonyPrefix]
        static bool StateLeavePrefix(EnemyHunter __instance, ref float ___stateTimer, Vector3 ___leavePosition, ref bool ___stateImpulse)
        {
            HunterAmmoTracker t = GetOrAddTracker(__instance);
            if (t == null) return true;

            PhotonView pv = Plugin.GetPhotonView(__instance);
            if (pv == null) return true;

            int instanceId = __instance.GetInstanceID();
            // Check synced states to determine if mod logic applies
            bool needsToRunAwayReloading = Plugin.ShouldRunAwayWhileReloading() && t.IsReloading;
            bool needsToLeavePermanently = t.isOutOfAmmoPermanently;

            // If neither mod condition applies, let original logic run (only on Master)
            if (!needsToRunAwayReloading && !needsToLeavePermanently)
            {
                if (pv.IsMine) stateLeaveFailCounters.Remove(instanceId); // Master resets counter if no longer relevant
                return pv.IsMine; // Only Master runs original StateLeave
            }

            // --- MasterClient Authoritative Movement/State Change Logic ---
            if (pv.IsMine)
            {
                EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance);
                if (enemyNavAgent == null) return true; // Cannot proceed without nav agent

                // Check if the Hunter has reached its destination or the timer ran out
                bool exitLeaveCondition = (___stateTimer <= Time.deltaTime || Vector3.Distance(__instance.transform.position, ___leavePosition) < 1f);

                if (exitLeaveCondition)
                {
                    if (needsToLeavePermanently) // Check Master's authoritative state
                    {
                        Plugin.LogInfoF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} OOA and reached Leave destination/timer. Forcing Despawn.");
                        Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); // Master changes state
                        stateLeaveFailCounters.Remove(instanceId);
                        return false; // Block original
                    }
                    else // Must be needsToRunAwayReloading case
                    {
                        Plugin.LogDebugF($"StateLeavePrefix (Master): {__instance.gameObject.name} reloading & reached dest/timer. Finding new retreat point.");
                        if (Plugin.FindRetreatPoint(__instance, out Vector3 newLeavePos)) // Try find new point
                        {
                            stateLeaveFailCounters.Remove(instanceId); // Reset fail counter on success
                                                                       // Update the internal leavePosition field via reflection
                            var leavePosField = AccessTools.Field(typeof(EnemyHunter), "leavePosition");
                            if (leavePosField != null) leavePosField.SetValue(__instance, newLeavePos);
                            else Plugin.LogErrorF("StateLeavePrefix (Master): Could not find leavePosition field!");

                            enemyNavAgent.SetDestination(newLeavePos); // Master sets new destination
                            ___stateTimer = 5f; // Reset timer on Master
                            ___stateImpulse = false; // Prevent immediate re-entry? Check base game logic if needed.
                        }
                        else // Failed to find new point
                        {
                            int currentFails = stateLeaveFailCounters.ContainsKey(instanceId) ? stateLeaveFailCounters[instanceId] : 0;
                            currentFails++; stateLeaveFailCounters[instanceId] = currentFails;
                            Plugin.LogWarningF($"StateLeavePrefix (Master): No new retreat point for {__instance.gameObject.name} while reloading. Failure {currentFails}/{MAX_LEAVE_FAILURES_WHILE_RELOADING}");
                            ___stateTimer = 1f; // Short timer to retry quickly

                            if (currentFails >= MAX_LEAVE_FAILURES_WHILE_RELOADING)
                            {
                                Plugin.LogErrorF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} failed find retreat point {MAX_LEAVE_FAILURES_WHILE_RELOADING} times WHILE RELOADING. Forcing Despawn.");
                                Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); // Force despawn after max failures
                                stateLeaveFailCounters.Remove(instanceId);
                            }
                        }
                        return false; // Block original StateLeave logic after handling
                    }
                }
                // If not at destination/timer up, allow original movement towards current leavePosition
                Plugin.LogDebugF($"StateLeavePrefix (Master): Timer > deltaT ({___stateTimer:F3}), allowing original movement.");
                return true; // Allow original StateLeave logic (which includes SetDestination)
            }
            else // --- Client Logic ---
            {
                // Clients should simply observe. They don't control movement or state transitions.
                return false; // Block clients from running original StateLeave logic
            }
        }
        // --- End StateLeave ---
    }
}