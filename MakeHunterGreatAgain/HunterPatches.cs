// File: HunterPatches.cs
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI; // Required for NavMeshAgent
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

        // Reflection fields cache
        private static FieldInfo agentVelocityField = AccessTools.Field(typeof(EnemyNavMeshAgent), "AgentVelocity");
        private static FieldInfo animatorField = AccessTools.Field(typeof(EnemyHunterAnim), "animator");
        // Reflection for aim targets (used when overriding rotation)
        private static FieldInfo horizontalAimTargetField = AccessTools.Field(typeof(EnemyHunter), "horizontalAimTarget");
        private static FieldInfo investigateAimVerticalField = AccessTools.Field(typeof(EnemyHunter), "investigateAimVertical");


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


        // --- Helper Method to Handle Wander Sound Logic ---
        private static void HandleWanderSound(EnemyHunter instance, HunterAmmoTracker tracker)
        {
            if (tracker == null || !Plugin.EnableWanderSound.Value || Plugin.LoadedWanderAudioClip == null) return;
            tracker.wanderSoundTimer -= Time.deltaTime;
            if (tracker.wanderSoundTimer <= 0f)
            {
                try { AudioSource.PlayClipAtPoint(Plugin.LoadedWanderAudioClip, instance.transform.position, Plugin.WanderSoundVolume.Value); Plugin.LogDebugF($"Played wander sound for {instance.gameObject.name} using PlayClipAtPoint"); }
                catch (Exception ex) { Plugin.LogErrorF($"Error playing wander sound: {ex}"); }
                float min = Plugin.WanderSoundMinInterval.Value; float max = Plugin.WanderSoundMaxInterval.Value; tracker.wanderSoundTimer = UnityEngine.Random.Range(min, max);
            }
        }

        // --- Helper method to get the Animator via Reflection ---
        private static Animator GetHunterAnimator(EnemyHunter instance)
        {
            if (instance?.enemyHunterAnim == null) return null;
            if (animatorField == null) { Plugin.LogErrorF("GetHunterAnimator: animatorField info is null! Cannot access Animator."); return null; }
            try { return animatorField.GetValue(instance.enemyHunterAnim) as Animator; }
            catch (Exception ex) { Plugin.LogErrorF($"GetHunterAnimator: Error accessing animator field: {ex}"); return null; }
        }


        // --- StateIdlePrefix (Transition to Leave when running away) ---
        [HarmonyPatch("StateIdle")]
        [HarmonyPrefix]
        static bool StateIdlePrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true;
            PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;

            HandleWanderSound(__instance, tracker); // Play sound locally

            if (tracker.isOutOfAmmoPermanently) { Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave."); if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }

            // Transition to Leave state if running away during reload
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading)
            {
                Plugin.LogDebugF($"StateIdlePrefix: Hunter {__instance.gameObject.name} reloading (Synced). Transitioning to Leave.");
                if (pv.IsMine)
                {
                    Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); // Force state change
                    ___stateImpulse = false; // Prevent original idle logic timer reset ? (Maybe needed)
                }
                // Skip original Idle logic (prevents fighting with Leave state)
                // Also ensure aiming animation is off visually
                Animator animator = GetHunterAnimator(__instance);
                if (animator != null) { animator.SetBool("Aiming", false); }
                return false;
            }

            // Ensure Aiming bool is false if not running away
            Animator animatorIdle = GetHunterAnimator(__instance);
            if (animatorIdle != null) { animatorIdle.SetBool("Aiming", false); }

            return true; // Allow original Idle if no conditions met
        }


        // --- StateRoamPrefix (Transition to Leave when running away) ---
        [HarmonyPatch("StateRoam")]
        [HarmonyPrefix]
        static bool StateRoamPrefix(EnemyHunter __instance, ref bool ___stateImpulse)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true;
            PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;

            HandleWanderSound(__instance, tracker); // Play sound locally

            if (tracker.isOutOfAmmoPermanently) { if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }

            // Transition to Leave state if running away during reload
            if (Plugin.ShouldRunAwayWhileReloading() && tracker.IsReloading)
            {
                Plugin.LogDebugF($"StateRoamPrefix: Hunter {__instance.gameObject.name} reloading (Synced). Transitioning to Leave.");
                if (pv.IsMine)
                {
                    Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); // Force state change
                    ___stateImpulse = false; // Prevent original roam logic timer reset ? (Maybe needed)
                }
                // Skip original Roam logic
                // Also ensure aiming animation is off visually
                Animator animator = GetHunterAnimator(__instance);
                if (animator != null) { animator.SetBool("Aiming", false); }
                return false;
            }

            // Ensure Aiming bool is false if not running away
            Animator animatorRoam = GetHunterAnimator(__instance);
            if (animatorRoam != null) { animatorRoam.SetBool("Aiming", false); }

            return true; // Allow original Roam if no conditions met
        }

        // --- StateAimPrefix ---
        [HarmonyPatch("StateAim")]
        [HarmonyPrefix]
        static bool StateAimPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            if (tracker.isOutOfAmmoPermanently) { Plugin.LogDebugF($"StateAimPrefix: Hunter {__instance.gameObject.name} OOA (Synced). Forcing Leave."); if (pv.IsMine) { Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); } return false; }

            bool blockAimProgression = tracker.IsReloading || tracker.IsInterrupted || !tracker.HasTotalAmmo;
            if (blockAimProgression)
            {
                string reason = !tracker.HasTotalAmmo ? "OOA" : (tracker.IsReloading ? $"reloading" : $"interrupted");
                Plugin.LogDebugF($"StateAimPrefix BLOCKED: {__instance.gameObject.name} is {reason} (Synced).");
                // If blocked, ensure aiming animation is off
                Animator animator = GetHunterAnimator(__instance);
                if (animator != null) { animator.SetBool("Aiming", false); }
                if (___stateTimer <= Time.deltaTime) ___stateTimer = 0.1f; // Keep timer from expiring immediately
                return false; // Block original StateAim
            }
            return true; // Allow original Aim state logic
        }

        // --- StateShootPrefix ---
        [HarmonyPatch("StateShoot")]
        [HarmonyPrefix]
        static bool StateShootPrefix(EnemyHunter __instance, ref float ___stateTimer)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance); if (tracker == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true;
            if (!pv.IsMine) { Plugin.CallAimLogic(__instance); return false; }

            if (!tracker.HasTotalAmmo) { Plugin.LogErrorF($"StateShootPrefix (Master): {__instance.gameObject.name} OOA! Forcing Leave."); if (!tracker.isOutOfAmmoPermanently && !Plugin.IsRepoLastStandActive()) { tracker.isOutOfAmmoPermanently = true; } Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; }

            // Friendly Fire Check
            RaycastHit ffHit; bool hitFriend = false; Transform fireOrigin = __instance.gunTipTransform ?? __instance.gunAimTransform;
            if (Physics.Raycast(fireOrigin.position, fireOrigin.forward, out ffHit, 5f, LayerMask.GetMask("Enemy"))) { EnemyHunter otherHunter = ffHit.collider.GetComponentInParent<EnemyHunter>(); if (otherHunter != null && otherHunter != __instance) { hitFriend = true; Plugin.LogDebugF($"StateShootPrefix (Master): Prevented shooting - direct line of sight to another Hunter ({otherHunter.name}) at short range."); } }
            if (hitFriend) { Plugin.CallAimLogic(__instance); ___stateTimer = 0.2f; return false; }

            // Standard Shoot
            if (!Plugin.IsMinigunModeEnabled()) { if (!tracker.TryDecrementTotalAmmo()) { Plugin.LogInfoF($"StateShootPrefix(N) (Master): {__instance.gameObject.name} OOA this shot. Forcing Leave."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; } Plugin.LogDebugF($"StateShootPrefix (Master): Standard Shot. Calling ShootRPC, Starting Reload & Transitioning to Idle."); Plugin.CallShootRPC(__instance); tracker.StartReload(); Plugin.CallUpdateState(__instance, EnemyHunter.State.Idle); return false; } // BLOCK ORIGINAL
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               // Minigun Shoot
            else
            {
                if (tracker.IsReloading || tracker.IsInterrupted) { Plugin.LogDebugF($"StateShootPrefix(M) (Master): Blocked by Reload/Interrupt."); Plugin.CallAimLogic(__instance); Animator animator = GetHunterAnimator(__instance); if (animator != null) { animator.SetBool("Shooting", false); } return false; }
                if (!tracker.isMinigunBurstActive) { tracker.InitializeMinigunBurst(); ___stateTimer = tracker.ConfigMinigunShots * tracker.ConfigMinigunShotDelay + 1.5f; Plugin.LogDebugF($"StateShootPrefix (Master): Minigun burst started. State timer set to: {___stateTimer:F2}s"); }

                tracker.minigunShotTimer -= Time.deltaTime;
                if (tracker.minigunShotTimer <= 0f && tracker.minigunShotsRemaining > 0) { if (!tracker.TryDecrementTotalAmmo()) { Plugin.LogInfoF($"StateShootPrefix(M) (Master): {__instance.gameObject.name} OOA mid-burst. End & Leave."); tracker.EndMinigunBurst(); Plugin.CallUpdateState(__instance, EnemyHunter.State.Leave); stateLeaveFailCounters.Remove(__instance.GetInstanceID()); return false; } Plugin.LogDebugF($"StateShootPrefix (Master): Fire minigun {tracker.ConfigMinigunShots - tracker.minigunShotsRemaining + 1}/{tracker.ConfigMinigunShots}. Ammo: {tracker.currentTotalAmmo}"); Plugin.CallShootRPC(__instance); tracker.minigunShotsRemaining--; tracker.minigunShotTimer = tracker.ConfigMinigunShotDelay; }
                if (tracker.minigunShotsRemaining <= 0 && tracker.isMinigunBurstActive) { Plugin.LogInfoF($"StateShootPrefix (Master): Minigun burst done. Transitioning to ShootEnd & Starting Reload."); tracker.EndMinigunBurst(); Plugin.CallUpdateState(__instance, EnemyHunter.State.ShootEnd); tracker.StartReload(); return false; }

                Plugin.CallAimLogic(__instance);
                if (tracker.isMinigunBurstActive && ___stateTimer <= Time.deltaTime) { ___stateTimer = tracker.ConfigMinigunShotDelay + 0.1f; }
                Animator animatorLoop = GetHunterAnimator(__instance); if (animatorLoop != null) { animatorLoop.SetBool("Shooting", true); }
                return false;
            }
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
        static bool PreventReEngagePrefix(EnemyHunter __instance) { HunterAmmoTracker t = __instance?.GetComponent<HunterAmmoTracker>(); if (t != null && t.isOutOfAmmoPermanently) { Plugin.LogDebugF($"PreventReEngagePrefix: {__instance.gameObject.name} OOA (Synced). Prevent engage."); return false; } return true; }
        [HarmonyPatch(nameof(EnemyHunter.OnInvestigate))][HarmonyPrefix] static bool OnInvestigatePrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayer))][HarmonyPrefix] static bool OnTouchPlayerPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnTouchPlayerGrabbedObject))][HarmonyPrefix] static bool OnTouchPlayerGrabbedObjectPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);
        [HarmonyPatch(nameof(EnemyHunter.OnGrabbed))][HarmonyPrefix] static bool OnGrabbedPrefix(EnemyHunter __instance) => PreventReEngagePrefix(__instance);

        // --- StateLeavePrefix ---
        [HarmonyPatch("StateLeave")]
        [HarmonyPrefix]
        static bool StateLeavePrefix(EnemyHunter __instance, ref float ___stateTimer, Vector3 ___leavePosition, ref bool ___stateImpulse)
        {
            HunterAmmoTracker t = GetOrAddTracker(__instance); if (t == null) return true; PhotonView pv = Plugin.GetPhotonView(__instance); if (pv == null) return true; int instanceId = __instance.GetInstanceID();
            bool needsToRunAwayReloading = Plugin.ShouldRunAwayWhileReloading() && t.IsReloading; bool needsToLeavePermanently = t.isOutOfAmmoPermanently;

            // Ensure Aiming animation is off during Leave state
            Animator animator = GetHunterAnimator(__instance); if (animator != null) { animator.SetBool("Aiming", false); }

            if (!needsToRunAwayReloading && !needsToLeavePermanently) { if (pv.IsMine) stateLeaveFailCounters.Remove(instanceId); return pv.IsMine; } // Allow normal leave if master

            // MasterClient controls retreat/despawn logic
            if (pv.IsMine)
            {
                EnemyNavMeshAgent enemyNavAgent = GetEnemyNavMeshAgent(__instance); if (enemyNavAgent == null) return true;
                bool exitLeaveCondition = (___stateTimer <= Time.deltaTime || Vector3.Distance(__instance.transform.position, ___leavePosition) < 1f);
                if (exitLeaveCondition)
                {
                    if (needsToLeavePermanently) { Plugin.LogInfoF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} OOA and reached Leave destination/timer. Forcing Despawn."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); stateLeaveFailCounters.Remove(instanceId); return false; }
                    else
                    { // Running away while reloading
                        Plugin.LogDebugF($"StateLeavePrefix (Master): {__instance.gameObject.name} reloading & reached dest/timer. Finding new retreat point.");
                        if (Plugin.FindRetreatPoint(__instance, out Vector3 newLeavePos))
                        {
                            stateLeaveFailCounters.Remove(instanceId); var leavePosField = AccessTools.Field(typeof(EnemyHunter), "leavePosition");
                            if (leavePosField != null) leavePosField.SetValue(__instance, newLeavePos); else Plugin.LogErrorF("StateLeavePrefix (Master): Could not find leavePosition field!");
                            enemyNavAgent.SetDestination(newLeavePos); ___stateTimer = 5f; ___stateImpulse = false;
                        }
                        else
                        {
                            // Handle failure to find point
                            int currentFails = stateLeaveFailCounters.ContainsKey(instanceId) ? stateLeaveFailCounters[instanceId] : 0; currentFails++; stateLeaveFailCounters[instanceId] = currentFails; Plugin.LogWarningF($"StateLeavePrefix (Master): No new retreat point for {__instance.gameObject.name} while reloading. Failure {currentFails}/{MAX_LEAVE_FAILURES_WHILE_RELOADING}"); ___stateTimer = 1f; if (currentFails >= MAX_LEAVE_FAILURES_WHILE_RELOADING) { Plugin.LogErrorF($"StateLeavePrefix (Master): Hunter {__instance.gameObject.name} failed find retreat point {MAX_LEAVE_FAILURES_WHILE_RELOADING} times WHILE RELOADING. Forcing Despawn."); Plugin.CallUpdateState(__instance, EnemyHunter.State.Despawn); stateLeaveFailCounters.Remove(instanceId); }
                        }
                        return false; // Block original after handling retreat point
                    }
                }
                Plugin.LogDebugF($"StateLeavePrefix (Master): Timer > deltaT ({___stateTimer:F3}), allowing original movement.");
                return true; // Allow original movement if timer hasn't expired / destination not reached
            }
            else { return false; } // Clients just observe
        }


        // *** Prefixes to block original rotation logic when running away ***
        [HarmonyPatch("HorizontalRotationLogic")]
        [HarmonyPrefix]
        static bool HorizontalRotationLogic_Prefix(EnemyHunter __instance)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            // Check if reloading, configured to run away, AND currently in the Leave state
            if (tracker != null && tracker.IsReloading && Plugin.ShouldRunAwayWhileReloading() && __instance.currentState == EnemyHunter.State.Leave)
            {
                Plugin.LogDebugF($"HorizontalRotationLogic blocked for {__instance.gameObject.name} (reloading & running away in Leave state).");
                return false; // Skip original method
            }
            return true; // Allow original method otherwise
        }

        [HarmonyPatch("VerticalRotationLogic")]
        [HarmonyPrefix]
        static bool VerticalRotationLogic_Prefix(EnemyHunter __instance)
        {
            HunterAmmoTracker tracker = GetOrAddTracker(__instance);
            // Check if reloading, configured to run away, AND currently in the Leave state
            if (tracker != null && tracker.IsReloading && Plugin.ShouldRunAwayWhileReloading() && __instance.currentState == EnemyHunter.State.Leave)
            {
                Plugin.LogDebugF($"VerticalRotationLogic blocked for {__instance.gameObject.name} (reloading & running away in Leave state).");
                // Force vertical aim to neutral when running away
                // Access internal field directly via Reflection
                FieldInfo investigateAimVerticalFieldInternal = AccessTools.Field(typeof(EnemyHunter), "investigateAimVertical");
                if (investigateAimVerticalFieldInternal != null)
                {
                    try
                    {
                        investigateAimVerticalFieldInternal.SetValue(__instance, Quaternion.identity);
                    }
                    catch (Exception ex) { Plugin.LogErrorF($"VerticalRotationLogic_Prefix: Error forcing vertical aim: {ex}"); }
                }
                else { Plugin.LogErrorF("VerticalRotationLogic_Prefix: Could not find investigateAimVertical field!"); }

                return false; // Skip original method
            }
            return true; // Allow original method otherwise
        }
        // *** END Rotation Prefixes ***

    }
}