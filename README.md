# BetterSTT

System-wide dictation for Windows that removes filler words before your text reaches any app. Say it however it comes out; what gets typed is clean.

![Home screen](docs/screenshots/home-dark.png)

Press **Ctrl + Alt + Space** in any app to start listening, and press it again to stop. The cleaned text is pasted wherever your cursor is. The shortcut works at any time, including while the BetterSTT window is open. If BetterSTT itself has focus, the text is copied to the clipboard instead of pasted.

<p>
  <img src="docs/screenshots/overlay-listening-light.png" alt="Listening indicator" height="72">
  <img src="docs/screenshots/overlay-done-light.png" alt="Pasted confirmation" height="72">
</p>

## Built to be relied on

- **Press or hold.** Press the shortcut to start and again to stop, or switch to hold-to-talk and speak while the keys are held down.
- **Your last word isn't cut off.** It keeps listening for a moment after you stop (adjustable).
- **Esc cancels** a dictation without typing anything.
- **Nothing is lost.** Each recording is saved to disk while you speak. If the app crashes or transcription fails, the Home page offers to retry it.
- **Paste last dictation** (Ctrl + Alt + Shift + V) types your most recent dictation again, for when it went to the wrong window.
- **Admin windows are handled.** Windows blocks other apps from typing into programs running as administrator. BetterSTT detects this and puts the text on the clipboard instead of silently losing it.
- **Light on memory if you want.** It can unload the speech model after a period of idle time, and reloads it when you next dictate.
- **Keeps itself up to date.** New versions download in the background. A banner offers **Install and restart**; if you ignore it, the update installs the next time you open BetterSTT. Updates keep your settings, models and shortcuts.

## Make it yours

- **Word fixes.** If it keeps writing "git hub", click the word in a recent dictation and type "GitHub". From then on it's fixed, and the word is also given to the speech model as a hint. All fixes are listed on the Dictionary page.
- **Snippets.** Say "my email" on its own and your address is typed instead. Snippets can be several lines long, like a signature.
- **Per-app rules.** Different settings for different apps: exact words in your code editor, no trailing space in the terminal, typing instead of pasting where paste is blocked, no indicator in games, or Spanish in WhatsApp and English everywhere else.
- **Writing styles.** *Exact words* types what you said, *Natural* removes fillers and stutters, *Formal* also drops vague endings and adds paragraphs, and *Math* writes spoken math as symbols ("x squared plus one over two" → x² + 1/2) or LaTeX. You can edit any style, create your own, and switch from the Home page or the tray.
- **See it as you say it.** A live draft of your words appears in the indicator while you talk.
- **Put the indicator where you like.** Any corner or edge, on the screen with the mouse or always on the main one.

![Dictionary page](docs/screenshots/dictionary-dark.png)

## What gets removed

Every rule can be switched off, and every word list can be edited, on the Style page, which also has a live "Try it" box.

| Rule | Example |
|---|---|
| Filler sounds | "Um, I think, uh, we should" → "I think we should" |
| Pause marks | "I was thinking... maybe" → "I was thinking maybe" |
| Verbal fillers (only when they stand alone between commas) | "It was, like, huge" → "It was huge" (but "I like pizza" stays) |
| Stutters and restarts | "I-I think the the plan" → "I think the plan"; "I think I think we should" → "I think we should"; "we were go- going" → "we were going" |
| Non-speech tags | "[BLANK_AUDIO]", "(music)" → removed |
| Vague endings (Formal style) | "we could grab lunch or something" → "we could grab lunch" |

The **Formal** style also splits longer text into paragraphs, with a blank line between them. A new paragraph starts when you change topic ("Also…", "Next…", "Finally…"), after a greeting ("Hi Sarah,") and before a sign-off ("Thanks."), and when a paragraph reaches its sentence limit. These cues come from your wording: BetterSTT uses rules, not an AI model, so it doesn't rewrite what you meant.

![Style page](docs/screenshots/style-light.png)

## Private and local

Speech recognition runs **on your PC** with [Whisper](https://github.com/openai/whisper) via [Whisper.net](https://github.com/sandrohanea/whisper.net). It uses the GPU through Vulkan when one is available and falls back to the CPU otherwise. Audio never leaves your computer. The only network access is a one-time download of the speech model you pick, from Hugging Face, and a daily check of this repository's Releases for a new version, which you can turn off.

![Speech page](docs/screenshots/speech-light.png)

## Install

Download `BetterSTT-Setup-<version>.exe` from [Releases](../../releases) and run it. No admin rights are needed. Releases made before code signing was set up aren't signed, so Windows SmartScreen may ask you to confirm them (**More info → Run anyway**). See [Code signing policy](#code-signing-policy).

Installing a newer version upgrades in place and keeps your settings, downloaded models and shortcuts. From 2.6 on, BetterSTT updates itself (see above).

![Update banner](docs/screenshots/update-banner-dark.png)

## Build from source

Requirements: Windows 10/11 x64, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

This runs the tests, publishes a self-contained build to `publish\`, and writes the installer to `dist\`. Official releases are built by the [Release workflow](.github/workflows/release.yml) on GitHub, not on a developer's PC.

```
src/BetterSTT/           the app (.NET 8, WPF + WPF-UI Fluent design, lives in the tray)
  DictationController.cs the record → transcribe → clean → paste pipeline and settings changes
  TextCleaner.cs         filler-removal rules
  History.cs             last 3 dictations + lifetime stats, and the word diff shown in the UI
  Transcriber.cs         Whisper model download / load / transcribe
  SpokenMath.cs          spoken math → symbols or LaTeX
  Updater.cs             update check, download and verification against GitHub Releases
  Diagnostics.cs         the "Save diagnostics" bundle
  AudioRecorder.cs       mic capture, silence trimming, level meter
  Native.cs              global hotkey, paste/typing into other apps, startup registration
  Tray.cs                tray icon, menu and sounds
  UI/                    main window (Home, Style, Dictionary, Apps, Speech, General) and the on-screen pill
tests/BetterSTT.Tests    unit tests: cleanup rules, styles, snippets, spoken math, updates, diffs
installer/               Inno Setup script
tools/                   build script, icon generator
.github/workflows/       release workflow: build on GitHub, sign with SignPath, publish
.signpath/               SignPath artifact configuration (which file is signed, and its checks)
docs/screenshots/        images for this README (regenerate with --screenshots)
```

### Command line

- `--toggle`, `--paste-last` and `--cancel` start or stop a dictation, paste the last one, or cancel, in the copy that's already running (it's started in the tray if it isn't). Use them from a launcher, a Stream Deck button or a script.
- `--background` starts in the tray without opening the window (used by the Windows startup entry).
- `--transcribe input.wav result.txt` transcribes a 16 kHz mono WAV file and reports the runtime and timing.
- `--screenshots <folder>` renders every page in light and dark, plus the on-screen pill, to PNG files. It uses sample data.
- `--no-paste` sends every dictation to the clipboard only and never types into a window. It's for automated testing.

## Privacy

BetterSTT doesn't collect or send any personal data. Audio and text never leave your PC. The only network connections are the one-time speech model download from Hugging Face and the daily update check on GitHub, which can be turned off. Details are in the [privacy policy](PRIVACY.md).

Settings, the last 3 dictations (`history.json`), a log (timings, word counts and outcomes, never the dictated text), downloaded models and any not-yet-transcribed recording (`pending\`) are stored in `%LOCALAPPDATA%\BetterSTT`. Uninstalling asks whether to delete them.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Committers and reviewers:** [yello-gun](https://github.com/yello-gun)
- **Approvers:** [yello-gun](https://github.com/yello-gun)

Signed installers are built from this repository by the [Release workflow](.github/workflows/release.yml) on GitHub-hosted runners, and every signing request is approved by hand. Contributions from outside the team are reviewed before they're merged. Only BetterSTT's own installer is signed; the third-party open-source libraries it bundles are included as published by their projects.

Signing applies to releases made after the SignPath Foundation approves the project. Earlier installers are unsigned.

## License

[MIT](LICENSE). Third-party components: Whisper.net and whisper.cpp (MIT), the Whisper model weights (MIT, OpenAI), WPF-UI (MIT) and NAudio (MIT).
