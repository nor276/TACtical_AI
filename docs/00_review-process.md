# Pipeline Review & Fix Process

> **Category:** Methodology

## Summary

This is the repeatable, agent-driven process used to harden each pipeline doc (`01`–`20`) and the code it documents. It runs in four phases per pipeline: **(1) verify with 10 identical agents → (2) find the proper fix with 2 identical agents per issue → (3) implement ALL fixes in one pass, no deferral, best option not simplest → (4) clean up the doc to the `Issues: NONE` convention.** The throughline is *consensus over single-shot* and *evidence over assertion*: redundant independent passes catch hallucinations, stale line numbers, and overstated/fabricated claims that a single agent (or a single read) misses.

Work one pipeline at a time, start to finish, before moving to the next.

---

## Phase 1 — Verify (10 identical agents)

Dispatch **10 identical agents** against a single pipeline doc. Same prompt to each; do not vary them — the point is independent replication, not division of labor.

> **Agents are read-only.** Verification agents investigate and report. They do **not** edit code or docs. (Dispatch them with a read-only tool set, or state the constraint explicitly in the prompt.)

Each agent independently checks the doc against the actual source:
- **Logic flow** — does the described control/data flow match the code? (structure, branches, ordering, gates)
- **Line numbers** — every `file.cs:line` link still points at the named symbol.
- **Claimed issues** — each `BUG-N` / `DEAD-N` / `TD-N`: confirmed, stale, overstated, or fabricated?

Then **synthesize the 10 into one consensus report**:
- What's confirmed (cite how many agents agreed).
- What's stale (right idea, wrong line / drifted since last sync).
- What's **overstated** (real but the severity or blast radius is exaggerated).
- What's **refuted / fabricated** (symbol no longer exists, behavior never happened) — strike it.
- Overall confidence, and the handful of corrections that matter most.

Output is an evidence-backed, de-duplicated issue list with corrected severities. Nothing is "fixed" yet.

> Why 10: combat-subsystem code drifts and the docs accumulate stale refs. One agent confidently repeats a fabricated `BUG`; ten agents disagree about it, which is the signal to go look.

---

## Phase 2 — Find the proper fix (2 identical agents per issue)

For **each remaining issue**, put **two identical agents** on it.

> **Agents identify the fix — they do NOT apply it.** Each agent returns a *proposal* (root cause, exact location, code sketch). No agent edits files. Implementation happens once, by the orchestrator, in Phase 3 — after the two proposals are cross-checked and any divergence is decided. This order is not optional: it preserves the verify → review → implement sequence, and it prevents the concurrent-edit clobbering you get when multiple agents write to the same files at once. Spell this out in every fix-agent prompt.

The mandate, verbatim in spirit:

- Find the **proper** fix, **not the easiest**. Refactor over patch where the patch only hides the symptom.
- **Do not mask or defer.** If a fix is a real refactor, describe the whole refactor.
- If, after investigation, it's a **non-issue** or working-as-intended, say so with evidence.
- Output per issue: confirmed root cause (`file:line`), the proper fix (exact location + code sketch), alternatives considered and why rejected, blast radius / what else reads this state, and a revised severity.

Cross-check the two agents:
- **They agree** → high confidence, proceed.
- **They diverge** → surface the divergence as an explicit decision (don't silently pick one). Divergence usually means the issue has a real design choice in it.

**Fold issues that share one root cause or one code contract** into a single investigation so the pair attacks the whole problem, not half of it (e.g. a bug and the tech-debt entry describing the same smell).

---

## Phase 3 — Implement ALL fixes in one pass

- **All corrections, one pass, descending severity.** No "we'll get to the hard ones later." If something is left unfixed the question is *"when, then?"* — the answer is now.
- **Best option, not simplest.** The simplest change that makes the error go away is usually the mask. Prefer the change that makes the next reader's job easier.
- **Compose interacting edits deliberately.** When several fixes touch the same method/region, apply them as a coherent set and re-read the result to confirm they compose.
- **Flag build-before-shipping.** There is no compiler in the working environment, so every change ships unverified until built. Code must be **C# 7.3 / .NET 4.6.1** clean — no switch expressions, target-typed `new()`, `is not`/relational/property patterns, `using` declarations, etc. (see build/compat notes).
- Verify each change by re-grep / re-read, not by assertion.

If a fix is genuinely feature-sized (a new solver, a cross-cutting refactor), it still gets done in the pass — it's called out as larger, not skipped.

---

## Phase 4 — Clean up the doc (re-sync)

Re-sync the pipeline doc to the now-current code:
- Fix every line number (locate symbols by **name**, never trust the old number).
- Update prose, tables, and the mermaid diagram to describe **current** behavior.
- **Strike** references to deleted/fabricated symbols and removed dead code.
- Replace the entire issues section with the standing convention:

```text
## Issues

**NONE.**

If a new issue is found in this pipeline, replace `NONE.` above and add it under the matching heading,
using a stable ID (BUG-N, DEAD-N, or TD-N) and a clickable file.cs:line link.

### Bugs
- **BUG-1 (High | Medium | Low)** - [File.cs:line](path) - what is wrong, and the intended fix.
### Dead code
- **DEAD-1** - [File.cs:line](path) - what is orphaned or unreachable, and why.
### Tech debt
- **TD-1** - [File.cs:line](path) - the smell, and the cleaner shape.
```

**No resolved-problem changelogs.** The doc describes the code as it *now is* — it does not narrate what used to be broken or that something was "fixed." Past-tense language ("previously", "used to", "stale", "predates", "no longer") does not survive into the doc.

---

## Conventions & guardrails

- **Investigation agents never edit files.** Phase 1 and Phase 2 agents are read-only — they report and propose. Code is changed only in Phase 3, by the orchestrator, as one coordinated pass. (The Phase 4 doc re-sync may be delegated, but only to a single agent editing only that doc — never code, never concurrently.)
- **Consensus over single-shot.** 10 to verify, 2 to fix. Agreement is confidence; disagreement is a flag.
- **Evidence over assertion.** Every claim is a `file.cs:line` you can click and a grep you can rerun.
- **Severity is honest.** Downgrade what's dormant-by-default; escalate what the doc undersold.
- **Decisions are surfaced, not buried.** Where agents diverge or a fix changes player-facing behavior, raise it before acting.
- **One pipeline at a time, finished before the next.**

## Definition of done (per pipeline)

1. 10-agent verification synthesized; issue list corrected.
2. Every confirmed issue has a 2-agent-vetted proper fix.
3. All fixes implemented in one descending-severity pass, none deferred.
4. Doc re-synced, line numbers correct, `## Issues` → **NONE.**
5. Behavior changes worth playtesting are flagged; build-before-shipping noted.
