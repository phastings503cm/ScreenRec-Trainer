# ScreenRec Trainer

[![build-and-release](https://github.com/phastings503cm/ScreenRec-Trainer/actions/workflows/release.yml/badge.svg)](https://github.com/phastings503cm/ScreenRec-Trainer/actions/workflows/release.yml) · **[Download the latest release](https://github.com/phastings503cm/ScreenRec-Trainer/releases/latest)**

A C# desktop app for **Windows and macOS** that records how an expert works, has AI turn the recording into training
material, and puts the result into **Guardian**: as a skill (so a person, or Guardian's AI, can learn to work the way the
expert does) and as a Guardian **automation**.

> "I want her to start recording what she does and try to train a person in Guardian to be an expert at her stuff."

```
 Record (local)  ──►  AI processing  ──►  Review  ──►  Guardian
 screen + clicks       GLM-5.3 Flash on      training      • skill  (SKILL.md: steps, rules, pitfalls)
 keys, app switches    DGX Sparks, or        guide (HTML)  • automation (DESKTOP runbook, optional cron)
 the expert's notes    Guardian's models,               • Video intake (Guardian's own analysis)
                       or any OpenAI-compatible server     • expert profile across all recordings
```

## The three systems it talks to

| System | What the app uses |
|---|---|
| [GLM-5.3-Flash-EXL3 on 2× DGX Spark](https://github.com/MiaAI-Lab/GLM-5.3-Flash-EXL3-2x-DGX-Sparks) | The recipe's OpenAI-compatible API at `http://<head>:8888/v1`, model `GLM-5.3-Flash-EXL3`, image input, `chat_template_kwargs.reasoning_effort` (default `low`: the recipe advises it for long structured output), optional `VLLM_API_KEY`. Tested against a live Spark. |
| [Vision for Windows](https://github.com/phastings503cm/vision-windows) | Same conventions, so the two work together: Guardian's harness sign-in (password + TOTP, or browser PKCE with `client=vision-windows`/`vision-mac`), `x-guardian-org`, Guardian's model proxy, and the `SKILL.md` skill format. A Spark Swapper route (`https://<spark>:11443/v1`) works as "Other OpenAI-compatible server". |
| [Guardian](https://guardian.503cm.com) | Sign-in (`/api/auth/login`, `/api/auth/login/2fa`, `/app/connect/vision` → `/api/harness/connect/token`), `/api/harness/me`, `/api/harness/v1/models` (and chat completions through it), `/api/harness/skills`, `/api/harness/prompts`, uploads (`/api/uploads/init` → PUT → `/finalize`, multipart above 50 MB), **Video intake** `POST /api/videos {attachmentId, kind:"WORKFLOW"}` and **Automations** `POST /api/automations {name, description, prompt, mode:"DESKTOP", cron, timezone}`. |

## Using it

1. **Guardian tab:** sign in with your Guardian email and password (plus the 6-digit code if your account has 2FA), or
   use **Sign in with your browser** for Microsoft/SSO accounts. Guardian asks which organization to connect. You can
   revoke the sign-in in Guardian under *My profile › API keys › Connected apps*. The token is kept in the macOS Keychain
   (Windows: DPAPI) and never in a plain file.
2. **Settings tab:** pick the model server. The default is the GLM Spark: enter the head's address, e.g.
   `http://192.168.68.72:8888/v1`. Use **Save and test the model** to check it.
3. **Record tab:** enter the expert's name, the task, and one sentence on what she is about to do. Press **Start recording**
   and do the task normally. Use **Add note** to explain decisions as you go. Press **Stop** when done.
4. **Recordings tab:** **Process with AI**. Each step is described from screenshots taken just before and after it (a red
   ring marks the click), and the AI writes the procedure, the decision rules ("when … then …"), the pitfalls, open
   questions for the expert, and an automation runbook. **Open training guide** opens a single HTML file with the
   screenshots inside it. **Publish to Guardian…** creates the skill and the automation, and can also upload the video to
   Guardian's Video intake.
5. **Expert tab:** once several tasks are processed, **Build and save to Guardian as a skill** combines them into one
   expert profile skill (`<name>-expert`), with every procedure attached under `procedures/`. This is the "train a person
   to be an expert at her stuff" step.

Everything stays on the computer until you process (frames go to the model server you picked) or publish (to Guardian).
By default the text you type is **not** recorded, only that typing happened; turn it on in Settings if you need it.

### Permissions

* **macOS:** allow *Screen & System Audio Recording* and *Input Monitoring* for **ScreenRec Trainer** in System Settings ›
  Privacy & Security, then reopen the app. The Record tab shows the current state and has an **Ask macOS for access**
  button. If you run from Terminal, the permission goes to Terminal instead. `screenrec check-input` shows live whether
  clicks are being seen. Without Input Monitoring the screen is still recorded and the AI works from screen changes.
* **Windows:** nothing to grant. The app can't see input in windows running as administrator unless it runs as
  administrator too.

## Command line

`screenrec` uses the same engine as the app:

```
screenrec record --seconds 120 --title "Month-end close" --about "…" --expert "Maria"
screenrec demo                       # a made-up expense-approval recording, to try the pipeline safely
screenrec process latest --provider spark --url http://192.168.68.72:8888/v1
screenrec login [--browser]          # Guardian (GUARDIAN_PASSWORD env var for scripts)
screenrec publish latest [--cron "0 9 * * 1-5"] [--no-video]
screenrec expert Maria --publish
screenrec check-input | test-ai | whoami | models | list | settings
```

## Install

Download from **[the latest release](https://github.com/phastings503cm/ScreenRec-Trainer/releases/latest)**:

* **Windows:** run `ScreenRecTrainer-v*-win-x64.msi` (or `-win-arm64.msi`). It installs the desktop GUI plus the
  `screenrec` CLI into Program Files, adds a Start Menu shortcut and puts the CLI on PATH. A portable `.zip` is
  there too if you don't want to install anything.
* **macOS:** open `ScreenRecTrainer-v*-osx-arm64.dmg` (Apple silicon) or `-osx-x64.dmg` (Intel) and drag
  **ScreenRec Trainer** to Applications. First launch: right-click the app › **Open** (the build is ad-hoc signed,
  not notarized). Then allow *Screen Recording* and *Input Monitoring* in System Settings › Privacy & Security.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build ScreenRec.sln
dotnet test  tests/ScreenRec.Core.Tests
dotnet run --project src/ScreenRec.App            # the desktop app
./scripts/package-mac.sh osx-arm64                # → artifacts/osx-arm64/*.dmg + screenrec-cli zip (ad-hoc signed)
pwsh ./scripts/package-win.ps1                    # → artifacts/win-x64|win-arm64/ScreenRecTrainer-*.zip + .msi
ScreenRecTrainer --self-test <folder>             # renders every tab to PNG, exits non-zero on error
```

Both targets cross-compile from either OS (`dotnet publish -r win-x64` works on a Mac). The Windows MSI is built with
[WiX](https://wixtoolset.org) (`packaging/windows/ScreenRecTrainer.wxs`) and must be built on Windows — WiX refuses
`Directory/@Name` off-Windows (it canonicalises paths against `C:\`).

### Releases (CI/CD)

`.github/workflows/release.yml` runs on every push:

1. **Build and test** on macOS.
2. **Package** Windows x64/arm64 — the tests run again on Windows, then a **WiX MSI installer** is built — and macOS
   arm64/x64 — an **.app in a drag-to-Applications .dmg** plus the CLI zip. Each platform also runs the app's UI
   self-test against a demo recording.
3. **Release:** on `main` only, a GitHub release tagged `v<major>.<minor>.<commit count>` is published with the MSIs,
   DMGs, portable zips, `SHA256SUMS` and notes listing the commits. To change the major/minor version, edit
   `VersionPrefix` in `Directory.Build.props`.

Other branches and pull requests get steps 1–2 only. The Mac build is ad-hoc signed, not notarized, so open it the
first time with right-click › Open.

### Layout

| Path | What |
|---|---|
| `src/ScreenRec.Core/Recording` | Recorder (steady frame rate into an MJPEG AVI; key frames kept on change or input), session storage, AVI writer, ffmpeg → MP4 |
| `src/ScreenRec.Core/Platform` | macOS: CoreGraphics capture, CGEventTap, NSWorkspace front app. Windows: GDI capture with cursor, low-level hooks, foreground window |
| `src/ScreenRec.Core/Ai` | OpenAI-compatible client, step segmenter, the analysis pipeline (describe → synthesize → expert profile) |
| `src/ScreenRec.Core/Guardian` | Guardian client, browser sign-in (PKCE + loopback), account store |
| `src/ScreenRec.Core/Export` | Training guide (Markdown/HTML), SKILL.md, automation prompt |
| `src/ScreenRec.App` | Avalonia desktop app (Windows + macOS) |
| `src/ScreenRec.Cli` | `screenrec` command line |
| `tests/ScreenRec.Core.Tests` | xUnit tests with a fake model and a fake Guardian |

Data lives in `~/Library/Application Support/ScreenRecTrainer` (macOS) or `%APPDATA%\ScreenRecTrainer` (Windows).
Set `SCREENREC_HOME` to use another folder. Each recording is `sessions/<id>/` holding `session.json`, `events.jsonl`,
`frames/`, `recording.avi`, and `analysis/`, where `analysis/` contains `training.html`, `skill/SKILL.md` and
`automation.txt`.

### Notes

* The browser sign-in sends Guardian a `[::1]` loopback redirect. guardian.503cm.com's load balancer refuses any URL that
  contains `http://127.0.0.1` (HTTP 403), and Guardian's connect page accepts both forms.
* The GuardianProject repository is private, so the Guardian calls follow Vision for Windows' harness client and
  Guardian's own web app.
