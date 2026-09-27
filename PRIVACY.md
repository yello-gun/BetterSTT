# Privacy policy

BetterSTT does not collect, send or share any personal data.

**What stays on your PC.** Your voice is recorded only while dictation is on, and it's transcribed on your own computer. Audio is never sent anywhere. The following are stored only in `%LOCALAPPDATA%\BetterSTT`:
- your settings
- your last three dictations and the name of the app each went into (e.g. "Discord"), which you can clear at any time from the Home page
- your word fixes, snippets and per-app rules
- a log of timings, word counts and outcomes (such as "cancelled"), which never contains what you said
- the recording of a dictation in progress, in the `pending` folder, so it survives a crash. It's deleted as soon as the dictation is transcribed or cancelled. If transcription fails, it's kept until you retry or discard it from the Home page.
- a downloaded update installer, in the `updates` folder, until it's installed

**Network connections.** BetterSTT makes two kinds of requests, and neither contains anything about you or your dictations:
- **Speech model download.** The first time a speech model is used, BetterSTT downloads it from [Hugging Face](https://huggingface.co). Only the model file is requested. Hugging Face's handling of that request is covered by the [Hugging Face privacy policy](https://huggingface.co/privacy).
- **Update check.** Once a day, BetterSTT asks GitHub for the latest release of BetterSTT, and if there's a newer version it downloads the installer from GitHub Releases. The request identifies itself only as BetterSTT and its version number. GitHub's handling of these requests is covered by the [GitHub privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). You can turn automatic checks off under **General → Updates and help**. Then nothing is requested unless you click **Check now**.

There are no analytics and no telemetry.

**Diagnostics.** The **Save diagnostics** button writes a .zip file to a folder you choose. It isn't sent anywhere; you decide whether to share it. It contains the log, your app, Windows and GPU details, and your settings with your vocabulary, word fixes and snippets replaced by "(hidden)". Your Windows user name is removed from file paths. It never contains anything you dictated.

**Uninstalling.** The uninstaller offers to delete everything in `%LOCALAPPDATA%\BetterSTT`.
