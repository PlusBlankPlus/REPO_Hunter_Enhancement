// File: Plugin.cs
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System;
using Photon.Pun; // Required for PhotonView, RpcTarget, PhotonNetwork etc.

namespace HunterMod
{
    /// <summary>
    /// Main plugin class for Hunter Enhancements. Handles configuration, Harmony patching,
    /// mod compatibility detection, logging, and provides helper methods.
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("umbreon222.repo.laststand", BepInDependency.DependencyFlags.SoftDependency)] // Soft dependency for compatibility
    public class Plugin : BaseUnityPlugin
    {
        // --- Plugin Instance and Logging ---
        public static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        // --- End Instance/Log ---

        // --- Configuration Entries ---
        // Group A: General / Minigun
        public static ConfigEntry<bool> EnableMinigunMode { get; private set; }
        public static ConfigEntry<int> MinigunShots { get; private set; }
        public static ConfigEntry<float> MinigunShotDelay { get; private set; }
        // Group B: Reload Times
        public static ConfigEntry<float> FastReloadTimeConfig { get; private set; }
        public static ConfigEntry<float> MediumReloadTimeConfig { get; private set; }
        public static ConfigEntry<float> SlowReloadTimeConfig { get; private set; }
        // Group C: Skill Weights (Probability)
        public static ConfigEntry<int> FastSkillWeight { get; private set; }
        public static ConfigEntry<int> MediumSkillWeight { get; private set; }
        public static ConfigEntry<int> SlowSkillWeight { get; private set; }
        // Group D: Damage/Reload Behavior
        public static ConfigEntry<bool> EnableDamageInterruptConfig { get; private set; }
        public static ConfigEntry<float> DamageInterruptDelayConfig { get; private set; }
        public static ConfigEntry<bool> RunAwayWhileReloadingConfig { get; private set; }
        // Group E: Total Ammo Limit
        public static ConfigEntry<bool> EnableTotalAmmoLimitConfig { get; private set; }
        public static ConfigEntry<int> TotalAmmoCountConfig { get; private set; }
        // Group Z: Logging Levels
        public static ConfigEntry<bool> EnableInfoLogs { get; private set; }
        public static ConfigEntry<bool> EnableDebugLogs { get; private set; }
        public static ConfigEntry<bool> EnableWarningLogs { get; private set; }
        public static ConfigEntry<bool> EnableErrorLogs { get; private set; }
        // --- End Config Entries ---

        // --- Mod Compatibility (RepoLastStandMod) ---
        private static bool isRepoLastStandModPresent = false;
        private const string RepoLastStandGuid = "umbreon222.repo.laststand";
        private static Type repoStateManagerType = null;
        private static PropertyInfo repoStateManagerInstanceProperty = null;
        private static FieldInfo repoLastStandActiveField = null;
        // --- End Compatibility ---

        /// <summary>
        /// Called by BepInEx when the plugin is loaded. Sets up the instance, loads config,
        /// detects compatibility mods, and applies Harmony patches.
        /// </summary>
        private void Awake()
        {
            // Singleton pattern setup
            if (Instance == null) { Instance = this; Log = Logger; } else { Destroy(this); return; }

            Log.LogInfo("Loading configuration...");
            // Bind all configuration entries
            EnableMinigunMode = Config.Bind("A. General", "EnableMinigunMode", false, "Enable the Hunter's rapid-fire 'Minigun' mode.");
            MinigunShots = Config.Bind("A. General", "MinigunShots", 10, new ConfigDescription("Number of shots fired in one burst when Minigun Mode is enabled.", new AcceptableValueRange<int>(1, 50)));
            MinigunShotDelay = Config.Bind("A. General", "MinigunShotDelay", 0.1f, new ConfigDescription("Delay in seconds between each shot during a Minigun burst.", new AcceptableValueRange<float>(0.02f, 0.5f)));
            FastReloadTimeConfig = Config.Bind("B. ReloadTimes", "FastReloadTime", 2f, new ConfigDescription("Reload time (s) for Hunters with 'Fast' skill.", new AcceptableValueRange<float>(0.5f, 120f)));
            MediumReloadTimeConfig = Config.Bind("B. ReloadTimes", "MediumReloadTime", 5f, new ConfigDescription("Reload time (s) for Hunters with 'Medium' skill.", new AcceptableValueRange<float>(1f, 180f)));
            SlowReloadTimeConfig = Config.Bind("B. ReloadTimes", "SlowReloadTime", 7f, new ConfigDescription("Reload time (s) for Hunters with 'Slow' skill.", new AcceptableValueRange<float>(1.5f, 300f)));
            FastSkillWeight = Config.Bind("C. SkillWeights", "FastSkillWeight", 5, new ConfigDescription("Probability weight for spawning with Fast reload skill.", new AcceptableValueRange<int>(1, 100)));
            MediumSkillWeight = Config.Bind("C. SkillWeights", "MediumSkillWeight", 3, new ConfigDescription("Probability weight for spawning with Medium reload skill.", new AcceptableValueRange<int>(1, 100)));
            SlowSkillWeight = Config.Bind("C. SkillWeights", "SlowSkillWeight", 1, new ConfigDescription("Probability weight for spawning with Slow reload skill.", new AcceptableValueRange<int>(1, 100)));
            EnableDamageInterruptConfig = Config.Bind("D. DamageBehavior", "EnableDamageInterrupt", true, "If enabled, getting hurt while reloading cancels it and imposes a delay before reloading can start.");
            DamageInterruptDelayConfig = Config.Bind("D. DamageBehavior", "DamageInterruptDelay", 5f, new ConfigDescription("Delay (s) imposed after a reload is interrupted by damage.", new AcceptableValueRange<float>(0.5f, 15f)));
            RunAwayWhileReloadingConfig = Config.Bind("D. DamageBehavior", "RunAwayWhileReloading", true, "If enabled, the Hunter will actively move away from players while reloading (enters Leave state).");
            EnableTotalAmmoLimitConfig = Config.Bind("E. TotalAmmo", "EnableTotalAmmoLimit", false, "If enabled, the Hunter has a limited total number of shots before it runs away permanently.");
            TotalAmmoCountConfig = Config.Bind("E. TotalAmmo", "TotalAmmoCount", 30, new ConfigDescription("Total number of shots the Hunter has if the limit is enabled.", new AcceptableValueRange<int>(1, 200)));
            EnableInfoLogs = Config.Bind("Z. Logging", "EnableInfoLogs", true, "Enable standard informational logs.");
            EnableDebugLogs = Config.Bind("Z. Logging", "EnableDebugLogs", false, "Enable detailed debug logs (can be spammy).");
            EnableWarningLogs = Config.Bind("Z. Logging", "EnableWarningLogs", true, "Enable warning logs for potential issues.");
            EnableErrorLogs = Config.Bind("Z. Logging", "EnableErrorLogs", true, "Enable error logs for critical failures.");
            LogInfoF("Configuration loaded.");

            DetectRepoLastStandMod(); // Check for compatibility mod

            LogInfoF($"Applying Harmony patches for {PluginInfo.PLUGIN_NAME}...");
            harmony.PatchAll(typeof(HunterPatches)); // Apply all patches in HunterPatches class
            LogInfoF($"Harmony patches applied successfully!");

            LogInfoF($"Plugin {PluginInfo.PLUGIN_GUID} v{PluginInfo.PLUGIN_VERSION} is loaded!");
        }

        /// <summary>
        /// Uses BepInEx Chainloader and Reflection to detect if the RepoLastStandMod is loaded
        /// and finds the necessary fields/properties for compatibility checks.
        /// </summary>
        private static void DetectRepoLastStandMod()
        {
            isRepoLastStandModPresent = Chainloader.PluginInfos.ContainsKey(RepoLastStandGuid);
            if (isRepoLastStandModPresent)
            {
                LogInfoF("RepoLastStandMod detected. Enabling compatibility logic.");
                try
                {
                    // Attempt to find the StateManager type using reflection
                    string assemblyName = "RepoLastStandMod"; // Default assembly name
                    repoStateManagerType = Type.GetType($"RepoLastStandMod.StateManager, {assemblyName}", throwOnError: false);

                    // If not found with default name, try getting assembly name from loaded plugin instance
                    if (repoStateManagerType == null && Chainloader.PluginInfos.TryGetValue(RepoLastStandGuid, out var pluginInfo))
                    {
                        if (pluginInfo.Instance != null)
                        {
                            assemblyName = pluginInfo.Instance.GetType().Assembly.FullName;
                            repoStateManagerType = Type.GetType($"RepoLastStandMod.StateManager, {assemblyName}", throwOnError: false);
                            LogDebugF($"Attempting to load StateManager from assembly: {assemblyName}");
                        }
                    }

                    if (repoStateManagerType != null)
                    {
                        // Find the static Instance property and the LastStandActive field
                        repoStateManagerInstanceProperty = AccessTools.Property(repoStateManagerType, "Instance");
                        repoLastStandActiveField = AccessTools.Field(repoStateManagerType, "LastStandActive");

                        if (repoStateManagerInstanceProperty == null) LogErrorF("Could not find 'Instance' property on StateManager.");
                        if (repoLastStandActiveField == null) LogErrorF("Could not find 'LastStandActive' field on StateManager.");

                        // If reflection failed for either, disable compatibility
                        if (repoStateManagerInstanceProperty == null || repoLastStandActiveField == null)
                        {
                            LogErrorF("Failed reflection for RepoLastStandMod. Compatibility disabled.");
                            isRepoLastStandModPresent = false;
                        }
                    }
                    else
                    {
                        LogErrorF("Could not find type 'RepoLastStandMod.StateManager'. Compatibility disabled.");
                        isRepoLastStandModPresent = false;
                    }
                }
                catch (System.Exception ex)
                {
                    LogErrorF($"Error during reflection for RepoLastStandMod compatibility: {ex}");
                    isRepoLastStandModPresent = false;
                }
            }
            else
            {
                LogInfoF("RepoLastStandMod not detected.");
            }
        }

        // --- Logging Helper Methods (Respect Config Settings) ---
        public static void LogInfoF(string message) { if (Log != null && EnableInfoLogs.Value) Log.LogInfo(message); }
        public static void LogDebugF(string message) { if (Log != null && EnableDebugLogs.Value) Log.LogDebug(message); }
        public static void LogWarningF(string message) { if (Log != null && EnableWarningLogs.Value) Log.LogWarning(message); }
        public static void LogErrorF(string message) { if (Log != null && EnableErrorLogs.Value) Log.LogError(message); }
        // --- End Logging ---

        // --- Simple Config Check Helpers ---
        public static bool IsMinigunModeEnabled() => EnableMinigunMode.Value;
        public static bool ShouldRunAwayWhileReloading() => RunAwayWhileReloadingConfig.Value;
        // --- End Config Checks ---

        // --- Reflection Helpers to Call Base Game Methods ---
        /// <summary> Calls the private EnemyHunter.UpdateState method via reflection. </summary>
        public static void CallUpdateState(EnemyHunter instance, EnemyHunter.State newState)
        {
            MethodInfo m = AccessTools.Method(typeof(EnemyHunter), "UpdateState");
            if (m != null) m.Invoke(instance, new object[] { newState });
            else LogErrorF($"Could not find method 'UpdateState' on {instance?.gameObject?.name}!");
        }

        /// <summary> Calls the private EnemyHunter.AimLogic method via reflection. </summary>
        public static void CallAimLogic(EnemyHunter instance)
        {
            MethodInfo m = AccessTools.Method(typeof(EnemyHunter), "AimLogic");
            if (m != null) m.Invoke(instance, null);
            else LogErrorF($"Could not find method 'AimLogic' on {instance?.gameObject?.name}!");
        }
        // --- End Reflection Helpers ---

        /// <summary>
        /// Helper to get the PhotonView associated with an EnemyHunter instance,
        /// checking both the EnemyHunter and its base Enemy component via reflection.
        /// Essential for network synchronization.
        /// </summary>
        public static PhotonView GetPhotonView(EnemyHunter instance)
        {
            if (instance == null) return null;
            try
            {
                // 1. Try getting private field from EnemyHunter
                FieldInfo pvFieldHunter = AccessTools.Field(typeof(EnemyHunter), "photonView");
                if (pvFieldHunter != null)
                {
                    PhotonView pvHunter = pvFieldHunter.GetValue(instance) as PhotonView;
                    if (pvHunter != null) return pvHunter;
                }

                // 2. Fallback: Check the Enemy component and get its internal PhotonView via reflection
                Enemy enemy = instance.enemy; // Use the public 'enemy' field
                if (enemy != null)
                {
                    FieldInfo pvFieldEnemy = AccessTools.Field(typeof(Enemy), "PhotonView"); // Use reflection for internal field
                    if (pvFieldEnemy != null)
                    {
                        PhotonView pvEnemy = pvFieldEnemy.GetValue(enemy) as PhotonView;
                        if (pvEnemy != null) return pvEnemy;
                    }
                }

                // 3. Log error if not found in either place
                LogErrorF($"GetPhotonView: Could not find PhotonView on {instance.gameObject.name} via EnemyHunter or Enemy fields!");
                return null;
            }
            catch (Exception ex)
            {
                LogErrorF($"GetPhotonView: Error accessing PhotonView field on {instance.gameObject.name}: {ex.Message}");
                return null;
            }
        }


        /// <summary>
        /// [MasterClient Only] Calculates the target position for a shot and triggers the
        /// base game's ShootRPC via PhotonView.RPC to synchronize the shot effect for all players.
        /// </summary>
        public static void CallShootRPC(EnemyHunter instance)
        {
            // Guard: Only MasterClient should initiate shots
            if (!PhotonNetwork.IsMasterClient)
            {
                // LogDebugF($"CallShootRPC skipped on non-MasterClient for {instance?.gameObject?.name}"); // Optional debug log
                return;
            }

            PhotonView pv = GetPhotonView(instance);
            if (pv == null) { LogErrorF($"CallShootRPC: PhotonView is null for {instance?.gameObject?.name}! Cannot send RPC."); return; }
            if (instance == null || instance.gunAimTransform == null) { LogErrorF($"CallShootRPC: instance or gunAimTransform is null!"); return; }

            // --- Calculate Target Position (Logic from decompiled StateShoot, run by Master) ---
            Vector3 targetPosition = instance.gunAimTransform.position + instance.gunAimTransform.forward * 50f; // Default aim far
            try
            {
                // Use reflection to get the private investigatePoint field
                var investigatePointField = AccessTools.Field(typeof(EnemyHunter), "investigatePoint");
                if (investigatePointField != null)
                {
                    Vector3 currentInvestigatePoint = (Vector3)investigatePointField.GetValue(instance);
                    // Determine sphere cast radius based on distance (as in original)
                    float radius = Vector3.Distance(instance.transform.position, currentInvestigatePoint) > 10f ? 0.5f : 1f;
                    // Layers to hit and layers that obstruct vision
                    LayerMask hitMask = LayerMask.GetMask("Player", "Default", "PhysGrabObject", "Enemy");
                    LayerMask visionObstructMask = LayerMask.GetMask("Default"); // Only default geometry blocks LOS?
                    RaycastHit hitInfo;

                    // Try SphereCast first
                    if (Physics.SphereCast(instance.gunAimTransform.position, radius, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask))
                    {
                        // Check Linecast for obstruction only if SphereCast hit something
                        if (!Physics.Linecast(instance.gunAimTransform.position, hitInfo.point, visionObstructMask))
                        {
                            targetPosition = hitInfo.point; // No obstruction, use sphere hit point
                        }
                        // If obstructed, try a simple Raycast as fallback
                        else if (Physics.Raycast(instance.gunAimTransform.position, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask))
                        {
                            targetPosition = hitInfo.point; // Use raycast hit point if sphere was obstructed
                        }
                        // If both obstructed, targetPosition remains the default far point
                    }
                    // If SphereCast misses, try simple Raycast
                    else if (Physics.Raycast(instance.gunAimTransform.position, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask))
                    {
                        targetPosition = hitInfo.point; // Use raycast hit point
                    }
                    // If all miss, targetPosition remains the default far point
                }
                else { LogWarningF($"CallShootRPC: Could not access investigatePoint field for {instance.gameObject.name}!"); }
            }
            catch (System.Exception ex) { LogErrorF($"CallShootRPC: Error calculating target position: {ex.Message}"); }
            // --- End Target Calculation ---

            try
            {
                // Send the RPC using the found PhotonView
                LogDebugF($"CallShootRPC (Master): Sending ShootRPC for {instance.gameObject.name} targeting {targetPosition}");
                // Target the existing "ShootRPC" method on the EnemyHunter script (or wherever it's defined)
                pv.RPC("ShootRPC", RpcTarget.All, targetPosition);
            }
            catch (System.Exception ex)
            {
                LogErrorF($"CallShootRPC (Master): Failed to send ShootRPC for {instance.gameObject.name}: {ex}");
            }
        }


        /// <summary>
        /// Finds a suitable retreat point on the NavMesh away from the nearest player.
        /// </summary>
        /// <returns>True if a valid point was found, false otherwise.</returns>
        public static bool FindRetreatPoint(EnemyHunter instance, out Vector3 retreatPoint)
        {
            retreatPoint = Vector3.zero; // Initialize OUT parameter
            if (instance == null || GameDirector.instance == null) return false;

            PlayerAvatar nearestPlayer = null;
            float minDistance = float.MaxValue;

            if (GameDirector.instance.PlayerList == null || GameDirector.instance.PlayerList.Count == 0)
            {
                LogWarningF($"FindRetreatPoint: GameDirector.PlayerList is null or empty.");
                return false;
            }

            // Find the closest active player
            foreach (PlayerAvatar player in GameDirector.instance.PlayerList)
            {
                if (player == null || !player.gameObject.activeSelf) continue;
                float distance = Vector3.Distance(instance.transform.position, player.transform.position);
                if (distance < minDistance) { minDistance = distance; nearestPlayer = player; }
            }

            if (nearestPlayer == null)
            {
                LogWarningF($"FindRetreatPoint: Could not find an active player to run from.");
                return false;
            }

            // Calculate direction away from the player (flattened)
            Vector3 dirFromPlayer = (instance.transform.position - nearestPlayer.transform.position).normalized;
            dirFromPlayer.y = 0;
            if (dirFromPlayer == Vector3.zero) dirFromPlayer = instance.transform.forward; // Use forward if directly on top

            // Try finding a NavMesh point further away first
            float initialSearchDist = 20f;
            float searchRadius = 15f;
            Vector3 targetPos = instance.transform.position + dirFromPlayer * initialSearchDist;

            if (NavMesh.SamplePosition(targetPos, out NavMeshHit hit, searchRadius, NavMesh.AllAreas))
            {
                retreatPoint = hit.position;
                LogDebugF($"FindRetreatPoint: Found retreat point for {instance.gameObject.name} at {retreatPoint} (away from player {nearestPlayer.GetInstanceID()})");
                return true;
            }
            else
            {
                // Fallback: Try finding a point closer if the further one failed
                targetPos = instance.transform.position + dirFromPlayer * 8f;
                if (NavMesh.SamplePosition(targetPos, out hit, searchRadius, NavMesh.AllAreas))
                {
                    retreatPoint = hit.position;
                    LogDebugF($"FindRetreatPoint: Found fallback retreat point (closer) for {instance.gameObject.name} at {retreatPoint}");
                    return true;
                }
            }

            // If both searches failed
            LogWarningF($"FindRetreatPoint: Could not find NavMesh retreat point for {instance.gameObject.name} after searching.");
            return false;
        }

        /// <summary>
        /// Checks if the RepoLastStandMod is active by accessing its StateManager via reflection.
        /// Returns false if the mod isn't present or reflection fails.
        /// </summary>
        public static bool IsRepoLastStandActive()
        {
            // Check initial detection and reflection results
            if (!isRepoLastStandModPresent || repoStateManagerType == null || repoStateManagerInstanceProperty == null || repoLastStandActiveField == null)
            {
                return false;
            }
            try
            {
                // Get the singleton instance via the static property
                object stateManagerInstance = repoStateManagerInstanceProperty.GetValue(null);
                if (stateManagerInstance == null)
                {
                    LogWarningF("IsRepoLastStandActive: RepoLastStandMod StateManager.Instance returned null.");
                    return false;
                }
                // Get the value of the 'LastStandActive' field from the instance
                object activeValue = repoLastStandActiveField.GetValue(stateManagerInstance);
                return (bool)activeValue; // Cast and return the boolean value
            }
            catch (System.Exception ex)
            {
                // Log error and return false if reflection fails at runtime
                LogErrorF($"IsRepoLastStandActive: Error getting RepoLastStandMod state via reflection: {ex}");
                return false;
            }
        }
        // --- End Compatibility Check ---
    }
}