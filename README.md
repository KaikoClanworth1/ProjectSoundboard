# Project Soundboard

A Windows desktop soundboard built for Discord, VRChat, Steam voice, TeamSpeak, Mumble, OBS
and in-game chat. Point it at the folders your sounds already live in, and it plays them
into a virtual microphone your friends hear — while you keep hearing your game.

Built with **.NET 10 / WPF** and **NAudio (WASAPI)**.

> ### Built with AI
>
> This application was written by **Claude Opus 5** working in **Claude Code**, directed by a
> human across a series of conversations — the architecture, the DSP, the UI and this README
> included.
>
> That is worth stating plainly rather than hiding. Some of it went well: the Ogg-Opus
> decoder, the virtualizing wrap panel and the microphone feedback-loop guard were all real
> engineering that landed correctly. Some of it needed several passes, and a few bugs were
> only found by running the app and looking at it. Every feature listed below was manually
> exercised in the running application, and the **Known limitations** section at the bottom is
> deliberately honest about what is shallower than it sounds.
>
> Treat it as you would any code from a capable contributor you have not met: read it before
> you trust it.

---

## Download

Grab the latest zip from the [Releases page](https://github.com/KaikoClanworth1/ProjectSoundboard/releases),
unpack it anywhere you like, and run `ProjectSoundboard.exe`. There is no installer — it is a
portable app, so unpacking *is* the install.

**Requirements**

- Windows 10 (1607 or newer) or Windows 11
- [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) — a small
  one-time install. Releases are framework-dependent to keep update downloads at a few MB
  rather than 150 MB.
- A virtual audio cable, if you want sounds to reach voice chat. The setup wizard offers to
  install one for you — see below.

Releases are **not code-signed**, so Windows SmartScreen will warn the first time you run it.
Click *More info → Run anyway*, or check the file against the Releases page first.

## Building from source

```bash
git clone https://github.com/KaikoClanworth1/ProjectSoundboard.git
cd ProjectSoundboard
dotnet run --project src/ProjectSoundboard.App
```

Needs the .NET 10 SDK. There are no other prerequisites — NuGet restores everything else.

### Portable by design

Everything user-specific — settings, the library index, custom names, artwork, waveform
caches, backups and logs — lives in a **`Data`** folder next to the executable. Copy the
folder to a USB stick and your setup travels with it.

Your **sound files are never moved**. The library only stores paths to them, so your music
stays wherever you keep it.

If the app is installed somewhere read-only (Program Files, say), it falls back to
`%APPDATA%\Project Soundboard` automatically. An existing `%APPDATA%` profile is *copied*
into the portable folder on first run, never moved, so nothing is lost either way.

### Updating

On launch (at most once a day) the app asks GitHub whether there is a newer release. If there
is, it shows you the release notes and asks — it never updates itself silently. Choosing
*Update now* downloads the release, waits for the app to close, swaps the program files, and
restarts. The `Data` folder is explicitly excluded from the swap, so settings and artwork
survive.

You can also check manually, or skip a version, from **Settings → General → Updates**.

Releases are **not code-signed**, so Windows SmartScreen will warn on first run. Verify you
are downloading from the official repository.

On first launch a setup wizard walks through theme, sound folders, devices, the virtual
microphone, hotkeys and a playback test. Everything it asks can be changed later.

### You need a virtual audio cable

Windows has no built-in way to feed audio into a microphone, so a small free driver acts as
a cable. The setup wizard offers to install [VB-CABLE](https://vb-audio.com/Cable/) for you:
it downloads VB-Audio's official package, verifies the Authenticode signature really is
theirs, and hands it to Windows' elevation prompt. Nothing is bundled and nothing installs
silently — if the download, the signature check or the URL fails, it just opens the download
page instead.

Once a cable is present the app finds it, works out which recording device pairs with it,
and shows you the exact name to select elsewhere:

| Where | Set this |
|---|---|
| Project Soundboard → Audio → output | the cable's **playback** half (VB-CABLE calls it `CABLE Input`) |
| Discord → Voice & Video → Input device | the cable's **recording** half (`CABLE Output`) |
| VRChat → Audio & Voice → Microphone | same recording device |
| OBS → Audio Input Capture source | same recording device |

The Audio page shows that name in a copyable box, so you never have to guess which half is
which — the naming is genuinely inverted, and multi-cable setups are matched by pairing
`… Input`/`… In` with `… Output`/`… Out` under the same driver.

In Discord also turn **Noise Suppression**, **Echo Cancellation** and **Automatic Gain
Control** off — they are tuned for speech and will chew up sound effects.

Turn on **microphone passthrough** (Mic page) and the cable carries your voice *and* your
sounds, so the voice app only ever needs the one device selected.

Without a cable the app still works — it just plays to your own speakers only.

### About the device name

Inside Project Soundboard the cable is labelled **Project Soundboard Output**, with the real
Windows name always shown underneath or on hover. Other applications will still show the
driver's own name, because that name comes from the driver.

Shipping endpoints genuinely called "Project Soundboard Input/Output" means shipping a signed
kernel-mode audio driver — which needs an EV code-signing certificate, a Microsoft Partner
Center hardware account and attestation signing per build. The two realistic routes are
OEM-licensing a renamed build from VB-Audio, or building one from Microsoft's SysVAD sample.
Neither is done here; the aliasing is presentation only, and never hides a name you have to
type somewhere else.

---

## What it does

**Folder library.** No importing files one at a time. Choose folders; they are scanned
recursively in parallel and watched with `FileSystemWatcher`, so files you add or delete in
Explorer show up without a restart. MP3, WAV, FLAC, OGG, M4A, AAC, WMA, Opus and AIFF.

Ogg-Opus (`.opus`, what yt-dlp and most YouTube rippers produce) is decoded with Concentus,
because Windows Media Foundation only understands Opus inside WebM/Matroska and NAudio.Vorbis
decodes Vorbis rather than Opus. Without that, `.opus` files look perfectly healthy in the
library — TagLib# reads their tags and duration fine — and then silently refuse to play.

**Search.** Instant, fuzzy, across display names, file names, tags and group names, with
recent searches and favourites weighting. `wlhm` finds `Wilhelm Scream`.

**Display names that never touch your files.** Rename anything in the app; the file on disk
keeps its original name. Renaming the actual file is possible but off by default behind
Settings → Advanced.

**Artwork.** Upload, drag, or paste an image per sound, plus emoji. Sounds without artwork
get a coloured letter tile derived deterministically from the file name, so a sound always
looks the same.

**Groups and tags.** Nestable groups, multiple tags per sound, all virtual — nothing on disk
moves.

**Per-sound playback settings.** Volume, speed, loop, fade in/out, trim start/end,
peak normalisation, with a waveform preview you can click to seek.

**Audio routing.** Two simultaneous WASAPI outputs (virtual mic + your headphones), master
limiter / compressor / 5-band EQ, live peak+RMS metering on a dB scale, adjustable buffer,
latency readout.

**Microphone passthrough.** Capture → noise suppression → gate → compressor → limiter →
virtual cable, with input/output gain, boost, mono fold, self-monitoring, push-to-talk, and
a gate calibration that listens to your room and sets the threshold for you.

**Global hotkeys.** Play a specific sound, stop all, pause, next/previous, random, mute mic,
mute soundboard, toggle passthrough, push-to-talk, volume, show/hide. Conflicts with other
applications are detected and flagged instead of silently failing.

**Drag & drop import.** Drop files or whole folders and choose: copy into your main sound
library (recommended), or index them where they are. Per-file conflict resolution
(replace / keep both / skip, with apply-to-all) and a remember-my-choice option.

**Accessibility.** Dark, light, system and high-contrast themes; UI scaling; large text;
reduced motion; colour-blind palettes; keyboard navigation with a visible focus ring; and
automation names throughout for screen readers. Status is never conveyed by colour alone.

**Backup.** One `.psbackup` archive holds settings, groups, display names, tags, per-sound
settings and thumbnails. Automatic safety copies are taken before anything destructive.

---

## Performance notes

Designed for libraries in the tens of thousands.

- **Virtualizing wrap panel.** WPF ships no virtualizing `WrapPanel`, and a plain one
  realises every tile. `Controls/VirtualizingWrapPanel.cs` implements `IScrollInfo` with
  uniform tile sizing, so only the visible rows (plus a small cache) exist as visuals.
- **Sound cache.** Files are decoded once into memory at the mix format under an LRU byte
  budget; triggering a cached sound costs a memory read, not a file open and decode. Long
  files stream from disk instead.
- **Preloading.** Favourites and frequently played sounds are decoded in the background at
  startup.
- **Header-only metadata.** Duration/bitrate/sample rate come from TagLib# headers, not by
  decoding, so a 20,000-file scan stays fast. Scanning is parallel across folders.
- **Thumbnails decoded at tile size** under their own LRU budget, not at full resolution.
- **Waveforms cached to disk**, keyed by path + mtime + size.
- **Atomic JSON writes** with `File.Replace`, plus a `.bak`, so a crash mid-save cannot
  corrupt your library.

---

## Layout

```
src/
  ProjectSoundboard.Core/     Models, JSON storage, library scanning, search, import, backup
    Models/                   SoundEntry, SoundGroup, LibraryFolder, AppSettings, hotkeys
    Library/                  LibraryService, SearchService, ImportService, BackupService,
                              FuzzyMatcher, ImageStore, metadata reader
    Storage/                  AppPaths, JsonStore, SettingsService, Log
  ProjectSoundboard.Audio/    NAudio engine — no UI dependencies
    Dsp/                      Limiter, Compressor, NoiseGate, NoiseSuppressor, Equalizer,
                              LevelMeter
    Playback/                 SoundCache, VoiceBase, CachedVoice, StreamingVoice,
                              PlaybackHandle, FloatRingBuffer
    AudioEngine.cs            Two output buses, playback modes, voice management
    MicPassthrough.cs         WASAPI capture → DSP → virtual cable
    AudioDeviceService.cs     Endpoint enumeration, virtual cable detection, hot-plug
  ProjectSoundboard.App/      WPF front end
    Themes/                   Dark, Light, HighContrast palettes + control styles
    Controls/                 VirtualizingWrapPanel, LevelMeterControl, WaveformControl
    ViewModels/               MVVM via CommunityToolkit.Mvvm
    Views/                    Library, Audio, Microphone, Hotkeys, Settings, setup wizard,
                              import and conflict dialogs
    Services/                 Theme, global hotkeys, image cache, composition root
```

`Core` and `Audio` target plain `net10.0` and have no WPF dependency, so the engine is
reusable and testable outside the UI.

Data lives in `%APPDATA%\Project Soundboard` — `settings.json`, `library.json`, `images/`,
`waveforms/`, `backups/`, `logs/`.

---

## Known limitations

- **Playback speed changes pitch**, like a tape. There is no time-stretch; a pitch-preserving
  resampler was out of scope.
- **"Echo cancellation" is not true AEC.** It ducks your microphone while the soundboard is
  loud, which stops speaker bleed being sent back doubled. With headphones you do not need
  it. Real acoustic echo cancellation needs reference-signal alignment, which is not
  implemented.
- **Noise suppression is a broadband downward expander**, not a spectral or ML suppressor.
  It handles steady hiss and fan noise well; it will not remove someone talking in the next
  room.
- **WASAPI shared mode only.** Exclusive mode would lock other applications out of the
  virtual cable — exactly the device that needs sharing. Low-latency mode shrinks the buffer
  instead.
- **Push-to-talk uses a low-level keyboard hook**, because `RegisterHotKey` cannot report key
  release. The hook only inspects the single key you assigned. Some anti-cheat software is
  suspicious of any keyboard hook; leave push-to-talk off if that is a concern.
- **Update checking is a setting, not an implementation** — there is no release feed behind it.
- The generated tile colour, duplicate detection (size + duration + name) and the latency
  figure are all heuristics, not exact.

---

## Licence

Licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE).

In short: you may use, study, modify and share this freely. If you distribute a modified
version, or anything built on it, that has to be open source under GPL-3.0 as well.

### Third-party components

All of these are GPL-3.0 compatible:

| Component | Licence | Used for |
|---|---|---|
| [NAudio](https://github.com/naudio/NAudio) | MIT | WASAPI output and capture, resampling, biquad filters |
| [NAudio.Vorbis](https://github.com/naudio/Vorbis) | MIT | Ogg Vorbis decoding |
| [Concentus](https://github.com/lostromb/concentus) | BSD-3-Clause | Opus decoding |
| [Concentus.Oggfile](https://github.com/lostromb/concentus.oggfile) | MIT | Ogg container parsing for `.opus` |
| [TagLib#](https://github.com/mono/taglib-sharp) | LGPL-2.1 | Reading duration, bitrate and sample rate from tags |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM source generators |

TagLib# is LGPL and used as a separate assembly, which is exactly the arrangement the LGPL is
written for.

**VB-CABLE is not included.** It is a free-for-personal-use driver by
[VB-Audio](https://vb-audio.com/Cable/), and Project Soundboard neither bundles nor modifies
it — the setup wizard downloads VB-Audio's own installer, verifies its signature, and hands it
to Windows to install.
