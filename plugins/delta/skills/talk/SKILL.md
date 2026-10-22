---
name: talk
description: Have a spoken conversation with the user through Delta, with the microphone and Delta's voice, in a session that does not receive Delta's channel events. Use when the user asks to talk aloud, or by voice, with Delta or with Claude.
---

Run a spoken conversation in French with Delta's listen and say tools:

1. Say a short greeting with say, for example « Je t'écoute. »
2. Call listen with `timeout_seconds` 120.
3. If something was heard, it is the user speaking. It is a Whisper transcript, so some words may be misheard.
   - Act on it.
   - Answer with say: one to three short spoken sentences, without markdown, lists, code or URLs.
   - Before long work, say briefly what you are about to do. Afterwards, say how it went.
   - Then go back to step 2.
4. Stop, after a short goodbye with say, in either case:
   - the user says goodbye or asks to stop, for example « au revoir », « stop », « arrête » or « c'est tout »;
   - two listens in a row hear nothing.

Only what you pass to say is heard. Confirm anything destructive or surprising before you do it.
