# Isaac Desktop Pet 

A C# / WPF Windows desktop pet featuring the protagonist from *The Binding of Isaac: Repentance*. It wanders freely, sleeps, and interacts with you on your desktop.

Code was built with **Trae IDE**. Feel free to try it out — use this invite link and we both get bonus tokens:

> <https://www.trae.cn/events/code-fission/J8EKEB45Y68K?utm_source=copy_link&utm_medium=code_fission>

  ![issac is your friend!](./docs/thumbup.png)

***


## ✨ Features

| Feature               | Description                                                                                                                                                                                               |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 🚶 Auto-wandering     | Omni-directional random movement with edge bouncing, inertia acceleration and friction damping                                                                                                            |
| 🎮 Control Mode       | **Left-click** to enter: `WASD` moves the body (vector composition, diagonal normalization), **Arrow Keys** control head direction independently (with blink cycle), `Esc` or window deactivation to exit |
| 💤 Sleep Cycle        | Awake 100-140s → Idle 45-75s → Sleeping 4-6min, automatic transitions                                                                                                                                     |
| 👋 Cursor Interaction | After the cursor stays still for 45s, the pet walks under it and strikes a **hands-raised** pose to "hold" the cursor — any mouse movement instantly resumes wandering                                    |
| ✨ Hurt Animation      | Randomly triggered blinking / flash animation                                                                                                                                                             |
| 👍 Thumbs Up          | Randomly triggered, or displayed during cursor interaction                                                                                                                                                |
| 🖱️ Dragging          | Hold left button and drag to manually reposition the pet (5-pixel threshold distinguishes click vs. drag)                                                                                                 |

## 🚀 Quick Start

### Run directly (recommended)

Download `issac.exe` and double-click — no .NET runtime needed, no external assets required.

## 🎮 Controls

| Action             | Effect                                               |
| ------------------ | ---------------------------------------------------- |
| Left-click (short) | Enter control mode                                   |
| Left-click + drag  | Manually move the pet                                |
| `W` `A` `S` `D`    | Move in control mode (combinable for diagonals)      |
| `↑` `↓` `←` `→`    | Independently control head direction in control mode |
| `Esc`              | Exit control mode                                    |

## 🏗️ Tech Stack

- **Language**: C# 12

- **Framework**: .NET 10 / WPF (`AllowsTransparency` + `WindowStyle=None` layered transparent window)

- **Build**: `Microsoft.NET.Sdk` / bundled `csc.exe`

- **Window positioning**: `SetWindowPos` + DPI physical pixel snapping (fixes subpixel ghosting at 150% scaling)

- **Keyboard input**: `WH_KEYBOARD_LL` low-level global hook (keys are consumed during control mode so they won't leak into fullscreen games or text fields)

- **Mouse input**: Manual drag state machine (`MouseDown/Move/Up`, 5px threshold for click-vs-drag)

- **Physics**: Dual-velocity inertia model (actual velocity exponentially approaches target velocity)

## 📁 Project Structure

```
C#desk_issac/
├── issac.cs                    # All code in a single file
├── issac.csproj                # .NET SDK project config
├── build.ps1                   # Fallback build script using system csc.exe
├── README.md
└── src/
    └── character_001_isaac.png # Character sprite sheet (embedded as managed resource)
```

## ⚠️ Known Limitations

- **Windows only** (uses Win32 APIs: `SetWindowPos`, `SetWindowsHookEx`, `GetCursorPos`)

- **DPI scaling**: tested at 150% only

- **Fullscreen games**: the pet window is topmost and transparent, but fullscreen games capture raw input — clicks and control input won't reach the pet

- **One character**: only Isaac is bundled; swap the png under `src/` for a different character

                Found a bug or have an idea? Drop me a line: **<daojinkeke@qq.com>**

## 📜 License

This project is for learning and personal use only. Character art assets are copyright Edmund McMillen / Nicalis.
