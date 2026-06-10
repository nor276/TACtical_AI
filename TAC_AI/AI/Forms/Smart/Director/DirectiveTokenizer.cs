using System.Collections.Generic;
using System.Globalization;

namespace TAC_AI.AI.Forms.Smart.Director
{
    public enum TokenKind : byte
    {
        Ident = 0,        // identifiers-with-dots and bare keywords
        Number = 1,       // signed float
        Rate = 2,         // <float>/min
        Duration = 3,     // <int>{s|m|min|h} -> seconds in DurationSec
        LBracket = 4,
        RBracket = 5,
        Comma = 6,
        Op = 7,           // < > <= >= =
        End = 8,
    }

    public struct Token
    {
        public TokenKind Kind;
        public string Text;
        public float Number;     // Number / Rate numerator
        public int DurationSec;  // Duration

        public Token(TokenKind kind, string text)
        {
            Kind = kind; Text = text; Number = 0f; DurationSec = 0;
        }
    }

    // Hand-rolled lexer. Returns Ok=false + Error on the first unrecognised unit.
    public static class DirectiveTokenizer
    {
        public struct Result
        {
            public bool Ok;
            public string Error;
            public List<Token> Tokens;
        }

        public static Result Tokenize(string line)
        {
            var res = new Result { Ok = true, Error = null, Tokens = new List<Token>() };
            if (string.IsNullOrEmpty(line)) { res.Tokens.Add(new Token(TokenKind.End, "")); return res; }

            int i = 0;
            int n = line.Length;
            while (i < n)
            {
                char c = line[i];
                if (c == ' ' || c == '\t') { i++; continue; }

                if (c == '[') { res.Tokens.Add(new Token(TokenKind.LBracket, "[")); i++; continue; }
                if (c == ']') { res.Tokens.Add(new Token(TokenKind.RBracket, "]")); i++; continue; }
                if (c == ',') { res.Tokens.Add(new Token(TokenKind.Comma, ",")); i++; continue; }

                // Operators.
                if (c == '<' || c == '>' || c == '=')
                {
                    if ((c == '<' || c == '>') && i + 1 < n && line[i + 1] == '=')
                    {
                        res.Tokens.Add(new Token(TokenKind.Op, c == '<' ? "<=" : ">="));
                        i += 2;
                    }
                    else
                    {
                        res.Tokens.Add(new Token(TokenKind.Op, c.ToString()));
                        i++;
                    }
                    continue;
                }

                // Signed numbers, rates, durations. A leading +/- or digit starts a numeric run.
                if (c == '+' || c == '-' || (c >= '0' && c <= '9') || (c == '.' && i + 1 < n && line[i + 1] >= '0' && line[i + 1] <= '9'))
                {
                    int start = i;
                    if (c == '+' || c == '-') i++;
                    while (i < n && ((line[i] >= '0' && line[i] <= '9') || line[i] == '.')) i++;
                    string numStr = line.Substring(start, i - start);

                    // Rate: <float>/min
                    if (i < n && line[i] == '/')
                    {
                        int unitStart = i + 1;
                        int j = unitStart;
                        while (j < n && IsIdentChar(line[j])) j++;
                        string unit = line.Substring(unitStart, j - unitStart);
                        if (unit != "min")
                        {
                            res.Ok = false;
                            res.Error = "unrecognised-unit:/" + unit;
                            return res;
                        }
                        float rv;
                        if (!float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rv))
                        {
                            res.Ok = false;
                            res.Error = "bad-number:" + numStr;
                            return res;
                        }
                        var rt = new Token(TokenKind.Rate, numStr + "/min");
                        rt.Number = rv;
                        res.Tokens.Add(rt);
                        i = j;
                        continue;
                    }

                    // Duration: <int>{s|m|min|h} — only when the number is integer and an alpha unit follows.
                    if (i < n && IsAlpha(line[i]) && numStr.IndexOf('.') < 0)
                    {
                        int unitStart = i;
                        int j = unitStart;
                        while (j < n && IsAlpha(line[j])) j++;
                        string unit = line.Substring(unitStart, j - unitStart);
                        int intVal;
                        if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out intVal))
                        {
                            res.Ok = false;
                            res.Error = "bad-number:" + numStr;
                            return res;
                        }
                        int secs;
                        if (!DurationToSeconds(intVal, unit, out secs))
                        {
                            res.Ok = false;
                            res.Error = "unrecognised-unit:" + unit;
                            return res;
                        }
                        var dt = new Token(TokenKind.Duration, numStr + unit);
                        dt.DurationSec = secs;
                        dt.Number = intVal;
                        res.Tokens.Add(dt);
                        i = j;
                        continue;
                    }

                    float fv;
                    if (!float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out fv))
                    {
                        res.Ok = false;
                        res.Error = "bad-number:" + numStr;
                        return res;
                    }
                    var t = new Token(TokenKind.Number, numStr);
                    t.Number = fv;
                    res.Tokens.Add(t);
                    continue;
                }

                // Identifier (with dots / underscores).
                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < n && IsIdentChar(line[i])) i++;
                    res.Tokens.Add(new Token(TokenKind.Ident, line.Substring(start, i - start)));
                    continue;
                }

                res.Ok = false;
                res.Error = "unexpected-char:" + c;
                return res;
            }

            res.Tokens.Add(new Token(TokenKind.End, ""));
            return res;
        }

        private static bool DurationToSeconds(int v, string unit, out int secs)
        {
            secs = 0;
            switch (unit)
            {
                case "s": secs = v; return true;
                case "m":
                case "min": secs = v * 60; return true;
                case "h": secs = v * 3600; return true;
            }
            return false;
        }

        private static bool IsAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        private static bool IsIdentStart(char c) => IsAlpha(c) || c == '_';
        private static bool IsIdentChar(char c) => IsAlpha(c) || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
    }
}
