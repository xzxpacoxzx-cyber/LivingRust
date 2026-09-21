# LivingRust - project notes for Claude

Carbon C# plugin: persistent AI survivors (bots) for a Rust dedicated server.
Source: `Plugin/*.cs` (+ AI/Combat/Core/... folders). Server: `Rust/server`.

**Read first, every session:** `docs/claude-memory/MEMORY.md` (index) then the
files it links (roadmap, 2026-09-21 state, working style, reload discipline).
Older session write-ups: `docs/session-history/`.

## Build / deploy
- `dotnet build -p:NoDeploy=true` compiles only.
- Plain `dotnet build` also packages `LivingRust.cszip` into
  `Rust/server/carbon/plugins/` and triggers a LIVE plugin reload
  (all in-memory state resets). Never deploy during a timed test.
- Verify: `grep -n "Loaded plugin Project\|Failed compiling" Rust/server/LivingRust_log.txt`
- Requires .NET SDK + net48 targeting pack, and the Rust server files
  (steamcmd `app_update 258550` via `Rust/run.bat.example`, copy to `run.bat`, set a real RCON password).

## Working style
Evidence-driven: verify with logs/code, don't guess. Bots sprint by default.
Batch deploys; verbose logging default OFF.

## Not in this repo
The 11 GB Rust server binaries, steamcmd, logs/traces, and raw Claude transcripts
(too large; summaries live in docs/).
