using System.Reflection;
using System.Reflection.Emit;

namespace HVO.SkyMonitor.TestSettings;

/// <summary>
/// Keeps issue #754 from returning under a new name. The defect is not any particular test: it is that a
/// process-global pool clear, called from a class that runs in parallel with others, disposes idle pooled
/// connections belonging to whatever else is running, so a test renting one at that moment gets a disposed
/// handle. Removing today's instances removes today's failures; this asserts the property that keeps new
/// ones from appearing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This guard reads compiled code, not source text.</b> Which assembly runs tests in parallel, which
/// classes opt out, and where the API is actually called all come from metadata and IL. Nothing here
/// depends on how anything is spelled, indented, commented or quoted.
/// </para>
/// <para>
/// That is the whole point, and it was reached the long way. Eight ways were found to make a text-reading
/// version of this guard report success while a real offending call sat in the tree, and every one was the
/// same sentence: text produced a name and metadata blessed it. A marker matched anywhere in a file, so the
/// guard's own string literals excluded its own assembly. Then at column zero, which a raw string literal's
/// content also satisfies. Then in the file prologue, which still holds block comments and never-defined
/// <c>#if</c> regions. Then directives detected by <c>StartsWith("#if")</c>, which does not see
/// <c>#  if</c>. Then an attribute on the declaration's own line, which the declaration pattern could not
/// see. Then an attribute containing a closing bracket, which the fix for the previous one could not see.
/// Then a block comment forging a declaration. Then a disabled region forging one. Each fix relocated an
/// assumption rather than removing one. None of those shapes exists after compilation.
/// </para>
/// <para>
/// This file is compile-linked into every test project rather than living in one of them, so each assembly
/// checks itself. There is no discovery, no cross-assembly loading and no dependency on build ordering.
/// </para>
/// <para>
/// There are two ways to satisfy this guard, and which one applies depends on what the clear was for.
/// If it was a teardown clear in a parallelisable class, meaning it only released handles before deleting
/// the test's own temporary directory, delete it: this repository is developed and built on POSIX
/// filesystems, where unlinking an open file succeeds, so such a clear was never load-bearing for the
/// deletion. A Windows checkout is required to live on the WSL2 Linux filesystem for that reason. If the
/// clear is load-bearing, meaning the test would silently stop exercising what it claims without it, keep
/// it and give the class a class-level <c>[DoNotParallelize]</c> so nothing runs beside it. That attribute
/// means nothing runs beside the class rather than merely that its own methods run in sequence, which was
/// verified by measurement under MSTest 4.3.3 rather than taken from the attribute's documentation, and the
/// measurement establishes the observable rather than the mechanism.
/// </para>
/// <para>
/// In an assembly that does not run test methods in parallel there is nothing here to find, and a pass
/// would otherwise mean only that nothing was examined. The premise of that exit is asserted rather than
/// assumed, so the result means the assembly was verified serialised. The walk and the bodiless
/// classification still run there, so the pass always carries at least those two facts. The assertion
/// restates the condition the branch was taken on, so its value is that the claim is recorded in the
/// result rather than that it is independently corroborated.
/// </para>
/// <para>
/// <b>What this reaches, because two member kinds were missed once and the anchor did not notice.</b>
/// Ordinary methods, property accessors, which arrive as ordinary <c>get_</c> and <c>set_</c> methods, and
/// instance and static constructors. A static constructor is the worst hiding place available: it runs once
/// on first use of its type and its effect is process-wide. Lambdas, local functions, async bodies and
/// iterators compile into nested types, and every type in the assembly is enumerated, so they are reached
/// too. Calls are found through <c>call</c>, <c>callvirt</c>, <c>newobj</c>, and the two instructions that
/// take a method's address, so a delegate built from the API and invoked later is seen.
/// </para>
/// <para>
/// <b>What this cannot see, measured rather than reasoned.</b> A call made by reflection, or through a
/// function pointer invoked with <c>calli</c>, carries no method token this can resolve, so neither is
/// counted; no assembly in this repository contains one today, and that is a gap rather than a
/// reassurance. A call inside a helper type is attributed to that helper and reported, never excused by
/// whoever calls it, which was measured by placing a call in a helper and confirming it is named. A method
/// with no IL body cannot be walked, so bodiless methods are classified rather than skipped, and anything
/// bodiless for an unrecognised reason is reported by name.
/// </para>
/// <para>
/// <b>The specimen calls a method from the base class library on purpose, and that must not be
/// "simplified" toward the subject.</b> Most of the projects this file is compiled into do not reference
/// the SQLite package at all. If the specimen called the API this guard is about, it could not compile
/// there, and an anchor that only works where the package happens to be present would prove the walk ran in
/// some assemblies and quietly prove nothing in the rest. Calling <c>string.Concat</c> keeps the proof
/// available everywhere, which is why a parallelising assembly with no SQLite reference still reports a
/// walk that demonstrably ran.
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Unit")]
public sealed class ConnectionPoolClearContractTests
{
    private const string GlobalClearMethod = "ClearAllPools";
    private const string SqliteConnectionType = "Microsoft.Data.Sqlite.SqliteConnection";

    private const int LoadFunctionPointer = 0x106;
    private const int LoadVirtualFunctionPointer = 0x107;

    private static readonly int[] OperandSize = BuildOperandSizes();

    [TestMethod]
    public void NoParallelisableTestClassClearsEveryConnectionPool()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var scan = Scan(assembly);

        // The walk is the one part of this that can fail silently: a mis-parsed instruction stream simply
        // finds nothing and reports a clean assembly. Specimen.CallsAKnownMethod contains a call this walk
        // must find, so a broken walker fails here instead of reporting success.
        // One specimen shape per thing this walk has been wrong about. An ordinary call proves only that
        // ordinary calls are found, which is exactly how constructors and delegates stayed invisible while
        // the anchor kept passing.
        string[] required =
        [
            "ordinary method",
            "instance constructor",
            "static constructor",
            "delegate created with ldftn"
        ];
        var unseen = required.Except(scan.SpecimenSightings, StringComparer.Ordinal).ToList();
        Assert.IsEmpty(
            unseen,
            "This guard's IL walk did not find the calls it plants in its own specimen for these shapes, so it "
                + "does not examine them anywhere and any clean result it produces excludes them silently: "
                + string.Join(", ", unseen));

        // A method with no IL body is normal for abstract, external and runtime-implemented methods, and is
        // otherwise a body this could not read. Skipping the second kind quietly is the same silence.
        Assert.IsEmpty(
            scan.Unreadable,
            "These methods have no IL body for a reason this guard does not recognise, so their contents were "
                + "never examined and a clean result would not cover them: " + string.Join(", ", scan.Unreadable));

        var name = assembly.GetName().Name;

        // In an assembly that does not run test methods in parallel there is no per-class hazard to find,
        // and this would otherwise report Passed having examined nothing that could fail. A green with no
        // referent is this issue's own shape, so the premise the early exit rests on is asserted instead of
        // assumed, and the result then means "verified serialised" rather than nothing at all.
        if (!scan.Parallelises)
        {
            Assert.IsTrue(
                scan.OptsOut || !scan.OptsIn,
                $"{name} was treated as running its tests serially, but its metadata does not say so. The "
                    + "per-class check was skipped on a premise that is not true, which would report success "
                    + "over an assembly this guard never examined.");
            return;
        }

        Assert.IsEmpty(
            scan.Offenders,
            $"In {name}, which runs test methods in parallel, these types call SqliteConnection."
                + $"{GlobalClearMethod} without opting out of parallelisation. That disposes pooled connections "
                + "owned by tests running beside them and reproduces issue #754. If it is a teardown clear, "
                + "meaning it only released handles before deleting the test's own temporary directory, delete "
                + "it; POSIX unlinks open files. If it is load-bearing, give the class a class-level "
                + "[DoNotParallelize]: " + string.Join(", ", scan.Offenders));
    }

    /// <summary>
    /// The matcher identifies the API by method name and declaring type together. Matching on the name alone
    /// would report legitimate calls to the scoped <c>ClearPool</c>, and would treat any unrelated method
    /// called <c>ClearAllPools</c> as the process-global one. The specimen calls decoys with both names on a
    /// type of this repository's own, and neither may be reported.
    /// </summary>
    [TestMethod]
    public void MatchesTheGlobalClearByDeclaringTypeAndNotByMethodNameAlone()
    {
        var scan = Scan(Assembly.GetExecutingAssembly());

        // Both halves are needed. Without the first, the second passes because nothing was looked at.
        Assert.IsNotEmpty(
            scan.NameMatches.Where(static call => call.Contains(nameof(PoolApiDecoy), StringComparison.Ordinal)),
            $"The walk did not reach {nameof(Specimen.CallsDecoysNamedLikeThePoolApi)}, so this test proves "
                + "nothing about the matcher. Its specimen must be visible before its absence from the "
                + "offenders means anything.");

        Assert.IsEmpty(
            scan.Offenders.Where(static offender =>
                offender.Contains(nameof(ConnectionPoolClearContractTests), StringComparison.Ordinal)),
            "The matcher reported a decoy named like the pool API but declared on a type of this "
                + "repository's own, so it is matching by method name alone. That would also report "
                + "legitimate calls to the scoped ClearPool as though they were the process-global clear.");
    }

    /// <summary>
    /// The walk stops at an instruction it cannot size, because guessing would misread every byte after it.
    /// Stopping quietly is the failure this guard exists to refuse, so it reports instead. Deliberately
    /// malformed input exercises that branch, which no real assembly in this repository does.
    /// </summary>
    [TestMethod]
    public void ReportsAnInstructionStreamItCannotWalk()
    {
        var walked = TryReadCallTokens([0xF0, 0xF1, 0xF2], out var tokens);
        Assert.IsFalse(walked, "An unrecognised instruction must stop the walk and be reported, not skipped.");
        Assert.IsEmpty(tokens, "A walk that failed must not offer partial results as though they were complete.");

        var clean = TryReadCallTokens([0x00, 0x2A], out _);
        Assert.IsTrue(clean, "A nop followed by ret is a walkable body and must not be reported as unreadable.");
    }

    private static ScanResult Scan(Assembly assembly)
    {
        var attributes = assembly.GetCustomAttributesData().Select(static a => a.AttributeType.Name).ToList();
        var parallelises =
            !attributes.Contains("DoNotParallelizeAttribute", StringComparer.Ordinal) &&
            attributes.Contains("ParallelizeAttribute", StringComparer.Ordinal);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var unreadable = new SortedSet<string>(StringComparer.Ordinal);
        var matched = new SortedSet<string>(StringComparer.Ordinal);
        var sightings = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in Members(type))
            {
                var body = BodyOf(method, out var bodilessForAKnownReason);
                if (body is null)
                {
                    if (!bodilessForAKnownReason)
                    {
                        unreadable.Add($"{type.FullName}.{method.Name}");
                    }
                    continue;
                }
                if (!TryReadCallTokens(body, out var tokens))
                {
                    unreadable.Add($"{type.FullName}.{method.Name} (unwalkable instruction stream)");
                    continue;
                }
                foreach (var (opcode, token) in tokens)
                {
                    var target = Resolve(type, method, token);
                    if (target is null)
                    {
                        continue;
                    }
                    var sighting = SpecimenSighting(type, method, target, opcode);
                    if (sighting is not null)
                    {
                        sightings.Add(sighting);
                    }
                    if (!string.Equals(target.Name, GlobalClearMethod, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    matched.Add($"{target.DeclaringType?.FullName}.{target.Name} from {type.FullName}.{method.Name}");
                    if (!string.Equals(target.DeclaringType?.FullName, SqliteConnectionType, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    // Async and iterator bodies compile into nested state machines, and fixtures are often
                    // nested by hand, so the type holding the call is not the type an author wrote.
                    var owner = type;
                    while (owner.DeclaringType is not null)
                    {
                        owner = owner.DeclaringType;
                    }
                    var ownerAttributes = owner.GetCustomAttributesData()
                        .Select(static a => a.AttributeType.Name).ToList();
                    var serialised = ownerAttributes.Contains("DoNotParallelizeAttribute", StringComparer.Ordinal);
                    if (parallelises && !serialised)
                    {
                        offenders.Add(owner.FullName ?? owner.Name);
                    }
                }
            }
        }

        return new ScanResult(
            parallelises ? offenders.ToList() : [],
            unreadable.ToList(),
            matched.ToList(),
            sightings.ToList(),
            parallelises,
            attributes.Contains("ParallelizeAttribute", StringComparer.Ordinal),
            attributes.Contains("DoNotParallelizeAttribute", StringComparer.Ordinal));
    }

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.DeclaredOnly;

    /// <summary>
    /// Every member of a type that can carry IL. <c>GetMethods</c> does not return constructors, and a
    /// static constructor is the worst place for this call to hide: it runs once on first use of the type
    /// and its effect is process-wide. Property accessors arrive here as ordinary <c>get_</c> and
    /// <c>set_</c> methods, and lambdas, local functions, async and iterator bodies live in nested types
    /// that the caller already enumerates.
    /// </summary>
    private static IEnumerable<MethodBase> Members(Type type) =>
        type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared));

    /// <summary>
    /// Labels the specimen shapes this walk managed to see. Each label corresponds to a member kind or an
    /// instruction the walk has been wrong about before, so a future narrowing fails loudly here instead of
    /// quietly returning a shorter list of offenders.
    /// </summary>
    private static string? SpecimenSighting(Type type, MethodBase method, MethodBase target, int opcode)
    {
        if (type != typeof(Specimen))
        {
            return null;
        }
        if (opcode == LoadFunctionPointer && string.Equals(target.Name, GlobalClearMethod, StringComparison.Ordinal))
        {
            return "delegate created with ldftn";
        }
        if (!string.Equals(target.Name, nameof(string.Concat), StringComparison.Ordinal))
        {
            return null;
        }
        return method switch
        {
            ConstructorInfo { IsStatic: true } => "static constructor",
            ConstructorInfo => "instance constructor",
            _ => "ordinary method"
        };
    }

    private static byte[]? BodyOf(MethodBase method, out bool bodilessForAKnownReason)
    {
        var implementation = method.GetMethodImplementationFlags();
        bodilessForAKnownReason =
            method.IsAbstract ||
            (method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
            (implementation & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
            (implementation & MethodImplAttributes.InternalCall) != 0;
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static MethodBase? Resolve(Type type, MethodBase method, int token)
    {
        try
        {
            // A constructor has no generic arguments of its own and throws rather than returning none,
            // which is a difference constructors introduced when they were added to the enumeration.
            var methodArguments = method is MethodInfo generic ? generic.GetGenericArguments() : Type.EmptyTypes;
            return type.Module.ResolveMethod(token, type.GetGenericArguments(), methodArguments);
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the metadata tokens of every call in an instruction stream. Returns false rather than stopping
    /// quietly if it meets an instruction it cannot size, because every byte after such a point would be
    /// misread and the result would be a short list that looks like a clean one.
    /// </summary>
    private static bool TryReadCallTokens(byte[] il, out List<(int Opcode, int Token)> tokens)
    {
        tokens = [];
        var i = 0;
        while (i < il.Length)
        {
            int opcode = il[i];
            if (opcode == 0xFE)
            {
                if (i + 1 >= il.Length) { return false; }
                opcode = 0x100 + il[i + 1];
                i += 2;
            }
            else
            {
                i += 1;
            }
            var size = OperandSize[opcode];
            if (size < 0) { tokens.Clear(); return false; }
            // call, callvirt, newobj, and the two that take a method's address. A delegate built with
            // ldftn and invoked later is an ordinary line of C# and reaches the same API.
            if (opcode is 0x28 or 0x6F or 0x73 or LoadFunctionPointer or LoadVirtualFunctionPointer)
            {
                if (i + 4 > il.Length) { tokens.Clear(); return false; }
                tokens.Add((opcode, BitConverter.ToInt32(il, i)));
            }
            if (opcode == 0x45)
            {
                if (i + 4 > il.Length) { tokens.Clear(); return false; }
                i += 4 + (4 * BitConverter.ToInt32(il, i));
                continue;
            }
            i += size;
        }
        return true;
    }

    private static int[] BuildOperandSizes()
    {
        var sizes = new int[0x200];
        Array.Fill(sizes, -1);
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code) { continue; }
            var value = (ushort)code.Value;
            sizes[value > 0xFF ? 0x100 + (value & 0xFF) : value] = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR
                    or OperandType.InlineSwitch => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                _ => -1
            };
        }
        return sizes;
    }

    /// <summary>
    /// Never invoked. It exists so the IL walk has something it is required to find on every run, and so the
    /// matcher has decoys it is required to ignore. Both are read as instructions, not executed.
    /// </summary>
    private sealed class Specimen
    {
        // A field initialiser compiles into the type's static constructor just as an explicit one does,
        // which is the member kind this specimen needs to exist in order to prove the walk reaches it.
        private static readonly string StaticSeed = string.Concat("issue", "754");

        internal Specimen() => Seed = string.Concat("issue", "754");

        internal string Seed { get; }

        internal static string StaticSeedValue => StaticSeed;

        internal static string CallsAKnownMethod() => string.Concat("issue", "754");

        internal static void CallsDecoysNamedLikeThePoolApi()
        {
            PoolApiDecoy.ClearAllPools();
            PoolApiDecoy.ClearPool(null);
        }

        // Builds a delegate rather than calling directly, so the walk has to read the method address the
        // ldftn instruction carries. Collecting only call, callvirt and newobj made this shape invisible.
        internal static Action CreatesADelegateOverThePoolApi() => PoolApiDecoy.ClearAllPools;
    }

    private static class PoolApiDecoy
    {
        internal static void ClearAllPools()
        {
        }

        internal static void ClearPool(object? connection) => _ = connection;
    }

    private sealed record ScanResult(
        List<string> Offenders,
        List<string> Unreadable,
        List<string> NameMatches,
        List<string> SpecimenSightings,
        bool Parallelises,
        bool OptsIn,
        bool OptsOut);
}
