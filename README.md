# Hunter Enhancements Mod for R.E.P.O.

[![Thunderstore Version](https://img.shields.io/thunderstore/v/randomlygenerated/Hunter_Enhancements?color=success&label=Thunderstore)](https://thunderstore.io/c/repo/p/randomlygenerated/Hunter_Enhancements/)

**Current Version:** 1.6.1

This mod aims to make the Hunter enemy in `R.E.P.O.` more dynamic, tactical, and configurable by adding several new mechanics and options.

**➡️ [Download on Thunderstore](https://thunderstore.io/c/repo/p/randomlygenerated/Hunter_Enhancements/) ⬅️**

---

## Features

*   **Limited Ammo & Reload Skills:** Hunters no longer have infinite ammo. They must reload after firing a set number of shots (configurable, defaults likely based on base game behavior if not minigun). Each Hunter spawns with a randomly assigned reload skill (Fast/Medium/Slow), affecting their reload duration.
*   **Minigun Mode:** An optional, configurable rapid-fire burst attack for the Hunter. Rate of fire and shots per burst are adjustable.
*   **Damage Interrupt:** (Optional) Hurting a reloading Hunter cancels their current reload attempt and forces a brief cooldown before they can try reloading again.
*   **Run Away While Reloading:** (Optional) Configure Hunters to actively retreat to a safer position while they are reloading.
*   **Total Ammo Limit:** (Optional) Give Hunters a finite total ammo supply for their entire lifespan. Once depleted, they will attempt to leave the area permanently (compatible with RepoLastStandMod if detected).
*   **Recoil:** (Optional) Adds a chance for the Hunter's shots to deviate randomly from the target, simulating recoil or inaccuracy. Chance and maximum offset are configurable.
*   **Berserker Compatibility:** Includes specific compatibility logic for FNKTLabs' Berserker Enemies mod.
*   **RepoLastStandMod Compatibility:** Includes specific compatibility logic for umbreon222's RepoLastStandMod (prevents permanent despawn if Last Stand is active when total ammo runs out).
*   **Highly Configurable:** Almost all features (reload times, ammo counts, chances, enabling/disabling features) can be tweaked via the BepInEx configuration file.

---

## Installation (Mod Manager Recommended)

1.  **Install BepInEx:** Make sure you have BepInEx installed for `R.E.P.O.`.
2.  **Install Dependencies:** This mod requires only BepInEx.
3.  **Install Mod:** Use a mod manager (like r2modman or Thunderstore Mod Manager) to install `Hunter_Enhancements` directly from Thunderstore.
4.  **Run Game:** Launch the game. This will generate the configuration file.

**Manual Installation:**

1.  Install BepInEx.
2.  Download the mod from Thunderstore.
3.  Extract the contents and place the `HunterEnhancements.dll` file into your `BepInEx/plugins` folder.
4.  Run the game to generate the config file.

---

## Configuration

After running the game once with the mod installed, a configuration file will be generated at:

`BepInEx/config/com.plusblankplus.huntermod.cfg`

Open this file with a text editor to adjust all available settings, including:

*   Enabling/Disabling Minigun Mode, Damage Interrupt, Run Away, Total Ammo Limit, Recoil.
*   Adjusting Minigun shots/delay.
*   Setting reload times for Fast/Medium/Slow skills.
*   Configuring skill probability weights.
*   Setting the Damage Interrupt cooldown duration.
*   Defining the Total Ammo count.
*   Configuring Recoil chance and maximum offset.
*   Toggling different levels of logging (Debug, Info, Warning, Error).

---

## Dependencies

*   **BepInEx:** The modding framework required to load the mod.

## Compatibility

*   **Berserker Enemies:** Includes specific compatibility logic. Should work alongside it.
*   **RepoLastStandMod:** Includes specific compatibility logic (soft dependency). If detected, Hunters won't despawn permanently when out of total ammo if Last Stand is active.
*   **Other Hunter AI Mods:** May conflict with other mods that significantly alter the Hunter's core AI, state machine, or shooting behavior. Load order or specific patch interactions might cause issues. Please report any incompatibilities found!

---

## Source Code & License

This repository contains the full source code for the Hunter Enhancements mod.

*   **License:** This project is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for details. You are free to use, modify, and distribute the code as long as you include the original copyright and license notice.
*   **Why Open Source?** To allow transparency, enable community contributions (bug fixes, features), facilitate learning, and help ensure the mod can be maintained or updated by others if needed.

---

## Contributing

Contributions are welcome!

*   **Bug Reports:** If you find a bug, please check if it's already reported in the [Issues](https://github.com/PlusBlankPlus/REPO_Hunter_Enhancement/issues) tab. If not, create a new issue with clear steps to reproduce the problem, expected behavior, and actual behavior. Include your log file (`BepInEx/LogOutput.log`) and mention relevant config settings or conflicting mods if possible.
*   **Feature Suggestions:** Use the [Issues](https://github.com/PlusBlankPlus/REPO_Hunter_Enhancement/issues) tab to suggest new features or improvements.
*   **Code Contributions:** Feel free to fork the repository, make your changes, and submit a Pull Request. Please try to follow the existing code style and document your changes clearly.

---

## Credits

*   **PlusBlankPlus:** Mod Author
*   **R.E.P.O. Developers:** For creating the base game.
*   **BepInEx Team:** For the modding framework.
*   **HarmonyLib (pardeike):** For the patching library.
*   **Whiteline7:** For extensive testing and feedback!.
*   **Photon Unity Networking (PUN 2):** For the multiplayer framework used by the game.

---

Thank you for using Hunter Enhancements!
