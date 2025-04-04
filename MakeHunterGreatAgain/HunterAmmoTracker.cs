using UnityEngine;
using Photon.Pun;
using System.Reflection;
using HarmonyLib;

namespace HunterMod
{
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
        // --- End State Variables ---

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

            if (pv == null)
            {
                Plugin.LogErrorF($"HunterAmmoTracker on {gameObject.name} could NOT find a PhotonView! Mod features requiring network sync will fail.");
            }
            else
            {
                Plugin.LogDebugF($"HunterAmmoTracker on {gameObject.name} found PhotonView: {pv.ViewID}");
            }
        }

        void Update()
        {
            // Only the MasterClient authoritatively updates timers that change state
            if (pv != null && pv.IsMine)
            {
                // Update Interrupt Timer
                if (damageInterruptDelayTimer > 0f)
                {
                    damageInterruptDelayTimer -= Time.deltaTime;
                    if (damageInterruptDelayTimer <= 0f)
                    {
                        damageInterruptDelayTimer = 0f;
                        // State change (isInterruptedInternal becomes false) synced via OnPhotonSerializeView
                        Plugin.LogInfoF($"Hunter {GetHunterName()} damage interrupt delay finished (Master).");
                    }
                }

                // Update Reload Timer
                if (isReloading)
                {
                    currentReloadTimer -= Time.deltaTime;
                    if (currentReloadTimer <= 0f)
                    {
                        // Stop reloading state change synced via OnPhotonSerializeView
                        isReloading = false;
                        currentReloadTimer = 0f;
                        Plugin.LogInfoF($"Hunter {GetHunterName()} finished reloading (Master) (Skill: {currentSkill}).");
                    }
                }
            }

            // Clients (and Master) update the derived internal flag locally for immediate checks
            isInterruptedInternal = damageInterruptDelayTimer > 0f;
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
                stream.SendNext(isInterruptedInternal); // Send the derived boolean state
                stream.SendNext(isMinigunBurstActive);
            }
            else // Clients receive data
            {
                this.isReloading = (bool)stream.ReceiveNext();
                this.currentTotalAmmo = (int)stream.ReceiveNext();
                this.isOutOfAmmoPermanently = (bool)stream.ReceiveNext();
                this.isInterruptedInternal = (bool)stream.ReceiveNext();
                this.isMinigunBurstActive = (bool)stream.ReceiveNext();

                // Update local timers based on received state for better client-side prediction
                // Note: This doesn't perfectly sync the *exact* timer value, but ensures states match.
                if (!this.isReloading) this.currentReloadTimer = 0f;
                if (!this.isInterruptedInternal) this.damageInterruptDelayTimer = 0f;
            }
        }

        /// <summary>
        /// [MasterClient Only] Attempts to decrement the total ammo count, if enabled.
        /// Sets the permanent out-of-ammo flag if necessary.
        /// State changes are synced via IPunObservable.
        /// </summary>
        /// <returns>True if the Hunter had ammo for this shot, False if out of ammo.</returns>
        public bool TryDecrementTotalAmmo()
        {
            // Clients just check their synced state via HasTotalAmmo property
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
                        isOutOfAmmoPermanently = true; // Master sets state
                        Plugin.LogErrorF($"Hunter {GetHunterName()} HAS RUN OUT OF TOTAL AMMO (Master)! Perm Leave.");
                        CancelActions(); // Stop reload/burst locally on Master
                        return false; // Ran out this shot
                    }
                    else
                    {
                        Plugin.LogWarningF($"Hunter {GetHunterName()} ammo reached 0 (Master), but Last Stand IS Active. Not setting OOA flag.");
                        CancelActions();
                        // Still return true here because the shot was allowed, and perm OOA is prevented.
                        // Future shots will be blocked by HasTotalAmmo check.
                    }
                }
                return true; // Ammo used successfully
            }
            else // Already out of ammo
            {
                bool lastStandIsActive = Plugin.IsRepoLastStandActive();
                if (!isOutOfAmmoPermanently && !lastStandIsActive) // Ensure OOA state is set if needed
                {
                    isOutOfAmmoPermanently = true;
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
        /// [MasterClient Only] Starts the reload process based on the Hunter's skill, if possible.
        /// Sends an RPC to inform clients. State changes synced via IPunObservable.
        /// </summary>
        public void StartReload()
        {
            if (pv == null || !pv.IsMine) return; // Only MasterClient initiates reload

            // Check conditions locally on MasterClient first
            if (isOutOfAmmoPermanently) { Plugin.LogDebugF($"StartReload blocked (Master): Hunter {GetHunterName()} permanently OOA."); return; }
            if (damageInterruptDelayTimer > 0f) { Plugin.LogDebugF($"StartReload blocked (Master): Hunter {GetHunterName()} Interrupt Delay ({damageInterruptDelayTimer:F1}s)."); return; }
            if (isMinigunBurstActive) { Plugin.LogWarningF($"StartReload blocked (Master): Hunter {GetHunterName()} Minigun Active."); return; }

            if (!isReloading) // Check Master's state
            {
                isReloading = true; // Set state locally on Master
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

                // Send RPC to clients to update their local timers for better prediction/responsiveness
                // The core 'isReloading' state is handled by OnPhotonSerializeView
                pv.RPC(nameof(StartReloadRPC), RpcTarget.Others, actualReloadTime);
            }
            else { Plugin.LogWarningF($"Hunter {GetHunterName()} tried StartReload (Master) but was already reloading."); }
        }

        /// <summary>
        /// [Called via RPC on Clients] Updates client's local timer when reload starts for prediction.
        /// </summary>
        [PunRPC]
        private void StartReloadRPC(float reloadDuration)
        {
            if (pv == null || pv.IsMine) return; // Ignore if Master or no PV

            // Note: isReloading state itself is synced via OnPhotonSerializeView
            // This RPC primarily helps sync the *timer* start for smoother client prediction
            // If OnPhotonSerializeView sets isReloading=true *before* this RPC, this ensures timer is set.
            // If this RPC arrives first, timer starts, and OnPhotonSerializeView confirms isReloading.
            if (isReloading) // Only set timer if we know we should be reloading
            {
                currentReloadTimer = reloadDuration;
            }
            Plugin.LogDebugF($"Hunter {GetHunterName()} received StartReloadRPC (Time: {reloadDuration:F1}s).");
        }


        /// <summary>
        /// [MasterClient Only] Applies the damage interrupt logic if enabled and applicable.
        /// Sends an RPC to inform clients. State changes synced via IPunObservable.
        /// </summary>
        public void ApplyDamageInterrupt()
        {
            if (pv == null || !pv.IsMine) return; // Only Master can interrupt

            if (isOutOfAmmoPermanently || !ConfigEnableDamageInterrupt) return;

            if (isReloading) // Check Master's authoritative state
            {
                isReloading = false; // Update state locally on Master
                currentReloadTimer = 0f;
                damageInterruptDelayTimer = ConfigDamageInterruptDelay; // Start interrupt timer on Master

                Plugin.LogInfoF($"Hunter {GetHunterName()} hurt during reload (Master)! Reload cancelled, starting {ConfigDamageInterruptDelay:F1}s interrupt delay.");

                // Send RPC to clients to sync the interrupt delay timer start
                pv.RPC(nameof(InterruptReloadRPC), RpcTarget.Others, ConfigDamageInterruptDelay);
            }
            // Potential future extension: Interrupt minigun burst here as well
        }

        /// <summary>
        /// [Called via RPC on Clients] Updates client's local interrupt timer for prediction.
        /// </summary>
        [PunRPC]
        private void InterruptReloadRPC(float interruptDuration)
        {
            if (pv == null || pv.IsMine) return; // Ignore if Master or no PV

            // Similar to StartReloadRPC, this helps sync the timer start.
            // The core state change (isReloading=false, isInterruptedInternal=true) comes via OnPhotonSerializeView.
            // This ensures the client's local timer reflects the interrupt duration.
            damageInterruptDelayTimer = interruptDuration;
            isInterruptedInternal = true; // Update derived flag locally immediately

            // Reset reload timer locally if needed, though OnPhotonSerializeView should handle isReloading=false
            if (!isReloading) currentReloadTimer = 0f;

            Plugin.LogDebugF($"Hunter {GetHunterName()} received InterruptReloadRPC (Delay: {interruptDuration:F1}s).");
        }

        /// <summary>
        /// [MasterClient Only] Sets the initial synchronized state (Skill, Ammo) for this Hunter.
        /// Sends a buffered RPC so late-joining players also receive this state.
        /// </summary>
        public void NetworkInitialize(ReloadSkill skill, int totalAmmo)
        {
            if (pv == null || !pv.IsMine) return;

            // Set initial state locally on Master
            this.currentSkill = skill;
            this.currentTotalAmmo = totalAmmo;
            this.isOutOfAmmoPermanently = false;
            this.isReloading = false;
            this.isMinigunBurstActive = false;
            this.damageInterruptDelayTimer = 0f;
            this.isInterruptedInternal = false;

            // Send buffered RPC to set initial state on all other clients (including future joiners)
            pv.RPC(nameof(SyncInitialStateRPC), RpcTarget.OthersBuffered, (int)skill, totalAmmo);
            Plugin.LogInfoF($"Hunter {GetHunterName()} initialized (Master) - Skill: {skill}, Ammo: {totalAmmo}");
        }

        /// <summary>
        /// [Called via RPC on Clients, including late joiners] Sets the initial synced state.
        /// </summary>
        [PunRPC]
        private void SyncInitialStateRPC(int skillIndex, int totalAmmo)
        {
            if (pv == null || pv.IsMine) return; // Master already initialized

            this.currentSkill = (ReloadSkill)skillIndex;
            this.currentTotalAmmo = totalAmmo;
            // Reset other states to default initial values
            this.isOutOfAmmoPermanently = false;
            this.isReloading = false;
            this.isMinigunBurstActive = false;
            this.damageInterruptDelayTimer = 0f;
            this.isInterruptedInternal = false;
            Plugin.LogInfoF($"Hunter {GetHunterName()} received SyncInitialStateRPC - Skill: {currentSkill}, Ammo: {totalAmmo}");
        }

        /// <summary> [MasterClient Only] Initializes the state for a minigun burst. Synced via IPunObservable. </summary>
        public void InitializeMinigunBurst()
        {
            if (pv == null || !pv.IsMine) return;

            if (!isMinigunBurstActive)
            {
                isMinigunBurstActive = true; // Master sets state
                minigunShotsRemaining = ConfigMinigunShots;
                minigunShotTimer = 0f; // Fire first shot immediately on Master's next check
                Plugin.LogInfoF($"Hunter {GetHunterName()} initializing minigun burst (Master) ({minigunShotsRemaining} shots).");
                // isMinigunBurstActive flag is synced via OnPhotonSerializeView
            }
        }

        /// <summary> [MasterClient Only] Cleans up the minigun burst state. Synced via IPunObservable. </summary>
        public void EndMinigunBurst()
        {
            if (pv == null || !pv.IsMine) return;

            if (isMinigunBurstActive)
            {
                isMinigunBurstActive = false; // Master sets state
                minigunShotsRemaining = 0;
                minigunShotTimer = 0f;
                Plugin.LogInfoF($"Hunter {GetHunterName()} minigun burst ended/cancelled (Master).");
                // isMinigunBurstActive flag change is synced via OnPhotonSerializeView
            }
        }


        // --- Helper methods ---
        /// <summary> Gets the remaining reload time based on the local timer. </summary>
        public float GetRemainingReloadTime() { return !isReloading ? 0f : currentReloadTimer; }

        /// <summary> [MasterClient Only] Helper to stop current actions like reloading or minigun burst. </summary>
        private void CancelActions()
        {
            if (pv == null || !pv.IsMine) return;
            isReloading = false;
            currentReloadTimer = 0f;
            EndMinigunBurst(); // Ensure minigun stops if active
        }

        /// <summary> Helper to get a clean game object name for logging. </summary>
        private string GetHunterName() => gameObject?.name ?? "Unknown Hunter";
        // --- End Helper methods ---
    }
}