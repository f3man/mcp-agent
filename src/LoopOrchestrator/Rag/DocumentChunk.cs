namespace LoopOrchestrator.Rag;

/// <summary>Verbatim from docs/task-2/02-rag-and-data.md.</summary>
public sealed record DocumentChunk(string Id, string SourceFile, string Text, float[]? Embedding = null);
