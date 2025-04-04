// File: Plugin.cs
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking; // Needed for UnityWebRequestMultimedia
using UnityEngine.AI;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System;
using System.IO; // Needed for Path combine
using System.Collections; // Needed for Coroutine (IEnumerator)
using Photon.Pun;

namespace HunterMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("umbreon222.repo.laststand", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        // --- Plugin Instance and Logging ---
        public static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

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
        // Group F: Recoil
        public static ConfigEntry<bool> EnableRecoil { get; private set; }
        public static ConfigEntry<float> RecoilChance { get; private set; }
        public static ConfigEntry<float> MaxRecoilOffset { get; private set; }
        // Group G: Wander Sound
        public static ConfigEntry<bool> EnableWanderSound { get; private set; }
        public static ConfigEntry<string> WanderSoundFileName { get; private set; }
        public static ConfigEntry<float> WanderSoundMinInterval { get; private set; }
        public static ConfigEntry<float> WanderSoundMaxInterval { get; private set; }
        public static ConfigEntry<float> WanderSoundVolume { get; private set; }
        public static ConfigEntry<float> WanderSoundMaxDistance { get; private set; } // Added distance config
        // Group Z: Logging Levels
        public static ConfigEntry<bool> EnableInfoLogs { get; private set; }
        public static ConfigEntry<bool> EnableDebugLogs { get; private set; }
        public static ConfigEntry<bool> EnableWarningLogs { get; private set; }
        public static ConfigEntry<bool> EnableErrorLogs { get; private set; }
        // --- End Config Entries ---

        // --- Public Static Field to Hold Loaded AudioClip ---
        public static AudioClip LoadedWanderAudioClip { get; private set; } = null;

        // --- Mod Compatibility (RepoLastStandMod) ---
        private const string RepoLastStandGuid = "umbreon222.repo.laststand";
        private static bool isRepoLastStandModPresent = false;
        private static Type repoStateManagerType = null;
        private static PropertyInfo repoStateManagerInstanceProperty = null;
        private static FieldInfo repoLastStandActiveField = null;
        // --- End Compatibility ---

        private void Awake()
        {
            if (Instance == null) { Instance = this; Log = Logger; } else { Destroy(this); return; }

            Log.LogInfo("Loading configuration...");
            // --- Bind all config entries ---
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
            EnableRecoil = Config.Bind("F. Recoil", "EnableRecoil", false, "If enabled, the Hunter has a chance to miss shots intentionally (recoil).");
            RecoilChance = Config.Bind("F. Recoil", "RecoilChance", 0.1f, new ConfigDescription("Chance (0.0 to 1.0) for recoil to occur on any given shot, if enabled.", new AcceptableValueRange<float>(0f, 1f)));
            MaxRecoilOffset = Config.Bind("F. Recoil", "MaxRecoilOffset", 3.0f, new ConfigDescription("Maximum random offset distance applied to the target position when recoil occurs.", new AcceptableValueRange<float>(0.1f, 10f)));
            EnableWanderSound = Config.Bind("G. Wander Sound", "EnableWanderSound", true, "Enable a subtle periodic sound while the Hunter is idle or roaming nearby.");
            WanderSoundFileName = Config.Bind("G. Wander Sound", "WanderSoundFileName", "HunterTap.ogg", "Name of the .ogg audio file (including extension) located in the plugin folder to use for the wander sound.");
            WanderSoundMinInterval = Config.Bind("G. Wander Sound", "WanderSoundMinInterval", 2.0f, new ConfigDescription("Minimum time (seconds) between wander sounds.", new AcceptableValueRange<float>(0.5f, 10f)));
            WanderSoundMaxInterval = Config.Bind("G. Wander Sound", "WanderSoundMaxInterval", 4.5f, new ConfigDescription("Maximum time (seconds) between wander sounds.", new AcceptableValueRange<float>(1.0f, 20f)));
            WanderSoundVolume = Config.Bind("G. Wander Sound", "WanderSoundVolume", 0.3f, new ConfigDescription("Volume multiplier (0.0 to 1.0) for the wander sound. Affects the perceived distance/radius.", new AcceptableValueRange<float>(0f, 1f)));
            WanderSoundMaxDistance = Config.Bind("G. Wander Sound", "WanderSoundMaxDistance", 15f, new ConfigDescription("Maximum distance (in game units) the wander sound can be heard from. Used with PlayClipAtPoint.", new AcceptableValueRange<float>(5f, 50f)));
            EnableInfoLogs = Config.Bind("Z. Logging", "EnableInfoLogs", true, "Enable standard informational logs.");
            EnableDebugLogs = Config.Bind("Z. Logging", "EnableDebugLogs", false, "Enable detailed debug logs (can be spammy).");
            EnableWarningLogs = Config.Bind("Z. Logging", "EnableWarningLogs", true, "Enable warning logs for potential issues.");
            EnableErrorLogs = Config.Bind("Z. Logging", "EnableErrorLogs", true, "Enable error logs for critical failures.");
            // --- End Binding ---

            LogInfoF("Configuration loaded.");
            DetectRepoLastStandMod(); // Detect compatibility mod

            // Start loading the custom audio clip
            if (EnableWanderSound.Value && !string.IsNullOrWhiteSpace(WanderSoundFileName.Value))
            {
                StartCoroutine(LoadCustomAudioClip(WanderSoundFileName.Value));
            }
            else if (EnableWanderSound.Value)
            {
                LogWarningF("Wander sound enabled but WanderSoundFileName is empty. Wander sound disabled.");
            }

            LogInfoF($"Applying Harmony patches for {PluginInfo.PLUGIN_NAME}...");
            harmony.PatchAll(typeof(HunterPatches)); // Apply all patches
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
            if (isRepoLastStandModPresent) { LogInfoF("RepoLastStandMod detected. Enabling compatibility logic."); try { string assemblyName = "RepoLastStandMod"; repoStateManagerType = Type.GetType($"RepoLastStandMod.StateManager, {assemblyName}", throwOnError: false); if (repoStateManagerType == null && Chainloader.PluginInfos.TryGetValue(RepoLastStandGuid, out var pluginInfo)) { if (pluginInfo.Instance != null) { assemblyName = pluginInfo.Instance.GetType().Assembly.FullName; repoStateManagerType = Type.GetType($"RepoLastStandMod.StateManager, {assemblyName}", throwOnError: false); LogDebugF($"Attempting to load StateManager from assembly: {assemblyName}"); } } if (repoStateManagerType != null) { repoStateManagerInstanceProperty = AccessTools.Property(repoStateManagerType, "Instance"); repoLastStandActiveField = AccessTools.Field(repoStateManagerType, "LastStandActive"); if (repoStateManagerInstanceProperty == null) LogErrorF("Could not find 'Instance' property on StateManager."); if (repoLastStandActiveField == null) LogErrorF("Could not find 'LastStandActive' field on StateManager."); if (repoStateManagerInstanceProperty == null || repoLastStandActiveField == null) { LogErrorF("Failed reflection for RepoLastStandMod. Compatibility disabled."); isRepoLastStandModPresent = false; } } else { LogErrorF("Could not find type 'RepoLastStandMod.StateManager'. Compatibility disabled."); isRepoLastStandModPresent = false; } } catch (System.Exception ex) { LogErrorF($"Error during reflection for RepoLastStandMod compatibility: {ex}"); isRepoLastStandModPresent = false; } } else { LogInfoF("RepoLastStandMod not detected."); }
        }

        // --- Logging Helpers ---
        public static void LogInfoF(string message) { if (Log != null && EnableInfoLogs.Value) Log.LogInfo(message); }
        public static void LogDebugF(string message) { if (Log != null && EnableDebugLogs.Value) Log.LogDebug(message); }
        public static void LogWarningF(string message) { if (Log != null && EnableWarningLogs.Value) Log.LogWarning(message); }
        public static void LogErrorF(string message) { if (Log != null && EnableErrorLogs.Value) Log.LogError(message); }

        // --- Config Check Helpers ---
        public static bool IsMinigunModeEnabled() => EnableMinigunMode.Value;
        public static bool ShouldRunAwayWhileReloading() => RunAwayWhileReloadingConfig.Value;

        // --- Reflection Helpers ---
        public static void CallUpdateState(EnemyHunter instance, EnemyHunter.State newState) { MethodInfo m = AccessTools.Method(typeof(EnemyHunter), "UpdateState"); if (m != null) m.Invoke(instance, new object[] { newState }); else LogErrorF($"Could not find method 'UpdateState' on {instance?.gameObject?.name}!"); }
        public static void CallAimLogic(EnemyHunter instance) { MethodInfo m = AccessTools.Method(typeof(EnemyHunter), "AimLogic"); if (m != null) m.Invoke(instance, null); else LogErrorF($"Could not find method 'AimLogic' on {instance?.gameObject?.name}!"); }
        public static PhotonView GetPhotonView(EnemyHunter instance) { if (instance == null) return null; try { FieldInfo pvFieldHunter = AccessTools.Field(typeof(EnemyHunter), "photonView"); if (pvFieldHunter != null) { PhotonView pvHunter = pvFieldHunter.GetValue(instance) as PhotonView; if (pvHunter != null) return pvHunter; } Enemy enemy = instance.enemy; if (enemy != null) { FieldInfo pvFieldEnemy = AccessTools.Field(typeof(Enemy), "PhotonView"); if (pvFieldEnemy != null) { PhotonView pvEnemy = pvFieldEnemy.GetValue(enemy) as PhotonView; if (pvEnemy != null) return pvEnemy; } } LogErrorF($"GetPhotonView: Could not find PhotonView on {instance.gameObject.name} via EnemyHunter or Enemy fields!"); return null; } catch (Exception ex) { LogErrorF($"GetPhotonView: Error accessing PhotonView field on {instance.gameObject.name}: {ex.Message}"); return null; } }

        // --- CallShootRPC (Updated LayerMask) ---
        public static void CallShootRPC(EnemyHunter instance)
        {
            if (!PhotonNetwork.IsMasterClient) return;
            PhotonView pv = GetPhotonView(instance);
            if (pv == null) { LogErrorF($"CallShootRPC: PhotonView is null for {instance?.gameObject?.name}! Cannot send RPC."); return; }
            if (instance == null || instance.gunAimTransform == null) { LogErrorF($"CallShootRPC: instance or gunAimTransform is null!"); return; }

            Vector3 intendedTargetPosition = instance.gunAimTransform.position + instance.gunAimTransform.forward * 50f;
            try
            {
                var investigatePointField = AccessTools.Field(typeof(EnemyHunter), "investigatePoint");
                if (investigatePointField != null)
                {
                    Vector3 currentInvestigatePoint = (Vector3)investigatePointField.GetValue(instance);
                    float radius = Vector3.Distance(instance.transform.position, currentInvestigatePoint) > 10f ? 0.5f : 1f;
                    // *** Updated LayerMask to exclude "Enemy" layer ***
                    LayerMask hitMask = LayerMask.GetMask("Player", "Default", "PhysGrabObject");
                    LayerMask visionObstructMask = LayerMask.GetMask("Default");
                    RaycastHit hitInfo;
                    if (Physics.SphereCast(instance.gunAimTransform.position, radius, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask)) { if (!Physics.Linecast(instance.gunAimTransform.position, hitInfo.point, visionObstructMask)) { intendedTargetPosition = hitInfo.point; } else if (Physics.Raycast(instance.gunAimTransform.position, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask)) { intendedTargetPosition = hitInfo.point; } } else if (Physics.Raycast(instance.gunAimTransform.position, instance.gunAimTransform.forward, out hitInfo, 50f, hitMask)) { intendedTargetPosition = hitInfo.point; }
                }
                else { LogWarningF($"CallShootRPC: Could not access investigatePoint field for {instance.gameObject.name}!"); }
            }
            catch (System.Exception ex) { LogErrorF($"CallShootRPC: Error calculating target position: {ex.Message}"); }

            Vector3 finalTargetPosition = intendedTargetPosition;
            if (EnableRecoil.Value && UnityEngine.Random.value < RecoilChance.Value) { Vector3 randomOffset = UnityEngine.Random.insideUnitSphere * UnityEngine.Random.Range(0f, MaxRecoilOffset.Value); finalTargetPosition = intendedTargetPosition + randomOffset; LogDebugF($"CallShootRPC (Master): Recoil applied! Original: {intendedTargetPosition}, Final: {finalTargetPosition}"); }

            try { LogDebugF($"CallShootRPC (Master): Sending ShootRPC for {instance.gameObject.name} targeting {finalTargetPosition}"); pv.RPC("ShootRPC", RpcTarget.All, finalTargetPosition); }
            catch (System.Exception ex) { LogErrorF($"CallShootRPC (Master): Failed to send ShootRPC for {instance.gameObject.name}: {ex}"); }
        }

        // --- FindRetreatPoint ---
        public static bool FindRetreatPoint(EnemyHunter instance, out Vector3 retreatPoint) { retreatPoint = Vector3.zero; if (instance == null || GameDirector.instance == null) return false; PlayerAvatar nearestPlayer = null; float minDistance = float.MaxValue; if (GameDirector.instance.PlayerList == null || GameDirector.instance.PlayerList.Count == 0) { LogWarningF($"FindRetreatPoint: GameDirector.PlayerList is null or empty."); return false; } foreach (PlayerAvatar player in GameDirector.instance.PlayerList) { if (player == null || !player.gameObject.activeSelf) continue; float distance = Vector3.Distance(instance.transform.position, player.transform.position); if (distance < minDistance) { minDistance = distance; nearestPlayer = player; } } if (nearestPlayer == null) { LogWarningF($"FindRetreatPoint: Could not find an active player to run from."); return false; } Vector3 dirFromPlayer = (instance.transform.position - nearestPlayer.transform.position).normalized; dirFromPlayer.y = 0; if (dirFromPlayer == Vector3.zero) dirFromPlayer = instance.transform.forward; float initialSearchDist = 20f; float searchRadius = 15f; Vector3 targetPos = instance.transform.position + dirFromPlayer * initialSearchDist; if (NavMesh.SamplePosition(targetPos, out NavMeshHit hit, searchRadius, NavMesh.AllAreas)) { retreatPoint = hit.position; LogDebugF($"FindRetreatPoint: Found retreat point for {instance.gameObject.name} at {retreatPoint} (away from player {nearestPlayer.GetInstanceID()})"); return true; } else { targetPos = instance.transform.position + dirFromPlayer * 8f; if (NavMesh.SamplePosition(targetPos, out hit, searchRadius, NavMesh.AllAreas)) { retreatPoint = hit.position; LogDebugF($"FindRetreatPoint: Found fallback retreat point (closer) for {instance.gameObject.name} at {retreatPoint}"); return true; } } LogWarningF($"Could not find NavMesh retreat point for {instance.gameObject.name} after searching."); return false; }

        // --- IsRepoLastStandActive ---
        public static bool IsRepoLastStandActive() { if (!isRepoLastStandModPresent || repoStateManagerType == null || repoStateManagerInstanceProperty == null || repoLastStandActiveField == null) { return false; } try { object stateManagerInstance = repoStateManagerInstanceProperty.GetValue(null); if (stateManagerInstance == null) { LogWarningF("IsRepoLastStandActive: RepoLastStandMod StateManager.Instance returned null."); return false; } object activeValue = repoLastStandActiveField.GetValue(stateManagerInstance); return (bool)activeValue; } catch (System.Exception ex) { LogErrorF($"IsRepoLastStandActive: Error getting RepoLastStandMod state via reflection: {ex}"); return false; } }

        // --- Coroutine to load custom audio ---
        private IEnumerator LoadCustomAudioClip(string fileName) { string pluginDirectory = Path.GetDirectoryName(Info.Location); string fullPath = Path.Combine(pluginDirectory, fileName); if (!File.Exists(fullPath)) { LogErrorF($"Wander sound file not found at: {fullPath}. Wander sound disabled."); yield break; } string uri = "file:///" + fullPath.Replace("\\", "/"); LogInfoF($"Attempting to load wander sound from: {uri}"); using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS)) { yield return www.SendWebRequest(); if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError) { LogErrorF($"Error loading wander sound '{fileName}': {www.error}"); } else { LoadedWanderAudioClip = DownloadHandlerAudioClip.GetContent(www); if (LoadedWanderAudioClip == null) { LogErrorF($"Failed to decode wander sound '{fileName}' after download."); } else { LogInfoF($"Successfully loaded wander sound: {fileName}"); LoadedWanderAudioClip.name = "HunterWanderTapSound"; } } } }
    }
}