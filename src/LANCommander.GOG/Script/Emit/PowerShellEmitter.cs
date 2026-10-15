using System.Globalization;
using System.Text;
using LANCommander.GOG.Script.Ast;
using LANCommander.GOG.Script.Ifps;

namespace LANCommander.GOG.Script.Emit;

/// <summary>
/// Writes a recovered script as PowerShell, against the <see cref="InnoApi"/> shim module.
/// </summary>
/// <remarks>
/// <para>
/// The translation is faithful rather than idiomatic: every routine becomes a function, control
/// flow keeps its shape, and Inno's script API becomes calls into the shim. A reader who knows the
/// Pascal output can follow the PowerShell line for line.
/// </para>
/// <para>
/// PowerShell differs from Pascal in ways that quietly corrupt a translation if they are ignored,
/// and each is handled here: an unsuppressed expression becomes the function's return value, so
/// discarded results are assigned to <c>$null</c> and a result is returned once and explicitly;
/// strings are zero-indexed, so <c>Copy</c> and <c>Pos</c> go through the shim rather than being
/// inlined; a by-reference parameter is a <c>[ref]</c> whose value has to be reached through
/// <c>.Value</c>; and one instruction covers both logical and bitwise operators, so the operands
/// decide which is meant.
/// </para>
/// <para>
/// A routine whose control flow did not reduce keeps a jump the language cannot express. Those
/// become a <c>throw</c> naming the routine, so such a script stops rather than running half of
/// what it was asked to.
/// </para>
/// </remarks>
public static class PowerShellEmitter
{
    /// <summary>Writes the whole script.</summary>
    public static string Emit(ScriptUnit unit)
    {
        var stubs = new SortedSet<string>(StringComparer.Ordinal);
        var globals = unit.Globals.Select(global => global.Name).ToHashSet(StringComparer.Ordinal);

        var booleanRoutines = unit.Routines
            .Where(routine => routine.ReturnType is { } type && Initial(type) == "$false")
            .Select(routine => routine.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bodies = new StringBuilder();

        foreach (var routine in unit.Routines)
            EmitRoutine(bodies, routine, stubs, globals, booleanRoutines);

        var output = new StringBuilder();

        EmitPreamble(output, unit, stubs);
        output.Append(bodies);

        return output.ToString();
    }

    private static void EmitPreamble(StringBuilder output, ScriptUnit unit, SortedSet<string> stubs)
    {
        var unstructured = unit.Routines.Where(routine => !routine.IsFullyStructured).ToList();

        output.AppendLine("<#")
            .AppendLine("    Translated from an Inno Setup installer's compiled Pascal Script.")
            .AppendLine()
            .AppendLine("    Routine names are the compiler's own. Parameter and local names are not: compilation")
            .AppendLine("    discarded them, so they are positional here.")
            .AppendLine()
            .AppendLine($"    Import {InnoApi.ModuleFileName} beside this file, then call Initialize-InnoEnvironment")
            .AppendLine("    with the constants the script expands before calling anything else.");

        if (unstructured.Count > 0)
            output.AppendLine()
                .AppendLine($"    {unstructured.Count} of {unit.Routines.Count} routines kept control flow that PowerShell")
                .AppendLine("    cannot express. Those routines throw where the jump was, rather than running part of")
                .AppendLine("    what they were meant to. The Pascal output carries them in full.");

        output.AppendLine("#>")
            .AppendLine()
            .AppendLine("Set-StrictMode -Version Latest")
            .AppendLine($"Import-Module (Join-Path $PSScriptRoot '{InnoApi.ModuleFileName}') -Force")
            .AppendLine();

        if (stubs.Count > 0)
        {
            output.AppendLine("# Inno Setup functions this script calls that the shim module does not provide. Each")
            .AppendLine("# one fails loudly, so a run that needs the installer itself stops here.");

            foreach (var stub in stubs)
                output.AppendLine(
                    $"function {stub} {{ throw '{stub} is part of Inno Setup and has no stand-in outside the installer.' }}");

            output.AppendLine();
        }

        if (unit.Globals.Count > 0)
        {
            output.AppendLine("# The script's global variables.");

            foreach (var global in unit.Globals)
                output.AppendLine($"$script:{Sanitise(global.Name)} = {Initial(global.TypeName)}");

            output.AppendLine();
        }

        if (unit.EntryPoint is { } entryPoint)
            output.AppendLine($"# The installer calls {entryPoint} first.").AppendLine();
    }

    private static void EmitRoutine(
        StringBuilder output,
        ScriptRoutine routine,
        SortedSet<string> stubs,
        IReadOnlySet<string> globals,
        IReadOnlySet<string> booleanRoutines)
    {
        var context = new RoutineContext(routine, stubs, globals, booleanRoutines);

        output.AppendLine($"function {Sanitise(routine.Name)} {{");

        if (routine.Parameters.Count > 0)
        {
            var parameters = routine.Parameters.Select(parameter =>
                parameter.ByReference ? $"[ref] ${Sanitise(parameter.Name)}" : $"${Sanitise(parameter.Name)}");

            output.AppendLine($"    param({string.Join(", ", parameters)})");
            output.AppendLine();
        }

        if (routine.Error is { } error)
        {
            output.AppendLine($"    throw '{Escape(routine.Name)} was not recovered: {Escape(error)}'");
            output.AppendLine("}").AppendLine();

            return;
        }

        foreach (var local in routine.Locals)
            output.AppendLine($"    ${Sanitise(local.Name)} = {Initial(local.TypeName)}");

        if (routine.ReturnsValue)
            output.AppendLine($"    $Result = {Initial(routine.ReturnType!)}");

        if (routine.Locals.Count > 0 || routine.ReturnsValue)
            output.AppendLine();

        EmitBlock(output, routine.Body, 1, context);

        if (routine.ReturnsValue)
            output.AppendLine().AppendLine("    return $Result");

        output.AppendLine("}").AppendLine();
    }

    private static void EmitBlock(
        StringBuilder output,
        IReadOnlyList<ScriptStatement> statements,
        int depth,
        RoutineContext context)
    {
        if (statements.Count == 0)
        {
            output.AppendLine(Indent(depth) + "# nothing");

            return;
        }

        foreach (var statement in statements)
            EmitStatement(output, statement, depth, context);
    }

    private static void EmitStatement(
        StringBuilder output,
        ScriptStatement statement,
        int depth,
        RoutineContext context)
    {
        var pad = Indent(depth);

        switch (statement)
        {
            case ScriptAssignment assignment:
                output.AppendLine($"{pad}{context.Assignable(assignment.Target)} = {context.Expression(assignment.Value)}");

                break;

            case ScriptCallStatement call:
                // Anything left on the pipeline becomes the function's return value, so a call made
                // only for its effect has to be discarded explicitly.
                output.AppendLine($"{pad}$null = {context.Expression(call.Call)}");

                break;

            case ScriptStep step:
                output.AppendLine(
                    $"{pad}{context.Assignable(step.Target)} = {context.Expression(step.Target)} {(step.Decrement ? "-" : "+")} 1");

                break;

            case ScriptIf conditional:
                output.AppendLine($"{pad}if ({context.Condition(conditional.Condition)}) {{");
                EmitBlock(output, conditional.Then, depth + 1, context);

                if (conditional.Else.Count > 0)
                {
                    output.AppendLine($"{pad}}} else {{");
                    EmitBlock(output, conditional.Else, depth + 1, context);
                }

                output.AppendLine($"{pad}}}");

                break;

            case ScriptWhile loop:
                output.AppendLine($"{pad}while ({context.Condition(loop.Condition)}) {{");
                EmitBlock(output, loop.Body, depth + 1, context);
                output.AppendLine($"{pad}}}");

                break;

            case ScriptRepeat loop:
                output.AppendLine($"{pad}do {{");
                EmitBlock(output, loop.Body, depth + 1, context);
                output.AppendLine($"{pad}}} until ({context.Condition(loop.Until)})");

                break;

            case ScriptTry guarded:
                output.AppendLine($"{pad}try {{");
                EmitBlock(output, guarded.Body, depth + 1, context);

                if (guarded.Except.Count > 0)
                {
                    output.AppendLine($"{pad}}} catch {{");
                    EmitBlock(output, guarded.Except, depth + 1, context);
                }

                if (guarded.Finally.Count > 0)
                {
                    output.AppendLine($"{pad}}} finally {{");
                    EmitBlock(output, guarded.Finally, depth + 1, context);
                }
                else if (guarded.Except.Count == 0)
                {
                    // PowerShell needs one or the other, and Pascal's bare try guards nothing.
                    output.AppendLine($"{pad}}} catch {{");
                    output.AppendLine($"{pad}    throw");
                }

                output.AppendLine($"{pad}}}");

                break;

            case ScriptBreak:
                output.AppendLine($"{pad}break");

                break;

            case ScriptContinue:
                output.AppendLine($"{pad}continue");

                break;

            case ScriptExit:
                output.AppendLine(context.Routine.ReturnsValue ? $"{pad}return $Result" : $"{pad}return");

                break;

            case ScriptLabel label:
                output.AppendLine($"{pad}# {label.Name}:");

                break;

            case ScriptGoto jump:
            {
                var message = $"{context.Routine.Name} jumps to {jump.Target}, which PowerShell cannot express.";

                output.AppendLine(jump.Condition is { } condition
                    ? $"{pad}if ({context.Condition(condition)}) {{ throw '{Escape(message)}' }}"
                    : $"{pad}throw '{Escape(message)}'");

                break;
            }

            case ScriptNote note:
                output.AppendLine($"{pad}# {note.Text}");

                break;
        }
    }

    /// <summary>A sensible starting value for a variable, so arithmetic on it behaves.</summary>
    private static string Initial(string typeName) => typeName.ToUpperInvariant() switch
    {
        "BOOLEAN" or "BYTEBOOL" or "WORDBOOL" or "LONGBOOL" => "$false",
        "BYTE" or "SHORTINT" or "WORD" or "SMALLINT" or "CARDINAL" or "INTEGER" or "INT64" or "UINT64" => "0",
        "SINGLE" or "DOUBLE" or "EXTENDED" or "CURRENCY" => "0",
        "STRING" or "ANSISTRING" or "WIDESTRING" or "ANSICHAR" or "WIDECHAR" or "PCHAR" => "''",
        _ => "$null",
    };

    /// <summary>Makes a Pascal identifier safe to use as a PowerShell name.</summary>
    private static string Sanitise(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var character in name)
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');

        return builder.Length == 0 || char.IsDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    private static string Escape(string text) => text.Replace("'", "''");

    private static string Indent(int depth) => new(' ', depth * 4);

    /// <summary>
    /// What one routine needs while it is being written: which of its parameters are references,
    /// and somewhere to record the shim functions it turned out to need.
    /// </summary>
    private sealed class RoutineContext(
        ScriptRoutine routine,
        SortedSet<string> stubs,
        IReadOnlySet<string> globals,
        IReadOnlySet<string> booleanRoutines)
    {
        private readonly HashSet<string> _references = routine.Parameters
            .Where(parameter => parameter.ByReference)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);

        public ScriptRoutine Routine { get; } = routine;

        /// <summary>Writes an expression in a value position.</summary>
        public string Expression(ScriptExpression expression) => expression switch
        {
            ScriptVariableReference reference => Variable(reference.Name),
            ScriptLiteral literal => Literal(literal),
            ScriptOpaque opaque => $"$null <# {opaque.Text} #>",

            // A callback is passed by name; the script block behind it is only resolved if something
            // actually calls it, which Inno Setup does from inside the installer.
            ScriptRoutineReference routine =>
                routine.Name is null ? "$null" : $"'{Escape(Sanitise(routine.Name))}'",
            ScriptFieldAccess field => $"{Expression(field.Target)}.{field.Name}",
            ScriptElementAccess element => $"{Expression(element.Target)}[{Expression(element.Index)}]",
            ScriptUnary unary => Unary(unary),
            ScriptBinary binary => Binary(binary),
            ScriptCall call => Call(call),
            _ => "$null",
        };

        /// <summary>Writes an expression in a position PowerShell expects to be a boolean.</summary>
        public string Condition(ScriptExpression expression) =>
            IsBooleanShaped(expression) ? Expression(expression) : $"[bool]({Expression(expression)})";

        /// <summary>Writes the left-hand side of an assignment, where a reference needs its value.</summary>
        public string Assignable(ScriptExpression expression) => Expression(expression);

        private string Variable(string name)
        {
            var safe = Sanitise(name);

            if (_references.Contains(name))
                return $"${safe}.Value";

            return globals.Contains(name) ? $"$script:{safe}" : $"${safe}";
        }

        private static string Literal(ScriptLiteral literal)
        {
            var value = literal.Value;

            return value.BaseType switch
            {
                IfpsBaseType.U8 when literal.IsBoolean => value.AsInt64() == 0 ? "$false" : "$true",
                IfpsBaseType.String or IfpsBaseType.PChar or IfpsBaseType.WideString
                    or IfpsBaseType.UnicodeString or IfpsBaseType.Char or IfpsBaseType.WideChar =>
                    QuotePowerShell(value.AsString()),
                IfpsBaseType.Set => $"@() <# set literal {Convert.ToHexString(value.Encoded.Span)} #>",
                _ => value.ToLiteral(asBoolean: false),
            };
        }

        private string Unary(ScriptUnary unary) => unary.Operator switch
        {
            ScriptUnaryOperator.Not when IsBooleanShaped(unary.Operand) => $"(-not {Expression(unary.Operand)})",
            ScriptUnaryOperator.Not => $"(InnoNot {Expression(unary.Operand)})",
            ScriptUnaryOperator.BitwiseNot => $"(-bnot {Expression(unary.Operand)})",
            ScriptUnaryOperator.Negate => $"(-{Expression(unary.Operand)})",
            _ => Expression(unary.Operand),
        };

        private string Binary(ScriptBinary binary)
        {
            var left = Expression(binary.Left);
            var right = Expression(binary.Right);

            // One instruction covers both the logical and the bitwise operator, so when the operands
            // do not settle it the shim decides at runtime from what they turn out to be.
            var logical = IsBooleanShaped(binary.Left) && IsBooleanShaped(binary.Right);

            return binary.Operator switch
            {
                ScriptBinaryOperator.Add => $"({left} + {right})",
                ScriptBinaryOperator.Subtract => $"({left} - {right})",
                ScriptBinaryOperator.Multiply => $"({left} * {right})",
                ScriptBinaryOperator.Divide => $"({left} / {right})",
                ScriptBinaryOperator.Modulus => $"({left} % {right})",
                ScriptBinaryOperator.ShiftLeft => $"({left} -shl {right})",
                ScriptBinaryOperator.ShiftRight => $"({left} -shr {right})",
                ScriptBinaryOperator.And => logical ? $"({left} -and {right})" : $"(InnoAnd {left} {right})",
                ScriptBinaryOperator.Or => logical ? $"({left} -or {right})" : $"(InnoOr {left} {right})",
                ScriptBinaryOperator.Xor => logical ? $"({left} -xor {right})" : $"(InnoXor {left} {right})",
                ScriptBinaryOperator.Equal => $"({left} -eq {right})",
                ScriptBinaryOperator.NotEqual => $"({left} -ne {right})",
                ScriptBinaryOperator.Less => $"({left} -lt {right})",
                ScriptBinaryOperator.LessOrEqual => $"({left} -le {right})",
                ScriptBinaryOperator.Greater => $"({left} -gt {right})",
                ScriptBinaryOperator.GreaterOrEqual => $"({left} -ge {right})",
                ScriptBinaryOperator.In => $"(InnoIn {left} {right})",
                ScriptBinaryOperator.Is => $"(InnoIs {left} {right})",

                // A checked cast has nothing to check in a language without the class it names.
                ScriptBinaryOperator.Cast => left,
                _ => left,
            };
        }

        private string Call(ScriptCall call)
        {
            var arguments = string.Join(" ", call.Arguments.Select(Argument));

            switch (call.Kind)
            {
                case ScriptCallKind.Method:
                    return $"(Invoke-InnoMethod {Expression(call.Instance!)} '{Escape(call.Callee)}' @({ArgumentList(call)}))";

                case ScriptCallKind.Native:
                {
                    var split = call.Callee.IndexOf('!');
                    var library = split < 0 ? call.Callee : call.Callee[..split];
                    var entryPoint = split < 0 ? call.Callee : call.Callee[(split + 1)..];

                    return $"(Invoke-InnoNative '{Escape(library)}' '{Escape(entryPoint)}' @({ArgumentList(call)}))";
                }

                case ScriptCallKind.Indirect:
                    return $"(Invoke-InnoMethod $null '{Escape(call.Callee)}' @())";

                case ScriptCallKind.Host:
                {
                    var shim = InnoApi.Resolve(call.Callee);

                    if (shim is null)
                    {
                        shim = InnoApi.StubFor(call.Callee);
                        stubs.Add(shim);
                    }

                    return arguments.Length == 0 ? $"({shim})" : $"({shim} {arguments})";
                }

                default:
                    return arguments.Length == 0
                        ? $"({Sanitise(call.Callee)})"
                        : $"({Sanitise(call.Callee)} {arguments})";
            }
        }

        private string ArgumentList(ScriptCall call) =>
            string.Join(", ", call.Arguments.Select(Argument));

        /// <summary>
        /// Writes one argument. A routine that writes back through a parameter needs a reference,
        /// and a reference already held is passed on rather than wrapped again.
        /// </summary>
        private string Argument(ScriptArgument argument)
        {
            if (!argument.ByReference)
                return Expression(argument.Value);

            if (argument.Value is ScriptVariableReference reference && _references.Contains(reference.Name))
                return $"${Sanitise(reference.Name)}";

            return $"([ref]{Expression(argument.Value)})";
        }

        /// <summary>
        /// Whether an expression is certainly a boolean, which decides between the logical and the
        /// bitwise reading of an operator.
        /// </summary>
        private bool IsBooleanShaped(ScriptExpression expression) => expression switch
        {
            ScriptLiteral literal => literal.IsBoolean && literal.Value.BaseType == IfpsBaseType.U8,
            ScriptCall { Kind: ScriptCallKind.Host } host => InnoApi.ReturnsBoolean(host.Callee),
            ScriptCall { Kind: ScriptCallKind.Script } script => booleanRoutines.Contains(script.Callee),
            ScriptUnary { Operator: ScriptUnaryOperator.Not } => true,
            ScriptBinary binary => binary.Operator switch
            {
                ScriptBinaryOperator.Equal or ScriptBinaryOperator.NotEqual or ScriptBinaryOperator.Less
                    or ScriptBinaryOperator.LessOrEqual or ScriptBinaryOperator.Greater
                    or ScriptBinaryOperator.GreaterOrEqual or ScriptBinaryOperator.In
                    or ScriptBinaryOperator.Is => true,
                ScriptBinaryOperator.And or ScriptBinaryOperator.Or or ScriptBinaryOperator.Xor =>
                    IsBooleanShaped(binary.Left) && IsBooleanShaped(binary.Right),
                _ => false,
            },
            _ => false,
        };

        /// <summary>
        /// Quotes a string for PowerShell, preferring the single-quoted form that interpolates
        /// nothing and falling back to escapes only when the text will not fit in it.
        /// </summary>
        private static string QuotePowerShell(string value)
        {
            if (!value.Any(char.IsControl))
                return $"'{value.Replace("'", "''")}'";

            var builder = new StringBuilder("\"");

            foreach (var character in value)
                builder.Append(character switch
                {
                    '\r' => "`r",
                    '\n' => "`n",
                    '\t' => "`t",
                    '\0' => "`0",
                    '"' => "`\"",
                    '$' => "`$",
                    '`' => "``",
                    _ when char.IsControl(character) =>
                        $"$([char]{((int)character).ToString(CultureInfo.InvariantCulture)})",
                    _ => character.ToString(),
                });

            return builder.Append('"').ToString();
        }
    }
}
