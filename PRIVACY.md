# Privacy policy

BetterSTT does not collect, send or share any personal data.

**What stays on your PC.** Your voice is recorded only while dictation is on, and it's transcribed on your own computer. Audio is never sent anywhere. The following are stored only in `%LOCALAPPDATA%\BetterSTT`:
- your settings
- your last three dictations and the name of the app each went into (e.g. "Discord"), which you can clear at any time from the Home page
- your word fixes and per-app rules
- a log of timings, word counts and outcomes (such as "cancelled"), which never contains what you said
- the recording of a dictation in progress, in the `pending` folder, so it survives a crash. It's deleted as soon as the dictation is transcribed or cancelled. If transcription fails, it's kept until you retry or discard it from the Home page.

**The one network connection.** The first time a speech model is used, BetterSTT downloads it from [Hugging Face](https://huggingface.co). Only the model file is requested; nothing about you or your dictations is sent. Hugging Face's handling of that request is covered by the [Hugging Face privacy policy](https://huggingface.co/privacy). No other connections are made: no analytics, no telemetry, no update checks.

**Uninstalling.** The uninstaller offers to delete everything in `%LOCALAPPDATA%\BetterSTT`.
