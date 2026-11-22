// Formula language used by FormulaSource (input tables) and FormulaColumn (method tables).
//
//   expr    := term (('+' | '-') term)*
//   term    := unary (('*' | '/') unary)*
//   unary   := '-' unary | primary
//   primary := number | '$' n | identifier | identifier '(' expr (',' expr)* ')' | '(' expr ')'
//
// $n (1-based) is the n-th InputRequirement of the formula. A bare identifier is another column of the
// same method table. Functions: latest(cell) and current(cell | devPeriod) reduce to the exposure period
// at its latest diagonal; min, max, coalesce take any number of arguments; abs takes one.
//
// Values broadcast upwards: a scalar fits anywhere, an exposure-period or development-period value
// fits a development cell. Reductions are explicit. Grain is checked at publish, so the engine never
// meets an ill-shaped formula.

using System.Globalization;

namespace Actuarial.Domain.Formulas;

public abstract record Expr;
public sealed record NumberExpr(decimal Value) : Expr;
public sealed record RefExpr(int Index) : Expr;                       // 1-based
public sealed record ColumnExpr(string Key) : Expr;
public sealed record NegateExpr(Expr Operand) : Expr;
public sealed record BinaryExpr(char Op, Expr Left, Expr Right) : Expr;
public sealed record CallExpr(string Function, IReadOnlyList<Expr> Args) : Expr;

public sealed class FormulaException(string message) : Exception(message);

public static class Formula
{
    public static readonly IReadOnlySet<string> Functions =
        new HashSet<string> { "latest", "current", "min", "max", "abs", "coalesce" };

    public static Expr Parse(string text) => new Parser(text).ParseAll();

    public static bool TryParse(string text, out Expr? expr, out string? error)
    {
        try { expr = Parse(text); error = null; return true; }
        catch (FormulaException e) { expr = null; error = e.Message; return false; }
    }

    public static IEnumerable<string> ColumnReferences(Expr e) => e switch
    {
        ColumnExpr c => new[] { c.Key },
        NegateExpr n => ColumnReferences(n.Operand),
        BinaryExpr b => ColumnReferences(b.Left).Concat(ColumnReferences(b.Right)),
        CallExpr c => c.Args.SelectMany(ColumnReferences),
        _ => Enumerable.Empty<string>()
    };

    public static IEnumerable<int> RefIndices(Expr e) => e switch
    {
        RefExpr r => new[] { r.Index },
        NegateExpr n => RefIndices(n.Operand),
        BinaryExpr b => RefIndices(b.Left).Concat(RefIndices(b.Right)),
        CallExpr c => c.Args.SelectMany(RefIndices),
        _ => Enumerable.Empty<int>()
    };

    /// <summary>Least grain that holds both values (broadcasting).</summary>
    public static GrainKind Join(GrainKind a, GrainKind b)
    {
        if (a == b) return a;
        if (a == GrainKind.Scalar) return b;
        if (b == GrainKind.Scalar) return a;
        return GrainKind.DevelopmentCell;
    }

    /// <summary>True when a value of grain <paramref name="value"/> can be stored in a column of grain <paramref name="target"/>.</summary>
    public static bool Fits(GrainKind value, GrainKind target) => Join(value, target) == target;

    /// <summary>Infers the grain of an expression, or throws a FormulaException describing the problem.</summary>
    public static GrainKind InferGrain(Expr e, IReadOnlyList<InputRequirement> refs, Func<string, GrainKind?> column)
    {
        switch (e)
        {
            case NumberExpr: return GrainKind.Scalar;
            case RefExpr r:
                if (r.Index < 1 || r.Index > refs.Count)
                    throw new FormulaException($"${r.Index} does not exist; this formula declares {refs.Count} reference(s).");
                return refs[r.Index - 1].Grain;
            case ColumnExpr c:
                return column(c.Key) ?? throw new FormulaException($"Unknown column '{c.Key}'.");
            case NegateExpr n: return InferGrain(n.Operand, refs, column);
            case BinaryExpr b: return Join(InferGrain(b.Left, refs, column), InferGrain(b.Right, refs, column));
            case CallExpr call:
                var args = call.Args.Select(a => InferGrain(a, refs, column)).ToList();
                switch (call.Function)
                {
                    case "latest":
                        Arity(call, 1);
                        if (args[0] != GrainKind.DevelopmentCell)
                            throw new FormulaException($"latest() needs a development-cell value, got {args[0]}.");
                        return GrainKind.ExposurePeriod;
                    case "current":
                        Arity(call, 1);
                        if (args[0] is not (GrainKind.DevelopmentCell or GrainKind.DevelopmentPeriod))
                            throw new FormulaException($"current() needs a development-period or development-cell value, got {args[0]}.");
                        return GrainKind.ExposurePeriod;
                    case "abs":
                        Arity(call, 1);
                        return args[0];
                    default:
                        if (args.Count == 0) throw new FormulaException($"{call.Function}() needs at least one argument.");
                        return args.Aggregate(Join);
                }
            default: throw new FormulaException("Unknown expression.");
        }
    }

    private static void Arity(CallExpr c, int n)
    {
        if (c.Args.Count != n) throw new FormulaException($"{c.Function}() takes {n} argument(s).");
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public Expr ParseAll()
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormulaException("The formula is empty.");
            var e = ParseExpr();
            Skip();
            if (_pos < text.Length) throw new FormulaException($"Unexpected '{text[_pos]}' at position {_pos + 1}.");
            return e;
        }

        private void Skip() { while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++; }

        private bool Eat(char c)
        {
            Skip();
            if (_pos < text.Length && text[_pos] == c) { _pos++; return true; }
            return false;
        }

        private Expr ParseExpr()
        {
            var left = ParseTerm();
            while (true)
            {
                if (Eat('+')) left = new BinaryExpr('+', left, ParseTerm());
                else if (Eat('-')) left = new BinaryExpr('-', left, ParseTerm());
                else return left;
            }
        }

        private Expr ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Eat('*')) left = new BinaryExpr('*', left, ParseUnary());
                else if (Eat('/')) left = new BinaryExpr('/', left, ParseUnary());
                else return left;
            }
        }

        private Expr ParseUnary() => Eat('-') ? new NegateExpr(ParseUnary()) : ParsePrimary();

        private Expr ParsePrimary()
        {
            Skip();
            if (_pos >= text.Length) throw new FormulaException("Unexpected end of formula.");
            var c = text[_pos];

            if (Eat('('))
            {
                var inner = ParseExpr();
                if (!Eat(')')) throw new FormulaException("Missing ')'.");
                return inner;
            }

            if (c == '$')
            {
                _pos++;
                var start = _pos;
                while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
                if (start == _pos) throw new FormulaException("'$' must be followed by a reference number.");
                return new RefExpr(int.Parse(text[start.._pos], CultureInfo.InvariantCulture));
            }

            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.')) _pos++;
                if (!decimal.TryParse(text[start.._pos], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                    throw new FormulaException($"Invalid number '{text[start.._pos]}'.");
                return new NumberExpr(v);
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_')) _pos++;
                var name = text[start.._pos];

                if (!Eat('(')) return new ColumnExpr(name);

                if (!Functions.Contains(name)) throw new FormulaException($"Unknown function '{name}'.");
                var args = new List<Expr>();
                if (!Eat(')'))
                {
                    do args.Add(ParseExpr()); while (Eat(','));
                    if (!Eat(')')) throw new FormulaException($"Missing ')' after the arguments of {name}().");
                }
                return new CallExpr(name, args);
            }

            throw new FormulaException($"Unexpected '{c}' at position {_pos + 1}.");
        }
    }
}
