# NativeAOT

The core library is designed to remain NativeAOT-friendly: typed schema/filter helpers inspect expression trees without `Compile()`, HTTP/native JSON uses source generation, and reflection-heavy dynamic serialization is avoided on critical paths.

The repository contains a NativeAOT smoke project plus a CI matrix. The C ABI project is published as a native shared library rather than requiring a managed host.

Provider AOT compatibility is tested rather than assumed. SQLite is the first-class native deployment path. Server providers are ordinary managed providers and their AOT support should be judged by the current CI matrix and the AOT characteristics of their client drivers.

AOT compatibility does not mean every arbitrary custom application callback is automatically trim-safe. Consumers should keep their own reflection/serialization code AOT-safe as usual.

See [Native interop](native-interop.md) for the stable C-facing API.
