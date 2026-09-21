---
name: user-lucas-profile
description: "User's technical background and working style on the LivingRust project"
metadata: 
  node_type: memory
  type: user
  originSessionId: 4b429f6a-3ccf-4922-a7d0-f5e9d986b615
  modified: 2026-08-07T08:18:49.044Z
---

Building [[project-livingrust-overview]] (Rust game server Carbon plugin) solo. Comfortable working close to game-engine internals — Unity physics layers, raycasting, NavMesh, entity spawning/lifecycle.

Takes a rigorous "verify via an in-game debug command or reflection, don't guess" approach to reverse-engineering Rust/Unity internals: e.g. confirmed physics layer masks via a custom `/lr.debug.look` raycast tool rather than assuming from docs; confirmed `ModelState`/`SendModelState` behavior and a renamed class (`RustNavMesh`→`RustNavMeshHelpers` after a game update) via PowerShell reflection over the decompiled `Assembly-CSharp.dll` rather than guessing.

Dev loop: runs a local Carbon test server, manually drives an in-game character to test AI behavior via chat/console commands, reads the server log (`LivingRust_log.txt`) and captured CSV traces (`/lr.debug.trace`) to see what actually happened, then edits C# source in Claude Code sessions.

**Why this matters:** pushes back with precise counter-examples when an explanation is wrong (e.g. caught a bot-despawn-count bug and a "clean" trace that was actually jittery) rather than accepting a plausible-sounding first guess — re-derive from actual log/trace data when corrected, don't just re-assert.

**How to apply:** don't hand-wave engine behavior — check via debug tooling, decompiled assembly, or reflection when uncertain. Prefer targeted architecture changes (e.g. the authored-monument-routes decision) over indefinitely patching a generic heuristic once the same root cause has surfaced 3+ times.
