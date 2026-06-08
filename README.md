# TACtical_AI

A personal fork of TAC AI by Legionite. Not maintained for public distribution. Not intended to compete with or replace the original. Lives on its own branch — takes from upstream what helps me, gives back only what someone else might find useful.

## Homage

This fork would not exist without Legionite and the original contributors. TAC AI is years of careful work — every behavior identity, every avoidance heuristic, every base-and-ally protection rule, every pathing reroute around scenery and water and unclimbable terrain. The shape of how AI techs feel in TerraTech is largely Legionite's design. What I do here builds on top of that work; it does not replace it. Where my changes succeed it is because the foundation was solid.

To Legionite — if anything in this fork is useful upstream, take it freely. No attribution needed, no expectations. If none of it is, that is fine too. This exists for my own play and for the work I want to do with it.

## What the player picks

When a tech is set to AI control, the player chooses which AI drives it:

**Vanilla** — TerraTech's built-in AI. Untouched. The reference point. Pick this if you want unmodified behavior or want to A/B compare against the other forms.

**ORIGINAL TAC-AI (Modified)** — Legionite's TAC AI in its full current behavior. Each tech is read for what it carries — cab, props, receivers, weapons, drills, hover thrusters, wings — and dispatched to the matching behavior.

*Allied identities*: Escort (the classic player defender), Assault (runs off beyond radar range to attack enemies on the player's radar), Aegis (follows the nearest non-player allied tech and will chase an enemy some distance), Prospector (harvests chunks and returns them to base), Scrapper (grabs loose blocks but avoids combat), Energizer (charges and heals other techs), Aviator (flies aircraft death-from-above and keeps distance from the player), Buccaneer (sails ships and avoids terrain above water level), Astrotech (flies hoverships and can follow the player into the sky), MTTurret / MTStatic / MTMimic (multi-tech turret modes — aim only, shoot back only, or mimic the nearest non-MT tech).

*Bases*: Defense (strictly defensive elements), Harvesting (delivery cannons), Autominer (unlimited BB generation), Garrison (TechProduction via Explosive Bolts — spawns reinforcements), Headquarters (calls in techs from orbit using funds). Bases choose their own purposes from what they have attached.

*Enemy AI* is tuned per vehicle type — wheeled, aircraft, helicopter, naval, space, station — each with its own combat, retreat, idle, and pathing logic, plus dedicated handling for miners, scavengers, guardians, repair-techs, and reinforcement-spawning bolt platforms.

Avoidance dodges scenery and other techs. Pathfinding reroutes around obstacles in 2D for ground vehicles and 3D for aircraft. Combat ranges, engagement angles, and retreat thresholds are per-identity. This is the form for day-to-day play.

**Smart** — A from-scratch neural-network AI brain that learns during play. See below — Smart is the direction I am pushing this fork.

## Across all three forms

The fork adds a few pieces that all three forms benefit from:

- **AI Form plugin architecture.** The form picker is folder-discovered — drop a new form folder under `AI/Forms/` and it appears in the in-game selector. Per-tank AI profile selection persists with the world save and syncs in multiplayer, so the AI you set on a tech stays set across sessions and across clients.

- **Live in-game tunables.** A large catalog of behavior values — combat ranges, retreat thresholds, avoidance distances, weapon-aim behavior, perception cadence, threat scoring, and more — can be adjusted at runtime from in-game menus. No recompile, no restart. Tweaks take effect on the next AI tick, so you can dial behavior in while watching it happen.

- **AI warning routing.** Stale or failed tech-load warnings go to the log file under an `[AIWARN]` tag instead of popup-spamming the screen on every bad load. Real errors still pop. Multi-hour sessions stop accumulating dialog boxes.

- **Training mode tech-spawn filter.** An in-game toggle (default off) that constrains what techs the world spawns during a session — useful when training Smart against a curated tech distribution rather than the full chaos of player-published creations, or just when you want a cleaner test environment for Original TAC-AI.

## Smart, and what I am going for

Smart is not a replacement for the Original TAC-AI form. Legionite's rule-based AI is well-tuned and proven, and the player who just wants TerraTech to play well will pick Original. Smart is for the player who wants to see what happens when the AI brain actually learns during play, and for me, who wants to drive that training toward specific behaviors.

### What Smart is

Smart replaces the rule-based per-form behavior with a small neural-network architecture that runs alongside the game:

- **Four online-trained ML models.** `OpponentIntentClassifier` predicts what enemy techs are about to do from observed motion sequences. `ActionValueEstimator` is a Q-function over driving decisions. `TrajectoryResidualModel` predicts where techs will be a moment from now and corrects coast extrapolation. `ThreatAssessmentModel` scores enemy configurations. Each is a small MLP with Adam optimization, trained from gameplay events as they happen.
- **Aether belief system.** Per-tech world model with coast extrapolation and Kalman-style fusion over observation streams. Background daemon at roughly 30 Hz writes belief snapshots through a double-buffer; every other Smart subsystem reads from it.
- **Continuous control.** An MPC-style `ContinuousController` plus `TacticalOptimizer` reads model outputs and the per-tech belief and emits drive commands every physics frame. Goal sources are arranged in a strict precedence chain — Coordinator external goal, identity goal source, tactical fallback — with NaN sanitization at the output.
- **Online training.** The four models update from observed gameplay — block deliveries, kills scored, base holds, ally protections. Bounded event queues, per-model save mutex, autosave on a worker thread.
- **Snapshot rollback.** Per-model checkpoints in a three-tier ring: in-memory recency for fast revert, disk auto-checkpoint at interval, operator-named checkpoints for long-term keeps. If training goes bad, revert.
- **Full multithreading.** This is the big architectural departure from Original TAC-AI, which runs on the main Unity update loop. Smart runs nine canonical background daemons — Aether (belief fusion), Coordinator (strategic planning), Planner, four model trainers, two pathing workers — plus a worker pool for ad-hoc tasks. A central worker registry tracks every thread; a daemon watchdog respawns dead ones; a cancel-and-join shutdown path bounds form-swap and quit time so no thread hangs; an abort-survival test suite stress-checks the lifecycle. Save-mutex contracts protect model parameters from concurrent training and persistence. Smart is the threading-heavy form by design — the brain needs to think while the game ticks.

- **Background pathing.** A thread-safe `TerrainMap` with double-buffered publication lets background workers do reachability queries without blocking the main thread. Path solves go through a backpressure-aware request queue so a flood of route requests cannot starve the main thread.

### Training Director — what I want to do with it

The neural networks above train on whatever gameplay happens to produce, with no operator in the loop. That is fine for emergent behavior but bad for steering. If I want the AI to get better at, say, defending a base, I have no good way to point training there. The networks will drift toward whatever event mix the world gives them.

The **Training Director** is the piece I am building to put the operator (or, eventually, an LLM) into that loop. A three-layer orchestrator:

- **Layer 1 — operator or LLM.** Sends directives in a simple grammar. Examples: `maintain hunter.kills_per_min >= 0.5 for 30min`, `prioritize defensive_hold for 1hour`, `rollback threat to .previous`. Positional grammar that survives the console-command tokenizer.
- **Layer 2 — constraint engine and verb dispatcher.** Translates directives into seven concrete training actions: `lr_scale`, `temp_adjust`, `replay_bank`, `freeze_model`, `scenario_set`, `scenario_respawn`, `rollback`. Decides which actions to take and when, based on a constraint table that tracks measured rates — kills, deliveries, ally protections, base holds, and pathology rates fed in by behavior guards.
- **Layer 3 — trainers, scenarios, and guards.** The four neural-net trainers from above. Six controlled scenarios for repeatable training conditions: Open Brawl, SubNeutral Resource Race, Defensive Hold, Air Sortie, Mining and Defend, Patrol Stress. Eleven behavior guards that watch for pathological tech behavior — techs stuck reversing into trees, grounded aircraft, overdue gatherers, orbit-no-fire — and feed observation-only training signal back to the constraint engine.

The goals I am going for with this:

1. **Operator-in-the-loop training control.** Point training at a specific behavior in plain words. Have the system actually do it. See constraint-and-guard feedback when training drifts off the requested behavior.
2. **Pathology detection without stifling.** Catch the failure modes that emerge during training — techs that learn to reverse-spam, aircraft that crash, gatherers that idle on a full hold — and signal them as training error rather than fixing them by hard rule. Let the model learn around them. The hardest part of training emergent behavior is knowing when to intervene; the guards are the trip wires.
3. **Recoverable training.** Every checkpoint promotion is auditable and revertible. A bad training session does not poison the profile permanently.
4. **Eventual quality gating.** A fixed-seed scenario battery that scores candidate checkpoints against the incumbent and requires non-regression for disk-level promotions. This is the precondition for enabling longer-context training and the dormant models, which is the longer-term direction.

## The three forms

| Form | What it is |
|---|---|
| Vanilla | TerraTech's built-in AI, untouched. |
| Original TAC-AI (Modified) | The full Legionite behavior. The form for day-to-day play. |
| Smart | Neural-network AI brain that learns during play. The direction of active development. |
