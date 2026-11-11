# Portable agent definitions

This directory contains the canonical agent definitions for Codex, Claude, GitHub Copilot, and Hermes.

The files intentionally use only:

- Markdown instructions
- YAML front matter
- JSON handoff contracts
- GitHub labels, issues, pull requests, and checks

Provider adapters should translate these definitions into native configuration when needed. The canonical instructions in this directory remain provider-neutral.

## Agent contract

Every agent must:

1. Read the linked GitHub issue, relevant repository instructions, and prior agent reports.
2. Work only within the declared scope.
3. Preserve human-authored issue content.
4. Report assumptions, decisions, changed artifacts, verification, and unresolved risks.
5. Return a structured handoff matching `contracts/agent-result.schema.json`.

## Issue status dashboard

`docs/issues-status.html` is the static matrix for all open, non-confidential
issues. It records the issue, status, workflow phase, and current agent (the
agent's `id` from front matter); use `human` while approval or a product
decision is pending, and `triage` for issues not yet claimed. Update the
matrix whenever an agent accepts, hands off, or completes a phase, before
returning the handoff. Keep the issue link and last-updated date current.
Never include confidential security reports or their identifying details.
The `update_issue_status_dashboard` permission is limited to this file.

## Definitions

- `triage.md` — classify and route new issues.
- `planner.md` — turn a broad issue into an approved execution plan.
- `requirements.md` — clarify product behavior and acceptance criteria.
- `architect.md` — assess system design and implementation boundaries.
- `implementer.md` — implement an approved plan on a branch.
- `code-reviewer.md` — review correctness and maintainability.
- `security-reviewer.md` — assess security and privacy risks.
- `performance-reviewer.md` — assess latency, throughput, and resource impact.
- `tester.md` — verify behavior and add regression coverage.
- `documentation.md` — update user, developer, API, and operational documentation.
- `release.md` — prepare release notes and deployment considerations.

## Approval policy

Agents may comment, label, create branches, and open pull requests when their workflow grants those permissions. No agent may merge its own pull request. Human approval is required before implementation and before merging.
