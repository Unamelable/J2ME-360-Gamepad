# J2ME 360 Gamepad

Xbox 360 / DirectInput controller → keyboard input mapper designed for J2ME emulators (KEmulator). Translates physical controller input into keyboard key presses that KEmulator understands, with an OSD overlay, reusable profiles, and three DPAD modes.

![Overview](https://github.com/user-attachments/assets/030f3ced-5aae-45ef-a623-0342d8fa2c02)

---

## Table of Contents

- [Architecture](#architecture)
- [NativeMethods Layer](#nativemethods-layer)
- [Data Models](#data-models)
- [Services Layer](#services-layer)
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
  XInput.cs            → xinput1_4.dll (gamepad state / vibration)
  KeyboardInput.cs     → user32.dll (SendInput key injection)
  JoyInput.cs          → winmm.dll (legacy joystick API, DInput fallback)

Models/                → Data containers
  GamepadState.cs      → Live button states, DPadKey resolution, DInput decoding
  KeyMapProfile.cs     → JSON-serializable profile with mappings
  DInputMapping.cs     → DInput action-to-button-index mapping, persisted as JSON
  DInputCalibration.cs → D-Pad analog calibration data (center + 8 directions)

Services/              → Core logic
  KeyboardInjector.cs  → Track held keys, press/release via SendInput
  ProfileManager.cs    → Thread-safe CRUD, JSON storage, FileSystemWatcher
  GamepadPoller.cs     → 8ms poll loop, XInput→DInput→JoyAPI triple fallback, DPAD state machine
  ControllerWatchdog.cs → 1s XInput connection check (independent of poller)
  DirectInputReader.cs → SharpDX-based DirectInput joystick reader
  LogHelper.cs         → File-based logging to %APPDATA%\J2MEGamepad\crash.log

Windows/               → WPF UI
  OverlayWindow.xaml/.cs  → Transparent click-through OSD overlay
  MainWindow.xaml/.cs     → Main GUI (profile editor, key capture, tray icon, DInput controls)
  App.xaml/.cs            → Entry point, single-instance Mutex, startup watchdog
```

The application polls at **8ms intervals (~125Hz)** using a three-tier fallback: XInput → DirectInput (SharpDX) → legacy `joyGetPosEx` (winmm.dll). Auto-detects controller type on connect.

---

## NativeMethods Layer

### XInput.cs

P/Invoke wrapper for `xinput1_4.dll`. Uses raw DLL imports with no NuGet dependency.

**Structs**

| Struct | Fields |
| --- | --- |
| `XInputState` | `dwPacketNumber`, `Gamepad` (XInputGamepad) |
| `XInputGamepad` | `wButtons`, `bLeftTrigger`, `bRightTrigger`, `sThumbLX`, `sThumbLY`, `sThumbRX`, `sThumbRY` |
| `XInputVibration` | `wLeftMotorSpeed`, `wRightMotorSpeed` |

**Constants** (`XInputButtons`)

Button flag bitmasks matching the XINPUT_GAMEPAD_* constants: `DPAD_UP` (0x0001), `DPAD_DOWN` (0x0002), `DPAD_LEFT` (0x0004), `DPAD_RIGHT` (0x0008), `START` (0x0010), `BACK` (0x0020), `LEFT_THUMB` (0x0040), `RIGHT_THUMB` (0x0080), `LEFT_SHOULDER` (0x0100), `RIGHT_SHOULDER` (0x0200), `A` (0x1000), `B` (0x2000), `X` (0x4000), `Y` (0x8000).

**Methods**

| Method | Returns | Description |
| --- | --- | --- |
| `GetState(int userIndex, out XInputState state)` | `uint` | Calls XInputGetState. Returns `ERROR_SUCCESS` (0) or `ERROR_DEVICE_NOT_CONNECTED` (1167). |
| `SetState(int userIndex, ref XInputVibration vibration)` | `uint` | Calls XInputSetState (rumble). Currently unused by the application. |

### KeyboardInput.cs

P/Invoke wrapper for `user32.dll` `SendInput` for keyboard key injection.

**Internal Structures**

- `INPUT` — union wrapper with `type` field and `InputUnion`
- `InputUnion` — explicit-layout union of `MOUSEINPUT`, `KEYBDINPUT`, `HARDWAREINPUT`
- `KEYBDINPUT` — `wVk`, `wScan`, `dwFlags`, `time`, `dwExtraInfo`

**Methods**

| Method | Description |
| --- | --- |
| `SendKeyDown(ushort virtualKeyCode)` | Sends a key-down event for the given virtual key code. |
| `SendKeyUp(ushort virtualKeyCode)` | Sends a key-up event for the given virtual key code. |

Both call `SendKey()` internally, which creates a single-element `INPUT` array with `INPUT_KEYBOARD` (1) type and the appropriate `KEYEVENTF_KEYDOWN` (0) or `KEYEVENTF_KEYUP` (2) flag.

### JoyInput.cs

P/Invoke wrapper for `winmm.dll` legacy joystick API. Used as a fallback when SharpDX DirectInput is unavailable.

**Struct**

- `JOYINFOEX` — `dwSize`, `dwFlags`, `dwXpos`/`dwYpos`/`dwZpos`/`dwRpos`/`dwUpos`/`dwVpos`, `dwButtons`, `dwButtonNumber`, `dwPOV` (hat switch angle)

**Constants**

- `JOYERR_NOERROR` (0), `JOYERR_UNPLUGGED` (165)
- `JOY_RETURNALL` (0x3FF) — flags for requesting all joystick data
- `POV_CENTERED` (-1 / 0xFFFF)

**Methods**

| Method | Description |
| --- | --- |
| `GetNumDevices()` | Returns number of joystick devices (`joyGetNumDevs`). |
| `GetPosEx(int uJoyID, ref JOYINFOEX pji)` | Gets full joystick state (`joyGetPosEx`). |

---

## Data Models

### GamepadState.cs

Holds boolean state for every controller button plus DPad directions.

**Properties**: `A`, `B`, `X`, `Y`, `LB`, `RB`, `LT`, `RT`, `Start`, `Back`, `LeftThumb`, `RightThumb`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`.

**Methods**

| Method | Description |
| --- | --- |
| `UpdateFromButtons(ushort buttons)` | Decodes the XInput `wButtons` bitmask into individual boolean properties. LT/RT are set to `false` here — use `UpdateTriggers`. |
| `UpdateFromDInput(uint buttons, int pov, int x, int y, int z, int r, DInputCalibration?)` | Decodes DInput button bitmask and decodes D-Pad from analog axes with calibration. |
| `DecodeDInputDpad(int pov, int x, int y, int z, int r, DInputCalibration?)` | Three-tier D-Pad decoding: analog stick → Z/R axis (with cheap-gamepad guard) → POV hat fallback. |
| `ReadAxisDirection(float rawX, float rawY, float cx, float cy, float deadzone)` | Normalizes analog axes to -1..1 float, computes angle and magnitude, maps to 8-direction sector. |
| `UpdateTriggers(byte leftTrigger, byte rightTrigger, byte threshold = 50)` | Sets `LT`/`RT` based on analog trigger values >= threshold (digital conversion). |
| `GetDPadKey()` | Returns a `DPadKey` enum (`None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`) from the current DPad boolean flags. Diagonal detection requires both axes simultaneously. |

**Enums**

- `DPadMode` — `Pad` (arrow keys), `Keypad` (numpad 2/4/6/8), `PadDiagonal` (arrows + diagonal numpad)
- `DPadKey` — `None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`

### DInputMapping.cs

Maps action names to DInput button indices. Serializable as JSON.

**Default Mappings**

| Action | Button Index |
| --- | --- |
| X   | 0   |
| A   | 1   |
| B   | 2   |
| Y   | 3   |
| LB  | 4   |
| RB  | 5   |
| LT  | 6   |
| RT  | 7   |
| RightThumb | 11  |
| Start | 9   |
| Back | 8   |
| LeftThumb | 10  |
| DPadUp/Down/Left/Right | 12–15 |

**Methods**

| Method | Description |
| --- | --- |
| `BuildReverseMap()` | Returns `Dictionary<int, string>` (button index → action name). |
| `ToJson()` | Serializes to indented JSON. |
| `FromJson(string)` | Deserializes from JSON. |
| `Save()` | Writes to `%APPDATA%\J2MEGamepad\dinput_mapping.json`. |
| `Load()` | Loads from file or returns defaults. |

### DInputCalibration.cs

Stores calibration data for analog D-Pad axes. Serializable as JSON.

**Properties**: `DeadzonePercent` (default 40), `CenterX`/`CenterY` (default 32767), plus X/Y for 8 directional points (Up, Down, Left, Right, UpLeft, UpRight, DownLeft, DownRight).

**Methods**

| Method | Description |
| --- | --- |
| `ToJson()` | Serializes to indented JSON. |
| `FromJson(string)` | Deserializes from JSON. |
| `Save()` | Writes to `%APPDATA%\J2MEGamepad\dpad_calibration.json`. |
| `Load()` | Loads from file or returns defaults. |

### KeyMapProfile.cs

JSON-serializable profile class.

**Properties**

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `Name` | `string` | `"Default"` | Profile name, max 18 characters |
| `Mappings` | `Dictionary<string, ushort>` | Default layout (see below) | Maps button names to virtual key codes |
| `DiagonalDelayMs` | `int` | 0   | Per-profile diagonal delay in milliseconds |
| `DirectionalDelayMs` | `int` | 0   | Per-profile directional delay in milliseconds |
| `DiagonalDelayHoldCardinals` | `bool` | false | Per-profile hold-cardinals-during-delay flag |

**Default Mappings**

| Button | VK Code | Key |
| --- | --- | --- |
| Y   | 0x6A | Numpad * |
| X   | 0x70 | F1  |
| A   | 0x0D | Enter (PAD mode) / Numpad 5 (KEYPAD/DIAGONAL) |
| B   | 0x71 | F2  |
| LB  | 0x6A | Numpad * |
| RB  | 0x6F | Numpad / |
| LT  | 0x70 | F1  |
| RT  | 0x71 | F2  |
| RightThumb | 0x0D | Enter |

**Static Key Constants**: `KeyEnter` (0x0D), `KeyNumpad5`-`KeyNumpad9` (0x65-0x69), `KeyNumpad1`/`KeyNumpad3` (0x61/0x63), `KeyUp`/`KeyDown`/`KeyLeft`/`KeyRight` (0x26-0x28), `KeyMultiply` (0x6A), `KeyDivide` (0x6F), `KeyF1`/`KeyF2` (0x70/0x71). These are `[JsonIgnore]` — not serialized.

**Methods**

| Method | Returns | Description |
| --- | --- | --- |
| `ToJson()` | `string` | Serializes this profile to indented JSON via `System.Text.Json`. |
| `FromJson(string json)` | `KeyMapProfile?` | Deserializes JSON to a profile, or null on failure. |

---

## Services Layer

### KeyboardInjector

Tracks which keys are currently held down and manages press/release via SendInput.

**Fields**

- `_keysDown` — `HashSet<ushort>` of currently-pressed virtual key codes.

**Methods**

| Method | Description |
| --- | --- |
| `PressKey(ushort vkCode)` | Sends a key-down event only if the key is not already tracked as held. Adds to `_keysDown`. |
| `ReleaseKey(ushort vkCode)` | Sends a key-up event only if the key is tracked as held. Removes from `_keysDown`. |
| `ReleaseAll()` | Releases every tracked key and clears the set. Used on disconnect and poller stop. |
| `Dispose()` | Calls `ReleaseAll()`. |

### ProfileManager

Thread-safe CRUD for profiles stored as JSON files in `%AppData%\J2MEGamepad\profiles\`.

**Fields**

- `_profilesDir` — `%AppData%\J2MEGamepad\profiles\`
- `_watcher` — `FileSystemWatcher` monitoring `*.json` changes
- `_lock` — synchronizes all profile list access
- `_isReloading` — guard flag preventing FileSystemWatcher feedback loops during `SaveToFile`
- `_profiles` — internal `List<KeyMapProfile>` backing field

**Properties**

| Property | Description |
| --- | --- |
| `Profiles` | Returns a **copy** of the internal list under lock (prevents collection-modified exceptions when UI enumerates while watcher updates). |
| `CurrentProfileIndex` | Index of the currently selected profile. |
| `CurrentProfile` | Returns `_profiles[CurrentProfileIndex]` under lock, or a fresh `KeyMapProfile()` if list is empty. |
| `IsDefaultProfile` | True if current profile's `Name` is `"Default"`. |
| `UserProfileCount` | Count of profiles whose `Name` is not `"Default"`. |

**Events**

| Event | Description |
| --- | --- |
| `ProfilesChanged` | Raised after external file changes trigger a reload. |

**Constructor**: Creates the profiles directory if missing, calls `LoadProfiles()`, starts the `FileSystemWatcher`.

**Methods**

| Method | Description |
| --- | --- |
| `LoadProfiles()` | Clears internal list, always adds a `"Default"` profile first, then loads all `*.json` files from disk, skipping any whose name is `"Default"`. If the result is empty, re-adds `"Default"`. |
| `SaveProfile(KeyMapProfile profile)` | Removes any existing profile with the same name, adds the new one, and saves to disk. |
| `DeleteProfile(string name)` | Refuses to delete `"Default"`. Removes from internal list and deletes the JSON file. |
| `RenameProfile(string oldName, string newName)` | Refuses to rename to/from `"Default"`. Deletes old JSON file, updates profile name, saves under new name. |
| `CycleForward(bool skipDefault)` | Moves `CurrentProfileIndex` forward by 1 (wrapping). If `skipDefault` is true and there are user profiles, skips past `"Default"`. |
| `CycleBackward(bool skipDefault)` | Same as forward but in reverse direction. |
| `ExportProfile(string name)` | Returns the profile's JSON string. |
| `ImportProfile(string json, string? renameTo)` | Deserializes JSON, optionally overrides the profile name, saves. |
| `GetNextAvailableName(string baseName)` | Finds the first unused name in the pattern `"{baseName} 1"`, `"{baseName} 2"`, etc. |
| `Dispose()` | Disposes the FileSystemWatcher. |

**File Format**: Each profile is saved as `{SanitizedName}.json` in `%AppData%\J2MEGamepad\profiles\`. Filename sanitization replaces invalid path characters with `_`. Import deduplication appends ` (1)`, ` (2)` etc.

### GamepadPoller

Core polling engine. Runs at 8ms (125 Hz) via `System.Timers.Timer`. Uses a three-tier fallback: XInput → DirectInput (SharpDX) → legacy `joyGetPosEx`.

**ControllerType enum**: `None`, `XInput`, `DInput` — auto-detected during polling loop.

**Fields**

- `_timer` — 8ms interval timer
- `_lastSentDPadKey` — tracks last DPAD state to detect changes
- `_*WasPressed` — per-button edge-detection flags for `A`, `B`, `X`, `Y`, `Back`, `Start`, `LeftThumb`, `RightThumb`, `LB`, `RB`, `LT`, `RT`
- `_current*HeldKey` — stores the VK code currently held for each action button
- `_connected` — cached connection state
- `_heldPadKeys` — `HashSet<ushort>` of currently-pressed DPAD-related keys
- `_diagonalDelayMs`, `_diagonalDelayHoldCardinals` — diagonal delay configuration
- `_diagonalEntryTime`, `_pendingDiagonalKey`, `_diagonalActivated` — diagonal delay state machine
- `_directionalDelayMs` — configurable directional delay for crosstalk suppression
- `_directInput` — `DirectInputReader` instance
- `_dinputMapping`, `_dinputCalibration` — DInput configuration
- `_dinputButtonToAction` — reverse mapping (button index → action name)
- `_dinputWasPressed`, `_dinputHeldKeys` — DInput edge tracking
- `_lastDInputButtons` — previous button state for delta detection
- `_dinputBackIdx`, `_dinputStartIdx`, `_dinputLeftThumbIdx` — cached DInput system button indices

**Properties**

| Property | Description |
| --- | --- |
| `CurrentDPadMode` | Gets/sets DPAD mode (`Pad`/`Keypad`/`PadDiagonal`). Setting triggers `ForceReleaseDPad()` and fires `ModeChanged`. |
| `BackCyclesYxab` | When true, View/Back cycles profiles backward instead of cycling DPAD modes. |
| `SkipDefault` | When true, profile cycling skips the `"Default"` profile. |
| `DiagonalDelayMs` | Diagonal delay in ms (0 = off). Setting to 0 resets the pending diagonal state. |
| `DiagonalDelayHoldCardinals` | If true, cardinal arrow/numpad keys are held during the delay window (no output if false). |
| `DirectionalDelayMs` | Directional delay for crosstalk suppression (0 = off). |
| `IsConnected` | Current XInput controller connection state. |
| `IsDInputMode` | True when controller type is `DInput`. |
| `IsRemapping` | When true, DInput button presses fire `DInputButtonPressed` instead of normal processing. |

**Events**

| Event | Args | Description |
| --- | --- | --- |
| `ModeChanged` | `string` | Fired on DPAD mode change (mode name) or profile switch (`"YXAB: {name}"`). |
| `ConnectionChanged` | `bool` | Fired when controller connects (true) or disconnects (false). |
| `BackCyclesToggled` | `bool` | Fired when Left Stick Button toggles the BackCyclesYxab flag. |
| `DInputButtonPressed` | `int` | Fired during DInput remapping when a button is pressed (button index). |
| `DInputModeChanged` | `string` | Fired when DInput mode is first entered. |

**Methods**

| Method | Description |
| --- | --- |
| `Start()` | Starts the polling timer. |
| `Stop()` | Stops the timer and releases all held keys via `KeyboardInjector.ReleaseAll()`. |
| `Dispose()` | Stops and disposes the timer. |
| `Poll(...)` | **Internal.** Called every 8ms. Tries XInput first. On `ERROR_DEVICE_NOT_CONNECTED`, falls through to `PollDInput`. On disconnect, fires events and releases all keys. |
| `PollDInput()` | **Internal.** Tries DirectInput via `_directInput.Poll()`. Falls back to `joyGetPosEx` if SharpDX fails. Calls `InitDInputMode()` on first DInput connect. |
| `InitDInputMode()` | **Internal.** Sets `_controllerType = DInput`, loads mapping and calibration, creates new `DirectInputReader`, fires `ConnectionChanged` and `DInputModeChanged`. |
| `MakeStateFromDirectInput()` | **Internal.** Builds `GamepadState` from `DirectInputReader` state via `UpdateFromDInput`. |
| `MakeStateFromJoyInfo(JOYINFOEX)` | **Internal.** Builds `GamepadState` from legacy joystick API state via `UpdateFromDInput`. |
| `ProcessDInput(GamepadState, JOYINFOEX?)` | **Internal.** Checks button-mapped D-Pad if analog/POV didn't detect direction, calls `ProcessDPad`, then either handles remapping button capture or normal action/mode-switch processing. |
| `ProcessDInputActions(uint buttons)` | **Internal.** Iterates all `_dinputButtonToAction` mappings, tracks edge transitions (press/release), sends key down/up via `KeyboardInjector`. |
| `ProcessDInputModeSwitches(uint buttons)` | **Internal.** Edge-detection for Back/Start/LeftThumb for mode cycling (same as XInput logic). |
| `GetVKForDInputAction(string action)` | **Internal.** Maps DInput action names to virtual key codes from profile or defaults. |
| `ReloadDInputMapping()` | Reloads `DInputMapping` and rebuilds reverse map. |
| `ReloadCalibration()` | Reloads `DInputCalibration` from disk. |
| `ProcessDPad(GamepadState)` | **Internal.** DPAD state machine. Handles diagonal delay, diagonal crosstalk suppression, diagonal-to-cardinal crosstalk suppression, and cardinal delay. |
| `SwapPadKeys(ushort[] newKeys)` | **Internal.** Atomically releases all held pad keys then presses the new set. Used during diagonal delay transitions to eliminate key gaps. |
| `GetCardinalKeys(DPadKey diagonal)` | **Internal.** Returns the cardinal direction keys (arrows or numpad depending on mode) for a diagonal input. |
| `GetKeysForDPad(DPadKey)` | **Internal.** Returns the VK code array for a given DPAD key in the current mode. |
| `ProcessActionButtons(GamepadState)` | **Internal.** Calls `ProcessHoldButton` for A/B/X/Y using profile mappings. |
| `ProcessShoulderButtons(GamepadState)` | **Internal.** Calls `ProcessHoldButton` for LB/RB/LT/RT/RightThumb. |
| `ProcessHoldButton(bool current, ref bool wasPressed, ref ushort heldKey, Func<ushort> getKey)` | **Internal.** Hold-button handler: on rising edge, calls `getKey()` and presses it; on falling edge, releases the tracked key. |
| `ProcessModeSwitches(GamepadState)` | **Internal.** Edge-detection for Back/Start/LeftThumb. |
| `ProcessButtonEdge(bool current, ref bool wasPressed, Action onPress)` | **Internal.** Generic edge-detection helper for tap-style buttons. |
| `ForceReleaseDPad()` | Releases all held pad keys, clears all state. Called when DPAD mode changes. |
| `GetModeName(DPadMode)` | **Internal static.** Returns human-readable name. |

**DPAD Crosstalk Suppression** — Diagonal crosstalk (cardinal → diagonal flicker) and diagonal-to-cardinal (release one axis early) are handled with either poll-count confirmation (2 consecutive same-state polls) or a configurable directional delay timer (0–300ms). Cardinal delay also prevents brief cardinal pass-through before settling on a diagonal.

**DPAD State Machine + Diagonal Delay** — See the original README structure for detailed state machine steps; the behavior is identical for both XInput and DInput sources.

### ControllerWatchdog

Separate 1-second timer for XInput connection state monitoring (independent of the poller's 8ms loop).

**Fields**

- `_timer` — 1000ms interval timer
- `_wasConnected` — previous connection state for edge detection

**Properties**

| Property | Description |
| --- | --- |
| `IsConnected` | Current XInput connection state. |

**Events**

| Event | Description |
| --- | --- |
| `Connected` | Fired on transition from disconnected → connected. |
| `Disconnected` | Fired on transition from connected → disconnected. |

**Methods**

| Method | Description |
| --- | --- |
| `Start()` | Runs an immediate connection check, then starts the timer. |
| `Stop()` | Stops the timer. |
| `Dispose()` | Disposes the timer. |
| `CheckConnection()` | **Internal.** Calls `XInput.GetState(0, ...)` and fires events on state changes. |

### DirectInputReader

SharpDX-based DirectInput joystick reader. Wraps the `SharpDX.DirectInput` library for raw controller access.

**Fields**

- `_directInput` — `DirectInput` instance
- `_joystick` — `Joystick` device instance
- `_lastState` — `JoystickState` from last successful poll
- `_pollsToIgnoreAfterAcquire` — counter (3) for driver-quirk garbage data after acquire

**Properties**

| Property | Type | Description |
| --- | --- | --- |
| `Available` | `bool` | Whether the device is initialized and ready. |
| `X`, `Y`, `Z`, `Rx`, `Ry`, `Rz` | `int` | Axis positions (thread-safe). |
| `Pov` | `int` | First POV hat angle (-1 if none). |
| `Buttons` | `bool[]` | Button array (thread-safe copy). |
| `DebugInfo` | `string?` | Device name or error message. |

**Methods**

| Method | Description |
| --- | --- |
| `Initialize()` | Creates `DirectInput`, enumerates attached `Joystick`/`Gamepad` devices, selects first, sets cooperative level to `NonExclusive |
| `Poll()` | Calls `_joystick.Poll()` + `GetCurrentState()`. Ignores first 3 polls after acquire. On failure, tries to re-acquire. |
| `GetButtonMask()` | Converts boolean button array to integer bitmask. |
| `Dispose()` | Unacquires and disposes the device and DirectInput instance. |

### LogHelper

Simple file-based logging service.

**Methods**

| Method | Description |
| --- | --- |
| `Info(string source, string msg)` | Appends a timestamped info line to `crash.log`. |
| `Error(string source, string context, Exception ex)` | Appends a timestamped error with stack trace to `crash.log`. |

Logs are written to `%APPDATA%\J2MEGamepad\crash.log`.

---

## UI Layer

### App

Entry point, inherits `Application`.

**Fields**: `_mutex` (named `"J2MEGamepad-SingleInstanceMutex"`), `_mainWindow`, `_windowReady` (ManualResetEvent for startup watchdog).

**Methods**

| Method | Description |
| --- | --- |
| `OnStartup(...)` | Creates a named `Mutex` to enforce single-instance. If another instance is running, shows a MessageBox and shuts down. Otherwise sets process priority to `BelowNormal`, starts a 15-second watchdog thread (terminates with error dialog if `OnLoaded` doesn't signal readiness), creates and shows `MainWindow`. |
| `SignalWindowReady()` | Called from `MainWindow.OnLoaded` to signal the startup watchdog that initialization completed successfully. |
| `ReleaseMutexForRestart()` | Releases the mutex so the restarted instance can acquire it. |
| `OnExit(...)` | Disposes the mutex. |

###

### MainWindow

Main configuration window. Fixed 690×590 `ToolWindow`, `NoResize`.

**Constructor**: Initializes all services (`KeyboardInjector`, `ProfileManager`, `GamepadPoller`, `ControllerWatchdog`, `OverlayWindow`), creates the `NotifyIcon` tray icon with "Exit" and "Show" context menu items, wires all service events (including `DInputModeChanged` and `DInputButtonPressed`), calls `RefreshProfileList()`, hooks `Loaded` and `Closed` events.

![](/C:/Users/leonp/AppData/Roaming/marktext/images/2026-07-03-18-03-37-image.png)

**Event Handlers and Methods**

| Method | Trigger | Description |
| --- | --- | --- |
| `OnLoaded(...)` | `Loaded` event | Shows overlay. Signals startup watchdog. Registers CTRL+R restart hotkey. Loads saved settings: diagonal delay, hold cardinal, per-profile, directional delay. Loads DInput deadzone from calibration. Calls `ApplyDiagonalDelayFromProfile()`, `ShowFirstRunWarning()`, then starts watchdog and poller. |
| `OnClosed(...)` | `Closed` event | Unregisters hotkey. Saves all settings. Stops disconnect timer. Disposes all services. |
| `WndProc(...)` | Window message hook | Handles `WM_HOTKEY` for CTRL+R restart. |
| `RestartApplication()` | CTRL+R | Disposes all services, releases mutex, starts a new process instance, calls `Environment.Exit(0)`. |
| `ShowFirstRunWarning()` | Called from `OnLoaded` | If `firstrun.txt` doesn't exist, shows a dialog explaining KEmulator KeyMap cleanup. |
| `DiagDelaySlider_ValueChanged(...)` | Slider | Updates `_poller.DiagonalDelayMs` and display text. |
| `DirDelaySlider_ValueChanged(...)` | Slider | Updates `_poller.DirectionalDelayMs`, display text, and hold cardinal checkbox state. |
| `DInputDeadzoneSlider_ValueChanged(...)` | Slider | Saves deadzone to calibration file and triggers poller reload. |
| `DiagDelayHold_Changed(...)` | Checkbox | Updates `_poller.DiagonalDelayHoldCardinals`. |
| `DiagDelayPerProfile_Changed(...)` | Checkbox | Calls `ApplyDiagonalDelayFromProfile()`. |
| `ApplyDiagonalDelayFromProfile()` | Mode change, profile selection | If per-profile mode is enabled, reads delay and hold settings from current profile and applies them. |
| `OnProfilesChanged()` | `ProfileManager.ProfilesChanged` | Dispatcher-invokes `RefreshProfileList()`. |
| `OnBackCyclesToggled(bool)` | `GamepadPoller.BackCyclesToggled` | Syncs GUI checkbox, shows OSD. |
| `OnControllerConnected()` | `ControllerWatchdog.Connected` | Hides disconnected overlay, updates status text (green for XInput, orange for DInput with device name). |
| `OnControllerDisconnected()` | `ControllerWatchdog.Disconnected` | Hides all DInput UI, stops remap animation, resets capturing state, red "Please connect Xbox/DInput controller". |
| `OnConnectionChanged(bool)` | `GamepadPoller.ConnectionChanged` | Shows/hides DInput panels and list items based on connection state. Shows disconnected overlay on disconnect. |
| `OnDInputModeChanged(string)` | `GamepadPoller.DInputModeChanged` | Shows DInput panels, updates status to orange with device name, shows DInput list items. |
| `OnDInputButtonPressed(int)` | DInput remap capture | Captures the button press, checks for conflicts, updates mapping, saves, updates UI. |
| `OnModeChanged(string)` | `GamepadPoller.ModeChanged` | Updates DPAD mode text, profile text, applies diagonal delay from profile. Detects DPAD mode vs profile swap (`"YXAB:"` prefix) and shows appropriate OSD. |
| `ShowDInputListItems(bool)` | Internal | Shows/hides DInput-specific ListBox items (Back, Start, D-Pad directions). |
| `UpdateDInputListLabels()` | Internal | Updates ListBox items to show DInput button indices (e.g., "X (1)"). |
| `RestoreXInputListLabels()` | Internal | Restores original XInput labels. |
| `ShowDisconnectedWarning()` | Disconnect | Shows overlay with disconnected animation, starts 60s timer to change text. |
| `DInputRemapButton_Click(...)` | Remap button | Toggles DInput remapping mode. When active, amber animation plays and next button press gets captured. |
| `StartDInputRemapAnimation()` / `StopDInputRemapAnimation()` | Internal | Amber pulsing background animation on remap button. |
| `CalibrateDpadButton_Click(...)` | Calibrate button | Opens D-Pad Calibration Wizard dialog. Auto-records center, walks 8 directions clockwise with 1-second sampling, shows 3x3 grid highlighting, progress bar, and countdown. On failure (stick released early) shows "FAIL" and waits for re-press. |
| `KeysHintButton_Click(...)` | Keys Reference button | Opens dark ScrollViewer window with keys reference text. |
| `MinimizeToTray_Click(...)` | Minimize button | Hides to tray. |
| `HideToTray()` / `TrayIcon_Click(...)` / `Show_Click(...)` / `Exit_Click(...)` | Tray lifecycle | Standard tray icon show/hide/exit behavior. |
| `BackCycles_Changed(...)` | Checkbox | Sets `_poller.BackCyclesYxab`. |
| `SkipDefault_Changed(...)` | Checkbox | Sets `_poller.SkipDefault`. |
| `UpdateSkipDefaultState()` | Internal | Enables "Skip Default" checkbox only when `UserProfileCount >= 2`. |
| `UpdateProfileEditingState()` | Internal | Enables/disables editor controls based on whether current profile is `"Default"`. |
| `RefreshProfileList()` | Internal | Clears and repopulates `ProfileListBox`. |
| `ProfileListBox_SelectionChanged(...)` | ListBox selection | Updates current profile, mapping display, editing state, diagonal delay. |
| `NewProfile_Click(...)` / `RenameProfile_Click(...)` / `SaveProfile_Click(...)` / `DeleteProfile_Click(...)` | Profile CRUD | Standard profile management. |
| `ImportProfile_Click(...)` / `ExportProfile_Click(...)` | Import/Export | File dialog-based profile I/O. |
| `KeyListBox_SelectionChanged(...)` | Key selection | Updates mapping display. |
| `UpdateCurrentMappingDisplay()` | Internal | Updates mapping display text and capture box. |
| `IsDefaultCheckbox_Changed(...)` | Checkbox | Toggles custom mapping. |
| `KeyCaptureBox_MouseDown(...)` / `OnCaptureKeyDown(...)` | Key capture | Captures keyboard key and assigns to selected button. |
| `GetKeyNameFromVK(ushort)` / `GetDefaultKeyName(string)` / `GetDefaultKeyValue(string)` | Static helpers | VK code ↔ display name conversions. |
| `ValidateProfileName(string)` | Static helper | Validates 18-char limit. |
| `SafeInvoke(Action)` | Error wrapper | Wraps action in try/catch, logs to `crash.log` and shows MessageBox. |

**DInput-specific XAML Elements**

| Element | Default | Description |
| --- | --- | --- |
| `DInputActionPanel` | Collapsed | Contains "Remap button" and "Calibrate D-Pad" buttons, shown only in DInput mode. |
| `DInputDeadzonePanel` | Collapsed | Contains deadzone slider (10-80%), shown only in DInput mode. |
| `DInputBackItem` | Collapsed | SELECT (Back) button in the remap list. |
| `DInputStartItem` | Collapsed | START button in the remap list. |
| `DInputDpadUpItem` / `DownItem` / `LeftItem` / `RightItem` | Collapsed | D-Pad direction entries in the remap list. |

### OverlayWindow

Transparent full-screen click-through OSD overlay. Uses `WS_EX_NOACTIVATE` and `WS_EX_TRANSPARENT` window styles for mouse/keyboard passthrough.

**Window Properties**: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `ShowInTaskbar="False"`, `Topmost="True"`, `WindowState="Maximized"`, `IsHitTestVisible="False"`.

**XAML Elements**

| Element | Opacity Default | Description |
| --- | --- | --- |
| `DisconnectedBorder` (yellow box) | 0   | "XBOX 360 CONTROLLER NOT DETECTED" text. Dark yellow background (`#80333300`), yellow foreground, 32pt bold. Content set dynamically via `SetDisconnectedText()`. |
| `OsdBorder` (dark box) | 0   | Dark semi-transparent background (`#80000000`), white 28pt bold text. Content set dynamically. |

**Methods**

| Method | Description |
| --- | --- |
| `OnLoaded(...)` | Applies WS_EX_NOACTIVATE and WS_EX_TRANSPARENT extended window styles via `SetWindowLong`. |
| `ClearAnimations()` | Stops all storyboards, sets all three animatable elements to null (`BeginAnimation(OpacityProperty, null)`) to reliably clear animated property values. |
| `SetDisconnectedText(string)` | Sets the disconnected overlay text content. |
| `ShowDisconnected()` | Starts an infinite pulse animation on `DisconnectedBorder`: 500ms fade to 0.8 → 1500ms hold → 500ms fade to 0 → repeat. |
| `HideDisconnected()` | Stops all animations, sets `DisconnectedBorder.Opacity = 0`. |
| `ShowOsd(string text)` | Shows a single OSD message: 80ms fade-in (opacity 0→0.5) → 1000ms hold → 420ms fade-out (0.5→0). |
| `ShowOsdSwap(string oldText, string newText)` | Cross-fade profile swap: 150ms fade-out of old text → at 150ms, `DispatcherTimer` swaps text to new → 150ms fade-in of new text → border holds for 700ms → 500ms border fade-out. Total ~1.5s. |

---

## Configuration Files

All files stored in `%AppData%\J2MEGamepad\`:

| File | Contents | Purpose |
| --- | --- | --- |
| `profiles\*.json` | JSON profile data | Per-user profile mappings. |
| `diagdelay.txt` | Integer (0-300) | Last used diagonal delay value. |
| `directional_delay.txt` | Integer (0-300) | Last used directional delay value. |
| `diaghold.txt` | `"True"` or `"False"` | Hold cardinal keys during delay flag. |
| `diagperprofile.txt` | `"True"` or `"False"` | Save delay per profile flag. |
| `keysfont.txt` | Integer (8-36) | KEys reference window font size (default 18). |
| `keyssize.txt` | `{width}x{height}` | KEys reference window size (default 593×682). |
| `firstrun.txt` | `"0"` | Sentinel file suppressing first-run warning. |
| `dinput_mapping.json` | JSON | DInput action-to-button-index mappings. |
| `dpad_calibration.json` | JSON | D-Pad analog calibration data (center + 8 directions + deadzone). |
| `crash.log` | Error details | Runtime logs from `LogHelper`. |

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
- **Hold cardinal keys (default OFF)**: cardinal arrow/numpad keys held during delay, then atomically swapped to diagonal key via `SwapPadKeys`.<br/>
  ![J2ME\\360\\Gamepad\\ipOMZ4fo1K](https://github.com/user-attachments/assets/4ab1d665-b319-4acc-83fa-1d0224723618)
- **Hold cardinal keys OFF**: no output during delay, diagonal key pressed after delay.<br/>
  ![J2ME\\360\\Gamepad\\tvXemuVPVp](https://github.com/user-attachments/assets/42f4f263-245b-4213-b55a-bb2705b3f6ff)
- **Save per profile (default OFF)**: stores/loads delay from profile JSON; when off, global slider value persists across profile switches.

**Directional Delay** (0-300ms): suppresses D-Pad crosstalk by requiring a hold duration before registering cardinal or diagonal transitions. When active, forces hold-cardinal to OFF.

**WITHOUT DELAY**

<img width="125" height="137" alt="firefox_JOcIZSgGyH" src="https://github.com/user-attachments/assets/54e06970-28d3-4d87-a241-99c18493ec40" />

**WITH MAX DELAY**

<img width="125" height="137" alt="342342432423342234324234" src="https://github.com/user-attachments/assets/95cead29-5f6b-41ab-98df-5538f3a2c227" />

---

## Button Mappings

All action buttons use **hold behavior** (press on down, release on up), not tap.

| Button | Default Key | Remappable |
| --- | --- | --- |
| A   | Enter (PAD) / Numpad 5 (KEYPAD/DIAGONAL) | Yes |
| B   | F2  | Yes |
| X   | F1  | Yes |
| Y   | Numpad * | Yes |
| LB  | Numpad * | Yes |
| RB  | Numpad / | Yes |
| LT  | F1  | Yes |
| RT  | F2  | Yes |
| Right Stick | Enter | Yes |
| View/Back | DPAD cycle (or profile backward) | No (system) |
| Start | Profile cycle forward | No (system) |
| Left Stick | Toggle BackCycles | No (system) |

---

## OSD Behavior

| Trigger | Animation | Duration |
| --- | --- | --- |
| **DPAD mode change**<br/><br/><img width="550" alt="DPAD mode change animation" src="https://github.com/user-attachments/assets/8896287d-f4a3-4f9e-b103-626269931692" /> | Fade in → hold → fade out | ~1.5s |
| **Profile swap (via gamepad)**<br/><br/><img width="550" alt="Profile swap animation" src="https://github.com/user-attachments/assets/0598ee6f-2113-48af-a84c-94a5458af455" /> | Crossfade old name → new name | ~1.5s |
| **Controller disconnected**<br/><br/><img width="550" alt="Controller disconnected animation" src="https://github.com/user-attachments/assets/90300b3c-b334-4f15-bcfd-f27c90b3ab21" /> | Infinite yellow pulse (0→0.8→0) | 2.5s per cycle |
| **Controller reconnected** | Immediate hide | Instant |

`ClearAnimations()` is called before every OSD operation. It uses `BeginAnimation(OpacityProperty, null)` on all three animated elements (`DisconnectedBorder`, `OsdBorder`, `OsdText`) to unconditionally clear any animated property values left by previous storyboards.

---

## DInput Support

The application supports DirectInput (non-XInput) gamepads via the **SharpDX.DirectInput** library, with a fallback to the legacy `winmm.dll` joystick API.

### Connection Fallback Chain

```
XInput.GetState()
  └── success → XInput mode
  └── ERROR_DEVICE_NOT_CONNECTED
        └── DirectInputReader.Poll()
              └── success → DInput mode
              └── failure
                    └── joyGetPosEx()
                          └── success → DInput mode (legacy)
                          └── failure → disconnected
```

### D-Pad Decoding (3-tier)

1. **Analog stick** — Normalize X/Y to -1..1 float, compute angle, map to 8-direction sector with configurable deadzone
2. **Z/R axis** — Falls back to secondary axis pair, with a guard against cheap gamepad triggers (both axes at rest produce 0,0 — skips if both >85% from center)
3. **POV hat** — POV angle → cardinal direction (0° = up, 90° = right, etc.)

### Button Remapping

- Default mapping: X=button1, A=button2, B=button3, Y=button4, LB=5, RB=6, LT=7, RT=8, Back=9, Start=10, LeftThumb=11, RightThumb=12, DPadUp=13, Down=14, Left=15, Right=16
- Saved to `%APPDATA%\J2MEGamepad\dinput_mapping.json`
- Remap via UI: select action → "Remap button" → press physical button → auto-detects conflicts
- Amber pulsing animation during capture mode

### D-Pad Calibration Wizard

- Auto-records analog center position from current stick rest position
- Walks through 8 directions clockwise (Up → UpRight → Right → DownRight → Down → DownLeft → Left → UpLeft)
- For each direction: user holds D-Pad for 1 second, samples averaged
- Visual feedback: 3×3 grid highlighting, progress bar, countdown timer
- On release-before-complete: shows "FAIL", waits for re-press
- Saves to `%APPDATA%\J2MEGamepad\dpad_calibration.json`

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
- Per-profile settings: diagonal delay, directional delay, hold-cardinals flag (all optional)

---

## Keys Reference Window

- Embedded as assembly resource (`KEys.txt`), shipped inside the EXE
- Black window, green Consolas text, black background
- CTRL + Mouse Wheel zooms text (8-36pt)
- Font size saved to `keysfont.txt` on close
- Window size saved to `keyssize.txt` on close (default 593×682)
- Text wrapping enabled, no horizontal scrollbar
- Opened via "Keys Reference" button in main window

---

## Build Instructions

```powershell
# Build (x86 SDK required)
& "C:\Program Files (x86)\dotnet\dotnet.exe" build `
  "C:\Projects\J2ME_gamepad\J2MEGamepad\J2MEGamepad.csproj" --nologo

# Clean
& "C:\Program Files (x86)\dotnet\dotnet.exe" clean `
  "C:\Projects\J2ME_gamepad\J2MEGamepad\J2MEGamepad.csproj" --nologo
```

The x86 SDK is required because XInput native interop may behave differently under x64. The project targets `net8.0-windows` with WPF and WinForms support.

---

## Dependencies

- Runtime: .NET 8 (Windows-only)
- SDK: x86 variant of .NET 8 SDK
- Native: `xinput1_4.dll` (part of DirectX, shipped with Windows), `user32.dll`, `winmm.dll`
- NuGet: `SharpDX.DirectInput 4.2.0` — DirectInput controller support
