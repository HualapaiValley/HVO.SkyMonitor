using System.Diagnostics.CodeAnalysis;

// CA1000: Static members on generic types are intentional for Result<T> and Option<T> factory methods
// These provide a clean, functional API similar to Result.Success() and Option.Some()
[assembly: SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods on Result<T> and Option<T> provide clean functional API", Scope = "type", Target = "~T:HVO.Result`1")]
[assembly: SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods on Result<T,TEnum> provide clean functional API", Scope = "type", Target = "~T:HVO.Result`2")]
[assembly: SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory methods on Option<T> provide clean functional API", Scope = "type", Target = "~T:HVO.Option`1")]

// CA1815: Result<T> types don't need Equals/GetHashCode - they're functional wrappers not meant for collections
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Result types are functional wrappers not intended for equality comparison or collection usage", Scope = "type", Target = "~T:HVO.Result`1")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Result types are functional wrappers not intended for equality comparison or collection usage", Scope = "type", Target = "~T:HVO.Result`2")]

// CA2225: Implicit operators intentionally don't have named alternatives - they're the primary API
[assembly: SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Implicit conversions are the primary API for Result types", Scope = "type", Target = "~T:HVO.Result`1")]
[assembly: SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Implicit conversions are the primary API for Result types", Scope = "type", Target = "~T:HVO.Result`2")]

// CA1715: Generic type parameter 'R' is conventional in functional programming for return types
[assembly: SuppressMessage("Naming", "CA1715:Identifiers should have correct prefix", Justification = "R is conventional for return type in functional programming Match methods", Scope = "type", Target = "~T:HVO.Result`1")]
[assembly: SuppressMessage("Naming", "CA1715:Identifiers should have correct prefix", Justification = "R is conventional for return type in functional programming Match methods", Scope = "type", Target = "~T:HVO.Result`2")]
