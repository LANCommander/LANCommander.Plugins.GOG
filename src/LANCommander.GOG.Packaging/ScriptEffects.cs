using System.Globalization;
using System.Text;
using LANCommander.GOG.Manifest;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Packaging;

/// <summary>A change the installer's script makes to the machine during installation.</summary>
/// <remarks>
/// Paths and values keep their Inno Setup constants, such as <c>{app}</c>, unexpanded; the
/// composer decides what each one becomes.
/// </remarks>
public abstract record ScriptEffect;

/// <summary>Writes a registry value.</summary>
public sealed record ScriptRegistryWrite(RegistryEntry Entry) : ScriptEffect;

/// <summary>Writes a key into an INI file.</summary>
public sealed record ScriptIniWrite(IniEntry Entry) : ScriptEffect;

/// <summary>Creates a directory, and any missing parents.</summary>
public sealed record ScriptCreateDirectory(string Path) : ScriptEffect;

/// <summary>Writes text to a file, replacing or appending to what is there.</summary>
public sealed record ScriptWriteFile(string Path, string Contents, bool Append) : ScriptEffect;

/// <summary>Copies a file.</summary>
public sealed record ScriptCopyFile(string Source, string Destination, bool FailIfExists) : ScriptEffect;

/// <summary>Moves or renames a file or directory.</summary>
public sealed record ScriptMoveFile(string Source, string Destination) : ScriptEffect;

/// <summary>Deletes files, which may be a wildcard pattern.</summary>
public sealed record ScriptDeleteFiles(string Path) : ScriptEffect;

/// <summary>Deletes a directory, with everything in it when <paramref name="Recursive"/> is set.</summary>
public sealed record ScriptDeleteDirectory(string Path, bool Recursive) : ScriptEffect;

/// <summary>An effect the script has that was not carried over, and why.</summary>
/// <param name="Routine">The routine it is in.</param>
/// <param name="Function">The Inno Setup function it calls.</param>
/// <param name="Reason">Why it was left out.</param>
public sealed record SkippedScriptEffect(string Routine, string Function, string Reason);

/// <summary>What the installer's script does to the machine, as far as can be told without running it.</summary>
/// <param name="Effects">The effects, in the order the script reaches them.</param>
/// <param name="Skipped">The effects that were seen but not carried over.</param>
public sealed record ScriptEffects(IReadOnlyList<ScriptEffect> Effects, IReadOnlyList<SkippedScriptEffect> Skipped)
{
    public static readonly ScriptEffects None = new([], []);
}

/// <summary>
/// Picks the concrete changes out of an installer's script — registry and INI writes, and files
/// and directories created, moved, or deleted — rather than translating the script whole.
/// </summary>
/// <remarks>
/// <para>
/// A GOG installer's script is mostly wizard pages, progress bars, and Galaxy notifications, none
/// of which mean anything once the game tree already exists. Translating all of it produced
/// thousands of lines that mostly threw. What a package actually needs is the handful of calls
/// that leave something behind on the machine.
/// </para>
/// <para>
/// Only routines reachable from <c>CurStepChanged</c>, the routine Inno calls as installation
/// proceeds, are looked at. Wizard callbacks and uninstall steps are not. Every step's branch is
/// taken, since a real install passes through each of them, and conditions are not evaluated.
/// </para>
/// <para>
/// An argument counts only when it is known without running anything: a literal, an expanded
/// constant, a concatenation of those, or a variable assigned exactly one such value anywhere it
/// is in scope. A call with any other argument is reported as skipped.
/// </para>
/// <para>
/// Deletions and moves are only kept when they stay inside <c>{app}</c>, so that a script
/// cleaning up after itself carries over but one reaching into the rest of the system does not.
/// </para>
/// </remarks>
public static class ScriptEffectExtractor
{
    /// <summary>The routine Inno calls as installation reaches each step.</summary>
    public const string StepRoutine = "CurStepChanged";

    /// <summary>Calls worth reporting when they are left out, beyond the ones that are carried over.</summary>
    private static readonly Dictionary<string, string> NotCarried = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EXEC"] = "runs a program",
        ["SHELLEXEC"] = "runs a program",
        ["CREATESHELLLINK"] = "creates a shortcut",
        ["REGDELETEVALUE"] = "deletes registry data",
        ["REGDELETEKEYINCLUDINGSUBKEYS"] = "deletes registry data",
        ["REGDELETEKEYIFEMPTY"] = "deletes registry data",
        ["SAVESTRINGSTOFILE"] = "writes a list of lines",
        ["SAVESTRINGSTOUTF8FILE"] = "writes a list of lines",
        ["REGISTERSERVER"] = "registers a COM server",
        ["UNREGISTERSERVER"] = "registers a COM server",
    };

    /// <summary>The constants a file effect may be rooted at; anything else is left out.</summary>
    private static readonly string[] FileRoots =
    [
        "{app}", "{userdocs}", "{mydocs}", "{userappdata}", "{localappdata}", "{commonappdata}",
        "{commondocs}", "{userdesktop}", "{win}", "{sys}", "{sd}",
    ];

    /// <summary>Finds the effects of the script's install steps.</summary>
    public static ScriptEffects Extract(ScriptUnit unit)
    {
        var routines = unit.Routines
            .GroupBy(routine => routine.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (!routines.TryGetValue(StepRoutine, out var entry))
            return ScriptEffects.None;

        var walker = new Walker(unit, routines);

        walker.Visit(entry);

        return new ScriptEffects(
            walker.Effects.Distinct().ToList(),
            walker.Skipped.Distinct().ToList());
    }

    private sealed class Walker(ScriptUnit unit, IReadOnlyDictionary<string, ScriptRoutine> routines)
    {
        private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConstantTable _constants = new(unit);
        private ScriptRoutine _routine = null!;

        public List<ScriptEffect> Effects { get; } = [];

        public List<SkippedScriptEffect> Skipped { get; } = [];

        public void Visit(ScriptRoutine routine)
        {
            if (!_visited.Add(routine.Name) || routine.Error is not null)
                return;

            var caller = _routine;

            _routine = routine;
            Block(routine.Body);
            _routine = caller;
        }

        private void Block(IEnumerable<ScriptStatement> statements)
        {
            foreach (var statement in statements)
                Statement(statement);
        }

        private void Statement(ScriptStatement statement)
        {
            switch (statement)
            {
                case ScriptAssignment assignment:
                    Expression(assignment.Target);
                    Expression(assignment.Value);
                    break;
                case ScriptCallStatement call:
                    Expression(call.Call);
                    break;
                case ScriptIf conditional:
                    Expression(conditional.Condition);
                    Block(conditional.Then);
                    Block(conditional.Else);
                    break;
                case ScriptWhile loop:
                    Expression(loop.Condition);
                    Block(loop.Body);
                    break;
                case ScriptRepeat loop:
                    Block(loop.Body);
                    Expression(loop.Until);
                    break;
                case ScriptTry guarded:
                    Block(guarded.Body);
                    Block(guarded.Except);
                    Block(guarded.Finally);
                    break;
                case ScriptGoto { Condition: { } condition }:
                    Expression(condition);
                    break;
            }
        }

        private void Expression(ScriptExpression expression)
        {
            switch (expression)
            {
                case ScriptCall call:
                    if (call.Instance is not null)
                        Expression(call.Instance);

                    foreach (var argument in call.Arguments)
                        Expression(argument.Value);

                    if (call.Kind == ScriptCallKind.Script && routines.TryGetValue(call.Callee, out var callee))
                        Visit(callee);
                    else if (call.Kind == ScriptCallKind.Host)
                        HostCall(call);

                    break;
                case ScriptUnary unary:
                    Expression(unary.Operand);
                    break;
                case ScriptBinary binary:
                    Expression(binary.Left);
                    Expression(binary.Right);
                    break;
                case ScriptFieldAccess field:
                    Expression(field.Target);
                    break;
                case ScriptElementAccess element:
                    Expression(element.Target);
                    Expression(element.Index);
                    break;
            }
        }

        private void HostCall(ScriptCall call)
        {
            var name = call.Callee.ToUpperInvariant();

            if (NotCarried.TryGetValue(name, out var why))
            {
                Skip(name, $"{why}, which is not carried over");

                return;
            }

            var known = name switch
            {
                "REGWRITESTRINGVALUE" or "REGWRITEEXPANDSTRINGVALUE" or "REGWRITEDWORDVALUE"
                    or "REGWRITEQWORDVALUE" or "REGWRITEMULTISTRINGVALUE" or "REGWRITEBINARYVALUE"
                    or "SETINISTRING" or "SETINIBOOL" or "SETINIINT"
                    or "CREATEDIR" or "FORCEDIRECTORIES" or "SAVESTRINGTOFILE" or "FILECOPY"
                    or "RENAMEFILE" or "DELETEFILE" or "DELTREE" or "REMOVEDIR" => true,
                _ => false,
            };

            if (!known)
                return;

            var values = new object?[call.Arguments.Count];

            for (var index = 0; index < values.Length; index++)
                values[index] = _constants.Evaluate(call.Arguments[index].Value, _routine);

            if (values.Any(value => value is null))
            {
                Skip(name, "its arguments are only known at run time");

                return;
            }

            var effect = Effect(name, values!);

            if (effect is null)
            {
                Skip(name, "its arguments are not in a form that can be carried over");

                return;
            }

            if (Unsafe(effect) is { } reason)
            {
                Skip(name, reason);

                return;
            }

            Effects.Add(effect);
        }

        private static ScriptEffect? Effect(string name, object[] values)
        {
            string Text(int index) => values.Length > index ? Constants.AsText(values[index]) ?? "" : "";
            bool Flag(int index) => values.Length > index && Constants.AsNumber(values[index]) is { } number && number != 0;

            return name switch
            {
                "REGWRITESTRINGVALUE" => Registry(values, RegistryValueType.String, Text(3)),
                "REGWRITEEXPANDSTRINGVALUE" => Registry(values, RegistryValueType.ExpandString, Text(3)),
                "REGWRITEMULTISTRINGVALUE" => Registry(values, RegistryValueType.MultiString, Text(3)),
                "REGWRITEDWORDVALUE" => Registry(values, RegistryValueType.DWord,
                    values.Length > 3 && Constants.AsNumber(values[3]) is { } dword
                        ? ((long)unchecked((uint)dword)).ToString(CultureInfo.InvariantCulture)
                        : null),
                "REGWRITEQWORDVALUE" => Registry(values, RegistryValueType.QWord,
                    values.Length > 3 && Constants.AsNumber(values[3]) is { } qword
                        ? qword.ToString(CultureInfo.InvariantCulture)
                        : null),
                "REGWRITEBINARYVALUE" => Registry(values, RegistryValueType.Binary,
                    Convert.ToHexString(Encoding.Latin1.GetBytes(Text(3)))),

                "SETINISTRING" when values.Length == 4 =>
                    new ScriptIniWrite(new IniEntry(Text(3), Text(0), Text(1), Text(2), EntryConditions.None)),
                "SETINIBOOL" when values.Length == 4 =>
                    new ScriptIniWrite(new IniEntry(Text(3), Text(0), Text(1), Flag(2) ? "1" : "0", EntryConditions.None)),
                "SETINIINT" when values.Length == 4 && Constants.AsNumber(values[2]) is { } number =>
                    new ScriptIniWrite(new IniEntry(Text(3), Text(0), Text(1),
                        number.ToString(CultureInfo.InvariantCulture), EntryConditions.None)),

                "CREATEDIR" or "FORCEDIRECTORIES" when values.Length == 1 => new ScriptCreateDirectory(Text(0)),
                "SAVESTRINGTOFILE" when values.Length == 3 => new ScriptWriteFile(Text(0), Text(1), Flag(2)),
                "FILECOPY" when values.Length == 3 => new ScriptCopyFile(Text(0), Text(1), Flag(2)),
                "RENAMEFILE" when values.Length == 2 => new ScriptMoveFile(Text(0), Text(1)),
                "DELETEFILE" when values.Length == 1 => new ScriptDeleteFiles(Text(0)),
                "REMOVEDIR" when values.Length == 1 => new ScriptDeleteDirectory(Text(0), Recursive: false),

                // DelTree(Path, IsDir, DeleteFiles, DeleteSubdirsAlso): without IsDir the path is a
                // wildcard naming files.
                "DELTREE" when values.Length == 4 => Flag(1)
                    ? Flag(2) && Flag(3) ? new ScriptDeleteDirectory(Text(0), Recursive: true) : null
                    : new ScriptDeleteFiles(Text(0)),

                _ => null,
            };
        }

        private static ScriptRegistryWrite? Registry(object[] values, RegistryValueType type, string? data)
        {
            if (values.Length != 4 || data is null || Constants.AsNumber(values[0]) is not { } root)
                return null;

            // The low byte picks the hive; the bits above it pick a 32- or 64-bit view.
            var hive = (unchecked((uint)root) & 0x800000FF) switch
            {
                0x80000000 => RegistryRoot.ClassesRoot,
                0x80000001 => RegistryRoot.CurrentUser,
                0x80000002 => RegistryRoot.LocalMachine,
                0x80000003 => RegistryRoot.Users,
                0x80000005 => RegistryRoot.CurrentConfig,
                _ => RegistryRoot.Unknown,
            };

            if (hive == RegistryRoot.Unknown)
                return null;

            return new ScriptRegistryWrite(new RegistryEntry(
                hive,
                Constants.AsText(values[1]) ?? "",
                Constants.AsText(values[2]) ?? "",
                type,
                data,
                Permissions: -1,
                RegistryEntryOptions.None,
                EntryConditions.None));
        }

        /// <summary>Why an effect reaches somewhere a package should not touch, or <c>null</c> when it does not.</summary>
        private static string? Unsafe(ScriptEffect effect) => effect switch
        {
            ScriptCreateDirectory create => OutsideRoots(create.Path),
            ScriptWriteFile write => OutsideRoots(write.Path),
            ScriptIniWrite ini => OutsideRoots(ini.Entry.Filename),
            ScriptCopyFile copy => OutsideRoots(copy.Source) ?? OutsideRoots(copy.Destination),
            ScriptMoveFile move => OutsideApp(move.Source) ?? OutsideApp(move.Destination),
            ScriptDeleteFiles delete => OutsideApp(delete.Path),
            ScriptDeleteDirectory delete => OutsideApp(delete.Path),
            _ => null,
        };

        private static string? OutsideRoots(string path) =>
            FileRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) && !Climbs(path)
                ? null
                : $"{Describe(path)} is not under a folder the package knows";

        private static string? OutsideApp(string path) =>
            path.StartsWith(@"{app}\", StringComparison.OrdinalIgnoreCase) && path.Length > 6 && !Climbs(path)
                ? null
                : $"{Describe(path)} is outside the install directory";

        private static bool Climbs(string path) =>
            path.Split('\\', '/').Any(segment => segment == "..");

        private static string Describe(string path) => path.Length == 0 ? "an empty path" : path;

        private void Skip(string function, string reason) =>
            Skipped.Add(new SkippedScriptEffect(_routine.Name, function, reason));
    }

    /// <summary>
    /// The values the script's variables hold, for the variables that only ever hold one.
    /// </summary>
    private sealed class ConstantTable
    {
        private readonly HashSet<string> _globals;
        private readonly Dictionary<(string? Scope, string Name), List<ScriptExpression>> _writes = new();
        private readonly HashSet<(string? Scope, string Name)> _poisoned = [];
        private readonly Dictionary<(string? Scope, string Name), object?> _resolved = new();
        private readonly HashSet<(string? Scope, string Name)> _resolving = [];
        private readonly Dictionary<ScriptExpression, ScriptRoutine> _owners = new(ReferenceEqualityComparer.Instance);

        public ConstantTable(ScriptUnit unit)
        {
            _globals = unit.Globals.Select(global => global.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var routine in unit.Routines)
            {
                // A parameter takes whatever each caller passes, so it is never a constant.
                foreach (var parameter in routine.Parameters)
                    _poisoned.Add((routine.Name, parameter.Name));

                Collect(routine, routine.Body);
            }
        }

        /// <summary>The value of an expression, or <c>null</c> when it is only known at run time.</summary>
        public object? Evaluate(ScriptExpression expression, ScriptRoutine routine) => expression switch
        {
            ScriptLiteral literal => Literal(literal.Value),
            ScriptVariableReference reference => Variable(Key(routine, reference.Name), routine),
            ScriptBinary { Operator: ScriptBinaryOperator.Add } add =>
                (Evaluate(add.Left, routine), Evaluate(add.Right, routine)) switch
                {
                    (long left, long right) => left + right,
                    var (left, right) when Constants.AsText(left) is { } l && Constants.AsText(right) is { } r
                        && (left is string || right is string) => l + r,
                    _ => null,
                },
            ScriptCall { Kind: ScriptCallKind.Host } call => Host(call, routine),
            _ => null,
        };

        private object? Host(ScriptCall call, ScriptRoutine routine)
        {
            if (call.Arguments.Count == 0)
                return null;

            var argument = Evaluate(call.Arguments[0].Value, routine);

            if (argument is null)
                return null;

            var text = Constants.AsText(argument);

            return call.Callee.ToUpperInvariant() switch
            {
                // Constants stay in their braces; the composer decides what each one becomes.
                "EXPANDCONSTANT" => text,
                "ADDBACKSLASH" => text is null ? null : text.EndsWith('\\') ? text : text + "\\",
                "REMOVEBACKSLASH" or "REMOVEBACKSLASHUNLESSROOT" => text?.TrimEnd('\\'),
                "EXTRACTFILEDIR" => text is null ? null : text[..Math.Max(0, text.LastIndexOf('\\'))],
                "EXTRACTFILEPATH" => text is null ? null : text[..(text.LastIndexOf('\\') + 1)],
                "EXTRACTFILENAME" => text?[(text.LastIndexOf('\\') + 1)..],
                "INTTOSTR" => Constants.AsNumber(argument)?.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        }

        private object? Variable((string? Scope, string Name) key, ScriptRoutine routine)
        {
            if (_resolved.TryGetValue(key, out var known))
                return known;

            if (_poisoned.Contains(key) || !_writes.TryGetValue(key, out var writes) || writes.Count != 1)
                return null;

            if (!_resolving.Add(key))
                return null;

            // A global's one assignment may sit in any routine; evaluate it where it is written.
            var scope = key.Scope is null ? _owners.GetValueOrDefault(writes[0], routine) : routine;
            var value = Evaluate(writes[0], scope);

            _resolving.Remove(key);
            _resolved[key] = value;

            return value;
        }

        private (string? Scope, string Name) Key(ScriptRoutine routine, string name) =>
            routine.Locals.Any(local => local.Name == name) || routine.Parameters.Any(parameter => parameter.Name == name)
                || !_globals.Contains(name)
                ? (routine.Name, name)
                : (null, name);

        private void Collect(ScriptRoutine routine, IEnumerable<ScriptStatement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case ScriptAssignment { Target: ScriptVariableReference target } assignment:
                    {
                        var key = Key(routine, target.Name);

                        if (!_writes.TryGetValue(key, out var writes))
                            _writes[key] = writes = [];

                        writes.Add(assignment.Value);
                        _owners[assignment.Value] = routine;
                        Expressions(routine, assignment.Value);

                        break;
                    }
                    case ScriptAssignment assignment:
                        Poison(routine, assignment.Target);
                        Expressions(routine, assignment.Target);
                        Expressions(routine, assignment.Value);
                        break;
                    case ScriptStep step:
                        Poison(routine, step.Target);
                        break;
                    case ScriptCallStatement call:
                        Expressions(routine, call.Call);
                        break;
                    case ScriptIf conditional:
                        Expressions(routine, conditional.Condition);
                        Collect(routine, conditional.Then);
                        Collect(routine, conditional.Else);
                        break;
                    case ScriptWhile loop:
                        Expressions(routine, loop.Condition);
                        Collect(routine, loop.Body);
                        break;
                    case ScriptRepeat loop:
                        Collect(routine, loop.Body);
                        Expressions(routine, loop.Until);
                        break;
                    case ScriptTry guarded:
                        Collect(routine, guarded.Body);
                        Collect(routine, guarded.Except);
                        Collect(routine, guarded.Finally);
                        break;
                }
            }
        }

        /// <summary>Marks every variable an expression may write through as not constant.</summary>
        private void Expressions(ScriptRoutine routine, ScriptExpression expression)
        {
            switch (expression)
            {
                case ScriptCall call:
                    if (call.Instance is not null)
                    {
                        Poison(routine, call.Instance);
                        Expressions(routine, call.Instance);
                    }

                    foreach (var argument in call.Arguments)
                    {
                        // A method's arguments are where the decompiler puts what a property
                        // read stores, so treat them as written through too.
                        if (argument.ByReference || call.Kind == ScriptCallKind.Method)
                            Poison(routine, argument.Value);

                        Expressions(routine, argument.Value);
                    }

                    break;
                case ScriptUnary unary:
                    Expressions(routine, unary.Operand);
                    break;
                case ScriptBinary binary:
                    Expressions(routine, binary.Left);
                    Expressions(routine, binary.Right);
                    break;
                case ScriptFieldAccess field:
                    Expressions(routine, field.Target);
                    break;
                case ScriptElementAccess element:
                    Expressions(routine, element.Target);
                    Expressions(routine, element.Index);
                    break;
            }
        }

        private void Poison(ScriptRoutine routine, ScriptExpression target)
        {
            switch (target)
            {
                case ScriptVariableReference reference:
                    _poisoned.Add(Key(routine, reference.Name));
                    break;
                case ScriptFieldAccess field:
                    Poison(routine, field.Target);
                    break;
                case ScriptElementAccess element:
                    Poison(routine, element.Target);
                    break;
            }
        }

        private static object? Literal(IfpsValue value) => value.BaseType switch
        {
            IfpsBaseType.String or IfpsBaseType.PChar or IfpsBaseType.WideString or IfpsBaseType.UnicodeString
                or IfpsBaseType.Char or IfpsBaseType.WideChar => value.AsString(),
            IfpsBaseType.U8 or IfpsBaseType.S8 or IfpsBaseType.U16 or IfpsBaseType.S16 or IfpsBaseType.U32
                or IfpsBaseType.S32 or IfpsBaseType.S64 or IfpsBaseType.U64 => value.AsInt64(),
            _ => null,
        };
    }

    private static class Constants
    {
        public static string? AsText(object? value) => value as string;

        public static long? AsNumber(object? value) => value as long?;
    }
}
