# BONGA

A native Windows voice-dictation app modeled on [Wispr Flow](https://wisprflow.ai/): hold a hotkey, speak, release — and clean, polished text appears in whatever app you're typing in. Works in Gmail, Slack, VS Code, terminals, anywhere text goes.

Unlike Wispr Flow (cloud-only), BONGA transcribes **entirely on-device** with Whisper (whisper.cpp via Whisper.net). No account, no API key, no audio ever leaves your PC. An optional bring-your-own-key cloud mode is included.

## Features

- **Push-to-talk dictation** — hold `Right Ctrl` (configurable), speak, release. Text is inserted into the focused app.
- **Hands-free mode** — quick-tap the hotkey to start, press again to finish. `Esc` cancels.
- **Flow Bar** — floating pill at the bottom of the screen with live waveform while recording; click it to dictate. Never steals focus.
- **AI auto-edits** — removes filler words (um, uh…), collapses stutters, auto-capitalizes, punctuates. Voice commands: say *"new line"* / *"new paragraph"*.
- **Personal dictionary** — add names/jargon (biases the recognizer via decode prompt) and hard corrections (*misheard → correct*).
- **Snippets** — say a trigger phrase ("insert my email") to expand full text blocks.
- **History & stats** — past dictations with copy buttons, words written, time saved vs typing.
- **100+ languages** — auto-detect or pin a language (Whisper multilingual models).
- **Tray app** — runs in the background; launch-at-startup option.
- **Optional cloud** — point at any OpenAI-compatible endpoint for cloud STT and/or an LLM "polish" pass.

## Build & run

Requires the .NET 8 SDK.

```powershell
dotnet build src/VoiceFlow/VoiceFlow.csproj -c Release
dotnet run --project src/VoiceFlow -c Release
```

Self-contained distributable (no .NET install needed on the target machine):

```powershell
dotnet publish src/VoiceFlow/VoiceFlow.csproj -c Release -r win-x64 --self-contained -o dist
```

On first run, download the speech model from the Home screen (base-q5_1, ~60 MB, one time). Models live in `%APPDATA%\Bonga\models`; settings and history are JSON files in `%APPDATA%\Bonga`. Five model options trade speed for accuracy (tiny → small); quantized q5_1 variants are recommended on modest CPUs.

## How it works

| Stage | Implementation |
|---|---|
| Hotkey | Low-level keyboard hook (`WH_KEYBOARD_LL`); hold = push-to-talk, tap = hands-free, other-key-while-held cancels so `Ctrl` shortcuts still work |
| Audio | NAudio `WaveInEvent`, 16 kHz mono PCM |
| Speech-to-text | whisper.cpp (Whisper.net) running locally; model preloaded at startup; encoder context sized to clip length (`audio_ctx`) for ~4× faster short dictations; personal dictionary words fed as a decode prompt |
| Formatting | Snippets → voice commands → filler removal → corrections → stutter collapse → capitalization/punctuation (`Core/TextFormatter.cs`) |
| Insertion | Clipboard-paste (`Ctrl+V` via `SendInput`, clipboard restored) or Unicode keystroke synthesis for terminals |
| UI | WPF: tray icon, borderless topmost Flow Bar (`WS_EX_NOACTIVATE`), dashboard window |

## Tests

`tools/SelfTest` runs the real formatting pipeline plus an end-to-end Whisper transcription against a WAV:

```powershell
dotnet run --project tools/SelfTest -c Release -- "%APPDATA%\Bonga\models\ggml-base-q5_1.bin" test_speech.wav
```
