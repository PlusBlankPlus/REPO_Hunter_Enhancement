// File: HunterPatches.cs
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using System.Reflection;
using System.Collections.Generic;
using Photon.Pun;
using System; // Needed for Exception

namespace HunterMod
{
    [HarmonyPatch(typeof(EnemyHunter))]
    internal static class HunterPatches
    {
        private static Dictionary<int, int> stateLeaveFailCounters = new Dictionary<int, int>();
        private const int MAX_LEAVE_FAILURES_WHILE_RELOADING = 5;

        // --- GetEnemyNavMeshAgent ---
        private static EnemyNavMeshAgent GetEnemyNavMeshAgent(EnemyHunter instance)
        {
            if (instance?.enemy == null) { Plugin.LogErrorF("GetEnemyNavMeshAgent: Instance or instance.enemy is null!"); return null; }
            try { FieldInfo navAgentField = AccessTools.Field(typeof(Enemy), "NavMeshAgent"); if (navAgentField != null) { object navAgentObject = navAgentField.GetValue(instance.enemy); return navAgentObject as EnemyNavMeshAgent; } else { Plugin.LogErrorF($"GetEnemyNavMeshAgent: Could not find Field 'NavMeshAgent' on type 'Enemy' for {instance.gameObject.name}!"); return null; } }
            catch (System.Exception ex) { Plugin.LogErrorF($"GetEnemyNavMeshAgent: Error accessing 'NavMeshAgent' field on {instance.gameObject.name}: {ex.Message}"); return null; }
        }

        // --- GetOrAddTracker ---
        private static HunterAmmoTracker GetOrAddTracker(EnemyHunter instance)
        {
            if (instance == null) return null;
            var tracker = instance.GetComponent<HunterAmmoTracker>();
            if (tracker == null) { tracker = instance.gameObject.AddComponent<HunterAmmoTracker>(); Plugin.LogInfoF($"Added HunterAmmoTracker to {instance.gameObject.name}. Initializing config..."); tracker.ConfiguredFastReloadTime = Plugin.FastReloadTimeConfig.Value; tracker.ConfiguredMediumReloadTime = Plugin.MediumReloadTimeConfig.Value; tracker.ConfiguredSlowReloadTime = Plugin.SlowReloadTimeConfig.Value; tracker.ConfigEnableDamageInterrupt = Plugin.EnableDamageInterruptConfig.Value; tracker.ConfigDamageInterruptDelay = Plugin.DamageInterruptDelayConfig.Value; tracker.ConfigMinigunShots = Plugin.MinigunShots.Value; tracker.ConfigMinigunShotDelay = Plugin.MinigunShotDelay.Value; tracker.ConfigEnableTotalAmmoLimit = Plugin.EnableTotalAmmoLimitConfig.Value; tracker.ConfigTotalAmmoCount = Plugin.TotalAmmoCountConfig.Value; tracker.ConfigRunAwayWhileReloading = Plugin.RunAwayWhileReloadingConfig.Value; }
            return tracker;
        }

        // --- OnSpawnPostfix ---
        [HarmonyPatch(nameof(EnemyHunter.OnSpawn))]
        [HarmonyPostfix]
        static void OnSpawnPostfix(EnemyHunter __instance)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null || !pv.IsMine) return;
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            if (tracker != null) { ReloadSkill assignedSkill; int wF = Plugin.FastSkillWeight.Value, wM = Plugin.MediumSkillWeight.Value, wS = Plugin.SlowSkillWeight.Value; int tW = wF + wM + wS; if (tW <= 0) assignedSkill = ReloadSkill.Medium; else { int r = UnityEngine.Random.Range(0, tW); if (r < wF) assignedSkill = ReloadSkill.Fast; else if (r < wF + wM) assignedSkill = ReloadSkill.Medium; else assignedSkill = ReloadSkill.Slow; } int initialAmmo = tracker.ConfigEnableTotalAmmoLimit ? tracker.ConfigTotalAmmoCount : 9999; tracker.NetworkInitialize(assignedSkill, initialAmmo); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); }
            else { Plugin.LogErrorF($"Failed GetOrAddTracker for {__instance?.gameObject?.name} in OnSpawnPostfix (Master)."); }
        }


        // --- Helper Method to Handle Wander Sound Logic (Runs Locally) ---
        private static void HandleWanderSound(EnemyHunter instance, HunterAmmoTracker tracker)
        {
            // Check if feature enabled, clip loaded, and tracker exists
            if (tracker == null || !Plugin.EnableWanderSound.Value || Plugin.LoadedWanderAudioClip == null)
            {
                return;
            }

            // Tick down the local timer
            tracker.wanderSoundTimer -= Time.deltaTime;

            if (tracker.wanderSoundTimer <= 0f)
            {
                Sound borrowedSound = null; // Keep track for restoration in finally block
                AudioClip[] originalClips = null;
                float originalVolume = 0f;
                float originalVolumeRand = 0f;
                float originalPitch = 1f;
                float originalPitchRand = 0f;
                AudioManager.AudioType originalType = AudioManager.AudioType.Default; // Store original Type

                try
                {
                    EnemyHunterAnim anim = instance.enemyHunterAnim;
                    if (anim == null) anim = instance.GetComponentInChildren<EnemyHunterAnim>();
                    if (anim == null) { Plugin.LogErrorF($"Could not find EnemyHunterAnim component for {instance.gameObject.name} to play wander sound."); tracker.wanderSoundTimer = 5f; return; }

                    string borrowTargetName = Plugin.WanderSoundBorrowTarget.Value;
                    FieldInfo soundField = AccessTools.Field(typeof(EnemyHunterAnim), borrowTargetName);
                    if (soundField == null) { Plugin.LogWarningF($"Could not find Sound field '{borrowTargetName}' on EnemyHunterAnim. Using 'soundFootstepShort' as fallback."); soundField = AccessTools.Field(typeof(EnemyHunterAnim), "soundFootstepShort"); if (soundField == null) { Plugin.LogErrorF($"Could not find fallback 'soundFootstepShort' field either. Cannot play wander sound."); tracker.wanderSoundTimer = 5f; return; } }

                    borrowedSound = soundField.GetValue(anim) as Sound;
                    if (borrowedSound == null) { Plugin.LogErrorF($"Borrowed Sound object ('{borrowTargetName}' or fallback) is null for {instance.gameObject.name}. Cannot play wander sound."); tracker.wanderSoundTimer = 5f; return; }

                    // Store original settings BEFORE modifying
                    originalClips = borrowedSound.Sounds;
                    originalVolume = borrowedSound.Volume;
                    originalVolumeRand = borrowedSound.VolumeRandom;
                    originalPitch = borrowedSound.Pitch;
                    originalPitchRand = borrowedSound.PitchRandom;
                    originalType = borrowedSound.Type; // Store original type

                    // --- Temporarily override settings ---
                    // *** ENSURE WE DO NOT OVERRIDE THE TYPE TO GLOBAL ***
                    // The sound will play with the 3D settings of originalType (e.g., Footstep)
                    borrowedSound.Volume = Plugin.WanderSoundVolume.Value; // Set volume from config
                    borrowedSound.VolumeRandom = 0f; // Disable random volume variation
                    borrowedSound.Pitch = 1f;        // Set fixed pitch
                    borrowedSound.PitchRandom = 0f;  // Disable random pitch variation
                    borrowedSound.Sounds = new AudioClip[] { Plugin.LoadedWanderAudioClip }; // Set our custom clip

                    // Play the sound using the game's system
                    borrowedSound.Play(instance.transform.position);
                    Plugin.LogDebugF($"Played wander sound for {instance.gameObject.name} using borrowed sound field '{soundField.Name}' with original type '{originalType}'");

                }
                catch (Exception ex)
                {
                    Plugin.LogErrorF($"Error playing wander sound: {ex}");
                }
                finally // Use finally block to ENSURE restoration
                {
                    if (borrowedSound != null)
                    {
                        // Restore original settings using captured values
                        borrowedSound.Sounds = originalClips;
                        borrowedSound.Volume = originalVolume;
                        borrowedSound.VolumeRandom = originalVolumeRand;
                        borrowedSound.Pitch = originalPitch;
                        borrowedSound.PitchRandom = originalPitchRand;
                        borrowedSound.Type = originalType; // Restore original type
                        // Plugin.LogDebugF($"Restored borrowed sound '{borrowTargetName}' to original settings."); // Optional log
                    }
                }
                // --- End Play Logic ---

                // Reset the timer to a new random interval
                float min = Plugin.WanderSoundMinInterval.Value;
                float max = Plugin.WanderSoundMaxInterval.Value;
                tracker.wanderSoundTimer = UnityEngine.Random.Range(min, max);
            }
        }
        // --- End Wander Sound Helper ---


        [HarmonyPatch("StateIdle")]
        [HarmonyPrefix]
        static bool StateIdlePrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            HandleWanderSound(__instance, tracker); // Play sound locally if conditions met
            if (tracker.isOutOfAmmoPermanently) { Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave."); if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading) { Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} reloading (Synced). Moving away."); if (pv.IsMine) { EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance); if (enemyNavAgent != null) { enemyNavAgent.ResetPath(); if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) { enemyNavAgent.SetDestination(p); } } ___stateImpulse = false; } return false; }
            return true;
        }


        [HarmonyPatch("StateRoam")]
        [HarmonyPrefix]
        static bool StateRoamPrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            HandleWanderSound(__instance, tracker); // Play sound locally if conditions met
            if (tracker.isOutOfAmmoPermanently) { if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading) { Plugin.LogDebugF($"StateRoamPrefix: Hunter {__instance.gameObject.name} reloading (Synced). Moving away."); if (pv.IsMine) { EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance); if (enemyNavAgent != null) { enemyNavAgent.ResetPath(); if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) enemyNavAgent.SetDestination(p); } ___stateImpulse = false; } return false; }
            return true;
        }

        // --- StateAimPrefix ---
        [HarmonyPatch("StateAim")]
        [HarmonyPrefix]
        static bool StateAimPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            if (tracker.isOutOfAmmoPermanently) { Plugin.LogDebugF($"StateAimPrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave."); if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }
            bool blockAimProgression = tracker.IsReloading || tracker.IsInterrupted || !tracker.HasTotalAmmo;
            if (blockAimProgression) { string reason = !tracker.HasTotalAmmo ? "OOA" : (tracker.IsReloading ? $"reloading" : $"interrupted"); Plugin.LogDebugF($"StateAimPrefix BLOCKED: {__instance.gameObject.name} is {reason} (Synced)."); if (pv.IsMine) { if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading) { Plugin.LogDebugF($"StateAimPrefix (Master): {__instance.gameObject.name} running away."); EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance); if (enemyNavAgent != null) { enemyNavAgent.ResetPath(); if (Plugin.FindRetreatPoint(__instance, out Vector3 p)) enemyNavAgent.SetDestination(p); } else Plugin.LogErrorF($"StateAimPrefix (Master): EnemyNavMeshAgent missing!"); } else { Plugin.CallAimLogic(__instance); } if (___stateTimer <= Time.deltaTime) ___stateTimer = 0.1f; } Plugin.CallAimLogic(__instance); if (___stateTimer <= Time.deltaTime) ___stateTimer = 0.1f; return false; }
            return true;
        }

        // --- StateShootPrefix ---
        [HarmonyPatch("StateShoot")]
        [HarmonyPrefix]
        static bool StateShootPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            if (!pv.IsMine) { Plugin.CallAimLogic(__instance); return false; } // Clients always block original

            // --- MasterClient Authoritative Logic ---
            if (!tracker.HasTotalAmmo) { Plugin.LogErrorF($"StateShootPrefix (Master): {__instance.gameObject.name} OOA! Forcing Leave."); if (!tracker.isOutOfAmmoPermanently && !Plugin.IsRepoLastStandActive()) { tracker.isOutOfAmmoPermanently = true; } Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; }

            // --- Standard Shoot Logic (Minigun OFF) ---
            if (!Plugin.IsMinigunModeEnabled()) { if (!tracker.TryDecrementTotalAmmo()) { Plugin.LogInfoF($"StateShootPrefix(N) (Master): {__instance.gameObject.name} OOA this shot. Forcing Leave."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; } Plugin.LogDebugF($"StateShootPrefix (Master): Standard Shot. Calling ShootRPC, Starting Reload & Transitioning to Idle."); Plugin.CallShootRPC(__instance); tracker.StartReload(); Plugin.CallUpdateState(__instance, EnemyHunter.State.Idle); return false; } // BLOCK ORIGINAL StateShoot
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               // --- Minigun Shoot Logic (Minigun ON) ---
            else { if (tracker.IsReloading || tracker.IsInterrupted) { Plugin.LogDebugF($"StateShootPrefix(M) (Master): Blocked by Reload/Interrupt."); Plugin.CallAimLogic(__instance); return false; } if (!tracker.isMinigunBurstActive) { tracker.InitializeMinigunBurst(); ___stateTimer = tracker.ConfigMinigunShots * tracker.ConfigMinigunShotDelay + 1.0f; } tracker.minigunShotTimer -= Time.deltaTime; if (tracker.minigunShotTimer <= 0f && tracker.minigunShotsRemaining > 0) { if (!tracker.TryDecrementTotalAmmo()) { Plugin.LogInfoF($"StateShootPrefix(M) (Master): {__instance.gameObject.name} OOA mid-burst. End & Leave."); tracker.EndMinigunBurst(); Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; } Plugin.LogDebugF($"StateShootPrefix (Master): Fire minigun {tracker.ConfigMinigunShots - tracker.minigunShotsRemaining + 1}/{tracker.ConfigMinigunShots}. Ammo: {tracker.currentTotalAmmo}"); Plugin.CallShootRPC(__instance); tracker.minigunShotsRemaining--; tracker.minigunShotTimer = tracker.ConfigMinigunShotDelay; } if (tracker.minigunShotsRemaining <= 0 && tracker.isMinigunBurstActive) { Plugin.LogInfoF($"StateShootPrefix (Master): Minigun burst done. Transitioning to ShootEnd & Starting Reload."); tracker.EndMinigunBurst(); Plugin.CallUpdateState(__instance, EnemyHunter.State.ShootEnd); tracker.StartReload(); return false; } Plugin.CallAimLogic(__instance); return false; }
        }

        // --- OnHurtPostfix ---
        [HarmonyPatch(nameof(EnemyHunter.OnHurt))]
        [HarmonyPostfix]
        static void OnHurtPostfix(EnemyHunter __instance)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null || !pv.IsMine) return;
            HunterAmmoTracker t = GetOrAddTracker(__instance);
            if (t != null) { t.ApplyDamageInterrupt(); }
            else Plugin.LogErrorF($"Failed GetOrAddTracker in OnHurtPostfix (Master).");
        }

        // --- StateShootEndPrefix ---
        [HarmonyPatch("StateShootEnd")]
        [HarmonyPrefix]
        static bool StateShootEndPrefix(EnemyHunter __instance, ref float ___stateTimer, ref int ___shotsFired, int ___shotsFiredMax)
        {
            PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            if (Plugin.IsMinigunModeEnabled()) { HunterAmmoTracker trackerCheck = GetOrAddTracker(__instance); if (trackerCheck != null && trackerCheck.IsReloading) { Plugin.LogDebugF("StateShootEndPrefix(M): Minigun recently ended, allowing original StateShootEnd."); return true; } Plugin.LogWarningF("StateShootEndPrefix(M): Minigun enabled but not reloading? Allowing original."); return true; }
            else { Plugin.LogWarningF($"StateShootEndPrefix(N): Entered for standard shot unexpectedly. Blocking original."); return false; }
        }


        // --- PreventReEngagePrefix & Usages ---
        static bool PreventReEngagePrefix(EnemyHunter __instance)
        {
            HunterAmmoTracker t = __instance?.GetComponent<HunterAmmoTracker>(); if (t != null && t.isOutOfAmmoPermanently) { Plugin.LogDebugF($"PreventReEngagePrefix: {__instance.gameObject.name} OOA (Synced). Prevent engage."); return false; }
            return true;
        }
        [HarmonyPatch(nameof(EnemyHunter.OnInvestigate))][HarmonyPrefix] static bool OnInvestigatePrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayer))][HarmonyPrefix] static bool OnTouchPlayerPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayerGrabbedObject))][HarmonyPrefix] static bool OnTouchPlayerGrabbedObjectPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnGrabbed))][HarmonyPrefix] static bool OnGrabbedPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);

        // --- StateLeavePrefix ---
        [HarmonyPatch("StateLeave")]
        [HarmonyPrefix]
        static bool StateLeavePrefix(EnemyHunter __instance, ref float ___stateTimer, Vector3 ___leavePosition, ref bool ___stateImpulse)
        {
            HunterAmmoTracker t = GetOrAddTracker(__instance); if (t == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true; int instanceId = __instance.GetInstanceID(); bool needsToRunAwayReloading = Plugin.ShouldRunAwayWhileReloading() && t.IsReloading; bool needsToLeavePermanently = t.isOutOfAmmoPermanently;
            if (!needsToRunAwayReloading && !needsToLeavePermanently) { if (pv.IsMine) stateLeaveFailCounters.Remove(instanceId); return pv.IsMine; }
            if (pv.IsMine) { EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance); if (enemyNavAgent == null) return true; bool exitLeaveCondition = (___stateTimer <= Time.deltaTime || Vector3.Distance(__instance.transform.position, ___leavePosition) < 1f); if (exitLeaveCondition) { if (needsToLeavePermanently) { Plugin.LogInfoF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} OOA and reached Leave destination/timer. Forcing Despawn."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); stateLeaveFailCounters.Remove(instanceId); return false; } else { Plugin.LogDebugF($"StateLeavePrefix (Master): {__instance.gameObject.name} reloading & reached dest/timer. Finding new retreat point."); if (Plugin.FindRetreatPoint(__instance, out Vector3 newLeavePos)) { stateLeaveFailCounters.Remove(instanceId); var leavePosField = AccessTools.Field(typeof(EnemyHunter), "leavePosition"); if (leavePosField != null) leavePosField.SetValue(__instance, newLeavePos); else Plugin.LogErrorF("StateLeavePrefix (Master): Could not find leavePosition field!"); enemyNavAgent.SetDestination(newLeavePos); ___stateTimer = 5f; ___stateImpulse = false; } else { int currentFails = stateLeaveFailCounters.ContainsKey(instanceId) ? stateLeaveFailCounters[instanceId] : 0; currentFails++; stateLeaveFailCounters[instanceId] = currentFails; Plugin.LogWarningF($"StateLeavePrefix (Master): No new retreat point for {__instance.gameObject.name} while reloading. Failure {currentFails}/{MAX_LEAVE_FAILURES_WHILE_RELOADING}"); ___stateTimer = 1f; if (currentFails >= MAX_LEAVE_FAILURES_WHILE_RELOADING) { Plugin.LogErrorF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} failed find retreat point {MAX_LEAVE_FAILURES_WHILE_RELOADING} times WHILE RELOADING. Forcing Despawn."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); stateLeaveFailCounters.Remove(instanceId); } } return false; } } Plugin.LogDebugF($"StateLeavePrefix (Master): Timer > deltaT ({___stateTimer:F3}), allowing original movement."); return true; }
            else { return false; }
        }
    }
}