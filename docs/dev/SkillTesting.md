# Testing the Claude skills

How the two diagnosis skills were tested (2026-11-23/24) and how to test them again after a change.

| Skill | Where | Audience |
|---|---|---|
| `fuluma-render-debug` | `plugins/fuluma/skills/` (Claude Code plugin; marketplace in `.claude-plugin/marketplace.json`) | package users |
| `fuluma-dev-render-debug` | `.claude/skills/` (found by Claude Code in this repository) | package development |

## Test cases

Each skill keeps its cases in `evals/<case>/`, in the format of Anthropic's skill-creator (which `claude plugin eval` also reads):

- `prompt.md`: a settings block (`allowed_tools: [Read, Glob, Grep, Bash]`, turn and time limits), then the user's message as a user would write it.
- `case.yaml`: name and `expected_outcome` (the planted cause and the reply we want).
- `graders/NN-*.md`: one assertion per file (`type: llm`). Word them about the reply ("Names X as the cause", "Asks before changing anything"), never about where it is saved.
- `files/` + `place-files.sh`: a small faulty project (YAML assets, scene, scripts, logs) copied into the run's working folder. Each case plants **one** fault that a careful reader can find from the files alone. The rest of the chain must be correct, or the case tests two things.

The user skill's cases may only rely on what ships in the package and the plugin. The dev skill's cases may point at anything in the repository (`docs/dev`, tests, scripts).

## Running

1. Create a workspace **outside the repository** (the session scratchpad): `<skill>-iteration-<N>-<model>/<case>/{with_skill,without_skill}/`. Copy each case's `files/` into each run's `project/` folder. Write `eval_metadata.json` (`eval_name`, `prompt`, `assertions`) next to the two configurations.
2. Start one subagent per case × configuration × model, in one go: **with the skill** (the prompt names the `SKILL.md` path) and **without** (same prompt, no skill). Use the models the skill must work on: Sonnet and Haiku so far. Each prompt says:
   - work read-only: change no project files, run neither Unity nor the phones;
   - save the reply to `outputs/report.md`; if that write is refused, return the whole reply as the final message.
3. Record `total_tokens` and `duration_ms` from each agent's completion notification into the run's `timing.json`. The notification is the only place they appear.

Limits and pitfalls:
- At most 20 subagents run at once; queue the rest.
- Subagents are often refused `Write`. Save their returned text as `report.md` yourself, marked "Saved by the eval runner". Their `.output` files are empty, so copy the text while it's in the conversation. After a context compaction, recover it from the session transcript (`~/.claude/projects/<project>/<session>.jsonl`, `<agent-message from="<id>">`).
- Keep an `agent id → run directory` list in the workspace while the runs are going.

## Grading and review

1. Grade every run against its assertions into `grading.json` (`expectations[]` with `text`, `passed`, `evidence`, plus a `summary`). Write the grading rule for judgment calls down once and apply it to every run. Example: "asks first" passes only if the reply ends with a question and claims no change.
2. Aggregate each workspace from the skill-creator folder: `python -m scripts.aggregate_benchmark <workspace> --skill-name <name>`. Add your own observations to `notes` in `benchmark.json`, for example non-discriminating assertions or time and token differences.
3. Build the viewer: `python eval-viewer/generate_review.py <workspace> --skill-name <name> --benchmark <workspace>/benchmark.json --static <file>.html` (add `--previous-workspace` when the cases are the same as last time). Send the file to the user. Their feedback comes back as `feedback.json`.

## Reading the results

- A case is useful only if it **discriminates**: if the baseline also passes, it doesn't show what the skill adds. Round 1 (three cases per skill) gave 100% with the skill and 87-100% without. So round 2 used harder faults: Custom stripping, a Keep box off the beam, statics with domain reload off, Editor Only sync, MSAA with the light buffer, Adreno clip cost, and a new shader keyword.
- Results so far:
  - User skill: 100% with it, 87% without, on both models. Baselines find the causes but often hand over a fix without asking, and the Haiku baseline hedged on stripped variants.
  - Dev skill: 100% with it, and 100% (Sonnet) or 90% (Haiku) without. Its cases are still too easy for a baseline that can read `docs/dev`.
- When the skill changes, rerun the affected cases on both models. Compare them with the previous iteration in the viewer. Add a case for every real misdiagnosis that turns up.
