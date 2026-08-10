# Demo script — Loop Orchestrator (Task 2)

Follows the 8 beats from `docs/initial-specs/task-2/04-demo.md`. This is the script to record
from — the exact commands and the output to expect at each step are the real output this session
produced against live Prozorro data, real Anthropic/OpenAI calls, and a real Slack channel (see
`docs/conclusions-2nd-iteration.md`); a real recording will show your own numbers, which will
differ run to run since Prozorro's tender list changes over time.

Architecture reference for beats 3–4: `docs/diagrams/architecture-components.puml` (agent ↔ RAG ↔
memory ↔ external APIs), `docs/diagrams/architecture-sequence.puml` (the exact call sequence that
produces the trace you'll be narrating, plus the Slack button-click round-trip as a second
diagram in the same file), and `docs/diagrams/architecture-analysis.puml` (the self-improvement
outer loop, kept separate for readability). Render any of these with a PlantUML viewer (VS Code's
PlantUML extension, JetBrains' PlantUML Integration plugin, or paste the file contents at
https://www.plantuml.com/plantuml/uml/).

## 1. Setup (10s)

```bash
dotnet user-secrets set "Parameters:mcp-api-key" "<key>" --project src/AppHost
dotnet user-secrets set "Parameters:anthropic-api-key" "<key>" --project src/AppHost
dotnet user-secrets set "Parameters:openai-api-key" "<key>" --project src/AppHost
dotnet user-secrets set "Parameters:slack-webhook-url" "<url>" --project src/AppHost

aspire run --project src/AppHost/AppHost.csproj
```

Open the printed Aspire Dashboard URL. Show `mcp-server`, `loop-orchestrator`, `storage`, and
`tender-state` all reaching **Running** in the resource list — that's the whole distributed
application, one command.

> If `aspire run` crashes immediately on your machine (some WSL2/containerized setups hit a known
> upstream orphan-detector bug — see README), use
> `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AppHost --no-launch-profile`
> instead; same result, just without the CLI's decorative wrapper.

## 2. Trigger a run (20s)

Find `loop-orchestrator`'s port from the dashboard, then:

```bash
curl -X POST http://localhost:<port>/run-now
```

Expected shape of the response (your counts will vary with the live tender list):

```json
{"started":true,"processed":100,"skipped":98,"verified":0,"handedOff":2,"failed":0}
```

## 3. Show the trace (45s)

In the dashboard's **Traces** view, open the `loop-run` trace for that request. Narrate while
clicking through — it's one connected trace across the whole pass:

- `discover` — one child span, tags for how many candidates came back and how many were new.
- one `classify` span per tender, each with a nested `llm-call` span (`llm.prompt.version`,
  `llm.input`, `llm.output` tags — the actual triage prompt and the model's JSON response).
- for tenders that passed Classify: a `verify` span, nested under it an MCP `get_tender` call
  (a real child span reaching into `mcp-server`'s own trace — free via W3C trace-context
  propagation, no extra wiring) and a `llm-call` for the verifier.
- `persist` spans (one per write to Table Storage).
- `handoff` spans for anything escalated, with their own `llm-call` for the Slack brief.

Point out that this single view is the entire pipeline for that run — nothing happened outside
what's visible here.

## 4. Show Verify up close (45s)

Pick one `verify` span that reached a real verdict (not the zero-snippets `uncertain` shortcut).
Open its `llm-call` child and read:

- `llm.input` — the tender's `eligibilityText` plus the qualification-doc snippets that
  `IEligibilityIndex.QueryAsync` retrieved (cosine similarity over
  `data/qualification-docs/*.md`, embedded via OpenAI `text-embedding-3-small`) — this *is* the
  RAG step made visible: real retrieved text, not a black box.
- `llm.output` — the `{verdict, rationale, citedClause}` JSON. Point out `citedClause` is a
  literal excerpt from `eligibilityText`, never invented — enforced in code
  (`VerifyStage.cs`), not just by asking the prompt nicely.

## 5. Show a clean skip (20s)

Find a tender persisted with `Status=Skipped` (Classify said not relevant) or `Status=Verified`
with no `HandoffSentAt` (eligible but under `HANDOFF_VALUE_THRESHOLD`, UAH 500,000 by default).
Point out: logged to Table Storage, visible in the trace, zero Slack noise generated.

## 6. Show a handoff (30s)

Open your Slack channel and show the message that landed — read the recommendation out loud. It
was written by the same LLM call visible as the `handoff` span's `llm-call` in step 3; the text is
plain, not JSON, per the prompt book's Stage 5 rule ("this goes straight into a Slack message").

## 7. Show persistence (20s)

```bash
curl -X POST http://localhost:<port>/run-now
```

Expected response — this is the idempotency proof, the actual point of this beat:

```json
{"started":true,"processed":0,"skipped":0,"verified":0,"handedOff":0,"failed":0}
```

Zero processed. Open the new `loop-run` trace in the dashboard: the `discover` span shows the same
candidate count as before but `0 new` — every tender from step 2 is now in Table Storage's
seen-ID set. State is genuinely load-bearing, not just written and ignored.

## 8. Wrap (20s)

One sentence: a procurement manager reading that Slack brief now decides bid / no-bid / "I need to
look at this myself" — the agent's job stops exactly there; there is no submit/bid tool anywhere
in this codebase, by design.
