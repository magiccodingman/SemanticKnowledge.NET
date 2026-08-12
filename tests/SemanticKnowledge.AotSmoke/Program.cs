using SemanticKnowledge;

var schema = KnowledgeSchemaBuilder.CreateDefaultDocument();
schema.Validate();
if (schema.Fields.Count == 0) return 1;
var filter = KnowledgeFilters.And(KnowledgeFilters.HasTag("wiki"), KnowledgeFilters.Gte("version", KnowledgeValue.From(2L)));
return filter is null ? 2 : 0;
