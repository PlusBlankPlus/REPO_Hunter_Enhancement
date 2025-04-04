// File: HunterAmmoTracker.cs
using UnityEngine;
using Photon.Pun;
using System.Reflection;
using HarmonyLib;

namespace HunterMod
{
    /// <summary>
    /// Tracks ammo, reload state, and other mod-specific behaviors for a Hunter enemy.
    /// Implements IPunObservable for multiplayer state synchronization.
    /// Holds the per-instance timer for the wander sound.
    /// </summary>
    public class HunterAmmoTracker : MonoBehaviourPunCallbacks, IPunObservable
    {
        private PhotonView pv; // Cached PhotonView for network operations

        // --- Config Values (Populated by Plugin/Patches on spawn) ---
        public float ConfiguredFastReloadTime = 2f;
        public float ConfiguredMediumReloadTime = 5f;
        public float ConfiguredSlowReloadTime = 7f;
        public bool ConfigEnableDamageInterrupt = true;
        public float ConfigDamageInterruptDelay = 5f;
        public int ConfigMinigunShots = 10;
        public float ConfigMinigunShotDelay = 0.1f;
        public bool ConfigEnableTotalAmmoLimit = false;
        public int ConfigTotalAmmoCount = 30;
        public bool ConfigRunAwayWhileReloading = true;
        // --- End Config Values ---

        // --- State Variables (MasterClient authoritative, synced to clients) ---
        public ReloadSkill currentSkill = ReloadSkill.Medium; // Synced once via RPC on spawn
        private float currentReloadTimer = 0f;           // Local timer, state change driven by Master
        private float damageInterruptDelayTimer = 0f;    // Local timer, state change driven by Master
        private bool isReloading = false;             // Synced via IPunObservable
        public int currentTotalAmmo = 30;             // Synced via IPunObservable
        public bool isOutOfAmmoPermanently = false;  // Synced via IPunObservable
        public bool isMinigunBurstActive = false;     // Synced via IPunObservable
        public int minigunShotsRemaining = 0;         // MasterClient tracks internally
        public float minigunShotTimer = 0f;           // MasterClient tracks internally
        private bool isInterruptedInternal = false;   // Derived from timer, Synced via IPunObservable

        // *** Wander Sound Timer (Local Only) ***
        public float wanderSoundTimer = 0f;
        // *** END Wander Timer ***

        // --- Properties (Reflect the current state) ---
        public bool IsReloading => isReloading;
        public bool IsInterrupted => isInterruptedInternal; // Use the synced derived flag
        public bool HasTotalAmmo => !ConfigEnableTotalAmmoLimit || currentTotalAmmo > 0;
        // --- End Properties ---

        void Awake()
        {
            // Attempt to find the PhotonView associated with this Hunter
            // Strategy: Check self -> Check EnemyHunter component -> Check Enemy component
            pv = GetComponent<PhotonView>();
            if (pv == null)
            {
                EnemyHunter hunter = GetComponent<EnemyHunter>();
                if (hunter != null)
                {
                    FieldInfo pvFieldHunter = AccessTools.Field(typeof(EnemyHunter), "photonView");
                    if (pvFieldHunter != null) pv = pvFieldHunter.GetValue(hunter) as PhotonView;
                }
            }
            if (pv == null)
            {
                Enemy enemy = GetComponent<Enemy>();
                if (enemy != null)
                {
                    FieldInfo pvFieldEnemy = AccessTools.Field(typeof(Enemy), "PhotonView");
                    if (pvFieldEnemy != null) pv = pvFieldEnemy.GetValue(enemy) as PhotonView;
                }
            }

            if (pv == null) { Plugin.LogErrorF($"HunterAmmoTracker on {gameObject.name} could NOT find a PhotonView! Mod features requiring network sync will fail."); }
            else { Plugin.LogDebugF($"HunterAmmoTracker on {gameObject.name} found PhotonView: {pv.ViewID}"); }

            // Initialize wander sound timer randomly if feature enabled in Plugin
            // Use safety checks for potentially unloaded config during Awake
            if (Plugin.EnableWanderSound != null && Plugin.EnableWanderSound.Value)
            {
                float min = Plugin.WanderSoundMinInterval?.Value ?? 3f;
                float max = Plugin.WanderSoundMaxInterval?.Value ?? 6f;
                if (min <= max) { wanderSoundTimer = Random.Range(min * 0.5f, max); } // Start with random delay
                else { wanderSoundTimer = 3f; } // Default if range invalid
                Plugin.LogDebugF($"Hunter {GetHunterName()} initialized wander timer to {wanderSoundTimer:F2}s");
            }
        }

        void Update()
        {
            // MasterClient authoritative timer logic for reload/interrupt
            if (pv != null && pv.IsMine)
            {
                // Update Interrupt Timer
                if (damageInterruptDelayTimer > 0f)
                {
                    damageInterruptDelayTimer -= Time.deltaTime;
                    if (damageInterruptDelayTimer <= 0f)
                    {
                        damageInterruptDelayTimer = 0f;
                        Plugin.LogInfoF($"Hunter {GetHunterName()} damage interrupt delay finished (Master).");
                    }
                }

                // Update Reload Timer
                if (isReloading)
                {
                    currentReloadTimer -= Time.deltaTime;
                    if (currentReloadTimer <= 0f)
                    {
                        isReloading = false;
                        currentReloadTimer = 0f;
                        Plugin.LogInfoF($"Hunter {GetHunterName()} finished reloading (Master) (Skill: {currentSkill}).");
                    }
                }
            }

            // Update internal interrupted flag derived from timer (runs on all clients)
            isInterruptedInternal = damageInterruptDelayTimer > 0f;

            // Wander sound timer logic is handled within HunterPatches state prefixes
        }

        /// <summary>
        /// Called by Photon PUN automatically to synchronize variables between MasterClient and clients.
        /// </summary>
        public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
        {
            if (stream.IsWriting) // MasterClient sends data
            {
                stream.SendNext(isReloading);
                stream.SendNext(currentTotalAmmo);
                stream.SendNext(isOutOfAmmoPermanently);
                stream.SendNext(isInterruptedInternal);
                stream.SendNext(isMinigunBurstActive);
            }
            else // Clients receive data
            {
                // Read data in the same order it was sent
                this.isReloading = (bool)stream.ReceiveNext();
                this.currentTotalAmmo = (int)stream.ReceiveNext();
                this.isOutOfAmmoPermanently = (bool)stream.ReceiveNext();
                this.isInterruptedInternal = (bool)stream.ReceiveNext();
                this.isMinigunBurstActive = (bool)stream.ReceiveNext();

                // Update local timers based on received state for better client-side prediction
                if (!this.isReloading) this.currentReloadTimer = 0f;
                if (!this.isInterruptedInternal) this.damageInterruptDelayTimer = 0f;
            }
        }

        /// <summary>
        /// [MasterClient Only] Attempts to decrement the total ammo count, if enabled.
        /// </summary>
        public bool TryDecrementTotalAmmo()
        {
            // Clients just rely on the synced HasTotalAmmo property for checks
            if (pv == null || !pv.IsMine) return HasTotalAmmo;

            // --- MasterClient Authoritative Logic ---
            if (!ConfigEnableTotalAmmoLimit) return true; // Limit disabled

            if (currentTotalAmmo > 0)
            {
                currentTotalAmmo--; // Master decrements
                Plugin.LogInfoF($"Hunter {GetHunterName()} ammo decremented (Master). Remaining: {currentTotalAmmo}");

                if (currentTotalAmmo <= 0) // Check if this shot made it run out
                {
                    bool lastStandIsActive = Plugin.IsRepoLastStandActive();
                    if (!lastStandIsActive)
                    {
                        isOutOfAmmoPermanently = true; // Master sets state (synced via IPunObservable)
                        Plugin.LogErrorF($"Hunter {GetHunterName()} HAS RUN OUT OF TOTAL AMMO (Master)! Perm Leave.");
                        CancelActions(); // Stop reload/burst locally on Master
                        return false; // Ran out this shot
                    }
                    else
                    {
                        Plugin.LogWarningF($"Hunter {GetHunterName()} ammo reached 0 (Master), but Last Stand IS Active. Not setting OOA flag.");
                        CancelActions();
                        // Still return true: shot was allowed, perm OOA prevented by Last Stand
                    }
                }
                return true; // Ammo used successfully
            }
            else // Already out of ammo
            {
                bool lastStandIsActive = Plugin.IsRepoLastStandActive();
                if (!isOutOfAmmoPermanently && !lastStandIsActive) // Ensure OOA state is set if needed
                {
                    isOutOfAmmoPermanently = true; // Master sets state (synced via IPunObservable)
                    Plugin.LogErrorF($"Hunter {GetHunterName()} WAS ALREADY OOA (Master)! Force Perm Leave state.");
                }
                else if (!isOutOfAmmoPermanently && lastStandIsActive)
                {
                    Plugin.LogWarningF($"Hunter {GetHunterName()} ammo already at 0 (Master), Last Stand active.");
                }
                return false; // No ammo left
            }
        }

        /// <summary>
        /// [MasterClient Only] Starts the reload process.
        /// </summary>
        public void StartReload()
        {
            if (pv == null || !pv.IsMine) return;
            // Check conditions locally on MasterClient first
            if (isOutOfAmmoPermanently) { Plugin.LogDebugF($"StartReload blocked (Master): Hunter {GetHunterName()} permanently OOA."); return; }
            if (damageInterruptDelayTimer > 0f) { Plugin.LogDebugF($"StartReload blocked (Master): Hunter {GetHunterName()} Interrupt Delay ({damageInterruptDelayTimer:F1}s)."); return; }
            if (isMinigunBurstActive) { Plugin.LogWarningF($"StartReload blocked (Master): Hunter {GetHunterName()} Minigun Active."); return; }

            if (!isReloading) // Check Master's state
            {
                isReloading = true; // Set state locally on Master (synced via IPunObservable)
                float actualReloadTime;
                switch (currentSkill)
                {
                    case ReloadSkill.Fast: actualReloadTime = ConfiguredFastReloadTime; break;
                    case ReloadSkill.Medium: actualReloadTime = ConfiguredMediumReloadTime; break;
                    case ReloadSkill.Slow: actualReloadTime = ConfiguredSlowReloadTime; break;
                    default: actualReloadTime = ConfiguredMediumReloadTime; Plugin.LogWarningF($"Hunter {GetHunterName()} unknown skill '{currentSkill}', defaulting Medium."); break;
                }
                currentReloadTimer = actualReloadTime; // Start timer locally on Master
                Plugin.LogInfoF($"Hunter {GetHunterName()} started reload (Master) (Skill: {currentSkill}, Time: {actualReloadTime:F1}s).");

                // Send RPC to clients to update their local timers for better prediction
                pv.RPC(nameof(StartReloadRPC), RpcTarget.Others, actualReloadTime);
            }
            else { Plugin.LogWarningF($"Hunter {GetHunterName()} tried StartReload (Master) but was already reloading."); }
        }

        /// <summary>
        /// [RPC on Clients] Updates local timer prediction.
        /// </summary>
        [PunRPC]
        private void StartReloadRPC(float reloadDuration)
        {
            if (pv == null || pv.IsMine) return; // Ignore if Master or no PV
                                                 // Only update local timer if the synced state confirms we should be reloading
            if (isReloading)
            {
                currentReloadTimer = reloadDuration;
            }
            Plugin.LogDebugF($"Hunter {GetHunterName()} received StartReloadRPC (Time: {reloadDuration:F1}s).");
        }


        /// <summary>
        /// [MasterClient Only] Applies damage interrupt.
        /// </summary>
        public void ApplyDamageInterrupt()
        {
            if (pv == null || !pv.IsMine) return;
            if (isOutOfAmmoPermanently || !ConfigEnableDamageInterrupt) return;

            if (isReloading) // Check Master's authoritative state
            {
                isReloading = false; // Update state locally on Master (synced via IPunObservable)
                currentReloadTimer = 0f;
                damageInterruptDelayTimer = ConfigDamageInterruptDelay; // Start interrupt timer on Master
                // isInterruptedInternal becomes true implicitly, synced via IPunObservable

                Plugin.LogInfoF($"Hunter {GetHunterName()} hurt during reload (Master)! Reload cancelled, starting {ConfigDamageInterruptDelay:F1}s interrupt delay.");

                // Send RPC to clients to sync the interrupt delay timer start
                pv.RPC(nameof(InterruptReloadRPC), RpcTarget.Others, ConfigDamageInterruptDelay);
            }
        }

        /// <summary>
        /// [RPC on Clients] Updates local timer prediction.
        /// </summary>
        [PunRPC]
        private void InterruptReloadRPC(float interruptDuration)
        {
            if (pv == null || pv.IsMine) return; // Ignore if Master or no PV

            damageInterruptDelayTimer = interruptDuration; // Start local interrupt timer
            isInterruptedInternal = true; // Update derived flag locally immediately
            if (!isReloading) currentReloadTimer = 0f; // Ensure local reload timer is stopped

            Plugin.LogDebugF($"Hunter {GetHunterName()} received InterruptReloadRPC (Delay: {interruptDuration:F1}s).");
        }

        /// <summary>
        /// [MasterClient Only] Sets initial state and syncs via RPC.
        /// </summary>
        public void NetworkInitialize(ReloadSkill skill, int totalAmmo)
        {
            if (pv == null || !pv.IsMine) return;
            // Set initial authoritative state
            this.currentSkill = skill; this.currentTotalAmmo = totalAmmo; this.isOutOfAmmoPermanently = false; this.isReloading = false; this.isMinigunBurstActive = false; this.damageInterruptDelayTimer = 0f; this.isInterruptedInternal = false;

            // Initialize wander timer randomly on Master
            float min = Plugin.WanderSoundMinInterval?.Value ?? 3f; float max = Plugin.WanderSoundMaxInterval?.Value ?? 6f; if (min > max) min = max;
            this.wanderSoundTimer = Random.Range(min * 0.5f, max);

            // Send buffered RPC to set initial state on all other clients
            pv.RPC(nameof(SyncInitialStateRPC), RpcTarget.OthersBuffered, (int)skill, totalAmmo);
            Plugin.LogInfoF($"Hunter {GetHunterName()} initialized (Master) - Skill: {skill}, Ammo: {totalAmmo}");
        }

        /// <summary>
        /// [RPC on Clients] Sets initial synced state.
        /// </summary>
        [PunRPC]
        private void SyncInitialStateRPC(int skillIndex, int totalAmmo)
        {
            if (pv == null || pv.IsMine) return;
            // Set initial client state based on RPC
            this.currentSkill = (ReloadSkill)skillIndex; this.currentTotalAmmo = totalAmmo; this.isOutOfAmmoPermanently = false; this.isReloading = false; this.isMinigunBurstActive = false; this.damageInterruptDelayTimer = 0f; this.isInterruptedInternal = false;

            // Initialize wander timer randomly on Client
            float min = Plugin.WanderSoundMinInterval?.Value ?? 3f; float max = Plugin.WanderSoundMaxInterval?.Value ?? 6f; if (min > max) min = max;
            this.wanderSoundTimer = Random.Range(min * 0.5f, max);

            Plugin.LogInfoF($"Hunter {GetHunterName()} received SyncInitialStateRPC - Skill: {currentSkill}, Ammo: {totalAmmo}");
        }

        /// <summary> [MasterClient Only] Initializes minigun burst state. </summary>
        public void InitializeMinigunBurst()
        {
            if (pv == null || !pv.IsMine) return;
            if (!isMinigunBurstActive) { isMinigunBurstActive = true; minigunShotsRemaining = ConfigMinigunShots; minigunShotTimer = 0f; Plugin.LogInfoF($"Hunter {GetHunterName()} initializing minigun burst (Master) ({minigunShotsRemaining} shots)."); }
        }

        /// <summary> [MasterClient Only] Ends minigun burst state. </summary>
        public void EndMinigunBurst()
        {
            if (pv == null || !pv.IsMine) return;
            if (isMinigunBurstActive) { isMinigunBurstActive = false; minigunShotsRemaining = 0; minigunShotTimer = 0f; Plugin.LogInfoF($"Hunter {GetHunterName()} minigun burst ended/cancelled (Master)."); }
        }

        // --- Helper methods ---
        /// <summary> Gets the remaining reload time based on the local timer. </summary>
        public float GetRemainingReloadTime() { return !isReloading ? 0f : currentReloadTimer; }

        /// <summary> [MasterClient Only] Helper to stop current actions like reloading or minigun burst. </summary>
        private void CancelActions() { if (pv == null || !pv.IsMine) return; isReloading = false; currentReloadTimer = 0f; EndMinigunBurst(); }

        /// <summary> Helper to get a clean game object name for logging. </summary>
        private string GetHunterName() => gameObject?.name ?? "Unknown Hunter";
    }
}