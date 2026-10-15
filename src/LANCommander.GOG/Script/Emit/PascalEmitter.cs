using System.Text;
using LANCommander.GOG.Script.Ast;

namespace LANCommander.GOG.Script.Emit;

/// <summary>
/// Writes a recovered script as Pascal.
/// </summary>
/// <remarks>
/// <para>
/// This is the form to read when checking whether a decompilation is right. Inno Setup scripts are
/// full of recognisable shapes — an <c>InitializeSetup</c> that extracts a file and checks a
/// registry key, a <c>CurStepChanged</c> that switches on the step — so output that has gone wrong
/// tends to look wrong at a glance.
/// </para>
/// <para>
/// The result is not meant to be recompiled. Parameter and local names are positional because the
/// compiler discarded the originals, a by-value parameter may have been written <c>const</c>, and
/// temporaries the compiler introduced appear as ordinary variables.
/// </para>
/// </remarks>
public static class PascalEmitter
{
    /// <summary>Writes the whole script.</summary>
    public static string Emit(ScriptUnit unit)
    {
        var output = new StringBuilder();

        output.AppendLine("{ Recovered from compiled Pascal Script bytecode. Nothing here was executed.")
            .AppendLine("  Routine and type names are the compiler's own; parameter and local names are not,")
            .AppendLine("  because compilation discarded them. This is a reading of the script, not its source. }")
            .AppendLine();

        if (unit.EntryPoint is { } entryPoint)
            output.AppendLine($"{{ entry point: {entryPoint} }}").AppendLine();

        if (unit.Globals.Count > 0)
        {
            output.AppendLine("var");

            foreach (var global in unit.Globals)
                output.AppendLine($"  {global.Name}: {global.TypeName};");

            output.AppendLine();
        }

        foreach (var routine in unit.Routines)
            EmitRoutine(output, routine);

        return output.ToString();
    }

    /// <summary>Writes one routine.</summary>
    public static string Emit(ScriptRoutine routine)
    {
        var output = new StringBuilder();

        EmitRoutine(output, routine);

        return output.ToString();
    }

    private static void EmitRoutine(StringBuilder output, ScriptRoutine routine)
    {
        output.AppendLine(Header(routine) + ";");

        if (routine.Error is { } error)
        {
            output.AppendLine($"{{ not recovered: {error} }}").AppendLine();

            return;
        }

        if (!routine.IsFullyStructured)
            output.AppendLine("{ contains control flow that did not reduce to loops and conditionals }");

        if (routine.Locals.Count > 0)
        {
            output.AppendLine("var");

            foreach (var local in routine.Locals)
                output.AppendLine($"  {local.Name}: {local.TypeName};");
        }

        output.AppendLine("begin");
        EmitBlock(output, routine.Body, 1);
        output.AppendLine("end;").AppendLine();
    }

    private static string Header(ScriptRoutine routine)
    {
        var parameters = routine.Parameters.Count == 0
            ? string.Empty
            : "(" + string.Join("; ", routine.Parameters.Select(parameter =>
                $"{(parameter.ByReference ? "var " : string.Empty)}{parameter.Name}: {parameter.TypeName}")) + ")";

        return routine.ReturnType is { } returnType
            ? $"function {routine.Name}{parameters}: {returnType}"
            : $"procedure {routine.Name}{parameters}";
    }

    private static void EmitBlock(StringBuilder output, IReadOnlyList<ScriptStatement> statements, int depth)
    {
        if (statements.Count == 0)
        {
            output.AppendLine(Indent(depth) + "{ nothing }");

            return;
        }

        foreach (var statement in statements)
            EmitStatement(output, statement, depth);
    }

    /// <summary>
    /// Writes a block where Pascal needs exactly one statement, wrapping it in <c>begin</c> and
    /// <c>end</c> only when it is not already one.
    /// </summary>
    private static void EmitNested(
        StringBuilder output,
        IReadOnlyList<ScriptStatement> statements,
        int depth,
        string terminator = ";")
    {
        output.AppendLine(Indent(depth) + "begin");
        EmitBlock(output, statements, depth + 1);
        output.AppendLine(Indent(depth) + "end" + terminator);
    }

    private static void EmitStatement(StringBuilder output, ScriptStatement statement, int depth)
    {
        var pad = Indent(depth);

        switch (statement)
        {
            case ScriptAssignment assignment:
                output.AppendLine($"{pad}{Expression(assignment.Target)} := {Expression(assignment.Value)};");

                break;

            case ScriptCallStatement call:
                output.AppendLine($"{pad}{Expression(call.Call)};");

                break;

            case ScriptStep step:
                output.AppendLine($"{pad}{(step.Decrement ? "Dec" : "Inc")}({Expression(step.Target)});");

                break;

            case ScriptIf conditional:
                output.AppendLine($"{pad}if {Expression(conditional.Condition)} then");
                EmitNested(output, conditional.Then, depth, conditional.Else.Count > 0 ? string.Empty : ";");

                if (conditional.Else.Count > 0)
                {
                    output.AppendLine($"{pad}else");
                    EmitNested(output, conditional.Else, depth);
                }

                break;

            case ScriptWhile loop:
                output.AppendLine($"{pad}while {Expression(loop.Condition)} do");
                EmitNested(output, loop.Body, depth);

                break;

            case ScriptRepeat loop:
                output.AppendLine($"{pad}repeat");
                EmitBlock(output, loop.Body, depth + 1);
                output.AppendLine($"{pad}until {Expression(loop.Until)};");

                break;

            case ScriptTry guarded:
                output.AppendLine($"{pad}try");
                EmitBlock(output, guarded.Body, depth + 1);

                if (guarded.Except.Count > 0)
                {
                    output.AppendLine($"{pad}except");
                    EmitBlock(output, guarded.Except, depth + 1);
                }

                if (guarded.Finally.Count > 0)
                {
                    output.AppendLine($"{pad}finally");
                    EmitBlock(output, guarded.Finally, depth + 1);
                }

                output.AppendLine($"{pad}end;");

                break;

            case ScriptBreak:
                output.AppendLine($"{pad}Break;");

                break;

            case ScriptContinue:
                output.AppendLine($"{pad}Continue;");

                break;

            case ScriptExit:
                output.AppendLine($"{pad}Exit;");

                break;

            case ScriptLabel label:
                output.AppendLine($"{label.Name}:");

                break;

            case ScriptGoto jump:
                output.AppendLine(jump.Condition is { } condition
                    ? $"{pad}if {Expression(condition)} then goto {jump.Target};"
                    : $"{pad}goto {jump.Target};");

                break;

            case ScriptNote note:
                output.AppendLine($"{pad}{{ {note.Text} }}");

                break;
        }
    }

    /// <summary>Writes an expression, parenthesising every compound so precedence cannot be misread.</summary>
    private static string Expression(ScriptExpression expression) => expression switch
    {
        ScriptVariableReference reference => reference.Name,
        ScriptLiteral literal => literal.Value.ToLiteral(literal.IsBoolean),
        ScriptOpaque opaque => $"{{ {opaque.Text} }}",
        ScriptRoutineReference routine => routine.Name is null ? "nil" : $"@{routine.Name}",
        ScriptFieldAccess field => $"{Expression(field.Target)}.{field.Name}",
        ScriptElementAccess element => $"{Expression(element.Target)}[{Expression(element.Index)}]",
        ScriptUnary unary => UnaryOperator(unary.Operator) + Group(unary.Operand),
        ScriptBinary binary => $"({Expression(binary.Left)} {BinaryOperator(binary.Operator)} {Expression(binary.Right)})",
        ScriptCall call => Call(call),
        _ => "{ unrecognised expression }",
    };

    /// <summary>Parenthesises an operand only when it is not already a self-contained term.</summary>
    private static string Group(ScriptExpression expression) =>
        expression is ScriptBinary or ScriptVariableReference or ScriptLiteral or ScriptCall
            or ScriptFieldAccess or ScriptElementAccess
            ? Expression(expression)
            : $"({Expression(expression)})";

    private static string Call(ScriptCall call)
    {
        var arguments = string.Join(", ", call.Arguments.Select(argument => Expression(argument.Value)));
        var name = call.Instance is { } instance ? $"{Expression(instance)}.{call.Callee}" : call.Callee;

        return call.Arguments.Count == 0 && call.Instance is null && call.Kind != ScriptCallKind.Indirect
            ? name
            : $"{name}({arguments})";
    }

    private static string UnaryOperator(ScriptUnaryOperator op) => op switch
    {
        ScriptUnaryOperator.Not => "not ",
        ScriptUnaryOperator.BitwiseNot => "not ",
        ScriptUnaryOperator.Negate => "-",
        _ => "?",
    };

    private static string BinaryOperator(ScriptBinaryOperator op) => op switch
    {
        ScriptBinaryOperator.Add => "+",
        ScriptBinaryOperator.Subtract => "-",
        ScriptBinaryOperator.Multiply => "*",
        ScriptBinaryOperator.Divide => "/",
        ScriptBinaryOperator.Modulus => "mod",
        ScriptBinaryOperator.ShiftLeft => "shl",
        ScriptBinaryOperator.ShiftRight => "shr",
        ScriptBinaryOperator.And => "and",
        ScriptBinaryOperator.Or => "or",
        ScriptBinaryOperator.Xor => "xor",
        ScriptBinaryOperator.Cast => "as",
        ScriptBinaryOperator.Equal => "=",
        ScriptBinaryOperator.NotEqual => "<>",
        ScriptBinaryOperator.Less => "<",
        ScriptBinaryOperator.LessOrEqual => "<=",
        ScriptBinaryOperator.Greater => ">",
        ScriptBinaryOperator.GreaterOrEqual => ">=",
        ScriptBinaryOperator.In => "in",
        ScriptBinaryOperator.Is => "is",
        _ => "?",
    };

    private static string Indent(int depth) => new(' ', depth * 2);
}
