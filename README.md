# J2ME 360 Gamepad

Xbox 360 / DirectInput controller → keyboard input mapper designed for J2ME emulators (KEmulator). Translates physical controller input into keyboard key presses that KEmulator understands, with an OSD overlay, reusable profiles, and three DPAD modes.

<img alt="Overview" src="https://github.com/user-attachments/assets/030f3ced-5aae-45ef-a623-0342d8fa2c02" />

---

## Table of Contents

- [Architecture](#architecture)
- [NativeMethods Layer](#nativemethods-layer)
- [Data Models](#data-models)
  - [AppSettings](#appsettingscs)
  - [ComboSettings](#combosettingscs)
- [Services Layer](#services-layer)
  - [DllSafety](#dllsafety)
  - [KeyboardInjector](#keyboardinjector)
  - [ProfileManager](#profilemanager)
  - [GamepadPoller](#gamepadpoller)
  - [ControllerWatchdog](#controllerwatchdog)
  - [DirectInputReader](#directinputreader)
  - [LogHelper](#loghelper)
- [UI Layer](#ui-layer)
  - [App](#app)
  - [MainWindow](#mainwindow)
  - [OverlayWindow](#overlaywindow)
- [Configuration Files](#configuration-files)
- **[DPAD Modes GIF](#dpad-modes)**
- [Button Mappings](#button-mappings)
- **[OSD Behavior GIF](#osd-behavior)**
- [DInput Support](#dinput-support)
- [Profile System](#profile-system)
- [Keys Reference Window](#keys-reference-window)
- [Build Instructions](#build-instructions)
- [Dependencies](#dependencies)

---

## Architecture

```
NativeMethods/         → Raw P/Invoke wrappers
  XInput.cs            → xinput1_3.dll (gamepad state / vibration)
  KeyboardInput.cs     → user32.dll (SendInput key injection + mouse middle button)
  JoyInput.cs          → winmm.dll (legacy joystick API, DInput fallback)

Models/                → Data containers
  GamepadState.cs      → Live button states, DPadKey resolution, DInput decoding
  KeyMapProfile.cs     → JSON-serializable profile with mappings + combo data
  DInputMapping.cs     → DInput action-to-button-index mapping, persisted as JSON
  DInputCalibration.cs → D-Pad analog calibration data (center + 8 directions)
  AppSettings.cs       → Centralized JSON settings (migrates legacy txt files)
  ComboSettings.cs     → Combo action macros & executable launchers, persisted as JSON

Services/              → Core logic
  KeyboardInjector.cs  → Track held keys, press/release via SendInput (thread-safe, with mouse btn)
  ProfileManager.cs    → Thread-safe CRUD, JSON storage, FileSystemWatcher
  GamepadPoller.cs     → 8ms poll loop, XInput↔WinMM alternate detection, DPAD state machine, combo system
  ControllerWatchdog.cs → 1s XInput connection check (independent of poller)
  DirectInputReader.cs → (dead code) Raw P/Invoke DirectInput8 via dinput8.dll — WinMM used instead
  DllSafety.cs         → Harden native DLL loading at startup (SetDllDirectory + pre-load)
  LogHelper.cs         → File-based logging to %APPDATA%\J2MEGamepad\crash.log (with repeat suppression)

Windows/               → WPF UI
  OverlayWindow.xaml/.cs  → Transparent click-through OSD overlay (with combo modifier indicator)
  MainWindow.xaml/.cs     → Main GUI (profile editor, key/combo capture, tray icon, DInput controls)
  App.xaml/.cs            → Entry point, single-instance Mutex, startup watchdog, start-minimized
```

The application polls at **8ms intervals (~125Hz)** alternating XInput and WinMM detection each tick. XInput is preferred (via `xinput1_3.dll`); on failure it falls to WinMM (`joyGetPosEx` / `winmm.dll`). DirectInput8 COM interop (`dinput8.dll`) is compiled in but disabled at runtime (0x80004002 on many Windows 11 systems). Auto-detects controller type on connect and upgrades WinMM stubs to XInput when available.

---

## NativeMethods Layer

### XInput.cs

P/Invoke wrapper for `xinput1_3.dll` (Windows XP-compatible). Uses raw DLL imports with no NuGet dependency.

**Structs**

| Struct            | Fields                                                                                      |
| ----------------- | ------------------------------------------------------------------------------------------- |
| `XInputState`     | `dwPacketNumber`, `Gamepad` (XInputGamepad)                                                 |
| `XInputGamepad`   | `wButtons`, `bLeftTrigger`, `bRightTrigger`, `sThumbLX`, `sThumbLY`, `sThumbRX`, `sThumbRY` |
| `XInputVibration` | `wLeftMotorSpeed`, `wRightMotorSpeed`                                                       |

**Constants** (`XInputButtons`)

Button flag bitmasks matching the XINPUT_GAMEPAD_* constants: `DPAD_UP` (0x0001), `DPAD_DOWN` (0x0002), `DPAD_LEFT` (0x0004), `DPAD_RIGHT` (0x0008), `START` (0x0010), `BACK` (0x0020), `LEFT_THUMB` (0x0040), `RIGHT_THUMB` (0x0080), `LEFT_SHOULDER` (0x0100), `RIGHT_SHOULDER` (0x0200), `A` (0x1000), `B` (0x2000), `X` (0x4000), `Y` (0x8000).

**Methods**

| Method                                                   | Returns | Description                                                                               |
| -------------------------------------------------------- | ------- | ----------------------------------------------------------------------------------------- |
| `GetState(int userIndex, out XInputState state)`         | `uint`  | Calls XInputGetState. Returns `ERROR_SUCCESS` (0) or `ERROR_DEVICE_NOT_CONNECTED` (1167). |
| `SetState(int userIndex, ref XInputVibration vibration)` | `uint`  | Calls XInputSetState (rumble). Currently unused by the application.                       |

### KeyboardInput.cs

P/Invoke wrapper for `user32.dll` `SendInput` for keyboard key injection.

**Internal Structures**

- `INPUT` — union wrapper with `type` field and `InputUnion`
- `InputUnion` — explicit-layout union of `MOUSEINPUT`, `KEYBDINPUT`, `HARDWAREINPUT`
- `KEYBDINPUT` — `wVk`, `wScan`, `dwFlags`, `time`, `dwExtraInfo`

**Methods**

| Method                               | Description                                            |
| ------------------------------------ | ------------------------------------------------------ |
| `SendKeyDown(ushort virtualKeyCode)` | Sends a key-down event for the given virtual key code. |
| `SendKeyUp(ushort virtualKeyCode)`   | Sends a key-up event for the given virtual key code.   |
| `SendMouseDown()`                    | Sends a middle-mouse-button-down event.               |
| `SendMouseUp()`                      | Sends a middle-mouse-button-up event.                 |

`SendKey`/`SendMouse` use `ThreadStatic` input buffer and call `SendInput` with a single-element `INPUT` array. VK code 0x04 (VK_MBUTTON) is routed to mouse events by `KeyboardInjector`.

### JoyInput.cs

P/Invoke wrapper for `winmm.dll` legacy joystick API. Used as a fallback when DirectInput8 COM interop is unavailable.

**Struct**

- `JOYINFOEX` — `dwSize`, `dwFlags`, `dwXpos`/`dwYpos`/`dwZpos`/`dwRpos`/`dwUpos`/`dwVpos`, `dwButtons`, `dwButtonNumber`, `dwPOV` (hat switch angle)

**Constants**

- `JOYERR_NOERROR` (0), `JOYERR_UNPLUGGED` (165)
- `JOY_RETURNALL` (0xFF) — flags for requesting all joystick data
- `JOY_RETURNPOVCTS` (0x200) — POV continuous flag for proper centered reporting
- `POV_CENTERED` (-1 / `0xFFFF`) — centered POV hat (`POV_CENTERED_U` for unsigned form)

**Methods**

| Method                                    | Description                                           |
| ----------------------------------------- | ----------------------------------------------------- |
| `GetNumDevices()`                         | Returns number of joystick devices (`joyGetNumDevs`). |
| `GetPosEx(int uJoyID, ref JOYINFOEX pji)` | Gets full joystick state (`joyGetPosEx`).             |

---

## Data Models

### GamepadState.cs

Value-type struct holding boolean state for every controller button plus DPad directions.

**Properties**: `A`, `B`, `X`, `Y`, `LB`, `RB`, `LT`, `RT`, `Start`, `Back`, `LeftThumb`, `RightThumb`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`.

**Methods**

| Method                                                                                         | Description                                                                                                                                                                                                                                                                                              |
| ---------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UpdateFromButtons(ushort buttons)`                                                            | Decodes the XInput `wButtons` bitmask into individual boolean properties. LT/RT are set to `false` here — use `UpdateTriggers`.                                                                                                                                                                          |
| `UpdateFromDInput(uint buttons, int pov, int x, int y, int z, int r, DInputCalibration?)`      | Decodes DInput button bitmask (hardcoded indices: X=0, A=1, B=2, Y=3, LB=4, RB=5, LT=6, RT=7, Back=8, Start=9, LeftThumb=10, RightThumb=11) and decodes D-Pad from analog axes with calibration.                                                                                                          |
| `DecodeDInputDpad(int pov, int x, int y, int z, int r, DInputCalibration?)`                   | Private. Three-tier D-Pad decoding: POV hat (priority) → X/Y analog stick → Z/R axis (with cheap-gamepad guard). POV=0 falls through to analog axes (WinMM quirk).                                                                                                                                      |
| `ReadAxisDirection(double rawX, double rawY, double cx, double cy, double deadzone, DInputCalibration?)` | Private. Normalizes analog axes to -1..1 double, computes angle and magnitude, maps to 8-direction sector. If user calibration exists, delegates to calibrated path.                                                                                                                            |
| `ReadAxisDirectionCalibrated(double rawX, double rawY, double cx, double cy, DInputCalibration)` | Private. Uses user-recorded calibration angles (nearest-neighbor to 8 recorded directions).                                                                                                                                                                                                              |
| `UpdateTriggers(byte leftTrigger, byte rightTrigger, byte threshold = 50)`                     | Sets `LT`/`RT` based on analog trigger values >= threshold (digital conversion).                                                                                                                                                                                                                         |
| `GetDPadKey()`                                                                                 | Returns a `DPadKey` enum (`None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`) from the current DPad boolean flags. Diagonal detection requires both axes simultaneously.                                                                                                |

**Enums**

- `DPadMode` — `Pad` (arrow keys), `Keypad` (numpad 2/4/6/8), `PadDiagonal` (arrows + diagonal numpad)
- `DPadKey` — `None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`

### DInputMapping.cs

Maps action names to DInput button indices. Serializable as JSON.

**Default Mappings**

| Action                 | Button Index |
| ---------------------- |:------------:|
| X                      | 0            |
| A                      | 1            |
| B                      | 2            |
| Y                      | 3            |
| LB                     | 4            |
| RB                     | 5            |
| LT                     | 6            |
| RT                     | 7            |
| RightThumb             | 11           |
| Start                  | 9            |
| Back                   | 8            |
| LeftThumb              | 10           |
| DPadUp/Down/Left/Right | 12–15        |

**Methods**

| Method              | Description                                                     |
| ------------------- | --------------------------------------------------------------- |
| `BuildReverseMap()` | Returns `Dictionary<int, string>` (button index → action name). |
| `ToJson()`          | Serializes to indented JSON.                                    |
| `FromJson(string)`  | Deserializes from JSON.                                         |
| `GetFilePath()`     | Returns `%APPDATA%\J2MEGamepad\dinput_mapping.json` path.       |
| `Save()`            | Writes to file.                                                  |
| `Load()`            | Loads from file or returns defaults.                            |

### AppSettings.cs

Centralized application settings stored as a single JSON file (`%APPDATA%\J2MEGamepad\app_settings.json`).

**Properties**: `Version`, `StartMinimized`, `TerminateIfKemulatorClosed`, `TerminateWarningHidden`, `DiagonalDelayMs`, `DirectionalDelayMs`, `DiagonalDelayHold` (default true), `DiagonalDelayPerProfile`, `BackCycles`, `SkipDefault`, `ComboPerProfile`, `DisableComboModifierOSD`, `ComboConfirmationHold`, `FirstRunCompleted`, `CustomWarningHidden`, `KeysFontSize` (18), `KeysWindowWidth` (593), `KeysWindowHeight` (682).

**Migration**: On first load (version 0), migrates from legacy individual txt files (`diagdelay.txt`, `directional_delay.txt`, `diaghold.txt`, `diagperprofile.txt`, `comboperprofile.txt`, `disable_combo_modifier_osd.txt`, `terminate.txt`, `terminate_warning_hidden.txt`, `start_minimized.txt`, `firstrun.txt`, `custom_warning_hidden.txt`, `keysfont.txt`, `keyssize.txt`) and saves as v1 JSON.

**Methods**: `ToJson()`, `FromJson()`, `GetFilePath()`, `Save()`, `Load()`

### ComboSettings.cs

Stores combo macro configurations (`%APPDATA%\J2MEGamepad\combo_settings.json`). Used when "Combo per profile" is disabled.

**Properties**: `Actions` (`Dictionary<string, List<ushort>>`), `OSDNames` (`Dictionary<string, string>`), `ExecPaths` (`Dictionary<string, string>`).

**Methods**: `ToJson()`, `FromJson()`, `GetFilePath()`, `Save()`, `Load()`

### DInputCalibration.cs

Stores calibration data for analog D-Pad axes. Serializable as JSON.

**Properties**: `DeadzonePercent` (default 40), `CenterX`/`CenterY` (default 32767), plus X/Y for 8 directional points (Up, Down, Left, Right, UpLeft, UpRight, DownLeft, DownRight) with raw defaults (0/65535 extremes and 32767 centers). `HasUserCalibration` (computed) — returns `true` when any directional point differs from the factory defaults, used by `GamepadState.ReadAxisDirection` to select the calibrated nearest-neighbor path.

**Methods**

| Method             | Description                                              |
| ------------------ | -------------------------------------------------------- |
| `GetFilePath()`     | Returns `%APPDATA%\J2MEGamepad\dpad_calibration.json` path.|
| `ToJson()`         | Serializes to indented JSON.                             |
| `FromJson(string)` | Deserializes from JSON.                                  |
| `Save()`           | Writes to file.                                           |
| `Load()`           | Loads from file or returns defaults.                     |

### KeyMapProfile.cs

JSON-serializable profile class.

**Properties**

| Property                     | Type                         | Default                    | Description                                   |
| ---------------------------- | ---------------------------- | -------------------------- | --------------------------------------------- |
| `Name`                       | `string`                     | `"Default"`                | Profile name, max 18 characters               |
| `Mappings`                   | `Dictionary<string, ushort>` | Default layout (see below) | Maps button names to virtual key codes        |
| `DiagonalDelayMs`            | `int`                        | 0                          | Per-profile diagonal delay in milliseconds    |
| `DirectionalDelayMs`         | `int`                        | 0                          | Per-profile directional delay in milliseconds |
| `DiagonalDelayHoldCardinals` | `bool`                       | false                      | Per-profile hold-cardinals-during-delay flag  |

**Default Mappings**

| Button     | VK Code | Key                                           |
| ---------- | ------- | --------------------------------------------- |
| Y          | 0x60    | Numpad 0                                      |
| X          | 0x70    | F1                                            |
| A          | 0x72    | F3 (PAD mode) / Numpad 5 (KEYPAD/DIAGONAL)    |
| B          | 0x71    | F2                                            |
| LB         | 0x6A    | Numpad *                                      |
| RB         | 0x6F    | Numpad /                                      |
| LT         | 0x70    | F1                                            |
| RT         | 0x71    | F2                                            |
| RightThumb | 0x72    | F3                                            |

**Note**: Back, Start, LeftThumb are system buttons (not in Mappings). LeftThumb can be remapped in non-Default profiles — if unmapped, it toggles BackCycles.

**Additional Properties**

| Property                               | Type                         | Description                                               |
| -------------------------------------- | ---------------------------- | --------------------------------------------------------- |
| `ComboActions`                         | `Dictionary<string, List<ushort>>` | Combo modifier + face-button → key sequence         |
| `ComboOSDNames`                        | `Dictionary<string, string>` | Custom OSD display names for combos                       |
| `ComboExecPaths`                       | `Dictionary<string, string>` | Executable paths for combo launches                       |

**Static Key Constants**: `KeyEnter` (0x0D), `KeyF3` (0x72), `KeyNumpad0`-`KeyNumpad9` (0x60-0x69), `KeyUp`/`KeyDown`/`KeyLeft`/`KeyRight` (0x26-0x28), `KeyMultiply` (0x6A), `KeyDivide` (0x6F), `KeyF1`/`KeyF2` (0x70/0x71). These are `[JsonIgnore]` — not serialized.

**Methods**

| Method                  | Returns          | Description                                                      |
| ----------------------- | ---------------- | ---------------------------------------------------------------- |
| `ToJson()`              | `string`         | Serializes this profile to indented JSON via `Newtonsoft.Json`. |
| `FromJson(string json)` | `KeyMapProfile?` | Deserializes JSON to a profile, or null on failure.              |
| `GetValueOrDefault(string key, ushort defaultValue)` | `ushort` | Safe lookup helper. |

---

## Services Layer

### KeyboardInjector

Tracks which keys are currently held down and manages press/release via SendInput. Thread-safe (all mutations under lock).

**Fields**

- `_keysDown` — `HashSet<ushort>` of currently-pressed virtual key codes
- `_lock` — synchronizes all access
- `_disposed` — guard flag

**Special behavior**: VK code `0x04` (VK_MBUTTON) is routed to `SendMouseDown()`/`SendMouseUp()` instead of keyboard events, allowing controller buttons to map to middle-mouse-click.

**Methods**

| Method                      | Description                                                                                 |
| --------------------------- | ------------------------------------------------------------------------------------------- |
| `PressKey(ushort vkCode)`   | Sends key-down (or mouse-down for 0x04) only if not already held. Adds to `_keysDown`.      |
| `ReleaseKey(ushort vkCode)` | Sends key-up (or mouse-up for 0x04) only if tracked as held. Removes from `_keysDown`.      |
| `ReleaseAll()`              | Releases every tracked key and clears the set. Used on disconnect and poller stop.          |
| `Dispose()`                 | Calls `ReleaseAll()`, sets disposed flag.                                                    |

### ProfileManager

Thread-safe CRUD for profiles stored as JSON files in `%AppData%\J2MEGamepad\profiles\`.

**Fields**

- `_profilesDir` — `%AppData%\J2MEGamepad\profiles\`
- `_watcher` — `FileSystemWatcher` monitoring `*.json` changes
- `_lock` — synchronizes all profile list access
- `_isReloading` — guard flag preventing FileSystemWatcher feedback loops during `SaveToFile`
- `_profiles` — internal `List<KeyMapProfile>` backing field

**Properties**

| Property              | Description                                                                                                                            |
| --------------------- | -------------------------------------------------------------------------------------------------------------------------------------- |
| `Profiles`            | Returns a **copy** of the internal list under lock (prevents collection-modified exceptions when UI enumerates while watcher updates). |
| `CurrentProfileIndex` | Index of the currently selected profile.                                                                                               |
| `CurrentProfile`      | Returns `_profiles[CurrentProfileIndex]` under lock, or a fresh `KeyMapProfile()` if list is empty.                                    |
| `IsDefaultProfile`    | True if current profile's `Name` is `"Default"`.                                                                                       |
| `UserProfileCount`    | Count of profiles whose `Name` is not `"Default"`.                                                                                     |

**Events**

| Event             | Description                                          |
| ----------------- | ---------------------------------------------------- |
| `ProfilesChanged` | Raised after external file changes trigger a reload. |

**Constructor**: Creates the profiles directory if missing, calls `LoadProfiles()`, starts the `FileSystemWatcher`.

**Methods**

| Method                                            | Description                                                                                                                                                                                              |
| ------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `LoadProfiles()`                                  | Clears internal list, always adds `"Default"` first, then loads `*.json` files from disk (skipping `"Default"`). Restores previous profile index by name if possible.                                    |
| `SaveProfile(KeyMapProfile)`                      | Removes any existing profile with the same name, adds the new one, saves to disk.                                                                                                                        |
| `DeleteProfile(string)`                           | Refuses to delete `"Default"`. Removes from list and deletes JSON file.                                                                                                                                 |
| `RenameProfile(string old, string new)`           | Refuses to rename to/from `"Default"`. Deletes old JSON, renames, saves.                                                                                                                                |
| `SetCurrentProfileByName(string)`                 | Finds profile index by name and sets `CurrentProfileIndex` (or returns false).                                                                                                                           |
| `CycleForward(bool skipDefault)`                  | Moves `CurrentProfileIndex` forward by 1 (wrapping). Optionally skips `"Default"`.                                                                                                                       |
| `CycleBackward(bool skipDefault)`                 | Same as forward but in reverse direction.                                                                                                                                                                |
| `ExportProfile(string)`                           | Returns the profile's JSON string.                                                                                                                                                                       |
| `ImportProfile(string json, string? renameTo)`    | Deserializes JSON, optionally overrides the profile name, saves.                                                                                                                                         |
| `GetNextAvailableName(string)`                    | Finds the first unused name in the pattern `"{baseName} 1"`, `"{baseName} 2"`, etc.                                                                                                                      |
| `Dispose()`                                       | Disposes the FileSystemWatcher.                                                                                                                                                                          |

**File Format**: Each profile is saved as `{SanitizedName}.json` in `%AppData%\J2MEGamepad\profiles\`. Filename sanitization replaces invalid path characters with `_`. Import deduplication appends ` (1)`, ` (2)` etc.

### GamepadPoller

Core polling engine. Runs at 8ms (125 Hz) via `System.Timers.Timer`. Alternates XInput and WinMM detection each tick. XInput preferred (`xinput1_3.dll`); WinMM fallback (`joyGetPosEx`). DirectInput8 COM is disabled at runtime.

**ControllerType enum**: `None`, `XInput`, `DInput` — auto-detected during polling loop.

**Key Architectural Notes**

- XInput detection and active polling run on alternating ticks with DInput detection. When a WinMM device is found but XInput also responds, the poller upgrades to XInput mode.
- DirectInput8 (`dinput8.dll`) COM interop is compiled in `DirectInputReader.cs` but **never called at runtime** — on many Windows 11 systems `DirectInput8Create`/`CoCreateInstance` fail with `0x80004002` (E_NOINTERFACE). WinMM (`winmm.dll` / `joyGetPosEx`) is the only active DInput path, confirmed working from Windows XP through 11.
- WinMM detection is cached: when no controller is detected, scans are skipped for 500ms.
- Combo modifier system: holding `RB+LB` or `RT+LT` simultaneously enables combo actions. Face buttons (Y/X/A/B) fire macro key sequences. Start/Back fire executable launches. Cached button indices ensure O(1) checks.

**Fields** (selected)

- `_timer` — 8ms interval timer
- `_controllerType` — current type: `None`, `XInput`, or `DInput`
- `_tryXInputNext` — alternates XInput/WinMM detection each tick when no controller
- `_*WasPressed` / `_current*HeldKey` — per-button edge tracking for A/B/X/Y/LB/RB/LT/RT/Back/Start/LeftThumb/RightThumb
- `_activeComboModifier` — combo state machine (`None`/`RbLb`/`RtLt`)
- `_comboHeldKeys` / `_comboWasPressed` — combo key tracking
- `_dinputMapping`, `_dinputCalibration`, `_comboSettings` — DInput+combo config
- `_dinputButtonToAction` — reverse mapping (button index → action name)
- `_dinputWasPressed`, `_dinputHeldKeys` — DInput edge tracking
- Cached DInput indices: `_dinputBackIdx`, `_dinputStartIdx`, `_dinputLeftThumbIdx`, `_dinputLBIdx`, `_dinputRBIdx`, `_dinputLTIdx`, `_dinputRTIdx`
- DPAD state machine fields: diagonal delay, diagonal crosstalk suppression, diagonal-to-cardinal suppression, cardinal delay

**Properties**

| Property                     | Description                                                                                                        |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| `CurrentDPadMode`            | Gets/sets DPAD mode (`Pad`/`Keypad`/`PadDiagonal`). Setting triggers `ForceReleaseDPad()` and fires `ModeChanged`. |
| `BackCyclesYxab`             | When true, View/Back cycles profiles backward instead of cycling DPAD modes.                                       |
| `SkipDefault`                | When true, profile cycling skips the `"Default"` profile.                                                          |
| `DiagonalDelayMs`            | Diagonal delay in ms (0 = off). Setting to 0 resets the pending diagonal state.                                    |
| `DiagonalDelayHoldCardinals` | If true, cardinal arrow/numpad keys are held during the delay window (no output if false).                         |
| `DirectionalDelayMs`         | Directional delay for crosstalk suppression (0 = off).                                                             |
| `IsConnected`                | Current connection state.                                                                                          |
| `IsDInputMode`               | True when controller type is `DInput`.                                                                             |
| `IsRemapping`                | When true, DInput button presses fire `DInputButtonPressed` instead of normal processing.                          |
| `DInputCalibration`          | Exposes current calibration data.                                                                                  |
| `DirectInputReader`          | Exposes the (unused) DirectInput8 reader instance.                                                                 |
| `DirectInputWindowHandle`    | HWND for SetCooperativeLevel (required on Windows XP).                                                             |
| `ComboConfirmationHold`      | When true, combo face/exec buttons require a 1-second hold before firing. During hold the OSD shows the combo name in green; releasing early cancels the action. |

**Events**

| Event                  | Args     | Description                                                                  |
| ---------------------- | -------- | ---------------------------------------------------------------------------- |
| `ModeChanged`         | `string` | Fired on DPAD mode change (mode name) or profile switch (`"YXAB: {name}"`).  |
| `ConnectionChanged`   | `bool`   | Fired when controller connects (true) or disconnects (false).                |
| `BackCyclesToggled`   | `bool`   | Fired when Left Stick Button toggles the BackCyclesYxab flag.                |
| `DInputButtonPressed` | `int`    | Fired during DInput remapping when a button is pressed (button index).       |
| `DInputModeChanged`   | `string` | Fired when DInput mode is first entered.                                     |
| `ComboTriggered`      | `string` | Fired when a combo action is triggered (OSD display name).                   |
| `ComboModifierActive` | `string` | Fired when RB+LB or RT+LT combo modifier is engaged.                         |
| `ComboModifierInactive` | (none) | Fired when combo modifier is released.                                       |
| `ComboConfirmationPending` | `string` | Fired when combo confirmation hold starts (OSD display name).               |
| `ComboConfirmationCancelled` | (none) | Fired when combo confirmation is cancelled (face button or modifier released before timer). |

**Methods**

| Method                                                                                          | Description                                                                                                                                                                                  |
| ----------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Start()`/`Stop()`/`Dispose()`                                                                  | Lifecycle: start/stop timer, release all keys.                                                                                                                                               |
| `Poll(...)`                                                                                     | **Internal.** Called every 8ms. When no controller: alternates `PollXInputDetection()`/`PollDInputDetection()` each tick. When active: calls `PollXInputActive()` or `PollDInputActive()`.   |
| `PollXInputActive()`/`PollXInputDetection()`                                                    | **Internal.** XInput GetState at user index 0. On success, processes state. On disconnect, falls back to None and tries DInput next tick.                                                     |
| `ProcessXInputState(XInputState)`                                                               | **Internal.** Builds GamepadState, processes DPAD, then checks combo modifiers (RB+LB / RT+LT). If combo active, routes to `ProcessComboXInput`; otherwise normal action/mode-switch path.   |
| `PollDInput()`                                                                                  | **Internal.** WinMM-only path: enumerates `joyGetPosEx` for devices 0-15. On device found: if XInput also responds, upgrades to XInput mode. Otherwise loads config and enters DInput mode.  |
| `LoadDInputConfig()`                                                                            | **Internal.** Loads DInput mapping, calibration, and combo settings from disk. Does NOT touch DirectInput8.                                                                                  |
| `MakeStateFromJoyInfo(JOYINFOEX)`                                                               | **Internal.** Builds `GamepadState` from WinMM state via `UpdateFromDInput`.                                                                                                                 |
| `ProcessDInput(GamepadState, JOYINFOEX?)`                                                       | **Internal.** Applies button-mapped D-Pad overrides, calls `ProcessDPad`, handles remapping capture, then processes combo or normal actions.                                                 |
| `ProcessDInputCombo(uint buttons, string prefix)`                                               | **Internal.** DInput combo modifier logic: caches modifier type, releases shoulder keys, routes face/exec buttons to `TriggerCombo`.                                                          |
| `ProcessDInputActions(uint buttons)`                                                            | **Internal.** Iterates `_dinputButtonToAction` (skipping system + DPad actions), tracks edge transitions, sends key down/up via `KeyboardInjector`.                                           |
| `ProcessDInputModeSwitches(uint buttons)`                                                       | **Internal.** Edge-detection for Back/Start/LeftThumb for mode/profile cycling.                                                                                                              |
| `GetVKForDInputAction(string action)`                                                           | **Internal.** Maps DInput action names to virtual key codes. Default profile returns hardcoded values; others look up profile Mappings.                                                      |
| `ReloadDInputMapping()`/`ReloadCalibration()`/`ReloadComboSettings()`                           | Reloads DInput mapping, calibration, or combo settings from disk.                                                                                                                            |
| `ApplyComboSettings(...)`                                                                       | Applies combo actions/OSD names/exec paths from the UI layer.                                                                                                                                |
| `ProcessDPad(GamepadState)`                                                                     | **Internal.** Full DPAD state machine: diagonal delay → diagonal crosstalk suppression → diagonal-to-cardinal crosstalk suppression → cardinal delay → normal transition.                     |
| `SwapPadKeys(ushort[] newKeys)`                                                                 | **Internal.** Atomically releases all held pad keys then presses the new set.                                                                                                                |
| `GetCardinalKeys(DPadKey)`/`GetKeysForDPad(DPadKey)`                                            | **Internal.** Returns VK code arrays for pad/keypad/diagonal modes. Uses cached statics to avoid per-call allocation.                                                                        |
| `ProcessActionButtons(GamepadState, bool)`/`ProcessShoulderButtons(GamepadState, bool)`         | **Internal.** Hold-button handlers for A/B/X/Y and LB/RB/LT/RT/LeftThumb/RightThumb. LeftThumb only emits key when mapped in non-Default profile.                                            |
| `ProcessModeSwitches(GamepadState)`                                                             | **Internal.** XInput mode switches: Back=DPAD cycle/profile cycle, Start=profile forward, LeftStick=BackCycles toggle.                                                                       |
| `ProcessComboXInput(GamepadState, string, bool)`                                                | **Internal.** XInput combo modifier logic — same pattern as DInput but reads XInput state directly.                                                                                          |
| `TriggerCombo(string comboName, bool isDefault)`                                                | **Internal.** Looks up combo in `_comboSettings`: if exec path found, validates extension (.exe/.bat/.cmd/.com/.ps1), launches via `Process.Start`. Otherwise sends key sequence via injector.|
| `EndComboKeys()`/`EndComboModifier()`                                                           | **Internal.** Releases combo-held keys and resets modifier state.                                                                                                                            |
| `ProcessHoldButton(bool, ref bool, ref ushort, Func<ushort>)`                                   | **Internal.** Hold-button edge handler: press on rising edge, release on falling.                                                                                                            |
| `ProcessButtonEdge(bool, ref bool, Action)`                                                     | **Internal.** Generic tap-button edge handler.                                                                                                                                               |
| `ForceReleaseDPad()`                                                                            | Releases all held pad keys, clears all DPAD state machine state.                                                                                                                             |
| `IsDiagonal(DPadKey)`/`IsSubsetCardinal(DPadKey, DPadKey)`/`GetModeName(DPadMode)`              | **Internal static helpers.**                                                                                                                                                                 |

**DPAD Crosstalk Suppression** — Diagonal crosstalk (cardinal → diagonal flicker) and diagonal-to-cardinal (release one axis early) are handled with either poll-count confirmation (2 consecutive same-state polls) or a configurable directional delay timer (0–300ms). Cardinal delay also prevents brief cardinal pass-through before settling on a diagonal.

**DPAD State Machine + Diagonal Delay** — See the original README structure for detailed state machine steps; the behavior is identical for both XInput and DInput sources.

### ControllerWatchdog

Separate 1-second timer for XInput connection state monitoring (independent of the poller's 8ms loop).

**Fields**

- `_timer` — 1000ms interval timer
- `_wasConnected` — previous connection state for edge detection

**Properties**

| Property      | Description                      |
| ------------- | -------------------------------- |
| `IsConnected` | Current XInput connection state. |

**Events**

| Event          | Description                                        |
| -------------- | -------------------------------------------------- |
| `Connected`    | Fired on transition from disconnected → connected. |
| `Disconnected` | Fired on transition from connected → disconnected. |

**Methods**

| Method              | Description                                                                      |
| ------------------- | -------------------------------------------------------------------------------- |
| `Start()`           | Runs an immediate connection check, then starts the timer.                       |
| `Stop()`            | Stops the timer.                                                                 |
| `Dispose()`         | Disposes the timer.                                                              |
| `CheckConnection()` | **Internal.** Calls `XInput.GetState(0, ...)` and fires events on state changes. |

### DirectInputReader

Raw P/Invoke DirectInput8 joystick reader via `dinput8.dll`. Uses COM interop with manual vtable resolution (no SharpDX dependency).

**Fields**

- `_diPtr` / `_devicePtr` — DirectInput8 and device COM interface pointers
- `_diVtable` / `_devVtable` — COM vtable pointers for manual method dispatch
- Vtable delegates — `_createDevice`, `_enumDevices`, `_acquire`, `_poll`, `_getDeviceState`, etc.
- `_initPollsToSkip` — counter (3) for driver-quirk garbage data after acquire
- `_stateLock` — synchronizes axis/button reads across threads
- `_buttons` — `bool[128]` button array

**Properties**

| Property                        | Type      | Description                                                    |
| ------------------------------- | --------- | -------------------------------------------------------------- |
| `Available`                     | `bool`    | Whether the device is initialized and ready.                   |
| `WindowHandle`                  | `IntPtr`  | HWND for cooperative level (required on Windows XP).           |
| `X`, `Y`, `Z`, `Rx`, `Ry`, `Rz` | `int`     | Axis positions (thread-safe).                                  |
| `Pov`                           | `int`     | First POV hat angle (-1 if none).                              |
| `Buttons`                       | `bool[]`  | Button array (thread-safe copy).                               |
| `DeviceName`                    | `string`  | Human-readable device name from enumeration.                   |
| `DebugInfo`                     | `string?` | Initialization status / error message.                         |

**Methods**

| Method            | Description                                                                                                                                                                      |
| ----------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Initialize()`    | Loads `dinput8.dll`, tries 3 init strategies (DirectInput8Create W/A → CoCreateInstance COM fallback), enumerates devices (prefers gamepad subtype), sets absolute axis mode.   |
| `Poll()`          | Calls vtable `Poll()` + `GetDeviceState()` into `DIJOYSTATE2` struct. Ignores first 3 polls after acquire. On `DIERR_INPUTLOST`/`DIERR_NOTACQUIRED`, re-acquires and retries.    |
| `GetButtonMask()` | Converts boolean button array to integer bitmask.                                                                                                                                |
| `Dispose()`       | Unacquires device, releases COM interfaces.                                                                                                                                      |

### DllSafety

Startup hardening for native DLL loading. Called from `App.OnStartup`.

**Methods**

| Method                       | Description                                                                                                                                      |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `HardenNativeLoading()`      | Calls `SetDllDirectory("")` to remove current directory from DLL search path. Pre-loads `dinput8.dll`, `xinput1_3.dll`, `winmm.dll` from System32 to prevent DLL preloading attacks. |

### LogHelper

File-based logging service with repeat-suppression. Logs written to `%APPDATA%\J2MEGamepad\crash.log`.

**Methods**

| Method                                               | Description                                                  |
| ---------------------------------------------------- | ------------------------------------------------------------ |
| `Info(string source, string msg)`                    | Appends timestamped info line. Repeats are suppressed (logged as "... repeated Nx"). |
| `Error(string source, string context, Exception ex)` | Appends timestamped error with stack trace.                  |

Logs are written to `%APPDATA%\J2MEGamepad\crash.log`.

---

## UI Layer

### App

Entry point, inherits `Application`.

**Static**: `GlobalKeyboard` — shared `KeyboardInjector` reference for safe disposal on crash/restart.

**Fields**: `s_mutex` (named with GUID `"Local\J2MEGamepad-D4E7A1F0-..."`), `_mainWindow`, `_windowReadyEvent` (ManualResetEvent for 6s startup watchdog).

**Methods**

| Method                     | Description                                                                                                                                                                                                                                                                                                                                                                                                     |
| -------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OnStartup(...)`           | Calls `DllSafety.HardenNativeLoading()`. Hooks `AppDomain.UnhandledException` (logs+exits), `DispatcherUnhandledException` (logs+handles), `ProcessExit`. Creates named Mutex for single-instance. If another instance is running, offers to kill the older instance and retry. Sets process priority to `BelowNormal`. Loads `AppSettings` — if `StartMinimized`, creates MainWindow, initializes, hides to tray; otherwise shows normally. Starts 6s watchdog thread (terminates with error dialog + restart if `OnLoaded` doesn't signal). |
| `SignalWindowReady()`      | Called from `MainWindow.OnLoaded` to signal the startup watchdog.                                                                                                                                                                                                                                                                                                                                               |
| `ReleaseMutexForRestart()` | Disposes the mutex so the restarted instance can acquire it.                                                                                                                                                                                                                                                                                                                                                    |
| `PerformShutdown()`        | Static. Disposes `GlobalKeyboard` and mutex. Guarded by `s_shutdownPerformed` flag to prevent double-execution from both `ProcessExit` and `OnExit`.                                                                                                                                                                                                                                                            |
| `OnExit(...)`              | Calls `PerformShutdown()`.                                                                                                                                                                                                                                                                                                                                                                                      |

### MainWindow

Main configuration window. Fixed-size `ToolWindow`, `NoResize`. Strips minimize/maximize buttons via `SetWindowLong`.

**Constructor**: Initializes all services (`KeyboardInjector`, `ProfileManager`, `GamepadPoller`, `ControllerWatchdog`, `OverlayWindow`), creates `NotifyIcon` tray icon with "Show"/"Exit" context menu. Wires events: `ModeChanged`, `ConnectionChanged`, `BackCyclesToggled`, `DInputModeChanged`, `DInputButtonPressed`, `ComboTriggered`, `ComboModifierActive`, `ComboModifierInactive`, `ProfilesChanged`. Registers `PreviewKeyDown` (Escape cancels DInput remap), `PreviewMouseDown` (click-outside cancels captures), `Deactivated` (cancel remap). Loads tray icon from embedded `icon.ico`.

**Event Handlers and Methods**

| Method                                                                                                             | Trigger                                | Description                                                                                                                                                                                                                                                                                                                                        |
| ------------------------------------------------------------------------------------------------------------------ | -------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Initialize()`                                                                                                     | `OnLoaded` or `App` (start minimized)  | Signals startup watchdog. Registers CTRL+R restart hotkey. Forwards HWND to poller for DInput cooperative level (XP requirement). Loads `AppSettings`: diagonal delay, hold cardinal, per-profile, directional delay, start minimized, terminate-if-kemulator-closed, DInput deadzone, combo settings, BackCycles, SkipDefault, ComboPerProfile, DisableComboModifierOSD. Warns if KEmulator not detected (terminate mode). Calls `ShowFirstRunWarning()`, then starts watchdog and poller. |
| `OnLoaded(...)`                                                                                                    | `Loaded` event                         | Calls `Initialize()`, shows overlay, activates window.                                                                                                                                                                                                                                                                                             |
| `OnClosed(...)`                                                                                                    | `Closed` event                         | Unregisters hotkey. Saves all settings to `AppSettings`. If per-profile diag delay, saves hold-cardinals to profile. Calls `ShutdownCleanup()`.                                                                                                                                                                                                    |
| `ShutdownCleanup()`                                                                                                | Close, CTRL+R restart                  | Stops terminate monitoring, DInput remap animation, disconnect timer. Disposes poller, watchdog, keyboard injector (`App.GlobalKeyboard = null`), profile manager, tray icon, overlay.                                                                                                                                                            |
| `WndProc(...)`                                                                                                     | Window message hook                    | Handles `WM_HOTKEY` (CTRL+R restart).                                                                                                                                                                                                                                                                                                              |
| `RestartApplication()`                                                                                             | CTRL+R                                 | Calls `ShutdownCleanup()`, releases mutex, starts new process instance, calls `Environment.Exit(0)`.                                                                                                                                                                                                                                               |
| `ShowFirstRunWarning()`                                                                                            | Called from `Initialize()`             | Shows KEmulator KeyMap cleanup dialog (or suppressed if `FirstRunCompleted` in `AppSettings`).                                                                                                                                                                                                                                                      |
| `DiagDelaySlider_ValueChanged(...)`                                                                                | Slider                                 | Updates `_poller.DiagonalDelayMs` and text display.                                                                                                                                                                                                                                                                                               |
| `DirDelaySlider_ValueChanged(...)`                                                                                 | Slider                                 | Updates `_poller.DirectionalDelayMs`, text display, hold cardinal checkbox state. When directional delay > 0, forces hold cardinal off and disables the checkbox (restores on return to 0).                                                                                                                                                       |
| `DInputDeadzoneSlider_ValueChanged(...)`                                                                           | Slider                                 | Saves deadzone (clamped 10-80%) to calibration file and triggers poller reload.                                                                                                                                                                                                                                                                    |
| `DiagDelayHold_Changed(...)`                                                                                       | Checkbox                               | Updates `_poller.DiagonalDelayHoldCardinals`. Saves to profile if per-profile mode enabled.                                                                                                                                                                                                                                                         |
| `DiagDelayPerProfile_Changed(...)`                                                                                 | Checkbox                               | Calls `ApplyDiagonalDelayFromProfile()`.                                                                                                                                                                                                                                                                                                           |
| `ApplyDiagonalDelayFromProfile()`                                                                                  | Mode change, profile selection         | If per-profile enabled, reads delay, directional delay, and hold cardinal from current profile and applies all three.                                                                                                                                                                                                                              |
| `ComboPerProfile_Changed(...)`                                                                                     | Checkbox                               | Saves setting to `AppSettings`. Calls `ApplyComboSettingsFromProfile()`.                                                                                                                                                                                                                                                                            |
| `ApplyComboSettingsFromProfile()`                                                                                  | Profile/Mode change, checkbox          | If per-profile combo enabled, copies combo data from profile into poller; otherwise loads from `combo_settings.json`. Updates combo OSD name and exec path fields for currently selected action.                                                                                                                                                   |
| `PersistComboSettings()`                                                                                           | Combo editor changes                   | If per-profile combo, saves to profile and applies; otherwise saves `combo_settings.json` and triggers poller reload.                                                                                                                                                                                                                              |
| `OnProfilesChanged()`                                                                                              | `ProfileManager.ProfilesChanged`       | Dispatcher-invokes `RefreshProfileList()`.                                                                                                                                                                                                                                                                                                         |
| `OnBackCyclesToggled(bool)`                                                                                        | `GamepadPoller.BackCyclesToggled`      | Syncs GUI checkbox, shows OSD.                                                                                                                                                                                                                                                                                                                     |
| `OnControllerConnected()`                                                                                          | `ControllerWatchdog.Connected`         | Hides disconnected overlay, updates status text (green for XInput, orange for DInput).                                                                                                                                                                                                                                                              |
| `OnControllerDisconnected()`                                                                                       | `ControllerWatchdog.Disconnected`      | Hides all DInput UI, stops remap animation, resets capturing state, red "Please connect Xbox/DInput controller".                                                                                                                                                                                                                                   |
| `OnConnectionChanged(bool)`                                                                                        | `GamepadPoller.ConnectionChanged`      | On connect: shows/hides DInput panels, updates status color. On disconnect: hides DInput UI, stops remap animation, resets capture, shows disconnected overlay with 60s timer.                                                                                                                                                                    |
| `OnDInputModeChanged(string)`                                                                                      | `GamepadPoller.DInputModeChanged`      | Shows DInput panels, updates status to orange with device name, shows DInput list items with button indices.                                                                                                                                                                                                                                       |
| `OnDInputButtonPressed(int)`                                                                                       | DInput remap capture                   | Captures button press, checks for conflicts (removes conflicting mapping), updates `dinput_mapping.json`, triggers poller reload. Auto-advances selection to next visible list item for sequential remapping. Escape-click or deactivate cancels.                                                                                                   |
| `OnModeChanged(string)`                                                                                            | `GamepadPoller.ModeChanged`            | Updates DPAD mode text, profile text, applies diagonal delay and combo from profile. Shows OSD: cross-fade for profile switch (`"YXAB:"` prefix), fade-in/out for DPAD mode.                                                                                                                                                                       |
| `OnComboTriggered(string)`                                                                                          | `GamepadPoller.ComboTriggered`         | Shows OSD with combo name (unless `DisableComboModifierOSD`).                                                                                                                                                                                                                                                                                      |
| `OnComboModifierActive(string)` / `OnComboModifierInactive()`                                                       | `GamepadPoller.ComboModifier*`         | Shows/hides combo modifier indicator on overlay (unless `DisableComboModifierOSD`).                                                                                                                                                                                                                                                                |
| `OnComboConfirmationPending(string)` / `OnComboConfirmationCancelled()`                                              | `GamepadPoller.ComboConfirmation*`    | Shows/hides combo confirmation hold OSD (lime text on dark green). On cancellation, re-shows the modifier prompt.                                                                                                                                                                |
| `ShowDInputListItems(bool)`                                                                                        | Internal                               | Shows/hides DInput-specific ListBox items (Back, Start, D-Pad directions). Deselects system buttons when hiding.                                                                                                                                                                  |
| `UpdateDInputListLabels()` / `RestoreXInputListLabels()`                                                           | Internal                               | Toggle ListBox labels between DInput button indices and human-readable XInput names.                                                                                                                                                                                                                                                               |
| `ShowDisconnectedWarning()`                                                                                        | Disconnect                             | Shows overlay with disconnect text (named if previously connected), starts 60s timer to change to "WAITING FOR CONTROLLER...".                                                                                                                                                                                                                     |
| `DInputRemapButton_Click(...)`                                                                                     | Remap button                           | Toggles DInput remapping mode. Active: amber background, "Remapping: \"{action}\"..." label. Cancel: restores default.                                                                                                                                                                                                                             |
| `CalibrateDpadButton_Click(...)`                                                                                   | Calibrate button                       | Opens D-Pad Calibration Wizard. Stops poller, auto-records center from WinMM. Walks 8 directions clockwise with 1s sampling. Shows 3x3 grid, progress bar, countdown. On release: shows "FAIL" and waits for re-press. On duplicate input: shows "SAME INPUT". Disconnect detection: closes dialog after 10 consecutive failures. Saves on completion. |
| `StartDInputRemapAnimation()` / `StopDInputRemapAnimation()`                                                       | Internal                               | Toggles amber background on remap button (simple color swap, no storyboard).                                                                                                                                                                                                                                                                       |
| `CancelDInputRemapping()`                                                                                          | Internal                               | Stops remap animation, resets all remap state, restores button appearance.                                                                                                                                                                                                                                                                         |
| `KeysHintButton_Click(...)`                                                                                  | Keys Reference button             | Opens dark ScrollViewer window with keys reference text (reads from embedded resource, saves font size and window size to `app_settings.json`).                                                                                                                                      |
| `MinimizeToTray_Click(...)`                                                                                  | Minimize button                   | Hides to tray.                                                                                                                                                                                                                                                                                            |
| `HideToTray()` / `TrayIcon_Click(...)` / `Show_Click(...)` / `Exit_Click(...)`                               | Tray lifecycle                    | Standard tray icon show/hide/exit behavior.                                                                                                                                                                                                                                                               |
| `BackCycles_Changed(...)`                                                                                    | Checkbox                          | Sets `_poller.BackCyclesYxab`.                                                                                                                                                                                                                                                                            |
| `SkipDefault_Changed(...)`                                                                                   | Checkbox                          | Sets `_poller.SkipDefault`.                                                                                                                                                                                                                                                                               |
| `UpdateSkipDefaultState()`                                                                                   | Internal                          | Enables "Skip Default" checkbox only when `UserProfileCount >= 2`.                                                                                                                                                                                                                                        |
| `UpdateProfileEditingState()`                                                                                | Internal                          | Enables/disables editor controls based on whether current profile is `"Default"`.                                                                                                                                                                                                                         |
| `RefreshProfileList()`                                                                                       | Internal                          | Clears and repopulates `ProfileListBox`.                                                                                                                                                                                                                                                                  |
| `ProfileListBox_SelectionChanged(...)`                                                                       | ListBox selection                 | Updates current profile, mapping display, editing state, diagonal delay.                                                                                                                                                                                                                                  |
| `NewProfile_Click(...)` / `RenameProfile_Click(...)` / `SaveProfile_Click(...)` / `DeleteProfile_Click(...)` | Profile CRUD                      | Standard profile management.                                                                                                                                                                                                                                                                              |
| `ImportProfile_Click(...)` / `ExportProfile_Click(...)`                                                      | Import/Export                     | File dialog-based profile I/O.                                                                                                                                                                                                                                                                            |
| `KeyListBox_SelectionChanged(...)`                                                                           | Key selection                     | Updates mapping display.                                                                                                                                                                                                                                                                                  |
| `UpdateCurrentMappingDisplay()`                                                                              | Internal                          | Updates mapping display text and capture box.                                                                                                                                                                                                                                                             |
| `IsDefaultCheckbox_Changed(...)`                                                                             | Checkbox                          | Toggles custom mapping.                                                                                                                                                                                                                                                                                   |
| `KeyCaptureBox_MouseDown(...)` / `OnCaptureKeyDown(...)`                                                     | Key capture                       | Captures keyboard key and assigns to selected button.                                                                                                                                                                                                                                                     |
| `GetKeyNameFromVK(ushort)` / `GetDefaultKeyName(string)` / `GetDefaultKeyValue(string)`                      | Static helpers                    | VK code ↔ display name conversions.                                                                                                                                                                                                                                                                       |
| `ValidateProfileName(string)`                                                                                | Static helper                     | Validates 18-char limit.                                                                                                                                                                                                                                                                                  |
| `SafeInvoke(Action)`                                                                                         | Error wrapper                     | Wraps action in try/catch, logs to `crash.log` and shows MessageBox.                                                                                                                                                                                                                                      |
| `PerformRename()`                                                                                            | Profile name box Enter/LostFocus  | Renames current profile. Validates 18-char limit, checks for duplicates.                                                                                                                                                                                                                                  |
| `StartComboCapture()` / `CancelComboCapture()` / `OnCaptureComboKeyDown(...)`                                 | Key capture box click             | Combo macro key capture: records modifier keys (Ctrl/Shift/Alt) + main key as a key sequence. Auto-generates OSD name.                                                                                                                                                                                   |
| `ClearComboButton_Click(...)`                                                                                | Clear button                      | Removes combo action/exec/OSD assignment for currently selected combo.                                                                                                                                                                                                                                    |
| `ComboOSDNameBox_TextChanged(...)`                                                                           | Combo OSD name text box           | Persists custom OSD display name for selected combo action.                                                                                                                                                                                                                                               |
| `ExecPathBox_TextChanged(...)`                                                                               | Exec path text box                | Persists executable path for selected combo exec action (clears stale key binding).                                                                                                                                                                                                                       |
| `BrowseExecButton_Click(...)`                                                                                | Browse button                     | Opens `OpenFileDialog` for executable selection (.exe/.bat/.ps1/.cmd).                                                                                                                                                                                                                                    |
| `OpenExecButton_Click(...)`                                                                                  | Open button                       | Launches the configured executable (validates extension against allowed list).                                                                                                                                                                                                                            |
| `StartMinimizedCheckbox_Changed(...)`                                                                        | Start minimized checkbox          | Saves start-minimized preference to `AppSettings`.                                                                                                                                                                                                                                                        |
| `TerminateIfKemulatorClosed_Changed(...)`                                                                    | Terminate checkbox                | Saves terminate-if-kemulator-closed preference. Starts or stops 2s timer monitoring `java.exe` process existence.                                                                                                                                                                                         |
| `StartTerminateMonitoring()` / `StopTerminateMonitoring()`                                                   | Internal                          | 2s timer watches for `java.exe`. On first sighting marks `_javaSeenOnce = true`. If java disappears afterwards, calls `ShutdownCleanup()` + `Close()`.                                                                                                                                                   |
| `OpenConfigFolder_Click(...)`                                                                                | Config folder button              | Opens `%AppData%\J2MEGamepad\profiles\` in Explorer.                                                                                                                                                                                                                                                      |
| `DisableComboModifierOSD_Changed(...)`                                                                       | Disable combo modifier OSD checkbox | Toggles combo modifier OSD visibility.                                                                                                                                                                                                                                                                |
| `ComboConfirmationHold_Changed(...)`                                                                         | Confirmation combo-hold checkbox     | Toggles combo confirmation hold mode: requires 1s hold of face/exec button before firing. Saves to `AppSettings.ComboConfirmationHold`.                                                                                                                                                                   |
| `DisconnectTimer_Tick(...)`                                                                                  | 60s timer                         | Changes disconnected overlay text to "WAITING FOR CONTROLLER..." after 60 seconds.                                                                                                                                                                                                                        |
| `HideToTray()` / `ShowFromTray()` / `TrayIcon_DoubleClick(...)` / `StartMinimized()`                          | Tray lifecycle                    | Standard tray icon show/hide/start-minimized behavior.                                                                                                                                                                                                                                                    |

**DInput-specific XAML Elements**

| Element                                                    | Default   | Description                                                                       |
| ---------------------------------------------------------- | --------- | --------------------------------------------------------------------------------- |
| `DInputActionPanel`                                        | Collapsed | Contains "Remap button" and "Calibrate D-Pad" buttons, shown only in DInput mode. |
| `DInputDeadzonePanel`                                      | Collapsed | Contains deadzone slider (10-80%), shown only in DInput mode.                     |
| `DInputBackItem`                                           | Collapsed | SELECT (Back) button in the remap list.                                           |
| `DInputStartItem`                                          | Collapsed | START button in the remap list.                                                   |
| `DInputDpadUpItem` / `DownItem` / `LeftItem` / `RightItem` | Collapsed | D-Pad direction entries in the remap list.                                        |

### OverlayWindow

Transparent full-screen click-through OSD overlay. Uses `WS_EX_NOACTIVATE` and `WS_EX_TRANSPARENT` window styles for mouse/keyboard passthrough.

**Window Properties**: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `ShowInTaskbar="False"`, `Topmost="True"`, `WindowState="Maximized"`, `IsHitTestVisible="False"`.

**XAML Elements**

| Element                               | Opacity Default | Description                                                                                                                                                       |
| ------------------------------------- | --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DisconnectedBorder` (yellow box)     | 0               | "XBOX 360 CONTROLLER NOT DETECTED" text. Dark yellow background (`#80333300`), yellow foreground, 32pt bold. Content set dynamically via `SetDisconnectedText()`. |
| `ComboModifierBorder` (cyan box)      | 0               | "Press combo key..." text. Dark cyan background (`#80003333`), cyan foreground, 28pt bold. Infinite fade-in/out pulse while combo modifier is active.             |
| `ConfirmationOsdBorder` (green box)   | 0               | Combo confirmation hold overlay. Dark green background (`#80003300`), lime foreground, 28pt bold. Shown when holding a combo button during confirmation hold.     |
| `OsdBorder` (dark box)                | 0               | Dark semi-transparent background (`#80000000`), white 28pt bold text. Content set dynamically.                                                                    |

**Methods**

| Method                                        | Description                                                                                                                                                                                    |
| --------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OnLoaded(...)`                               | Applies WS_EX_NOACTIVATE and WS_EX_TRANSPARENT extended window styles via `SetWindowLong`.                                                                                                     |
| `ClearAnimations()`                           | Stops all storyboards, sets all animated elements to null (`BeginAnimation(OpacityProperty, null)`).                                                                                           |
| `SetDisconnectedText(string)`                 | Sets the disconnected overlay text content.                                                                                                                                                    |
| `ShowDisconnected()`                          | Starts an infinite pulse animation on `DisconnectedBorder`: 500ms fade to 0.8 → 1500ms hold → 500ms fade to 0 → repeat.                                                                        |
| `HideDisconnected()`                          | Stops all animations, sets `DisconnectedBorder.Opacity = 0`.                                                                                                                                   |
| `ShowOsd(string text)`                        | Shows a single OSD message: 80ms fade-in (opacity 0→0.5) → 1000ms hold → 420ms fade-out (0.5→0).                                                                                               |
| `ShowOsdSwap(string old, string new)`         | Cross-fade profile swap: 150ms fade-out of old text → DispatcherTimer swaps text → 150ms fade-in of new text → hold 700ms → 500ms border fade-out. Total ~1.5s.                                |
| `ShowComboModifier()`                         | Shows combo modifier indicator (dark cyan overlay box, "Press combo key..." text) with infinite fade-in/out pulse.                                                                             |
| `HideComboModifier()`                         | Hides combo modifier indicator immediately.                                                                                                                                                    |
| `ShowComboConfirmation(string text)`          | Shows combo confirmation overlay (dark green background, lime text). Stays visible until confirmed or cancelled — no animation, static display.                                               |
| `HideComboConfirmation()`                     | Hides combo confirmation overlay immediately.                                                                                                                                                  |
| `ShowLastOsd()`                               | Re-shows the last OSD message (used when overlay becomes visible again after being hidden).                                                                                                    |

---

## Configuration Files

All files stored in `%AppData%\J2MEGamepad\`:

| File                       | Contents              | Purpose                                                               |
| -------------------------- | --------------------- | --------------------------------------------------------------------- |
| `profiles\*.json`          | JSON profile data     | Per-user profile mappings (includes combo actions per profile).       |
| `app_settings.json`        | JSON                  | Centralized app settings (migrated from legacy txt files on v1 load). |
| `combo_settings.json`      | JSON                  | Combo macro key sequences + executable launch paths.                  |
| `dinput_mapping.json`      | JSON                  | DInput action-to-button-index mappings.                               |
| `dpad_calibration.json`    | JSON                  | D-Pad analog calibration data (center + 8 directions + deadzone).     |
| `crash.log`                | Error details         | Runtime logs from `LogHelper` (with repeat suppression).              |

**Legacy files** (`diagdelay.txt`, `directional_delay.txt`, `diaghold.txt`, `diagperprofile.txt`, `firstrun.txt`, `keysfont.txt`, `keyssize.txt`, `start_minimized.txt`, `terminate.txt`, `terminate_warning_hidden.txt`, `custom_warning_hidden.txt`, `comboperprofile.txt`, `disable_combo_modifier_osd.txt`) are migrated into `app_settings.json` on first load and can be safely deleted.

---

## DPAD Modes

Three modes cycled via View/Back (or gamepad start for profiles):

| Mode | Cardinal Directions | Diagonal Directions |
| --- | --- | --- |
| **PAD**<br/><br/><img width="250" alt="PAD Mode Layout" src="https://github.com/user-attachments/assets/85f2d4e2-53f3-4af5-b870-3486e67017bf" /> | Arrow Up / Down / Left / Right | Not supported — traditional gamepad mode. |
| **KEYPAD**<br/><br/><img width="250" alt="KEYPAD Mode Layout" src="https://github.com/user-attachments/assets/6563dd8b-08de-4bb4-b151-8b724475898d" /> | Numpad 8 / 2 / 4 / 6 | Numpad 7 / 9 / 1 / 3 (no delay support on these). |
| **PAD+DIAGONAL**<br/><br/><img width="250" alt="PAD+DIAGONAL Mode Layout" src="https://github.com/user-attachments/assets/bad07610-f268-4229-aabc-dec861c17cd2" /> | Arrow Up / Down / Left / Right | Numpad 7 / 9 / 1 / 3 only (arrows NOT sent for diagonals). |

**Diagonal Delay** (0-300ms, 0 = off, excludes PAD mode):

- Works in PAD+DIAGONAL and KEYPAD modes.
- **Hold cardinal keys (default OFF)**: cardinal arrow/numpad keys held during delay, then atomically swapped to diagonal key via `SwapPadKeys`.

  ![J2ME\_360\_Gamepad\_ipOMZ4fo1K](https://github.com/user-attachments/assets/4ab1d665-b319-4acc-83fa-1d0224723618)
- **Hold cardinal keys OFF**: no output during delay, diagonal key pressed after delay.

  ![J2ME\_360\_Gamepad\_tvXemuVPVp](https://github.com/user-attachments/assets/42f4f263-245b-4213-b55a-bb2705b3f6ff)
- **Save per profile (default OFF)**: stores/loads delay from profile JSON; when off, global slider value persists across profile switches.

**Directional Delay** (0-300ms): suppresses D-Pad crosstalk by requiring a hold duration before registering cardinal or diagonal transitions. When active, forces hold-cardinal to OFF.

**WITHOUT DELAY**

<img width="125" height="137" alt="firefox_JOcIZSgGyH" src="https://github.com/user-attachments/assets/54e06970-28d3-4d87-a241-99c18493ec40" />

**WITH MAX DELAY**

<img width="125" height="137" alt="342342432423342234324234" src="https://github.com/user-attachments/assets/95cead29-5f6b-41ab-98df-5538f3a2c227" />

---

## Button Mappings

All action buttons use **hold behavior** (press on down, release on up), not tap.

| Button      | Default Key                                       | Remappable             |
| ----------- | ------------------------------------------------- | ---------------------- |
| A           | F3 (PAD) / Numpad 5 (KEYPAD/DIAGONAL)             | Yes                    |
| B           | F2                                                | Yes                    |
| X           | F1                                                | Yes                    |
| Y           | Numpad 0                                          | Yes                    |
| LB          | Numpad *                                          | Yes                    |
| RB          | Numpad /                                          | Yes                    |
| LT          | F1                                                | Yes                    |
| RT          | F2                                                | Yes                    |
| Right Stick | F3                                                | Yes                    |
| Left Stick  | (none in Default) / profile mapping               | Only in custom profile |
| View/Back   | DPAD cycle (or profile backward)                  | No (system)            |
| Start       | Profile cycle forward                             | No (system)            |

---

## OSD Behavior

| Trigger | Animation | Duration |
| --- | --- | --- |
| **DPAD mode change**<br/><br/><img width="550" alt="DPAD mode change animation" src="https://github.com/user-attachments/assets/8896287d-f4a3-4f9e-b103-626269931692" /> | Fade in → hold → fade out | ~1.5s |
| **Profile swap (via gamepad)**<br/><br/><img width="550" alt="Profile swap animation" src="https://github.com/user-attachments/assets/0598ee6f-2113-48af-a84c-94a5458af455" /> | Crossfade old name → new name | ~1.5s |
| **Combo triggered (key sequence / exec launch)**<br/><br/><img width="550" alt="J2MEGamepad_gSxtZ7kQUW" src="https://github.com/user-attachments/assets/154254e2-872e-4eff-8cee-88be87558251" /> | Fade in → hold → fade out | ~1.5s |
| **Combo modifier active (RB+LB / RT+LT)**<br/><br/><img width="550" alt="J2MEGamepad_MBweB5tliq" src="https://github.com/user-attachments/assets/1fb8e81e-9d3d-41d5-8025-98a9f86dce1e" /> | Semi-transparent overlay box | While held |
| **Combo confirmation hold** <img width="550" alt="J2MEGamepad_YfTs2pitYF" src="https://github.com/user-attachments/assets/52d57ea8-a16c-49dd-a932-d363e0d7b885" /> | Static green overlay box with combo name | Up to 1s (until confirmed or cancelled) |
| **Controller disconnected**<br/><br/><img width="550" alt="Controller disconnected animation" src="https://github.com/user-attachments/assets/90300b3c-b334-4f15-bcfd-f27c90b3ab21" /> | Infinite yellow pulse (0→0.8→0) | 2.5s per cycle |
| Controller reconnected | Immediate hide | Instant |

`ClearAnimations()` is called before every OSD operation. It uses `BeginAnimation(OpacityProperty, null)` on all three animated elements (`DisconnectedBorder`, `OsdBorder`, `OsdText`) to unconditionally clear any animated property values left by previous storyboards.

---

## DInput Support

The application supports DirectInput (non-XInput) gamepads via the legacy `winmm.dll` joystick API (`joyGetPosEx`). DirectInput8 COM interop (`dinput8.dll`) is compiled in `DirectInputReader.cs` but never called at runtime (see [GamepadPoller notes](#gamepadpoller)).

### Detection Strategy

```
Alternating each 8ms tick:

[XInput tick]
  XInputGetState(0)
    └── ERROR_SUCCESS → XInput mode
    └── ERROR_DEVICE_NOT_CONNECTED → continue

[DInput tick]
  joyGetPosEx(0..15)  ← WinMM only, DirectInput8 is dead
    └── device found → check XInput also responds?
    |     ├── yes → upgrade to XInput mode (Xbox controllers appear as WinMM stubs)
    |     └── no  → DInput mode (WinMM)
    └── no device → disconnected (cached 500ms, skip scans)
```

### D-Pad Decoding (3-tier, in priority order)

1. **POV hat** — POV angle → 8-direction sector with ±2250° tolerance per 45° sector. POV=0 falls through to analog axes (WinMM returns 0 when there is no POV hat, so it can't be trusted).
2. **X/Y analog stick** — Normalize X/Y to -1..1 double, compute angle, map to 8-direction sector with configurable deadzone. If user calibration exists, uses nearest-neighbor against 8 recorded angles.
3. **Z/R axis** — Falls back to secondary axis pair, with a guard against cheap gamepad triggers (both axes at rest produce 0,0 — skips if both >85% from center).

### Button Remapping

- Default mapping: X=button1, A=button2, B=button3, Y=button4, LB=5, RB=6, LT=7, RT=8, Back=9, Start=10, LeftThumb=11, RightThumb=12, DPadUp=13, Down=14, Left=15, Right=16
- Saved to `%APPDATA%\J2MEGamepad\dinput_mapping.json`
- Remap via UI: select action → "Remap button" → press physical button → auto-detects conflicts
- Amber pulsing animation during capture mode

### D-Pad Calibration Wizard

- Stops poller during calibration; exclusively uses WinMM (`joyGetPosEx`) for reliable sampling
- Auto-records analog center position from current stick rest position
- Walks through 8 directions clockwise (Up → UpRight → Right → DownRight → Down → DownLeft → Left → UpLeft)
- For each direction: user holds D-Pad for 1 second, samples averaged
- Visual feedback: 3×3 grid highlighting, progress bar, countdown timer
- On release-before-complete: shows "FAIL", waits for re-press of the direction
- Duplicate input detection: if stick position matches a previously recorded direction, shows "SAME INPUT" and waits for a different direction
- Disconnect detection: 10 consecutive failed WinMM reads closes the dialog
- Saves to `%APPDATA%\J2MEGamepad\dpad_calibration.json`, triggers poller reload

### Default Deadzone

40% (configurable 10–80% via slider, persisted immediately).

---

## Profile System

- JSON files in `%AppData%\J2MEGamepad\profiles\`
- `"Default"` profile: read-only, always present, always first in list
- FileSystemWatcher detects external changes (add/remove/edit profile files)
- `_isReloading` guard prevents save-triggered reload loops
- Import: filename defines profile name (ignores internal JSON "Name" field), truncated to 18 chars, deduplicated with ` (1)`/` (2)` suffix
- Export: any location via `SaveFileDialog`
- Profile cycling: Start = forward, Back = backward (when BackCycles enabled)
- "Skip Default" cycling requires 2+ user profiles
- Profile names: max 18 characters, auto-named "Profile 1", "Profile 2", etc.
- Thread safety: all list access under `lock(_lock)`, `Profiles` getter returns a copy
- Per-profile settings: diagonal delay, directional delay, hold-cardinals flag, combo actions/OSD names/exec paths (all optional)

---

## Combo System

The application supports macro key sequences and executable launching via combo modifiers.

### Combo Modifiers

- **RB+LB**: Hold both right and left bumpers simultaneously to activate combo modifier
- **RT+LT**: Hold both right and left triggers simultaneously to activate combo modifier

When a modifier is active, the shoulder/trigger keys are released (preventing spurious output) and the following face/action buttons trigger combos:

| Button | Behavior                                                         |
| ------ | ---------------------------------------------------------------- |
| Y/X/A/B | Sends configured key sequence (list of virtual key codes)       |
| Start  | Launches configured executable                                   |
| Back   | Launches configured executable                                   |

### Configuration

- **Per-profile**: When "Combo per profile" is enabled, combo data is stored in the profile JSON (`ComboActions`, `ComboOSDNames`, `ComboExecPaths`)
- **Global**: When disabled, combo data is stored in `%APPDATA%\J2MEGamepad\combo_settings.json`
- Each combo can have: key sequence (list of VK codes), custom OSD display name, and optional executable path

### Security

Executable launches are restricted to safe extensions: `.exe`, `.bat`, `.cmd`, `.com`, `.ps1`. Paths are canonicalized with `Path.GetFullPath` and validated for existence before execution.

### Confirmation Combo-Hold

When the **"Confirmation combo-hold"** checkbox is enabled, pressing a combo face button (Y/X/A/B) or combo-exec button (Start/Back) while a modifier is active does **not** fire the action immediately. Instead:

1. **Hold requirement** — The button must be held for **1 second** while the modifier remains held.
2. **OSD feedback** — During the hold the OSD shows the configured combo name in **vivid green** on a dark green background, replacing the "Press combo key..." modifier prompt.
3. **On confirmation** — If held for the full second, the combo fires normally and the OSD transitions to the standard white/gray fade animation.
4. **On cancel** — If the face/exec button is released before the timer expires, the action is silently cancelled and the modifier prompt ("Press combo key...") reappears. If the modifier itself is released, all OSD elements are hidden.
5. **Empty combo** — Combos with no key sequence and no executable path assigned are silently ignored (no OSD shown).

The setting is saved in `AppSettings.ComboConfirmationHold` and persisted to `app_settings.json`.

---

## Keys Reference Window

- Embedded as assembly resource (`KEys.txt`), shipped inside the EXE
- Black window, green Consolas text, black background
- CTRL + Mouse Wheel zooms text (8-36pt)
- Font size and window size saved to `app_settings.json` on close (`KeysFontSize` default 18, `KeysWindowWidth` default 593, `KeysWindowHeight` default 682)
- Text wrapping enabled, no horizontal scrollbar
- Opened via "Keys Reference" button in main window

---

## Build Instructions

```powershell
# Build (MSBuild via VS or Build Tools)
msbuild "C:\Projects\J2ME_gamepad\J2MEGamepad\J2MEGamepad.csproj" /p:Configuration=Release

# Clean
msbuild "C:\Projects\J2ME_gamepad\J2MEGamepad\J2MEGamepad.csproj" /t:Clean
```

The project targets `net40-windows` (NET Framework 4.0) with WPF and WinForms support for Windows XP compatibility.

<img width="974" height="775" alt="image" src="https://github.com/user-attachments/assets/620ce83b-0845-4078-8d68-9fff6cb5426a" />


---

## Dependencies

- Runtime: .NET Framework 4.0 (Windows XP SP3+)
- SDK: .NET Framework 4.0 SDK or Visual Studio with MSBuild
- Native: `xinput1_3.dll` (Windows XP-compatible XInput), `user32.dll`, `winmm.dll`, `dinput8.dll` (unused at runtime)
- NuGet: `Newtonsoft.Json 12.0.3` — JSON serialization; `Microsoft.NETFramework.ReferenceAssemblies 1.0.3` — build-time reference assemblies
