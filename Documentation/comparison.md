<!-- Recordings are committed at Documentation/media/{no-hades,hades}-run.mp4 (H.264, inline-playable on GitHub).
     Numbers below are REAL (captured from /usage on clean single-prompt runs). Do not round. -->

# With and without Hades: one prompt, side by side

This is a single, reproducible task run twice under identical conditions — once with stock Claude Code, once with Hades. Same prompt, same model, same project. The only variable is whether Hades is connected.

The point isn't speed. Both runs took about the same wall-clock time. The point is that **one answer is correct and one is confidently wrong** — and the wrong one would lead you to break your project. Cheaper is a bonus.

## The setup

| | |
|---|---|
| **Model** | Claude Opus 4.8 (1M context, high effort), Claude Code v2.1.169 |
| **Prompt** (verbatim, both runs) | *"I want to change how EnemyAI works on enemies. Which prefabs and scenes are affected?"* |
| **Project** | A small purpose-built arena (see below) |
| **Only variable** | Hades on (`.mcp.json` + `CLAUDE.md` present) vs off (both removed) |

### The project

A textbook Unity setup — the kind of relationship that lives in *asset wiring*, not code:

- `Enemy.prefab` — base prefab, has an `EnemyAI` component (and `Health`)
- `Enemy_Fast`, `Enemy_Tank`, `Enemy_Boss` — **prefab variants** of `Enemy`, each overriding stats. They inherit `EnemyAI` from the base; they do **not** re-declare it.
- Three scenes — `Level1`, `Level2`, `BossArena` — instantiating overlapping mixes of those four prefabs.

The correct answer to the prompt: **4 prefabs** (the base + 3 variants that inherit the component) across **3 scenes**.

## The results

| | Without Hades | With Hades |
|---|---|---|
| **Prefabs found** | 1 ❌ (missed all 3 variants) | **4 ✅** (base + 3 inherited variants) |
| **Scenes found** | 1 ❌ (missed Level2 + BossArena) | **3 ✅** |
| **Verdict** | **Confidently wrong** | **Correct** |
| **Recommended next step** | "Add `EnemyAI` to the variants" — *would break inheritance* | "Edit the base; mind variant overrides" |
| **Tool calls** | ~17 (across 2 search sub-agents) | ~7 |
| **Context pulled in** | 205.6k tokens (cache read) | 117.4k (**~43% less**) |
| **Session cost** | $0.3838 | $0.2815 (**~27% cheaper**) |

📹 **Proof — full uncut runs:**
[Without Hades](media/no-hades-run.mp4) · [With Hades](media/hades-run.mp4)

## What each agent actually said

**Without Hades** (stock grep/read) concluded:

> "`Enemy.prefab` — the only prefab with EnemyAI on it."
> "`Enemy_Boss`, `Enemy_Fast`, and `Enemy_Tank` exist but have no EnemyAI component… these three are currently **outside the system** — you may want to **bring them in, which would be new work**."

This is wrong twice over. The three variants *do* have `EnemyAI` (inherited). And the suggested fix — adding the component to each variant — would create duplicate components and corrupt the prefab inheritance the project relies on.

**With Hades** concluded:

> "**Prefabs affected (4):** `Enemy` (direct), `Enemy_Boss` / `Enemy_Fast` / `Enemy_Tank` (inherited). The three variants inherit EnemyAI from the base, so changing the component on the base propagates to all of them."
> "**Scenes affected (3):** Level1, Level2, BossArena."
> "Edit at the base prefab… watch for serialized field overrides on the variants — if you rename or remove a serialized field, those overrides can be lost."

## Why the stock agent got it wrong

This isn't a knock on the model — it's the same model both times. It's a tooling-blindness problem.

In Unity, a **prefab variant does not re-serialize the components it inherits.** Its `.prefab` file stores only *overrides*. So when the stock agent greps the variant files for the `EnemyAI` script GUID, it finds **nothing** — and reasonably (but wrongly) concludes the variants don't use it. The link from variant → base is a `fileID`/GUID reference that text search can't follow.

Hades models that link as a structural `inherits_from` edge in the graph, so "which prefabs have an `EnemyAI`?" returns the base **and** every variant that inherits it — a fact, not a guess.

## Reproduce it yourself

The comparison is only worth anything if you can run it. Here's exactly how.

1. **With Hades** — open Claude Code in a Hades-enabled Unity project and ask the prompt. Note the answer and `/usage`.
2. **Without Hades** — temporarily disable both Hades layers, in the project root:
   ```bash
   mv .mcp.json .mcp.json.off    # disconnects the Hades MCP tools
   mv CLAUDE.md  CLAUDE.md.off    # removes the graph-first guidelines
   ```
   Disabling only the MCP server is **not** a fair baseline — the `CLAUDE.md` still tells the agent to use a graph it can no longer reach. Remove both.
3. Open a fresh Claude Code session, run `/mcp` to confirm no servers are connected, and ask the identical prompt.
4. Restore when done:
   ```bash
   mv .mcp.json.off .mcp.json
   mv CLAUDE.md.off  CLAUDE.md
   ```

Keep everything else identical — same model, same project state, same prompt — so the only variable is Hades.

## Honest notes

- **Numbers are from clean runs.** Each side was a fresh Claude Code conversation containing only this one prompt, captured via `/usage`. Cost is the cleanest single metric to quote (it folds in a small amount of auxiliary model usage), rather than cherry-picking a single model line.
- **Hades wasn't flawless.** One query (`find_references_to` on the shared `DamageConfig` asset) returned the right count but without paths, so the agent spent a few extra calls tracing prefab→scene links itself. It still landed the correct, complete answer — but the graph has rough edges we're filing down.
- **Time is not the story** and isn't reported here. Both runs were within seconds of each other. The value is correctness and cost, not speed.
