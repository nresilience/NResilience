using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
///     Constant folding for the handful of ways a <see cref="TimeSpan" /> is written at a call site.
///     Anything it does not recognize is reported as unknown, and the analyzer stays quiet - a
///     configuration diagnostic that guesses is worse than none.
/// </summary>
internal static class TimeSpanValue
{
    internal static bool TryEvaluate(IOperation operation, KnownSymbols known, out TimeSpan value)
    {
        value = default;

        if (operation is IConversionOperation conversion)
            return TryEvaluate(conversion.Operand, known, out value);

        if (operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } negation)
        {
            if (!TryEvaluate(negation.Operand, known, out var operand))
                return false;

            value = operand.Negate();
            return true;
        }

        if (operation is IFieldReferenceOperation field)
            return TryWellKnownField(field, known, out value);

        if (operation is IInvocationOperation invocation)
            return TryFactory(invocation, known, out value);

        if (operation is IObjectCreationOperation creation)
            return TryConstructor(creation, known, out value);

        return false;
    }

    private static bool TryWellKnownField(IFieldReferenceOperation field, KnownSymbols known, out TimeSpan value)
    {
        value = default;
        ITypeSymbol? owner = field.Field.ContainingType;

        if (SymbolEqualityComparer.Default.Equals(owner, known.Timeout) && field.Field.Name == "InfiniteTimeSpan")
        {
            value = Timeout.InfiniteTimeSpan;
            return true;
        }

        if (!SymbolEqualityComparer.Default.Equals(owner, known.TimeSpan))
            return false;

        switch (field.Field.Name)
        {
            case "Zero":
                value = TimeSpan.Zero;
                return true;
            case "MaxValue":
                value = TimeSpan.MaxValue;
                return true;
            case "MinValue":
                value = TimeSpan.MinValue;
                return true;
            default:
                return false;
        }
    }

    private static bool TryFactory(IInvocationOperation invocation, KnownSymbols known, out TimeSpan value)
    {
        value = default;

        if (!SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, known.TimeSpan)
            || invocation.Arguments.Length != 1
            || !TryConstant(invocation.Arguments[0].Value, out var amount))
            return false;

        try
        {
            switch (invocation.TargetMethod.Name)
            {
                case "FromTicks":
                    value = TimeSpan.FromTicks((long)amount);
                    return true;
                case "FromMilliseconds":
                    value = TimeSpan.FromMilliseconds(amount);
                    return true;
                case "FromSeconds":
                    value = TimeSpan.FromSeconds(amount);
                    return true;
                case "FromMinutes":
                    value = TimeSpan.FromMinutes(amount);
                    return true;
                case "FromHours":
                    value = TimeSpan.FromHours(amount);
                    return true;
                case "FromDays":
                    value = TimeSpan.FromDays(amount);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception error) when (error is OverflowException or ArgumentException)
        {
            // A literal that does not fit a TimeSpan throws at runtime too, and saying so is the job
            // of whoever owns that message rather than of a configuration diagnostic.
            return false;
        }
    }

    private static bool TryConstructor(IObjectCreationOperation creation, KnownSymbols known, out TimeSpan value)
    {
        value = default;

        if (!SymbolEqualityComparer.Default.Equals(creation.Type, known.TimeSpan))
            return false;

        var parts = new double[creation.Arguments.Length];

        for (var i = 0; i < creation.Arguments.Length; i++)
        {
            if (!TryConstant(creation.Arguments[i].Value, out parts[i]))
                return false;
        }

        try
        {
            switch (parts.Length)
            {
                case 1:
                    value = TimeSpan.FromTicks((long)parts[0]);
                    return true;
                case 3:
                    value = new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2]);
                    return true;
                case 4:
                    value = new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2], (int)parts[3]);
                    return true;
                case 5:
                    value = new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2], (int)parts[3], (int)parts[4]);
                    return true;
                default:
                    return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryConstant(IOperation operation, out double value)
    {
        value = 0;
        var unwrapped = operation is IConversionOperation conversion ? conversion.Operand : operation;

        if (!unwrapped.ConstantValue.HasValue || unwrapped.ConstantValue.Value is null)
            return false;

        try
        {
            value = Convert.ToDouble(unwrapped.ConstantValue.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception error) when (error is InvalidCastException or OverflowException or FormatException)
        {
            // Not a number the fold can use, which is the same answer as not being a constant.
            return false;
        }
    }

    /// <summary>How the library says "no bound", spelled the same way here.</summary>
    internal static bool IsUnbounded(this TimeSpan value) => value == Timeout.InfiniteTimeSpan;

    private static TimeSpan Negate(this TimeSpan value) => value == TimeSpan.MinValue ? TimeSpan.MaxValue : -value;

    /// <summary>The shortest honest rendering of a duration for a diagnostic message.</summary>
    internal static string Describe(this TimeSpan value) => value.ToString("g", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The compact rendering <c>Resilience.Explain()</c> uses, duplicated here on purpose: an
    ///     analyzer targets <c>netstandard2.0</c> and loads into the compiler's own process, so it
    ///     cannot reference the runtime. What it can share is the format, so the sentence a developer
    ///     reads in the IDE and the one <c>Explain()</c> prints at runtime are the same sentence.
    /// </summary>
    internal static string Compact(this TimeSpan value)
    {
        if (value.IsUnbounded())
            return "no bound";

        var magnitude = value == TimeSpan.MinValue ? TimeSpan.MaxValue : value < TimeSpan.Zero ? value.Negate() : value;

        if (magnitude < TimeSpan.FromSeconds(1))
            return Number(value.TotalMilliseconds) + "ms";

        if (magnitude < TimeSpan.FromMinutes(1))
            return Number(value.TotalSeconds) + "s";

        return magnitude < TimeSpan.FromHours(1) ? Number(value.TotalMinutes) + "m" : Number(value.TotalHours) + "h";
    }

    /// <summary>
    ///     How much of a bound something got, as a percentage of it. The same clause
    ///     <c>Resilience.Explain()</c> writes for a deadline-clamped attempt.
    /// </summary>
    internal static string Share(this TimeSpan part, TimeSpan whole) =>
        whole <= TimeSpan.Zero || whole.IsUnbounded()
            ? "an unknown share"
            : Number(part.Ticks / (double)whole.Ticks * 100) + "%";

    private static string Number(double amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
}
