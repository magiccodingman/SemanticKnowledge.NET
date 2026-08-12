# Native C interop

`SemanticKnowledge.Native` is a NativeAOT shared-library facade intended for Rust, C, C++, Python FFI, Go cgo, and other native consumers.

ABI v1 uses:

- opaque store handles;
- blocking C calls;
- UTF-8 JSON for complex declarative requests/results;
- stable integer success/error results;
- thread-local last-error text;
- library-owned output buffers released with `sk_buffer_free`.

The header is `src/SemanticKnowledge.Native/include/semantic_knowledge.h`.

Key exports include store open/close/initialize/reset plus KnowledgeBase, Collection, Schema, Document, and Search operations. `sk_abi_version()` lets consumers guard ABI compatibility.

The native SQLite open function creates a self-contained SQLite-backed SemanticKnowledge store using the managed library's normal semantics. JSON uses camelCase and accepts case-insensitive property names; callers do not need to mirror C# naming conventions.

Never free a returned SemanticKnowledge buffer with the caller's allocator. Always call `sk_buffer_free`.

CI publishes the NativeAOT library for supported target RIDs and compiles/runs a standalone C consumer against it.
