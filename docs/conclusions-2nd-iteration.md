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

## Local run still has full observability parity with the cloud plan

Same story as Task 1: nothing about running this locally trades away the telemetry story. The
same `ActivitySource`s, the same OpenTelemetry SDK wiring in `ServiceDefaults`, and the same
Application-Insights-when-publishing / Aspire-Dashboard-when-local conditional apply to
`loop-orchestrator` exactly as they already did to `mcp-server`.

*(Aspire Dashboard trace screenshot placeholder — a `loop-run` root span with nested
`discover`/`classify`/`verify`/`persist`/`handoff` children and an `mcp-server` child span for the
`tools/call` request, from the fully-successful run above.)*

## Cloud deployment status

Not deployed for this iteration — `loop-orchestrator` and `storage`/`tender-state` are wired into
`AppHost.cs`'s existing `IsPublishMode` gating (same pattern as `mcp-server`/`appinsights`), so
`aspire deploy` should pick them up alongside the already-deployed `mcp-server` without changes,
but this has not been run yet.
