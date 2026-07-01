# J2ME 360 Gamepad

Xbox 360 controller → keyboard input mapper designed for J2ME emulators (KEmulator). Translates physical controller input into keyboard key presses that KEmulator understands, with an OSD overlay, reusable profiles, and three DPAD modes.
<img width="674" height="511" alt="Overview" src="https://github.com/user-attachments/assets/030f3ced-5aae-45ef-a623-0342d8fa2c02" />
<img width="1117" height="444" alt="VirusTotal" src="https://github.com/user-attachments/assets/e7c43ff4-1a2d-4b07-a029-5e656f67aa5e" />

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
- [UI Layer](#ui-layer)
  - [App](#app)
  - [MainWindow](#mainwindow)
  - [OverlayWindow](#overlaywindow)
- [Configuration Files](#configuration-files)
- [DPAD Modes](#dpad-modes)
- [Button Mappings](#button-mappings)
- [OSD Behavior](#osd-behavior)
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

Models/                → Data containers
  GamepadState.cs      → Live button states, DPadKey resolution
  KeyMapProfile.cs     → JSON-serializable profile with mappings

Services/              → Core logic
  KeyboardInjector.cs  → Track held keys, press/release via SendInput
  ProfileManager.cs    → Thread-safe CRUD, JSON storage, FileSystemWatcher
  GamepadPoller.cs     → 8ms XInput poll loop, DPAD state machine, diagonal delay
  ControllerWatchdog.cs → 1s connection check, connected/disconnected events

Windows/               → WPF UI
  OverlayWindow.xaml/.cs  → Transparent click-through OSD overlay
  MainWindow.xaml/.cs     → Main GUI (profile editor, key capture, tray icon)
  App.xaml/.cs            → Entry point, single-instance Mutex
```

---

## NativeMethods Layer

### XInput.cs

P/Invoke wrapper for `xinput1_4.dll`. Uses raw DLL imports with no NuGet dependency.

**Structs**

| Struct | Fields |
|--------|--------|
| `XInputState` | `dwPacketNumber`, `Gamepad` (XInputGamepad) |
| `XInputGamepad` | `wButtons`, `bLeftTrigger`, `bRightTrigger`, `sThumbLX`, `sThumbLY`, `sThumbRX`, `sThumbRY` |
| `XInputVibration` | `wLeftMotorSpeed`, `wRightMotorSpeed` |

**Constants** (`XInputButtons`)

Button flag bitmasks matching the XINPUT_GAMEPAD_* constants: `DPAD_UP` (0x0001), `DPAD_DOWN` (0x0002), `DPAD_LEFT` (0x0004), `DPAD_RIGHT` (0x0008), `START` (0x0010), `BACK` (0x0020), `LEFT_THUMB` (0x0040), `RIGHT_THUMB` (0x0080), `LEFT_SHOULDER` (0x0100), `RIGHT_SHOULDER` (0x0200), `A` (0x1000), `B` (0x2000), `X` (0x4000), `Y` (0x8000).

**Methods**

| Method | Returns | Description |
|--------|---------|-------------|
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
|--------|-------------|
| `SendKeyDown(ushort virtualKeyCode)` | Sends a key-down event for the given virtual key code. |
| `SendKeyUp(ushort virtualKeyCode)` | Sends a key-up event for the given virtual key code. |

Both call `SendKey()` internally, which creates a single-element `INPUT` array with `INPUT_KEYBOARD` (1) type and the appropriate `KEYEVENTF_KEYDOWN` (0) or `KEYEVENTF_KEYUP` (2) flag.

---

## Data Models

### GamepadState.cs

Holds boolean state for every controller button plus DPad directions.

**Properties**: `A`, `B`, `X`, `Y`, `LB`, `RB`, `LT`, `RT`, `Start`, `Back`, `LeftThumb`, `RightThumb`, `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight`.

**Methods**

| Method | Description |
|--------|-------------|
| `UpdateFromButtons(ushort buttons)` | Decodes the XInput `wButtons` bitmask into individual boolean properties. LT/RT are set to `false` here — use `UpdateTriggers`. |
| `UpdateTriggers(byte leftTrigger, byte rightTrigger, byte threshold = 50)` | Sets `LT`/`RT` based on analog trigger values >= threshold (digital conversion). |
| `GetDPadKey()` | Returns a `DPadKey` enum (`None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`) from the current DPad boolean flags. Diagonal detection requires both axes simultaneously. |

**Enums**

- `DPadMode` — `Pad` (arrow keys), `Keypad` (numpad 2/4/6/8), `PadDiagonal` (arrows + diagonal numpad)
- `DPadKey` — `None`, `Up`, `Down`, `Left`, `Right`, `UpLeft`, `UpRight`, `DownLeft`, `DownRight`

### KeyMapProfile.cs

JSON-serializable profile class.

**Properties**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Name` | `string` | `"Default"` | Profile name, max 18 characters |
| `Mappings` | `Dictionary<string, ushort>` | Default layout (see below) | Maps button names to virtual key codes |
| `DiagonalDelayMs` | `int` | 0 | Per-profile diagonal delay in milliseconds |

**Default Mappings**

| Button | VK Code | Key |
|--------|---------|-----|
| Y | 0x6A | Numpad * |
| X | 0x70 | F1 |
| A | 0x0D | Enter (PAD mode) / Numpad 5 (KEYPAD/DIAGONAL) |
| B | 0x71 | F2 |
| LB | 0x6A | Numpad * |
| RB | 0x6F | Numpad / |
| LT | 0x70 | F1 |
| RT | 0x71 | F2 |
| RightThumb | 0x0D | Enter |

**Static Key Constants**: `KeyEnter` (0x0D), `KeyNumpad5`-`KeyNumpad9` (0x65-0x69), `KeyNumpad1`/`KeyNumpad3` (0x61/0x63), `KeyUp`/`KeyDown`/`KeyLeft`/`KeyRight` (0x26-0x28), `KeyMultiply` (0x6A), `KeyDivide` (0x6F), `KeyF1`/`KeyF2` (0x70/0x71). These are `[JsonIgnore]` — not serialized.

**Methods**

| Method | Returns | Description |
|--------|---------|-------------|
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
|--------|-------------|
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
|----------|-------------|
| `Profiles` | Returns a **copy** of the internal list under lock (prevents collection-modified exceptions when UI enumerates while watcher updates). |
| `CurrentProfileIndex` | Index of the currently selected profile. |
| `CurrentProfile` | Returns `_profiles[CurrentProfileIndex]` under lock, or a fresh `KeyMapProfile()` if list is empty. |
| `IsDefaultProfile` | True if current profile's `Name` is `"Default"`. |
| `UserProfileCount` | Count of profiles whose `Name` is not `"Default"`. |

**Events**

| Event | Description |
|-------|-------------|
| `ProfilesChanged` | Raised after external file changes trigger a reload. |

**Constructor**: Creates the profiles directory if missing, calls `LoadProfiles()`, starts the `FileSystemWatcher`.

**Methods**

| Method | Description |
|--------|-------------|
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

Core polling engine. Runs at 8ms (125 Hz) via `System.Timers.Timer`.

**Fields**

- `_timer` — 8ms interval timer
- `_lastSentDPadKey` — tracks last DPAD state to detect changes
- `_*WasPressed` — per-button edge-detection flags for `A`, `B`, `X`, `Y`, `Back`, `Start`, `LeftThumb`, `RightThumb`, `LB`, `RB`, `LT`, `RT`
- `_current*HeldKey` — stores the VK code currently held for each action button
- `_connected` — cached connection state
- `_heldPadKeys` — `HashSet<ushort>` of currently-pressed DPAD-related keys
- `_diagonalDelayMs`, `_diagonalDelayHoldCardinals` — diagonal delay configuration
- `_diagonalEntryTime`, `_pendingDiagonalKey`, `_diagonalActivated` — diagonal delay state machine

**Properties**

| Property | Description |
|----------|-------------|
| `CurrentDPadMode` | Gets/sets DPAD mode (`Pad`/`Keypad`/`PadDiagonal`). Setting triggers `ForceReleaseDPad()` and fires `ModeChanged`. |
| `BackCyclesYxab` | When true, View/Back cycles profiles backward instead of cycling DPAD modes. |
| `SkipDefault` | When true, profile cycling skips the `"Default"` profile. |
| `DiagonalDelayMs` | Diagonal delay in ms (0 = off). Setting to 0 resets the pending diagonal state. |
| `DiagonalDelayHoldCardinals` | If true, cardinal arrow/numpad keys are held during the delay window (no output if false). |
| `IsConnected` | Current XInput controller connection state. |

**Events**

| Event | Args | Description |
|-------|------|-------------|
| `ModeChanged` | `string` | Fired on DPAD mode change (mode name) or profile switch (`"YXAB: {name}"`). |
| `ConnectionChanged` | `bool` | Fired when controller connects (true) or disconnects (false). |
| `BackCyclesToggled` | `bool` | Fired when Left Stick Button toggles the BackCyclesYxab flag. |

**Methods**

| Method | Description |
|--------|-------------|
| `Start()` | Starts the polling timer. |
| `Stop()` | Stops the timer and releases all held keys via `KeyboardInjector.ReleaseAll()`. |
| `Dispose()` | Stops and disposes the timer. |
| `Poll(...)` | **Internal.** Called every 8ms. Calls `XInput.GetState(0, ...)`, manages connection state transitions, then calls `ProcessDPad`, `ProcessActionButtons`, `ProcessShoulderButtons`, `ProcessModeSwitches`. |
| `ProcessDPad(GamepadState)` | **Internal.** DPAD state machine. Handles diagonal delay (PAD+DIAGONAL and KEYPAD modes only, skipped for PAD mode). Normal path: releases previous keys, presses new keys. |
| `SwapPadKeys(ushort[] newKeys)` | **Internal.** Atomically releases all held pad keys then presses the new set. Used during diagonal delay transitions to eliminate key gaps. |
| `GetCardinalKeys(DPadKey diagonal)` | **Internal.** Returns the cardinal direction keys (arrows or numpad depending on mode) for a diagonal input. |
| `GetKeysForDPad(DPadKey)` | **Internal.** Returns the VK code array for a given DPAD key in the current mode. |
| `ProcessActionButtons(GamepadState)` | **Internal.** Calls `ProcessHoldButton` for A/B/X/Y using profile mappings (or default keys for the `"Default"` profile). A button context-switches: Enter in PAD mode, Numpad 5 in Keypad/PAD+DIAGONAL. |
| `ProcessShoulderButtons(GamepadState)` | **Internal.** Calls `ProcessHoldButton` for LB/RB/LT/RT/RightThumb. |
| `ProcessHoldButton(bool current, ref bool wasPressed, ref ushort heldKey, Func<ushort> getKey)` | **Internal.** Hold-button handler: on rising edge, calls `getKey()` and presses it; on falling edge, releases the tracked key. |
| `ProcessModeSwitches(GamepadState)` | **Internal.** Edge-detection for Back/Start/LeftThumb. Back cycles modes or profiles backward. Start cycles profiles forward. LeftThumb toggles `BackCyclesYxab`. |
| `ProcessButtonEdge(bool current, ref bool wasPressed, Action onPress)` | **Internal.** Generic edge-detection helper for tap-style buttons (not hold). |
| `ForceReleaseDPad()` | Releases all held pad keys, clears state. Called when DPAD mode changes. |
| `GetModeName(DPadMode)` | **Internal static.** Returns human-readable name: `"PAD Mode"`, `"KEYPAD Mode"`, `"PAD+DIAGONAL Mode"`. |

**DPAD State Machine** — Normal Operation

1. Call `GetDPadKey()` to determine the current DPAD direction (including diagonals).
2. In `PadDiagonal` mode, explicitly re-detects diagonals as combined cardinal inputs.
3. Compare with `_lastSentDPadKey`. If unchanged, exit.
4. If changed: release all keys from the previous state, press all keys for the new state.
5. Update `_lastSentDPadKey`.

**Diagonal Delay State Machine**

1. If `_diagonalDelayMs > 0`, mode is not `Pad`, and current key is diagonal:
   - If `currentKey != _pendingDiagonalKey`: new diagonal started. Set timestamp, reset `_diagonalActivated`. Call `SwapPadKeys` with cardinal keys (if `_diagonalDelayHoldCardinals` is true) or empty array (no output during delay). Exit.
   - If `!_diagonalActivated` and elapsed >= delay: activate diagonal. Call `SwapPadKeys` with the diagonal key. Exit.
   - Otherwise: still in delay window, exit.
2. If diagonal state is pending but current input is not diagonal: cancel pending, clear keys.

### ControllerWatchdog

Separate 1-second timer for connection state monitoring (independent of the poller's 8ms loop).

**Fields**

- `_timer` — 1000ms interval timer
- `_wasConnected` — previous connection state for edge detection

**Properties**

| Property | Description |
|----------|-------------|
| `IsConnected` | Current XInput connection state. |

**Events**

| Event | Description |
|-------|-------------|
| `Connected` | Fired on transition from disconnected → connected. |
| `Disconnected` | Fired on transition from connected → disconnected. |

**Methods**

| Method | Description |
|--------|-------------|
| `Start()` | Runs an immediate connection check, then starts the timer. |
| `Stop()` | Stops the timer. |
| `Dispose()` | Disposes the timer. |
| `CheckConnection()` | **Internal.** Calls `XInput.GetState(0, ...)` and fires events on state changes. |

---

## UI Layer

### App

Entry point, inherits `Application`.

**Fields**: `_mutex` (named `"J2MEGamepad-SingleInstanceMutex"`), `_mainWindow`.

**Methods**

| Method | Description |
|--------|-------------|
| `OnStartup(...)` | Creates a named `Mutex` to enforce single-instance. If another instance is running, shows a MessageBox and shuts down. Otherwise sets process priority to `BelowNormal`, creates and shows `MainWindow`. |
| `OnExit(...)` | Disposes the mutex. |

### MainWindow

Main configuration window. Fixed 690×520 `ToolWindow`, `NoResize`.

**Constructor**: Initializes all services (`KeyboardInjector`, `ProfileManager`, `GamepadPoller`, `ControllerWatchdog`, `OverlayWindow`), creates the `NotifyIcon` tray icon with "Exit" and "Show" context menu items, wires all service events, calls `RefreshProfileList()`, hooks `Loaded` and `Closed` events.

**Event Handlers and Methods**

| Method | Trigger | Description |
|--------|---------|-------------|
| `OnLoaded(...)` | `Loaded` event | Shows overlay. Loads saved settings: diagonal delay (`diagdelay.txt`), hold cardinal flag (`diaghold.txt`), per-profile flag (`diagperprofile.txt`). Calls `ApplyDiagonalDelayFromProfile()`, `ShowFirstRunWarning()`, then starts watchdog and poller. |
| `OnClosed(...)` | `Closed` event | Saves diagonal delay, hold cardinal, and per-profile settings to files. Disposes all services and overlay. |
| `ShowFirstRunWarning()` | Called from `OnLoaded` | If `%AppData%\J2MEGamepad\firstrun.txt` doesn't exist, shows a dialog explaining KEmulator KeyMap cleanup. "Don't remind me again" checkbox creates the sentinel file with content `"0"`. |
| `DiagDelaySlider_ValueChanged(...)` | Slider value change | Updates `_poller.DiagonalDelayMs` and display text. |
| `DiagDelayHold_Changed(...)` | Checkbox check/uncheck | Updates `_poller.DiagonalDelayHoldCardinals`. |
| `DiagDelayPerProfile_Changed(...)` | Checkbox check/uncheck | Calls `ApplyDiagonalDelayFromProfile()`. |
| `ApplyDiagonalDelayFromProfile()` | Mode change, profile selection | If per-profile mode is enabled, reads `_profiles.CurrentProfile.DiagonalDelayMs` and applies it to the slider and poller. |
| `OnProfilesChanged()` | `ProfileManager.ProfilesChanged` | Dispatcher-invokes `RefreshProfileList()`. |
| `OnBackCyclesToggled(bool)` | `GamepadPoller.BackCyclesToggled` | Syncs GUI checkbox, shows OSD. |
| `OnControllerConnected()` | `ControllerWatchdog.Connected` | Hides disconnected overlay, updates status text to green "Controller connected". |
| `OnControllerDisconnected()` | `ControllerWatchdog.Disconnected` | Shows disconnected overlay, red "Please connect Xbox 360 Controller". |
| `OnConnectionChanged(bool)` | `GamepadPoller.ConnectionChanged` | Same as above — handles second event source. |
| `ShowDisconnectedWarning()` | Both connection events | Triggers overlay disconnected animation. |
| `OnModeChanged(string)` | `GamepadPoller.ModeChanged` | Updates DPAD mode text, profile text, applies diagonal delay from profile. Detects whether the event is a DPAD mode change or a YXAB profile swap (prefix `"YXAB:"`) and shows appropriate OSD animation (`ShowOsd` for mode, `ShowOsdSwap` for profile). |
| `KeysHintButton_Click(...)` | Keys Reference button | Loads `KEys.txt` from embedded assembly resource. Loads saved font size (`keysfont.txt`, default 18) and window size (`keyssize.txt`, default 593×682). Creates a black ScrollViewer window with green Consolas text, CTRL+scroll zoom (8-36pt), saves settings on close. |
| `MinimizeToTray_Click(...)` | Minimize to Tray button | Calls `HideToTray()`. |
| `HideToTray()` | Internal | Shows the `NotifyIcon`, hides the window. |
| `TrayIcon_Click(...)` / `TrayIcon_DoubleClick(...)` / `Show_Click(...)` | Tray events | Shows the window, activates it, hides the tray icon. |
| `Exit_Click(...)` | Tray menu "Exit" | Hides tray icon, calls `Application.Current.Shutdown()`. |
| `BackCycles_Changed(...)` | Checkbox | Sets `_poller.BackCyclesYxab`. |
| `SkipDefault_Changed(...)` | Checkbox | Sets `_poller.SkipDefault`. |
| `UpdateSkipDefaultState()` | Internal | Enables "Skip Default" checkbox only when `UserProfileCount >= 2`, resets to false otherwise. |
| `UpdateProfileEditingState()` | Internal | Enables/disables editor controls based on whether current profile is `"Default"`. |
| `RefreshProfileList()` | Internal | Clears and repopulates `ProfileListBox` from `_profiles.Profiles`. Selects the current profile. |
| `ProfileListBox_SelectionChanged(...)` | ListBox selection | Updates current profile index, name text box, mapping display, editing state, skip-default state, and applies diagonal delay. |
| `NewProfile_Click(...)` | New button | Creates a new profile with `GetNextAvailableName("Profile")`, copies mappings from current profile, optionally copies diagonal delay (if per-profile enabled), saves and selects it. |
| `RenameProfile_Click(...)` | Rename button | Validates new name from `ProfileNameBox` (not empty, not duplicate, <= 18 chars), calls `RenameProfile`. |
| `SaveProfile_Click(...)` | Save button | Saves current profile with name from text box. Optionally saves diagonal delay per profile. |
| `DeleteProfile_Click(...)` | Delete button | Deletes the currently selected profile name. Resets index if needed. |
| `ImportProfile_Click(...)` | Import button | Opens an `OpenFileDialog` for `.json` files. Reads file, uses filename (without extension) as profile name, truncates to 18 chars, deduplicates with `(1)`/`(2)` suffix, imports via `ProfileManager.ImportProfile()`. |
| `ExportProfile_Click(...)` | Export button | Opens a `SaveFileDialog`. Exports current profile JSON to chosen path. |
| `KeyListBox_SelectionChanged(...)` | Key selection | Stores selected button name, updates IsDefault checkbox and mapping display. |
| `UpdateCurrentMappingDisplay()` | Internal | Updates the mapping display text and key capture box background. |
| `IsDefaultCheckbox_Changed(...)` | Checkbox | If checked: removes custom mapping (reverts to default). If unchecked: sets mapping to default key value. Resets capture box background and text. |
| `KeyCaptureBox_MouseDown(...)` | Click on capture box | Enters capture mode: sets `_isCapturingKey = true`, subscribes `PreviewKeyDown`. |
| `OnCaptureKeyDown(...)` | PreviewKeyDown | Captures the pressed key, gets its VK code via `KeyInterop.VirtualKeyFromKey`, updates the profile mapping. Shows a "Custom Keybind Warning" MessageBox once per session. Exits capture mode. |
| `GetKeyNameFromVK(ushort)` | Static helper | Converts a virtual key code to a human-readable name (e.g., `0x70` → `"F1"`, `0x25` → `"Left Arrow"`). Handles digits (0-9), letters (A-Z), F-keys, numpad keys, and common special keys. Falls back to hex `"0xXXXX"`. |
| `GetDefaultKeyName(string)` | Static helper | Returns the default key name for a given button name (e.g., `"Y"` → `"Num *"`, `"A"` → `"Enter / Num 5"`). |
| `GetDefaultKeyValue(string)` | Static helper | Returns the default VK code for a given button name. |
| `ValidateProfileName(string)` | Static helper | Returns false and shows warning if name exceeds 18 characters. |
| `SafeInvoke(Action)` | Internal wrapper | Wraps any action in try/catch. On exception, writes to `%AppData%\J2MEGamepad\error.log` (creates directory if needed) and shows a MessageBox. |

### OverlayWindow

Transparent full-screen click-through OSD overlay. Uses `WS_EX_NOACTIVATE` and `WS_EX_TRANSPARENT` window styles for mouse/keyboard passthrough.

**Window Properties**: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `ShowInTaskbar="False"`, `Topmost="True"`, `WindowState="Maximized"`, `IsHitTestVisible="False"`.

**XAML Elements**

| Element | Opacity Default | Description |
|---------|----------------|-------------|
| `DisconnectedBorder` (yellow box) | 0 | "XBOX 360 CONTROLLER NOT DETECTED" text. Dark yellow background (`#80333300`), yellow foreground, 32pt bold. |
| `OsdBorder` (dark box) | 0 | Dark semi-transparent background (`#80000000`), white 28pt bold text. Content set dynamically. |

**Methods**

| Method | Description |
|--------|-------------|
| `OnLoaded(...)` | Applies WS_EX_NOACTIVATE and WS_EX_TRANSPARENT extended window styles via `SetWindowLong`. |
| `ClearAnimations()` | Stops all storyboards, sets all three animatable elements to null (`BeginAnimation(OpacityProperty, null)`) to reliably clear animated property values. |
| `ShowDisconnected()` | Starts an infinite pulse animation on `DisconnectedBorder`: 500ms fade to 0.8 → 1500ms hold → 500ms fade to 0 → repeat. |
| `HideDisconnected()` | Stops all animations, sets `DisconnectedBorder.Opacity = 0`. |
| `ShowOsd(string text)` | Shows a single OSD message: 80ms fade-in (opacity 0→0.5) → 1000ms hold → 420ms fade-out (0.5→0). |
| `ShowOsdSwap(string oldText, string newText)` | Cross-fade profile swap: 150ms fade-out of old text → at 150ms, `DispatcherTimer` swaps text to new → 150ms fade-in of new text → border holds for 700ms → 500ms border fade-out. Total ~1.5s. |

---

## Configuration Files

All files stored in `%AppData%\J2MEGamepad\`:

| File | Contents | Purpose |
|------|----------|---------|
| `profiles\*.json` | JSON profile data | Per-user profile mappings. |
| `diagdelay.txt` | Integer (0-300) | Last used diagonal delay value. |
| `diaghold.txt` | `"True"` or `"False"` | Hold cardinal keys during delay flag. |
| `diagperprofile.txt` | `"True"` or `"False"` | Save delay per profile flag. |
| `keysfont.txt` | Integer (8-36) | KEys reference window font size (default 18). |
| `keyssize.txt` | `{width}x{height}` | KEys reference window size (default 593×682). |
| `firstrun.txt` | `"0"` | Sentinel file suppressing first-run warning. |
| `error.log` | Error details | Exception logs from `SafeInvoke`. |

---

## DPAD Modes

Three modes cycled via View/Back (or gamepad start for profiles):

| Mode | Cardinal Directions | Diagonal Directions |
|------|-------------------|-------------------|
| **PAD** | Arrow Up / Down / Left / Right | Not supported — traditional gamepad mode. |
| **KEYPAD** | Numpad 8 / 2 / 4 / 6 | Numpad 7 / 9 / 1 / 3 (no delay support on these). |
| **PAD+DIAGONAL** | Arrow Up / Down / Left / Right | Numpad 7 / 9 / 1 / 3 only (arrows NOT sent for diagonals). |

**Diagonal Delay** (0-300ms, 0 = off, excludes PAD mode):
- Works in PAD+DIAGONAL and KEYPAD modes.
- **Hold cardinal keys (default OFF)**: cardinal arrow/numpad keys held during delay, then atomically swapped to diagonal key via `SwapPadKeys`.
- **Hold cardinal keys OFF**: no output during delay, diagonal key pressed after delay.
- **Save per profile (default OFF)**: stores/loads delay from profile JSON; when off, global slider value persists across profile switches.

---

## Button Mappings

All action buttons use **hold behavior** (press on down, release on up), not tap.

| Button | Default Key | Remappable |
|--------|------------|------------|
| A | Enter (PAD) / Numpad 5 (KEYPAD/DIAGONAL) | Yes |
| B | F2 | Yes |
| X | F1 | Yes |
| Y | Numpad * | Yes |
| LB | Numpad * | Yes |
| RB | Numpad / | Yes |
| LT | F1 | Yes |
| RT | F2 | Yes |
| Right Stick | Enter | Yes |
| View/Back | DPAD cycle (or profile backward) | No (system) |
| Start | Profile cycle forward | No (system) |
| Left Stick | Toggle BackCycles | No (system) |

---

## OSD Behavior

| Trigger | Animation | Duration |
|---------|-----------|----------|
| DPAD mode change | Fade in → hold → fade out | ~1.5s |
| Profile swap (via gamepad) | Crossfade old name → new name | ~1.5s |
| Controller disconnected | Infinite yellow pulse (0→0.8→0) | 2.5s per cycle |
| Controller reconnected | Immediate hide | Instant |

`ClearAnimations()` is called before every OSD operation. It uses `BeginAnimation(OpacityProperty, null)` on all three animated elements (`DisconnectedBorder`, `OsdBorder`, `OsdText`) to unconditionally clear any animated property values left by previous storyboards.

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
- Native: `xinput1_4.dll` (part of DirectX, shipped with Windows), `user32.dll`
- NuGet: None (pure P/Invoke, no external packages)
