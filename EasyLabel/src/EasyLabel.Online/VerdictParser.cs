using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyLabel.Online
{
    public sealed class VerdictParseException : FormatException
    {
        public VerdictParseException(int position, string message)
            : base("Verdict at offset " + position.ToString(CultureInfo.InvariantCulture) + ": " + message) { Position = position; }
        public int Position { get; private set; }
    }

    internal sealed class ClassStatistics
    {
        internal int Count;
        internal double Area;
    }

    internal abstract class RuleNode
    {
        internal abstract bool Evaluate(Dictionary<string, ClassStatistics> values);
    }

    /// <summary>
    /// expression := conjunction ('or' conjunction)*; conjunction := unary ('and' unary)*;
    /// unary := 'not' unary | '(' expression ')' | 'any' '(' class ')'
    ///        | ('count' | 'area') '(' class ')' ('=='|'!='|'&lt;'|'&lt;='|'&gt;'|'&gt;=') number.
    /// Class identifiers are case-sensitive; numbers are nonnegative invariant decimals.
    /// </summary>
    public static class VerdictParser
    {
        public static void Validate(string expression, IEnumerable<string> classNames) { Parse(expression, classNames); }

        internal static RuleNode Parse(string expression, IEnumerable<string> classNames)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 4096)
                throw new VerdictParseException(0, "Expected an expression of 1..4096 characters.");
            if (classNames == null) throw new ArgumentNullException(nameof(classNames));
            return new Parser(expression, new HashSet<string>(classNames, StringComparer.Ordinal)).Parse();
        }

        private sealed class BooleanNode : RuleNode
        {
            private readonly string operation;
            private readonly RuleNode left;
            private readonly RuleNode right;
            internal BooleanNode(string operation, RuleNode left, RuleNode right) { this.operation = operation; this.left = left; this.right = right; }
            internal override bool Evaluate(Dictionary<string, ClassStatistics> values)
            {
                if (operation == "not") return !left.Evaluate(values);
                if (operation == "and") return left.Evaluate(values) && right.Evaluate(values);
                return left.Evaluate(values) || right.Evaluate(values);
            }
        }

        private sealed class PredicateNode : RuleNode
        {
            private readonly string kind;
            private readonly string name;
            private readonly string comparison;
            private readonly double limit;
            internal PredicateNode(string kind, string name, string comparison, double limit)
            { this.kind = kind; this.name = name; this.comparison = comparison; this.limit = limit; }
            internal override bool Evaluate(Dictionary<string, ClassStatistics> values)
            {
                ClassStatistics stats = values[name];
                if (kind == "any") return stats.Count != 0;
                double value = kind == "count" ? stats.Count : stats.Area;
                switch (comparison)
                {
                    case "==": return value == limit;
                    case "!=": return value != limit;
                    case "<": return value < limit;
                    case "<=": return value <= limit;
                    case ">": return value > limit;
                    case ">=": return value >= limit;
                    default: throw new InvalidOperationException("Unknown parsed comparison.");
                }
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private readonly HashSet<string> names;
            private int offset;
            private int tokenOffset;
            private string token;
            private int depth;
            internal Parser(string text, HashSet<string> names) { this.text = text; this.names = names; Next(); }
            internal RuleNode Parse()
            {
                RuleNode node = Or();
                if (token.Length != 0) Fail("Unexpected token '" + token + "'.");
                return node;
            }
            private RuleNode Or()
            {
                RuleNode node = And();
                while (Take("or")) node = new BooleanNode("or", node, And());
                return node;
            }
            private RuleNode And()
            {
                RuleNode node = Unary();
                while (Take("and")) node = new BooleanNode("and", node, Unary());
                return node;
            }
            private RuleNode Unary()
            {
                if (++depth > 64) Fail("Nesting exceeds 64 levels.");
                RuleNode result;
                if (Take("not")) result = new BooleanNode("not", Unary(), null);
                else if (Take("(")) { result = Or(); Expect(")"); }
                else result = Predicate();
                depth--;
                return result;
            }
            private RuleNode Predicate()
            {
                string kind = token;
                if (kind != "any" && kind != "count" && kind != "area") Fail("Expected any(class), count(class) or area(class).");
                Next();
                Expect("(");
                string name = token;
                if (!names.Contains(name)) Fail("Unknown class '" + name + "'.");
                Next();
                Expect(")");
                if (kind == "any") return new PredicateNode(kind, name, null, 0);
                string comparison = token;
                if (comparison != "==" && comparison != "!=" && comparison != "<" && comparison != "<=" && comparison != ">" && comparison != ">=")
                    Fail("Expected comparison ==, !=, <, <=, > or >=.");
                Next();
                double number;
                if (!double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)
                    || !RecipeValidation.Range(number, 0, 1e12)) Fail("Expected a nonnegative decimal number <= 1e12.");
                if (kind == "count" && (number != Math.Floor(number) || number > 1000000)) Fail("Count comparison requires an integer 0..1000000.");
                Next();
                return new PredicateNode(kind, name, comparison, number);
            }
            private bool Take(string expected)
            {
                if (token != expected) return false;
                Next(); return true;
            }
            private void Expect(string expected) { if (!Take(expected)) Fail("Expected '" + expected + "'."); }
            private void Fail(string message) { throw new VerdictParseException(tokenOffset, message); }
            private void Next()
            {
                while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++;
                tokenOffset = offset;
                if (offset == text.Length) { token = string.Empty; return; }
                char c = text[offset++];
                if (IsLetter(c))
                {
                    while (offset < text.Length && (IsLetter(text[offset]) || IsDigit(text[offset]))) offset++;
                }
                else if (IsDigit(c))
                {
                    while (offset < text.Length && IsDigit(text[offset])) offset++;
                    if (offset < text.Length && text[offset] == '.')
                    {
                        offset++;
                        int start = offset;
                        while (offset < text.Length && IsDigit(text[offset])) offset++;
                        if (offset == start) Fail("A decimal point must be followed by digits.");
                    }
                }
                else if (c == '<' || c == '>' || c == '=' || c == '!')
                {
                    if (offset < text.Length && text[offset] == '=') offset++;
                }
                else if (c != '(' && c != ')') Fail("Unsupported character '" + c + "'.");
                token = text.Substring(tokenOffset, offset - tokenOffset);
            }
            private static bool IsLetter(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_'; }
            private static bool IsDigit(char c) { return c >= '0' && c <= '9'; }
        }
    }
}
