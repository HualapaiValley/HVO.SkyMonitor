// Keep Docker-free component suites independently parallelizable.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
