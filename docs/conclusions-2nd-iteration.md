# Conclusions — Task 2, 2nd Iteration (Loop Orchestrator)

Referenced from the main [`README.md`](../README.md). Covers `src/LoopOrchestrator` — the
autonomous discover → classify → verify → persist → handoff loop built on top of Task 1's MCP
server. See [`conclusions-1st-iteration.md`](conclusions-1st-iteration.md) for the MCP server
itself and [`prompt-book.md`](prompt-book.md) for the three system prompts and guardrails.

## What was built

- **State store**: Azure Table Storage (Azurite locally via Aspire's `RunAsEmulator()`, real
  Storage when publishing), one table holding both per-tender review records and a singleton
  "last successful run" marker. The seen-tender-ID set from this store is the **sole**
  authoritative idempotency guard — a date-based heuristic exists purely for logging, never for
  filtering.
- **RAG**: qualification docs (`data/qualification-docs/*.md`) chunked on `##` headings, embedded
  via OpenAI `text-embedding-3-small`, queried by in-memory cosine similarity. Missing/failing
  `OPENAI_API_KEY` doesn't crash anything — the index stays empty (logged, not thrown) and Verify
  falls back to `uncertain`, matching the guardrail philosophy ("escalate rather than decide
  silently").
- **LLM**: hand-rolled Anthropic Messages API client (`claude-haiku-4-5-20251001`), structured
  JSON-schema outputs for Classify/Verify, plain text for the Handoff summary.
- **Notifications**: a plain Slack incoming webhook POST.
- **Tracing**: a second `ActivitySource` (`TenderWatch.LoopOrchestrator.Stages`) alongside Task
  1's, so every stage and every LLM call shows up as its own span nested under one root
  `loop-run` activity per run.

## Fully verified live, end to end, with real credentials

After supplying real `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, and `SLACK_WEBHOOK_URL`, a complete
`/run-now` pass against 100 genuine active Prozorro tenders finished with **zero failures**:

```json
{"started":true,"processed":100,"skipped":98,"verified":0,"handedOff":2,"failed":0}
```

- **Classify**: 98 of 100 real tenders correctly judged not-relevant against the company profile,
  2 passed through to Verify.
- **Verify**: ran the real RAG query (embeddings + cosine similarity over the qualification docs)
  and the real verifier LLM call for both.
- **Handoff**: both resulted in a real Anthropic-written brief and a real Slack POST —
  **confirmed `200 OK` from Slack for both** (checked directly in the process log, not inferred).
- **Idempotency**: an immediate second `/run-now` returned
  `{"processed":0,"skipped":0,"verified":0,"handedOff":0,"failed":0}` — all 100 tenders correctly
  recognized as already-seen, nothing reprocessed, no duplicate Slack notification.

This is the actual, live, unmocked pipeline — real Prozorro data in, real Claude Haiku calls for
classification/verification/summarization, real OpenAI embeddings, a real message delivered to a
real Slack channel.

## Real bugs found and fixed via this live verification

Every one of these was invisible to the 28 fake-HTTP unit tests — they only surface against the
real APIs' actual validation rules and this environment's actual behavior. Found and fixed in this
order, each one gating the next:

1. **`aspire run` crashed immediately, every time, via the CLI wrapper**, in this WSL2 environment.
   Root cause: Aspire's `CliOrphanDetector` (anti-PID-reuse check) compares two independently
   computed process-start-time readings; here they disagreed by a few seconds in inconsistent
   directions, so the freshly-started AppHost concluded its parent CLI had died and shut itself
   down cleanly (exit 0, no visible error). Matches a known, unresolved upstream issue
   ([dotnet/aspire#8244](https://github.com/dotnet/aspire/issues/8244)). Worked around by running
   `dotnet run --project src/AppHost` directly — same underlying launch mechanism, minus the CLI's
   orphan-detector env vars.
2. **`--no-launch-profile` silently skipped `ASPNETCORE_ENVIRONMENT=Development`**, which is what
   makes user secrets load at all — `mcp-api-key` read as `ValueMissing` despite being correctly
   set, and `mcp-server` never started. Fixed by exporting the environment variable explicitly.
3. **Optional secret parameters blocked startup exactly like required ones.** `anthropic-api-key`
   / `slack-webhook-url` / `openai-api-key` with no configured value stayed `ValueMissing` forever
   and prevented `loop-orchestrator` from starting at all, even though the *app* code already
   treated them as optional. Fixed by resolving each to `""` when unset at the AppHost level
   (`builder.Configuration["Parameters:" + name] ?? ""`) so Aspire is satisfied immediately while a
   real secret still flows through unchanged when present.
4. **A single tender's failure aborted the entire batch with a raw `500`.** The very first live
   `/run-now` call (before real Anthropic credentials landed correctly) hit this immediately.
   Fixed by wrapping each tender's processing in `LoopRunner.RunAsync`'s loop in its own
   try/catch — one failure is now logged and skipped, the batch continues, and the response
   reports an honest per-tender count instead of crashing.
5. **A real OpenAI quota-exhausted `429` crashed the whole process at startup.** Startup indexing
   (`IndexQualificationDocsAsync` → `InMemoryEligibilityIndex.IndexAsync`) called the embedding API
   with no guard against the *configured-but-failing* case (only the *not-configured* case was
   handled) — an unhandled exception here takes the whole process down before it ever serves a
   request. Fixed with a try/catch around the embedding loop that degrades to an empty index +
   `LogError`, exactly like the not-configured case.
6. **`user-secrets set` on Windows vs. WSL are genuinely separate stores.** The three credentials
   were set via a Windows-side `dotnet`, landing in
   `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json` under the raw env-var names
   (`ANTHROPIC_API_KEY` etc.) — invisible to the WSL-side `dotnet run` this verification uses
   (which reads `~/.microsoft/usersecrets/<id>/secrets.json`), and under the wrong config keys
   besides (`AddParameter` needs `Parameters:<param-name>`, not the raw env-var name). Resolved by
   copying the three values across into the correct WSL-side keys via `jq` + `dotnet user-secrets
   set`, without ever printing the raw values.
7. **Anthropic's structured-outputs schema rejects `minimum`/`maximum` on a `number` type** —
   `output_config.format.schema` returned `400: "For 'number' type, properties maximum, minimum
   are not supported"` for the Classify schema's `relevanceScore` field. A real API-side constraint
   with zero documentation trace in this codebase's fake-HTTP tests. Fixed by dropping the
   `minimum`/`maximum` keywords and relying on the prompt's own "0.0–1.0" instruction instead
   (`JsonSchemas.TriageResult`).
8. **A tender with a blank `eligibilityText`** crashed `VerifyStage` at the embedding-API layer
   (`ArgumentException: Value cannot be an empty string`) — not every real Prozorro tender
   documents eligibility criteria. Fixed by short-circuiting to the existing "uncertain, no
   snippets" fallback *before* ever querying the index when `eligibilityText` is blank.
9. **`System.Text.Json` serializes a null `AnthropicMessageRequest.OutputConfig` as a literal
   `"output_config": null`**, which Anthropic's API rejects outright (`400: "Input does not match
   the expected shape"`) rather than treating as absent — this only affects the plain-text Handoff
   path, since Classify/Verify always pass a real `outputConfig`. Fixed with
   `JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)` so the field is omitted entirely.

Each fix was verified against the real system immediately after being made — not just re-run
against the unit test suite (which stayed green throughout, 61/61, since none of these were
reachable by fake-HTTP scenarios).

## Richer tender data, real Slack Block Kit UI, Ukrainian output

Extended `get_tender`'s response with fields the original scope didn't cover — `tenderId`
(Prozorro's own human-readable official number, e.g. `UA-2020-03-17-000090-a`, distinct from the
existing internal `Id`), `procurementMethod`, `mainProcurementCategory`, and a real `items[]`
array (`id`, `description`, `unit.name`, `quantity`, `deliveryAddress.region`/`.locality`) — all
confirmed against the real live Prozorro API before implementing, not guessed (e.g.
`mainProcurementCategory` is genuinely absent on legacy tender records; `deliveryAddress` can be
entirely null on some cancelled/older tenders).

That data now drives a real Slack message instead of a plain-text paragraph: `HandoffStage`
assembles genuine Slack Block Kit (`header`/`section`/`divider`/`actions` blocks) with two
interactive buttons (`✅ Подати заявку` / `❌ Відмовитися`, `action_id`s `tender_bid_action`/
`tender_nobid_action`). The deterministic parts (tender id, formatted value/deadline/delivery
region, recommendation label+emoji) are assembled in **code** from the verdict/`TenderDetail`
directly — the LLM's job narrowed to only the parts that genuinely need generating (a category
emoji, a short title, a description, the rationale, open questions), all **in Ukrainian**, as
structured JSON (`handoff v3`) rather than free-form English prose.

Receiving a real button click requires `POST /slack/interactions`, which verifies Slack's request
signature (HMAC-SHA256 per Slack's own documented algorithm) against a new `SLACK_SIGNING_SECRET`
— separate from `SLACK_WEBHOOK_URL` — before trusting anything; with no signing secret configured
it fails **closed** (503), never open. Getting real button clicks flowing end-to-end additionally
needs a one-time step outside this codebase: the Slack App that owns the incoming webhook must
have "Interactivity & Shortcuts" enabled with its Request URL pointed at
`PUBLIC_BASE_URL + /slack/interactions`.

**Verified live**: a real `/run-now` produced 8 genuine handoffs — real Ukrainian briefs
referencing real item descriptions/quantities/values (e.g. "500 пляшок... для доставки у
Львівську область"), each posted as real Block Kit and confirmed with a real `200 OK` from
Slack (which validates the block structure server-side, not just "something was sent"). A
second immediate run confirmed idempotency held (`processed:0`).

Also added: tenders with `procurementMethod == "limited"` (a real observed value — invite-only/
pre-selected procedures an outside supplier can't realistically bid on) are now excluded right
after Verify, before ever reaching Handoff — no wasted LLM call or Slack noise for a tender that
was never actually biddable.

## Self-improvement outer loop (hill-climbing)

`src/LoopOrchestrator` implements the *inner three* loops of LangChain's "Loop Engineering"
framing (scheduled trigger → agent loop → code-enforced verification gate) plus Cobus Greyling's
human-gate requirement, but was originally missing the outermost **hill-climbing loop**:
production outcomes feeding an analysis step that proposes prompt revisions, human-reviewed
before shipping. This MVP closes that gap:

- **Feedback signal**: `HandoffStage` now appends two plain links to every Slack brief —
  `GET /decisions/{tenderId}/Bid` and `.../NoBid` — so `HumanDecision` (previously written once as
  `"Pending"` and never updated) actually gets a real value when a human clicks one.
- **`Analysis/AnalysisRunner.cs`**: a second, much slower loop (default weekly,
  `ANALYSIS_INTERVAL_HOURS`, plus `POST /analyze-now` on demand) that reads resolved handoffs,
  looks for disagreements between the verdict and what the human decided (`uncertain`-but-bid,
  `eligible`-but-declined), and — only with at least 3 disagreements — asks Claude to propose a
  revision to one of the three prompts.
- **Human gate**: `AnalysisRunner` never writes to `PromptBook.cs` or any file (it has zero
  filesystem I/O — checkable directly: `grep -rn "File\.\|Directory\." Analysis/` returns nothing).
  It only persists a `PromptProposalRecord` and posts a Slack message; a human decides whether to
  manually paste the proposed text in as the next prompt version.
- **`Analysis/PromptGuardrails.cs`**: a deterministic, pre-Slack check that a proposal still
  contains every required safety phrase for its target prompt (e.g. the verifier prompt must still
  mention `citedClause`, `"Never invent a requirement"`, `"uncertain"`).

**Verified live**, with real credits, real disagreement signal, no synthetic seeding needed: a
`/run-now` pass produced 7 real `uncertain` handoffs; marking 3 of them `Bid` via the real decision
links created genuine disagreement signal; `POST /analyze-now` made a real Claude call and got back
a substantive, well-reasoned proposal (correctly citing all 3 tender IDs, arguing the verifier was
being needlessly conservative). That proposal was then **automatically rejected by
`PromptGuardrails`** — it had rewritten "Never invent a requirement" as "Never invent eligibility
criteria," a plausible-sounding rewording that nonetheless didn't survive the literal phrase check
— and was persisted with `Status="RejectedByGuardrail"`, `slackSentAt=null`, never reaching Slack.
This is a real, live demonstration of both halves of the design at once: the mechanism correctly
blocking an unreviewed change (exactly its job), *and* the documented, honest limitation of a
substring check (it can't tell "reworded safely" from "reworded unsafely," which is exactly why a
human reviews every proposal that *does* pass, and why a behavioral invariant-test suite is the
named Phase 2 upgrade below).

**Deferred (Phase 2, not built)**: audit-sampling of silently-`Skipped` tenders (Classify's real
blind spot — false negatives never reach a human at all today), `IPromptStore` for canary rollout
without a redeploy, behavioral `GuardrailInvariantTests` (stronger than the substring check — the
live run above is a direct illustration of why), and paired metrics reporting to resist a
prompt change that trivially improves one metric (e.g. more escalation) while making the system
less useful.

## Local run still has full observability parity with the cloud plan

Same story as Task 1: nothing about running this locally trades away the telemetry story. The
same `ActivitySource`s, the same OpenTelemetry SDK wiring in `ServiceDefaults`, and the same
Application-Insights-when-publishing / Aspire-Dashboard-when-local conditional apply to
`loop-orchestrator` exactly as they already did to `mcp-server`.

*(Aspire Dashboard trace screenshot placeholder — a `loop-run` root span with nested
`discover`/`classify`/`verify`/`persist`/`handoff` children and an `mcp-server` child span for the
`tools/call` request, from the fully-successful run above.)*

## Demo deliverables (`docs/initial-specs/task-2/04-demo.md`)

That spec asks for two things: a static architecture diagram (draw.io/Miro, not a dashboard
screenshot) and a demo recording/screenshot series following its 8-beat script. Two of these are
things a coding assistant can actually produce without a screen to record; one isn't:

- **Architecture diagram** — done, as PlantUML rather than draw.io/Miro (same "static diagram,
  not a dashboard screenshot" intent): `docs/diagrams/architecture-components.puml` (agent ↔ RAG
  ↔ memory ↔ external APIs, matching the spec's own framing),
  `docs/diagrams/architecture-sequence.puml` (the exact Discover→Assess→Persist→Handoff call
  sequence that produces the connected trace the demo script narrates, plus a second diagram
  in the same file for the Slack button-click round-trip), and
  `docs/diagrams/architecture-analysis.puml` (the self-improvement outer loop, split out of the
  components diagram once it grew too dense to read in one picture). All three rendered clean
  against the public PlantUML server with no syntax errors before being committed.
- **Demo script** — done: `docs/demo-script.md`, the 8 beats rewritten with exact commands and
  the real response shapes/numbers this session's live verification actually produced.
- **Demo recording or screenshot series** — still not done, and can't be produced by this
  assistant: it requires someone's screen, an Aspire Dashboard session, and a real Slack channel
  open side by side. `docs/demo-script.md` is written so anyone can follow it and produce that
  recording directly.

## Real LLM-driven MCP tool use — merging Classify+Verify into agentic Assess

Until this point, `loop-orchestrator` never let an LLM decide which MCP tool to call — every MCP
call site was a compile-time-fixed method on `IMcpTenderClient` (`DiscoverStage` always calls
`list_tenders` with fixed args, `VerifyStage` always calls `get_tender`), and every Anthropic call
was a single-shot `CompleteStructuredAsync`: all data pre-fetched deterministically in C# and
handed to the model as an already-assembled JSON blob, with zero ability for the model to request
more.

Changed that: `ClassifyStage` and `VerifyStage` are merged into one new `AssessStage`. Every
surviving (not-yet-seen) tender now gets one tool-augmented Claude session
(`AnthropicClient.RunAgenticToolLoopAsync`, new) where Claude decides itself whether/how to call
`get_tender`/`search_tenders` — the real MCP tools `mcp-server` exposes, discovered live via
`McpClient.ListToolsAsync()` rather than a hand-maintained schema copy — before producing both a
relevance judgment and an eligibility verdict. This genuinely satisfies "the LLM drives MCP tool
use," not a fixed wrapper around it.

One deliberate exception, **not** left to the model: `get_tender` is still called once in code up
front (exactly as `VerifyStage` used to), because `ProcurementMethodPolicy`'s exclusion filter is a
hard legal/business rule (an invite-only tender literally cannot be bid on), not a reasoning
judgment — it cannot depend on whether Claude happens to fetch `get_tender` in a given session.
Claude still gets `get_tender` (and `search_tenders`) as genuinely callable tools for the actual
relevance/eligibility research — free to re-fetch, or look at other tenders, or use neither.

Also deliberately **not** combined: the agentic tool-use phase and the final schema-guaranteed
verdict stay two separate Anthropic calls. The tool-use loop produces plain-text findings; a
normal (unchanged) `CompleteStructuredAsync` call afterward gets the schema-guaranteed JSON
verdict, with the loop's findings folded into that call's user message. This reuses 100% of the
already-tested structured-output retry/schema machinery rather than depending on whether
Anthropic's API cleanly supports mixing tool-use and `output_config.format` in one request.

Tradeoff accepted, explicitly: the former cheap `ClassifyStage`-first gate (skip an irrelevant
tender before spending any real LLM budget on it) is gone — every surviving tender now costs at
least one Claude call with tool access. `MAX_TENDERS_PER_RUN` remains the safety net against
runaway cost, and the tool loop itself has its own separate cap (`MaxToolIterations = 5`).

Guardrails carried over unchanged, verified still enforced: the citedClause requirement
(`AssessPolicy.EnforceCitedClauseGuardrail`, extracted as a pure function exactly like
`HandoffPolicy.ShouldEscalate`), the `ProcurementMethodPolicy` invite-only exclusion, and the
allow-list restricting which tool names `AssessStage`'s executor will actually call
(`AssessPolicy.IsAllowedTool` — `get_tender`/`search_tenders` only; `list_tenders`/
`get_company_profile` stay deterministic, hardcoded elsewhere). See
`docs/diagrams/architecture-components.puml` and `architecture-sequence.puml` for the updated
diagrams, and `docs/prompt-book.md`'s Prompt 1 for the merged `assess v1` prompt text.

## Cloud deployment status

Not deployed for this iteration — `loop-orchestrator` and `storage`/`tender-state` are wired into
`AppHost.cs`'s existing `IsPublishMode` gating (same pattern as `mcp-server`/`appinsights`), so
`aspire deploy` should pick them up alongside the already-deployed `mcp-server` without changes,
but this has not been run yet.
