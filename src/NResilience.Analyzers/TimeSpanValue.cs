using System;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NResilience.Analyzers;

/// <summary>
/// Constant folding for the handful of ways a <see cref="TimeSpan"/> is written at a call site.
/// Anything it does not recognise is reported as unknown, and the analyzer stays quiet - a
/// configuration diagnostic that guesses is worse than none.
/// </summary>
internal static class TimeSpanValue
{
    internal static bool TryEvaluate(IOperation operation, KnownSymbols known, out TimeSpan value)
    {
        value = default;

        if (operation is IConversionOperation conversion)
        {
            return TryEvaluate(conversion.Operand, known, out value);
        }

        if (operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Minus } negation)
        {
            if (!TryEvaluate(negation.Operand, known, out TimeSpan operand))
            {
                return false;
            }

            value = operand.Negate();
            return true;
        }

        if (operation is IFieldReferenceOperation field)
        {
            return TryWellKnownField(field, known, out value);
        }

        if (operation is IInvocationOperation invocation)
        {
            return TryFactory(invocation, known, out value);
        }

        if (operation is IObjectCreationOperation creation)
        {
            return TryConstructor(creation, known, out value);
        }

        return false;
    }

    private static bool TryWellKnownField(IFieldReferenceOperation field, KnownSymbols known, out TimeSpan value)
    {
        value = default;
        ITypeSymbol? owner = field.Field.ContainingType;

        if (SymbolEqualityComparer.Default.Equals(owner, known.Timeout) && field.Field.Name == "InfiniteTimeSpan")
        {
            value = System.Threading.Timeout.InfiniteTimeSpan;
            return true;
        }

        if (!SymbolEqualityComparer.Default.Equals(owner, known.TimeSpan))
        {
            return false;
        }

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
            || !TryConstant(invocation.Arguments[0].Value, out double amount))
        {
            return false;
        }

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
        catch (OverflowException)
        {
            // A literal that does not fit a TimeSpan throws at runtime too, and saying so is the job
            // of whoever owns that message rather than of a configuration diagnostic.
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryConstructor(IObjectCreationOperation creation, KnownSymbols known, out TimeSpan value)
    {
        value = default;

        if (!SymbolEqualityComparer.Default.Equals(creation.Type, known.TimeSpan))
        {
            return false;
        }

        double[] parts = new double[creation.Arguments.Length];

        for (int i = 0; i < creation.Arguments.Length; i++)
        {
            if (!TryConstant(creation.Arguments[i].Value, out parts[i]))
            {
                return false;
            }
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
        IOperation unwrapped = operation is IConversionOperation conversion ? conversion.Operand : operation;

        if (!unwrapped.ConstantValue.HasValue || unwrapped.ConstantValue.Value is null)
        {
            return false;
        }

        try
        {
            value = Convert.ToDouble(unwrapped.ConstantValue.Value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>How the library says "no bound", spelled the same way here.</summary>
    internal static bool IsUnbounded(this TimeSpan value) => value == System.Threading.Timeout.InfiniteTimeSpan;

    private static TimeSpan Negate(this TimeSpan value) => value == TimeSpan.MinValue ? TimeSpan.MaxValue : -value;

    /// <summary>The shortest honest rendering of a duration for a diagnostic message.</summary>
    internal static string Describe(this TimeSpan value) => value.ToString("g", CultureInfo.InvariantCulture);
}
