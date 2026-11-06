# Supplemental canonical sky pass-arc fixtures

`fixture-manifest.json` pins three **synthetic contract examples**, not physical
truth: `arc.json`, `search-identity.json` and `query-identity.json`. Their exact
constructor inputs live in `OrbitalPassArcContractTests.ExampleQuery/ExampleArc`;
the golden test recreates canonical bytes through production constructors.
The synthetic sampling estimate is illustrative, not numerical qualification.
The original contract commit is `771a0c91da55c57a1bfae9b49de7405cef244a76`,
with original pack manifest SHA256
`15627A25F62FD52A07CA804440280F11E63976BFAB6BB277A62F8FC6FDEB7643`.
Those original files are neither rewritten nor extended here.

`independent/` is the separately identified immutable numerical oracle from an
independent offline helper. Its original README, sources, generator, notices,
raw input subsets and artifact manifest are retained byte-for-byte. It uses no
HVO output. `cases.json` has SHA256
`582D78B90C17E890C6757D997293C69658739ABDC36A7CC59FB3EF6A53EB1EDC`;
its manifest SHA256 is
`CB2DB87F3227087E200E6541E2386A472C4AA02E391E7C71AEF64D8DDEC248E6`.
There is one repository copy of the 10MB case data, under this test fixture path.
See the [qualification record](../../../../docs/astronomy/orbital-pass-arc-qualification-v1.md)
for empirical interpolation derivation, fixed gates and bounded software scope.
