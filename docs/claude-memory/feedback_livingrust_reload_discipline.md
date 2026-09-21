---
name: livingrust-reload-discipline
description: "Hot-reloads hurt live testing and playability - batch deploys, never reload during a timed test"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: ea44b221-e47a-4c9f-a86e-4c4181731680
  modified: 2026-09-21T12:07:16.817Z
---

Every plugin hot-reload freezes the server a few seconds, resets all in-memory state (timers, airdrop tracking, guards, verbose toggle), and Lucas felt rubber-banding right after repeated deploys. Reload wiped an in-flight airdrop test and made results unreadable.

**GOTCHA (found 2026-09-21):** plain `dotnet build` in this project AUTO-PACKAGES AND DEPLOYS the plugin (post-build Exec in LivingRust.csproj), which hot-reloads the live server. To check compilation without deploying use `dotnet build -p:NoDeploy=true` (switch added to the csproj). Deploy only by a deliberate plain build / Package-CsZip.ps1.

**Why:** ~10 reloads in one session caused thread growth (147->212) and a killed test.
**How to apply:** batch changes into one deploy; check for an in-flight airdrop/test in the log before deploying; tell Lucas before reloading during a timed test. Verbose logging default is OFF (walk/aim/fire diagnostics moved behind it) - ask Lucas to `lr.debug.verbose on` for audits rather than flipping the default.
