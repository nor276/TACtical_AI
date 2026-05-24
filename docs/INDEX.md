# TAC AI Pipeline Documentation Index

Comprehensive Mermaid-flowchart documentation of every major logic pipeline in the TAC AI mod for TerraTech. Each pipeline was independently mapped by two agents, then merged into a single canonical document with discrepancies reconciled against source code.

## How to use these docs

- **Hunting a bug**: jump to the pipeline that owns the misbehaving behavior. The `## Known issues` section lists `BUG-*`, `DEAD-*`, and `SMELL-*` items with file:line refs and severity.
- **Onboarding to a subsystem**: read the canonical pipeline doc, then dive into source files cited in the `## Node reference` table.
- **`[?]` markers**: indicate ambiguity that the merge agent flagged but couldn't resolve from source alone — verify if you're acting on that node.
- **Reviewing / hardening a pipeline**: follow the four-phase playbook in [00 Review & fix process](00_review-process.md) — 10-agent verify → 2-agents-per-fix → all fixes in one pass (no deferral, best not simplest) → re-sync the doc to `Issues: NONE`.

## Canonical pipelines

### Initialization & Lifecycle
- **01** [Mod boot / patch installation](01_mod-boot.md) — DLL load → Harmony patches → KickStart init → world ready
- **02** [Tank spawn lifecycle](02_tank-spawn.md) — OnPool → RegisterTank patch → TankAIHelper → AI alignment → first tick
- **03** [World load/save](03_world-load-save.md) — ManSaveGame, SafeSaves `[SSaveField]`, OnWorldLoad/Save, migration

### AI Tick & Decision Pipelines
- **04** [Allied AI tick](04_allied-ai-tick.md) — TankAIHelper.OnUpdate → DetermineCombat → OpsController → B* dispatch
- **05** [Enemy AI tick](05_enemy-ai-tick.md) — RCore.BeEvil → EnemyMind → EnemyOpsController → R* dispatch → LollyGag fallback
- **06** [Target acquisition](06_target-acquisition.md) — FindEnemy, CheckEnemyAndAiming, lastEnemy lifecycle, hysteresis
- **07** [Combat FSM dispatch](07_combat-fsm.md) — EAttackMode (Safety/Circle/Ranged/Chase/Strong/Random) + distance buckets

### Movement & Pathing
- **08** [AICore drive pipeline](08_aicore-drive.md) — DriveMaintainer/Director, PlanningPathing, AvoidAssist, EControlCoreSet
- **09** [Stuck/unjam FSM](09_stuck-unjam.md) — IsTechMovingAbs, FrustrationMeter, ForceSetBeam, BeamMaintainer
- **10** [Dive attack FSM](10_dive-attack-fsm.md) — AirplaneAICore 4-state (Idle/Approach/Commit/Recover) + altitude gate
- **11** [Movement controller dispatch](11_movement-controller.md) — AIController + AICore selection per tech category

### Combat Subsystems
- **12** [Weapon firing](12_weapon-firing.md) — WeaponDirector/Maintainer, AIWeaponState, target lead prediction
- **13** [Operations dispatch](13_operations-dispatch.md) — B* (allied) + R* (enemy) modules + LollyGag fallback by CommanderMind

### World & Team
- **14** [Team management](14_team-management.md) — ManBaseTeams, alignment matrix, retreat coordination, seededSpawnCoords
- **15** [Enemy world / tile management](15_enemy-world-tile.md) — ManEnemyWorld, NP_Presence sim, tile spawn/recycle, EBU/EMU
- **16** [Base operations](16_base-operations.md) — RLoadedBases, InsureHarvester, ExpandBasePeaceful, BB economy

### Spawning
- **17** [RawTech spawn pipeline](17_rawtech-spawn.md) — RawTechLoader, FilteredSelectFromAll, fallback cascade, BasePurpose

### Infrastructure
- **18** [Harmony patch system](18_harmony-patches.md) — 42 patches (35 MassPatcher + 6 attribute + 1 transpiler)
- **19** [Multiplayer sync](19_multiplayer-sync.md) — NetworkHandler, TTMsgType 4317-4323, host/client gates
- **20** [Repair / damage system](20_repair-damage.md) — OnHit, OnBlockLoss, mind.Hurt lifecycle, repair stepper

### Cross-cutting
- **21** [Timing & cadence register](21_timing-cadence.md) — the four clock families, per-value units (seconds / tick-invariant / tick-dependent), frequency ladder, producer/consumer beats, per-pipeline coverage map, and a corroboration-rated candidate-issue list. **Covers all 20 pipelines.**

## Cross-pipeline integration diagram

```
                  ┌─── 01 Boot ───┐
                  │               │
                  ▼               ▼
              02 Spawn        03 Load/Save
                  │               │
        ┌─────────┼────────┐      │
        ▼         ▼        ▼      ▼
    04 Allied  05 Enemy  20 Repair  14 Teams
        │         │        ▲        ▲
        │     06 Target    │        │
        │         │        │        │
        └────►07 Combat◄───┘   15 World
              │     │              │
              ▼     ▼              ▼
        12 Weapon  08 Drive    16 Base Ops
                    │              │
                    ├──09 Stuck    ▼
                    │           17 Spawn
                    ├──10 Dive      
                    │           
                    └──11 Controller

   19 MP sync ── crosscuts allowed network paths
   18 Harmony ── patches into all of the above
   13 Operations dispatch ── invoked by 04 and 05
```

## Format conventions

Each canonical doc follows this unified template:

```markdown
# Pipeline NN: <Name>
> **Category:** ...

## Summary
[2-3 sentence overview]

## Entry points
| Trigger | Entry function | Reference |

## Flow
```mermaid
graph TD (or stateDiagram-v2)
    NodeA[Short label]
    NodeA --> NodeB{Decision?}
```

## Node reference
| ID | Description | Reference |

## Key data / state
[Optional]

## Exit points
| Output | Consumer | Reference |

## Cross-pipeline integration
- Inbound from: Pipeline NN
- Outbound to: Pipeline NN
- Patched by: Pipeline 18 (if applicable)

## Known issues
### Bugs
| ID | Description | Reference | Severity |

### Dead code
| ID | Description | Reference |

### Tech debt / smells
| ID | Description | Reference |
```

**Mermaid rules enforced:**
- Short alphanumeric node IDs
- Short labels (1-5 words; file:line goes in the Node reference table, NOT in node labels)
- Standard shapes: `[Action]`, `{Decision?}`, `((Start/End))`
- Subgraphs always closed with `end`
- Link labels: `-->|condition|`
- Relative markdown links: `[file.cs:42](../Modified/.../file.cs)`

**Marks used in canonical docs:**
- `BUG-N` — confirmed or suspected defect, with severity (High/Med/Low)
- `DEAD-N` — code path unreachable or never invoked
- `SMELL-N` — tech debt / refactor candidate
- `[?]` — merge ambiguity the agent couldn't resolve from source alone

## Status

20 canonical pipelines + 1 cross-cutting timing register (21) + this INDEX = 22 documents.

The timing register (21) was built by identical-prompt agent sweeps (7 agents on the 06/07/08 pilot, then 43 agents across the remaining pipelines — 3 per cadence-bearing pipeline, 1 per structural one), merged with every contested value re-verified against source (see its Reconciliation log). It now covers all 20 pipelines + the four clock families.

- **Investigation agents**: 28 (Pipeline 1-20 × 2, plus 8 retries for read-only agents)
- **Merge agents**: 20 (one per pipeline, reconciling A/B drafts against source)
- **Total agent-runs for documentation**: 48

Last updated: 2026-05-21.
