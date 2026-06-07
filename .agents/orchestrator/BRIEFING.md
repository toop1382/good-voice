# BRIEFING — 2026-06-06T20:39:28Z

## Mission
Coordinate and manage a team of specialists to implement a low-latency, scalable, and optimized voice chat system with a .NET server and a Unity client.

## 🔒 My Identity
- Archetype: orchestrator
- Roles: orchestrator, user_liaison, human_reporter, successor
- Working directory: i:/projects/voice chat/.agents/orchestrator/
- Original parent: main agent
- Original parent conversation ID: d66e0ccb-41f0-4507-94bc-bebf1773c187

## 🔒 My Workflow
- **Pattern**: Project Pattern
- **Scope document**: i:/projects/voice chat/PROJECT.md
1. **Decompose**: Decompose requirements into milestones (Implementation and E2E Testing tracks).
2. **Dispatch & Execute**:
   - **Direct (iteration loop)**: Explorer → Worker → Reviewer → gate
   - **Delegate (sub-orchestrator)**: Spawn sub-orchestrators for milestones and the E2E Testing track.
3. **On failure**: Retry, Replace, Skip, Redistribute, Redesign, Escalate.
4. **Succession**: Self-succeed at 16 spawns, write handoff.md, spawn successor.
- **Work items**:
  1. Decompose requirements and draft PROJECT.md [in-progress]
  2. Spawn E2E Testing Track Orchestrator [pending]
  3. Spawn Implementation Track Sub-orchestrators [pending]
  4. Integrate and run final verification [pending]
- **Current phase**: 1
- **Current focus**: Milestone decomposition and environment assessment

## 🔒 Key Constraints
- NEVER write, modify, or create source code files directly.
- NEVER run build/test commands yourself — require workers to do so.
- You MAY use file-editing tools ONLY for metadata/state files (.md) in your .agents/ folder or PROJECT.md.
- Never reuse a subagent after it has delivered its handoff — always spawn fresh.
- Network restrictions: CODE_ONLY network mode.
- E2E Test Suite must pass 100% before declaring completion.
- Forensic Auditor audit is a binary veto.

## Current Parent
- Conversation ID: d66e0ccb-41f0-4507-94bc-bebf1773c187
- Updated: not yet

## Key Decisions Made
- Initiated project. Classifying task as SWE + Project category since it requires codebase construction (.NET server + Unity client with native integrations).

## Team Roster
| Agent | Type | Work Item | Status | Conv ID |
|-------|------|-----------|--------|---------|
| E2E Testing Orch | self | Design and implement E2E test suite | pending | ee6ce96a-2581-49ea-9ba2-69478762568c |
| MS1 Sub-orch | self | Native Opus Codec Wrapper | pending | fec73b90-f3d7-45e1-9229-a03f3657c5c2 |
| MS3 Sub-orch | self | Network Transport & Room Server | pending | 98e3da8d-a700-42a9-807e-a06ca0c4705d |

## Succession Status
- Succession required: no
- Spawn count: 3 / 16
- Pending subagents: ee6ce96a-2581-49ea-9ba2-69478762568c, fec73b90-f3d7-45e1-9229-a03f3657c5c2, 98e3da8d-a700-42a9-807e-a06ca0c4705d
- Predecessor: none
- Successor: not yet spawned

## Active Timers
- Heartbeat cron: task-17
- Safety timer: none

## Artifact Index
- i:/projects/voice chat/PROJECT.md — Global index, architecture, milestones, interfaces
- i:/projects/voice chat/.agents/orchestrator/progress.md — heartbeat progress tracker
- i:/projects/voice chat/.agents/orchestrator/original_prompt.md — verbatim original prompt
