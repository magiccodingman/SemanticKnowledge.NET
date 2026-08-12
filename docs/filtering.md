# Filtering

Filters are a provider-neutral AST compiled into parameterized backend SQL. Filtering occurs inside SQLite/PostgreSQL/SQL Server before final semantic result selection.

```csharp
var filter = KnowledgeFilters.And(
    KnowledgeFilters.Gte("level", KnowledgeValue.From(5L)),
    KnowledgeFilters.Eq("alive", KnowledgeValue.From(true)),
    KnowledgeFilters.HasTag("npc"));

var hits = await store.SearchAsync("dangerous spellcaster", new KnowledgeSearchRequest
{
    KnowledgeBaseId = kb.Id,
    Filter = filter,
    Top = 20
});
```

Supported leaf operations include equality/inequality, numeric/date ordering, `In`, null checks, and tag containment. Boolean composition supports AND, OR, and NOT.

Typed filter helpers are available when your application defines a typed schema model. They inspect member-access expression trees and translate them to field keys; they never invoke `Expression.Compile()`.

Do not interpolate field names or values into provider SQL yourself. Dynamic logical field keys are data and are handled through the stable EAV schema and parameterized provider compilers.
