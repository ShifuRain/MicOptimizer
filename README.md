# Mic Optimizer

Real-time microphone processing for Windows that makes an ordinary condenser mic behave like a podcast mic: **steady volume, whispers that stay audible, and keyboard clicks, mouse noise and fans that disappear**. It sits between your audio interface and the apps that use your mic (Discord, OBS, Teams, a recorder) and applies a tuned processing chain with switchable profiles.

![Mic Optimizer window](docs/screenshot.png)

*The live trace shows your raw mic (grey) against what is sent out (vermilion), with a strip marking when the gate was closed.*

Built for a Blue Spark microphone on a Focusrite Scarlett Solo. It has been tested on that setup only (see [Status and limits](#status-and-limits)).

## What it does

```
mic ─▶ rumble cut ─▶ noise removal ─▶ gate ─▶ tone ─▶ auto level ─▶ compressor ─▶ de-esser ─▶ limiter ─▶ VB-Cable ─▶ your apps
```

| Stage | Purpose |
|---|---|
| **Rumble cut** | High-pass filter (90 Hz default) for desk thumps. |
| **Noise removal** | [RNNoise](https://gitlab.xiph.org/xiph/rnnoise) neural suppression for fans, hiss and keyboard noise under your voice. *Adaptive*: RNNoise treats whispers as noise, so the cleaned signal is only used while it hears normal or loud speech. Costs 10 ms. |
| **Gate** | A downward expander that turns the mic down between words. Its open/close decision listens only to 150 Hz to 5 kHz, so low-frequency desk thump from typing does not open it, while a whisper does. |
| **Tone** | Mild EQ: a cut around 250 Hz, a lift around 3.5 kHz, and a high shelf above 10 kHz. |
| **Auto level** | A slow gain rider that evens out soft and loud passages. Its range is deliberately small so whispers still sound softer than shouts. It never raises the gain on room noise. |
| **Compressor** | Soft-knee compression for fast peaks. |
| **De-esser** | A split-band (Linkwitz-Riley) sibilance reducer that only acts while you speak normally, never on whispers. |
| **Limiter** | A lookahead brickwall limiter so shouting never clips. |

### The app

- **Profiles**: Podcast, Whisper / ASMR, Loud / Gaming and Flat. Edit any of them, then Save, Save as, Revert or Delete. Changes apply live while you drag a slider. Profiles are JSON files in `%APPDATA%\MicOptimizer\profiles`.
- **Tray**: closing the window keeps processing in the tray. The menu has Start/Stop, Bypass, a profile switcher and Start with Windows.
- **Start with Windows**: starts hidden in the tray at sign-in and reconnects by itself if your interface or the virtual cable is not ready yet, or is unplugged and plugged back in.
- **Calibrate…** (window button or tray menu): a 40-second guided wizard that measures your room, keyboard and voice and sets the gate for you (see [Calibration](#calibration)).
- **Find my mic**: speak for three seconds and it picks the right input channel of your interface.
- **Latency readout**: shows exactly where the milliseconds go (see [Latency](#latency)).

## Requirements

- Windows 10 or 11, 64-bit.
- [VB-Cable](https://vb-audio.com/Cable/) (free virtual audio device). Mic Optimizer sends the processed audio to *CABLE Input*, and your apps use *CABLE Output* as their microphone. The app shows a banner with a download button if it is missing.
- A microphone on an audio interface or any Windows recording device running at **48 kHz** (the same rate as your output device; RNNoise requires 48 kHz).
- Optional: your interface's ASIO driver, for the lowest input delay.

## Install

**From source** (see [Build](#build)): run `build-installer.ps1` to produce `artifacts\installer\MicOptimizer-Setup-<version>.exe`. The installer is per-user (no administrator rights), self-contained (no .NET runtime to install) and offers Start with Windows and a desktop shortcut. It is **unsigned**, so Windows SmartScreen will show an "unknown publisher" warning.

Or just run it without installing: `dotnet run --project src/MicOptimizer.App`.

### Installer options

| Option | Default | What it does |
|---|---|---|
| Start Mic Optimizer hidden in the tray when I sign in | on | Adds a per-user sign-in entry that starts the app with `--background` |
| Create a desktop shortcut | off | |
| **Add Mic Optimizer to my PATH** | off | Appends the install folder to your **user** PATH (no administrator rights, the machine PATH is never touched), so `MicOptimizer` works in any new terminal |

The PATH option never adds a duplicate, leaves every other entry (including `%VARIABLES%`) exactly as written, and is removed again when you uninstall. The tray menu has the same switch (*Add to PATH*), which also works without the installer. Terminals that were already open do not see the change; open a new one.

From a terminal: `MicOptimizer` opens the window, and `MicOptimizer --background` starts it hidden in the tray. A second launch just brings up the running copy.

## Quick start

1. Install VB-Cable, then open Mic Optimizer.
2. Choose your microphone. If you are not sure which input of your interface carries it, press **Find my mic** and speak.
3. Leave **Send to** on *CABLE Input*.
4. In Discord, OBS, Teams or your recorder, choose **CABLE Output (VB-Audio Virtual Cable)** as the microphone.
5. Turn off the *Direct Monitor* button on your interface if you want to hear only the processed sound in your headphones.
6. Press **Calibrate…** so the gate suits your room and voice (about 40 seconds).
7. Pick a profile. Use **Bypass** to compare against the raw mic.

On Windows 11 new tray icons start collapsed under the `^` arrow; drag the icon out once to keep it visible.

## Calibration

Every room, keyboard and voice is different, so the gate's thresholds are worth measuring rather than guessing. **Calibrate…** walks through up to five short steps, each with a live level meter and a countdown:

| Step | Length | |
|---|---|---|
| Quiet room | 5 s | required |
| Typing and clicking | 6 s | optional |
| Whisper | 6 s | optional |
| Normal voice | 8 s | required |
| Loud | 5 s | optional |

While it listens the output to VB-Cable is **silenced**, so a call or stream never hears the test sounds. The recordings stay in memory and are discarded afterwards.

It then runs your recordings through the same front of the chain the live audio takes (input gain, rumble cut, noise removal) and tries about a hundred gate settings on them with the real gate. A setting must stay shut in the quiet room and keep nearly all of your voiced speech and shouting. Among those, it keeps as much of the whisper as it can while letting as little typing through as it can. When your typing and whispers overlap, you choose whether *Keep quiet speech*, *Balanced* or *Block more noise* should win; the choice only appears when it makes a difference for your recordings.

The result page shows the old and new gate values side by side. **Apply** puts them into the profile you are editing, marked *Edited*, so you can Save, Save as or Revert. Only the gate's open threshold, close threshold and hold time change. The page also gives plain advice from what it measured, for example "your voice is quiet at the input, turn the interface gain up by about 12 dB", "your loudest sounds clipped", or "your voice is only 14 dB above the room noise, move the mic closer".

Validated two ways: on the author's real recordings it chose settings within 2 dB of what took a long manual tuning session, and unit tests cover synthetic rooms, voices, keyboards and whispers. It calibrates the gate only; the other stages keep the profile's values.

## Latency

The window shows a live breakdown. Measured on the author's setup (Scarlett Solo, VB-Cable at its default setting):

| | ASIO input | Windows audio |
|---|---|---|
| Mic input | 3.9 ms | 10 ms |
| Processing (noise removal 10 ms + limiter 1 ms) | 11 ms | 11 ms |
| Buffer and output | about 8 ms | about 9 ms |
| **Delay inside Mic Optimizer** | **about 24 ms** | **about 30 ms** |
| VB-Cable's own buffering (measured separately) | about 60 ms | about 60 ms |

How it gets there:

- Windows' low-latency shared-mode streams with real-time thread priority.
- An optional **ASIO input** (Latency mode *Lowest*, the default), which falls back to Windows audio automatically if the driver cannot be opened. Other apps could still record from and play to the interface while ASIO was open.
- A small drift-correcting buffer between the two devices' clocks. When the clocks disagree it drops or repeats a single sample every few hundred samples (inaudible on speech), so the delay neither grows nor runs dry. Its cushion starts at about 6 ms and grows only if audio ever runs dry.

**The largest remaining delay belongs to VB-Cable.** A click written straight into CABLE Input takes about 60 ms to appear on CABLE Output with none of this app involved. VB-Cable's control panel has a *Max Latency* setting (the **Reduce VB-Cable delay** button opens it; changing it needs administrator rights). The `latency` command of the probe tool prints the cable-only number so you can see the effect of changing it.

Turning noise removal off (the Whisper profile does) saves another 10 ms.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
dotnet build
dotnet test                       # 56 tests: DSP stages, profiles, the elastic buffer, calibration, PATH editing
powershell -ExecutionPolicy Bypass -File build-installer.ps1   # tests, publish, installer
```

`build-installer.ps1` needs [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). `tools\make-icon.ps1` regenerates the app icon.

### Project layout

```
src/MicOptimizer.Core    DSP stages, the processing chain, profiles, calibration (no audio devices, fully testable)
src/MicOptimizer.Audio   WASAPI and ASIO capture, the render path, the elastic buffer, latency accounting
src/MicOptimizer.App     WPF window, tray, settings, start-with-Windows
tests/                   xUnit tests (synthetic tones, drift simulations)
tools/MicOptimizer.Probe Command-line developer tool (below)
installer/               Inno Setup script
```

### Probe tool (developer utility)

`dotnet run --project tools/MicOptimizer.Probe -- <command>` is how the defaults were measured and tuned:

| Command | What it does |
|---|---|
| *(none)* | List input and output devices |
| `run [seconds] [asio] [bypass]` | Run the engine with a text meter and glitch counter |
| `calibrate` | Records silence, typing, whisper, speech and shouting to `calibration/` (git-ignored). This is the developer version; the app has its own wizard |
| `autocal` | Runs the app's calibration algorithm on those recordings and prints the result for each bias |
| `offline`, `tune`, `dyn` | Run the recordings through the chain and compare or search settings |
| `mutetest` | Checks that muting really silences what arrives on CABLE Output |
| `pathcheck` | Adds and removes a dummy folder on your real user PATH and checks it is restored exactly |
| `meter`, `diag` | Windows' own level meter versus raw capture, per channel |
| `latency`, `jitter` | Click-to-click delay of the old and new pipelines, the cable-only floor, callback timing |
| `latinfo`, `asioinfo` | What each device and ASIO driver supports |

The calibration recordings are your own voice and are never committed (`*.wav` and `calibration/` are ignored).

## Status and limits

- **Tested on one setup**: Blue Spark + Focusrite Scarlett Solo (Focusrite USB ASIO) + VB-Cable on Windows 11. The defaults were tuned against that voice and room; expect to adjust the gate thresholds for yours.
- Only the **Podcast** profile was tuned against recordings. Whisper / ASMR and Loud / Gaming are reasoned starting points. Run **Calibrate…** to fit the gate to your own room and voice.
- Calibration has been tried with the author's voice and with synthetic sounds; the in-app wizard has not yet been run by other people on other microphones.
- **Mono**: one input channel is used. 48 kHz only for noise removal.
- WASAPI exclusive mode does not work with the Focusrite driver (it rejects the formats), so it is not offered.
- It is a tray app, not a Windows Service: services run in session 0 and Windows often blocks microphone access there.
- The installer is unsigned.

## Credits and license

Mic Optimizer is released under the [MIT License](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): [NAudio](https://github.com/naudio/NAudio), [RNNoise](https://gitlab.xiph.org/xiph/rnnoise) and its .NET wrapper. VB-Cable is a separate product by VB-Audio, installed by you and not included.
