# Search

SemanticKnowledge exposes three search modes.

**Global** searches documents across the selected KnowledgeBase. **Scoped** restricts retrieval to explicit Collections, optionally including descendants. **Smart** first searches Collection semantic identities and then searches documents inside the routed Collections.

```csharp
var request = new KnowledgeSearchRequest
{
    KnowledgeBaseId = kb.Id,
    Mode = KnowledgeSearchMode.Smart,
    Top = 10,
    CandidateCount = 100,
    Include = KnowledgeResultInclude.MatchedChunks
};

var hits = await store.SearchAsync("restore the postgres backup", request);
```

`CandidateCount` is optional. Providers use bounded candidate retrieval and rerank evidence using the pinned `OnnxTextEmbeddings.NET` scoring contract. `Top` controls final documents, not raw chunks.

Search results collapse semantic evidence back to Documents. A document may have evidence from several fields/chunks without flooding the result list with duplicate rows.

By default results are metadata-oriented. Request `MatchedChunks` when you need source evidence for RAG or UI excerpts. For a bounded context payload, prefer `IKnowledgeContentSearch` rather than hydrating whole documents.

You may also call the query-embedding overload. SemanticKnowledge validates dimensions and embedding-space fingerprint before search; same dimensions alone are not considered compatibility.
