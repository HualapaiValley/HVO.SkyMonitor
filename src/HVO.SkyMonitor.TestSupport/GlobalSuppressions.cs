using System.Diagnostics.CodeAnalysis;

// TestSupport project uses nested types for logical grouping of test data
// CA1034 (nested types) suppressed as the nested structure improves test code organization
[assembly: SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Nested types provide logical grouping for test data classes (TestUsers.Admin, TestApiKeys.ReadOnly, etc.)", Scope = "module")]

// CA1716: Test data class names intentionally use reserved keywords that match domain concepts
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Test data types use domain-meaningful names (ReadOnly, Operator) that are more important than cross-language compatibility.", Scope = "module")]
