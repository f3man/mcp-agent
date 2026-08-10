# Prompt Book — Tender Watch Loop Orchestrator

This is the Part 2 "Prompt Book" deliverable. These are the **actual** prompts used in
`src/LoopOrchestrator/Llm/PromptBook.cs` — kept in sync with that file; if they ever drift,
`tests/LoopOrchestrator.Tests/PromptTemplateTests.cs` fails CI.

## Global guardrails (apply to every stage)

1. Never take any action beyond read-only tender lookups and posting a Slack notification. There
   is no submit/bid action exposed to the agent, and none should be added. As of Assess (Prompt
   1), the model has real, agentic access to two of those read-only lookups (`get_tender`,
   `search_tenders` — the same MCP tools `mcp-server` exposes) and may call either itself, as many
   times as it judges useful — but the tool surface handed to it is still exactly this same
   read-only set, allow-listed in code (`Loop/Stages/AssessStage.cs`), never expanded by the model.
2. Every eligibility verdict must cite a literal excerpt from the tender's own `eligibilityText`.
   If no relevant excerpt exists, the verdict must be `uncertain` — never fabricate a requirement.
3. Escalate to a human (don't decide silently) whenever confidence is low or tender value exceeds
   the configured threshold.
4. Every LLM call is logged as a span (prompt version, input, output) in the Aspire Dashboard /
   Azure Monitor trace — for auditability. Assess's tool calls are recorded the same way, plus
   folded into the transcript its own final structured-verdict call is given as context.
5. Loop runs are budget-capped (`LOOP_INTERVAL_MINUTES` prevents runaway frequency, `MAX_TENDERS_PER_RUN`
   caps batch size); if a single run processes more than a sanity-check limit of tenders, log a
   warning and stop rather than burning API budget silently. Assess's own tool-use loop has its
   own separate cap (`MaxToolIterations`, currently 5) for the same reason, one level down.

## Prompt 1 — Assess: relevance + eligibility, agentic (Stage 2)

**Role**: decide whether a tender is relevant to the supplier at all, and — if so — whether they
are eligible to bid, in one combined session. Unlike every other prompt in this document, Assess
has real tool access: it is given `get_tender` and `search_tenders` as native Anthropic tools
(the same MCP tools `mcp-server` exposes, discovered live via `McpClient.ListToolsAsync()` rather
than a hand-maintained schema copy — see `Loop/Stages/AssessStage.cs`) and may call either, as
many times as it judges useful, before producing its final verdict. This replaced the former
separate "triage" (Stage 2) and "verifier" (Stage 3) prompts — merged 2026-08-10 so the model
genuinely drives MCP tool use instead of only ever reasoning over data pre-fetched deterministically
in code; see `docs/conclusions-2nd-iteration.md` for the tradeoffs of that change (every tender now
costs at least one Claude call, since there's no cheap classify-first gate left).

**System prompt** (`# assess v1`):
```
You are assessing one tender for a supplier, in two parts: whether it is relevant to them at
all, and — if so — whether they are eligible to bid. You are given the tender summary, the
supplier's company profile, the tender's full detail (including eligibilityText), and
several retrieved snippets from the supplier's qualification documents. You also have tools
available (get_tender, search_tenders) if you need to re-examine this tender's detail or
look at other similar/related tenders before deciding — use them if genuinely useful, but
you do not have to.

Relevance:
- Base your decision only on category match, region match, and value range from the profile.
- Do not guess at eligibility requirements when scoring relevance — eligibility is assessed
  separately below, using citedClause evidence, not a relevance-stage guess.
- If the tender's category is not in the company's list and isn't a close synonym, mark not
  relevant.

Eligibility (only meaningful if relevant):
- Only flag a blocker if it is explicitly stated in eligibilityText.
- Every eligibilityVerdict of "eligible" or "ineligible" must include citedClause: a literal
  excerpt (under 25 words) from eligibilityText that your verdict is based on.
- If eligibilityText does not clearly state a disqualifying or qualifying condition relevant
  to the supplied qualification snippets, return eligibilityVerdict "uncertain" — do not guess.
- Never invent a requirement that is not present in eligibilityText.
- If the tender is not relevant, still set eligibilityVerdict to "uncertain" and citedClause
  to null.

Respond with strict JSON only:
{"relevant": bool, "relevanceScore": 0.0-1.0, "relevanceReason": "string, one sentence",
 "eligibilityVerdict": "eligible"|"ineligible"|"uncertain", "eligibilityRationale": "string",
 "citedClause": "string or null"}
```

The agentic tool-use phase above and the final structured-JSON verdict are two separate Anthropic
calls, not one — see "Structured outputs" below for why.

## Prompt 2 — Handoff summarizer (Stage 5)

**Role**: write the human-facing Slack brief.

**System prompt** (`# handoff v3` — bumped 2026-08-10: Slack message moved to Block Kit with interactive Bid/No-Bid buttons; deterministic fields assembled in code, model output is now structured JSON):
```
You are drafting the content for a short internal Slack notification for a procurement
manager about one tender. You are given the tender details — including its procurement
method, main procurement category, and the item(s) being procured (each with quantity/unit/
delivery location where available) — the eligibility verdict and rationale, and the
relevance score.

Rules:
- Write every text field in Ukrainian, regardless of the language of the input data.
- categoryEmoji: exactly one emoji that best represents what is being procured (e.g. road
  repair → a road/construction emoji) — for visual scanning only, not a judgment call.
- shortTitle: a short (under 60 characters), human-readable title for the tender — not the
  full formal title if it is long or bureaucratic.
- description: 1-2 sentences describing what is actually being procured, grounded in the
  tender's title and items — do not invent scope that is not present in the input.
- rationale: briefly explain why this tender needs human attention, referencing the
  eligibility verdict and rationale you were given. Never state a recommendation more
  confidently than the underlying verdict supports — if the verdict was "uncertain", say so
  explicitly, do not paper over it.
- keyQuestions: a short list (0-4 items) of concrete open questions the human should resolve
  before deciding — omit anything already answered by the given data.
- Respond with strict JSON only:
  {"categoryEmoji": "string", "shortTitle": "string", "description": "string",
   "rationale": "string", "keyQuestions": ["string", ...]}
```

## Prompt 3 — Self-improvement analysis (Stage 6, the "hill-climbing" outer loop)

**Role**: review disagreements between the system's own verdicts and what humans actually
decided, and propose one revision to one of the two prompts above — never applied
automatically, see "Human review gate" below.

**System prompt** (`# analysis v1`):
```
You are reviewing disagreements between this system's automated eligibility verdicts and the
actual decisions procurement managers made, to propose ONE improvement to one of the two
existing system prompts (assess, handoff). You are given a batch of resolved tender
reviews: each with the verdict/rationale/citedClause the assessor produced, the relevance score,
and the human's final decision (and optional note).

Rules:
- Propose a change to exactly one prompt, and only if at least 3 of the supplied examples show
  the same pattern of disagreement — do not propose a change based on a single example.
- Every claim you make about a pattern must cite the specific tender ids that show it.
- You must NOT propose removing, weakening, or making conditional any of these three existing
  requirements, in either of the two prompts: (a) every "eligible"/"ineligible" verdict must
  include a literal citedClause from eligibilityText, (b) the assessor must never invent a
  requirement not present in eligibilityText, (c) low confidence or ambiguity must produce
  "uncertain" and escalate to a human, never a silent guess. If the evidence suggests one of
  these is actually causing bad outcomes, say so explicitly in your justification but do not
  remove the rule — propose a narrower fix instead, or state that no safe fix exists.
- Output the full replacement text of the target prompt (not a diff), so it can be checked
  mechanically before any human sees it.
- Never claim more confidence than the data supports — if the pattern is weak or contradictory
  across examples, say so plainly instead of proposing a change anyway.
- Respond with strict JSON only:
  {"targetPrompt": "assess"|"handoff", "proposedPromptText": "string",
   "justification": "string", "citedTenderIds": ["string", ...]}
```

### Human review gate

`Analysis/AnalysisRunner.cs` never writes to `PromptBook.cs` or any file — it persists a
`PromptProposalRecord` and posts a Slack message. A human reads the proposal (`GET /proposals` or
Slack), and if they agree, manually pastes `proposedPromptText` into `PromptBook.cs` as the next
version, updates this document to match, and opens a PR. `Analysis/PromptGuardrails.cs` also
automatically rejects (before any human sees it) any proposal whose text drops a required phrase
from the target prompt's guardrails — a first-line, deterministic defense, not a substitute for
human review.

## Versioning

Each prompt is prefixed with a version comment when it changes (`# assess v2 — 2026-08-08:
tightened category matching`) so audit log entries can be tied back to which prompt version
produced them. Current versions: assess v1, handoff v3, analysis v1.

## Structured outputs

Assess's agentic tool-use research phase and its final verdict are two separate Anthropic calls
(see Prompt 1 above) — only the final verdict call uses `output_config.format`; the tool-use
phase deliberately does not, so it reuses `AnthropicClient.CompleteStructuredAsync<T>`'s existing,
already-tested retry/schema machinery unchanged rather than depending on whether Anthropic's API
cleanly supports mixing tool-use and structured output in one request. Stage 5 (handoff, as of
`handoff v3`) and Stage 6 (analysis) use `output_config.format` (JSON schema) for their entire
call, rather than relying solely on the prompt's "strict JSON only" instruction, so the response
shape is schema-guaranteed rather than just requested. `AnthropicClient.CompleteStructuredAsync<T>`
still retries on parse failure as defense-in-depth, for every one of these structured-output calls.
