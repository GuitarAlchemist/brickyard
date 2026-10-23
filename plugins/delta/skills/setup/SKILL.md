---
name: setup
description: Set up, update or repair Delta, the robot that gives Claude Code a voice on Windows, in English or French. Delta has a window, ears (whisper.cpp) and a voice (Piper). Use when the user wants to install, set up, update or repair Delta, change the language it hears and speaks, or when Delta's say or listen tools report that something is missing.
---

Delta runs on Windows only. It hears and speaks one language: English by default, or French. Talk to the user in their
own language throughout. Set Delta up in four steps.

1. Ask which language Delta should hear and speak: English or French. If the user writes in French, suggest French.
   Pass `-Language en` or `-Language fr` to each command below. Setup records the choice, so a later update without
   `-Language` keeps it.

2. Check what is already installed. This changes nothing:

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/scripts/setup.ps1" -Data "${CLAUDE_PLUGIN_DATA}" -Language en -Check
   ```

3. Show the user what is missing and the total download size, and ask before downloading anything.

   The first setup downloads about 700 MB, most of it the Whisper model. The user may already have some of these:

   | They already have | Pass |
   |---|---|
   | Piper | `-Piper <path>` |
   | A Piper voice in Delta's language | `-Voice <path>` |
   | whisper.cpp's `whisper-server` | `-Whisper <path>` |
   | A ggml Whisper model | `-WhisperModel <path>` |

   Ask for their paths. Setup records those paths and does not download those files.

4. Install: run the same command without `-Check`, adding any paths from step 3.

   Setup checks every download against its SHA-256. If a check fails, stop and tell the user.

Then tell the user the two ways to talk to Delta:

- **Delta's voice coming in by itself.** Quit Claude Code and start it again with:

  ```
  claude --dangerously-load-development-channels plugin:delta@brickyard
  ```

  Channels are a research preview, so Claude Code asks for confirmation first. Team and Enterprise organisations must
  enable channels before this works.
- **Any session, without that flag.** Run `/delta:talk`.

Delta's window opens when the delta MCP server starts. If the server failed before setup, or the language changed, run
`/mcp` and reconnect delta, or restart Claude Code.

Delta only passes on these:
- what starts by calling it, "Delta, …", or its brain, "Claude, …";
- what follows, while the conversation goes on: each sentence for Delta and each of its answers keep it open 30
  seconds more, which a ring in its window counts down.

"I'm not talking to you" and « je ne te parle pas » are not passed on.
