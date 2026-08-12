# Troubleshooting

## Embedding dimensions exceed the provider limit

SQL Server supports at most 1,998 native vector dimensions. Configure `Embeddings.OutputDimensions` explicitly. SemanticKnowledge will not perform lossy reduction automatically.

## Query embedding rejected even though dimensions match

Embedding-space fingerprint must also match. Same-sized vectors from different models/revisions/transforms are not assumed compatible.

## PostgreSQL says the vector extension is missing

Install/enable pgvector outside the application. SemanticKnowledge intentionally does not execute privileged `CREATE EXTENSION` at startup.

## Startup asks for a logical migration

For an Authoritative store, register a `.Migrate(oldVersion, newVersion, ...)` chain. Missing, ambiguous, and downgrade paths fail closed. Rebuildable stores reset on a logical version change.

## Startup rebuilds embeddings

The configured semantic fingerprint/dimensions/storage differs from the active generation. Canonical data remains the source of truth and is re-embedded into a pending generation before activation.

## Smart Search routes somewhere unexpected

Improve Collection titles/descriptions/tags and verify the query's relevant subtree is represented semantically. Smart routing intentionally uses Collection metadata before document retrieval.

## HTTP/custom embeddings fail resolving candidate reranking

SemanticKnowledge registers a DefaultV1-compatible fallback reranker for non-ONNX providers. Ensure `AddSemanticKnowledge()` is called before the database provider registration.

## Native caller receives an error code

Call `sk_get_last_error` on the same thread. Do not log connection strings or secrets into caller-generated error messages.
