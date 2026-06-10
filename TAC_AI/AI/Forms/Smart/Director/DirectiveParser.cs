using System;
using System.Collections.Generic;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    public struct ParseResult
    {
        public bool Ok;
        public string Error;
        public Directive Directive;

        public static ParseResult Fail(string err)
        {
            return new ParseResult { Ok = false, Error = err, Directive = default(Directive) };
        }
    }

    // Hand-written recursive-descent over the verb-first grammar. No regex/Sprache/Antlr.
    // Parse(...) catches all internal exceptions and returns Ok=false — no exception
    // propagation past the call boundary.
    public static class DirectiveParser
    {
        // _nextId supplied by DirectiveSurface; parser produces the directive with id=0,
        // surface stamps the real id on accept. Keeps the parser stateless.
        public static ParseResult Parse(string line)
        {
            try { return ParseInner(line); }
            catch (Exception ex)
            {
                return ParseResult.Fail("internal:" + ex.GetType().Name);
            }
        }

        private static ParseResult ParseInner(string line)
        {
            var tok = DirectiveTokenizer.Tokenize(line);
            if (!tok.Ok) return ParseResult.Fail(tok.Error);
            var t = tok.Tokens;
            int p = 0;

            if (t[p].Kind != TokenKind.Ident)
                return ParseResult.Fail("expected-verb");
            string verb = t[p].Text.ToLowerInvariant();
            p++;

            switch (verb)
            {
                case "maintain": return ParseMaintain(t, p);
                case "prefer": return ParsePreferForce(t, p, DirectiveVerb.Prefer);
                case "force": return ParsePreferForce(t, p, DirectiveVerb.Force);
                case "freeze": return ParseFreeze(t, p);
                case "lr_scale": return ParseLrScale(t, p);
                case "replay_bank": return ParseReplayBank(t, p);
                case "rollback": return ParseRollback(t, p);
                case "checkpoint": return ParseCheckpoint(t, p);
                case "forbid": return ParseForbid(t, p);
                case "rescind": return ParseRescind(t, p);
                case "clear": return ParseClear(t, p);
                case "digest": return ParseBare(t, p, DirectiveVerb.Digest);
                case "status": return ParseBare(t, p, DirectiveVerb.Status);
                case "calibrate": return ParseCalibrate(t, p);
            }
            return ParseResult.Fail("unknown-verb:" + verb);
        }

        // maintain <subject> (op value | in [lo, hi]) [for <dur>] [tag <name>]
        private static ParseResult ParseMaintain(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-subject");
            string subject = t[p].Text;
            var sub = SubjectResolver.Resolve(subject);
            if (!sub.Ok)
                return ParseResult.Fail("unknown-subject:" + subject + " legal=[" + SubjectResolver.LegalSubjects() + "]");
            p++;

            DirectiveOp op = DirectiveOp.None;
            DirectiveValue val;

            // in [lo, hi]
            if (t[p].Kind == TokenKind.Ident && t[p].Text == "in")
            {
                p++;
                if (t[p].Kind != TokenKind.LBracket) return ParseResult.Fail("expected-[");
                p++;
                float lo;
                if (!ExpectNumber(t, ref p, out lo)) return ParseResult.Fail("expected-interval-lo");
                if (t[p].Kind != TokenKind.Comma) return ParseResult.Fail("expected-comma");
                p++;
                float hi;
                if (!ExpectNumber(t, ref p, out hi)) return ParseResult.Fail("expected-interval-hi");
                if (t[p].Kind != TokenKind.RBracket) return ParseResult.Fail("expected-]");
                p++;
                op = DirectiveOp.In;
                val = DirectiveValue.OfInterval(lo, hi);
            }
            else if (t[p].Kind == TokenKind.Op)
            {
                op = MapOp(t[p].Text);
                p++;
                // value can be a rate or a number
                if (t[p].Kind == TokenKind.Rate)
                {
                    val = DirectiveValue.OfRate(t[p].Number, "min");
                    p++;
                }
                else if (t[p].Kind == TokenKind.Number)
                {
                    val = DirectiveValue.OfScalar(t[p].Number);
                    p++;
                }
                else return ParseResult.Fail("expected-value");
            }
            else return ParseResult.Fail("expected-op-or-in");

            return FinishWithLifetime(t, p, DirectiveVerb.Maintain, subject, op, val);
        }

        // prefer scenario <id> weight <w> ; force scenario <id> intensity <i> [for <dur>]
        private static ParseResult ParsePreferForce(List<Token> t, int p, DirectiveVerb verb)
        {
            if (t[p].Kind != TokenKind.Ident || t[p].Text != "scenario")
                return ParseResult.Fail("expected-scenario");
            p++;
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-scenario-id");
            string scn = t[p].Text;
            p++;
            float amt = 1f;
            if (t[p].Kind == TokenKind.Ident && (t[p].Text == "weight" || t[p].Text == "intensity"))
            {
                p++;
                if (!ExpectNumber(t, ref p, out amt)) return ParseResult.Fail("expected-amount");
            }
            var val = DirectiveValue.OfScalar(amt);
            return FinishWithLifetime(t, p, verb, "scenario." + scn, DirectiveOp.None, val);
        }

        // freeze <model> for <dur>
        private static ParseResult ParseFreeze(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-model");
            string model = t[p].Text;
            p++;
            return FinishWithLifetime(t, p, DirectiveVerb.Freeze, model, DirectiveOp.None, DirectiveValue.None_);
        }

        // lr_scale <model> <factor> [until rescinded]
        private static ParseResult ParseLrScale(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-model");
            string model = t[p].Text;
            p++;
            float factor;
            if (!ExpectNumber(t, ref p, out factor)) return ParseResult.Fail("expected-factor");
            return FinishWithLifetime(t, p, DirectiveVerb.LrScale, model, DirectiveOp.None,
                DirectiveValue.OfScalar(factor));
        }

        // replay_bank <identity> <count>
        private static ParseResult ParseReplayBank(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-identity");
            string identity = t[p].Text;
            p++;
            float count;
            if (!ExpectNumber(t, ref p, out count)) return ParseResult.Fail("expected-count");
            return FinishWithLifetime(t, p, DirectiveVerb.ReplayBank, identity, DirectiveOp.None,
                DirectiveValue.OfScalar(count));
        }

        // rollback to previous | rollback memory <model> | rollback <tier> [label]
        private static ParseResult ParseRollback(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-tier");
            string first = t[p].Text;
            p++;
            string tier = first;
            string label = null;
            if (first == "to")
            {
                if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-tier");
                tier = t[p].Text;
                p++;
            }
            if (t[p].Kind == TokenKind.Ident) { label = t[p].Text; p++; }
            var val = label != null ? DirectiveValue.OfLabel(label) : DirectiveValue.None_;
            return FinishWithLifetime(t, p, DirectiveVerb.Rollback, tier, DirectiveOp.None, val);
        }

        private static ParseResult ParseCheckpoint(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-name");
            string name = t[p].Text;
            p++;
            return FinishWithLifetime(t, p, DirectiveVerb.Checkpoint, name, DirectiveOp.None,
                DirectiveValue.OfLabel(name));
        }

        // forbid scenario <id> until <metric op value>
        private static ParseResult ParseForbid(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident || t[p].Text != "scenario")
                return ParseResult.Fail("expected-scenario");
            p++;
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-scenario-id");
            string scn = t[p].Text;
            p++;
            return FinishWithLifetime(t, p, DirectiveVerb.Forbid, "scenario." + scn, DirectiveOp.None,
                DirectiveValue.None_);
        }

        // rescind <id-or-tag>
        private static ParseResult ParseRescind(List<Token> t, int p)
        {
            if (t[p].Kind == TokenKind.Ident && t[p].Text == "tag")
            {
                p++;
                if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-tag");
                string tag = t[p].Text;
                p++;
                return MakeBare(DirectiveVerb.Rescind, "tag", DirectiveValue.OfLabel(tag));
            }
            if (t[p].Kind != TokenKind.Ident && t[p].Kind != TokenKind.Number)
                return ParseResult.Fail("expected-id-or-tag");
            string target = t[p].Text;
            p++;
            return MakeBare(DirectiveVerb.Rescind, "id", DirectiveValue.OfLabel(target));
        }

        // clear directives
        private static ParseResult ParseClear(List<Token> t, int p)
        {
            if (t[p].Kind == TokenKind.Ident && t[p].Text == "directives") p++;
            return MakeBare(DirectiveVerb.Clear, "directives", DirectiveValue.None_);
        }

        // digest now | status
        private static ParseResult ParseBare(List<Token> t, int p, DirectiveVerb verb)
        {
            string sub = (t[p].Kind == TokenKind.Ident) ? t[p].Text : "";
            return MakeBare(verb, sub, DirectiveValue.None_);
        }

        // calibrate <constraintName> for <dur>
        private static ParseResult ParseCalibrate(List<Token> t, int p)
        {
            if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-constraint");
            string cname = t[p].Text;
            p++;
            return FinishWithLifetime(t, p, DirectiveVerb.Calibrate, cname, DirectiveOp.None,
                DirectiveValue.None_);
        }

        // ---- lifetime + tag suffix common tail ----
        private static ParseResult FinishWithLifetime(List<Token> t, int p, DirectiveVerb verb,
            string subject, DirectiveOp op, DirectiveValue val)
        {
            long expiresMono = 0L;
            string untilExpr = null;
            string tag = null;

            // [for <dur> | until <expr>]
            if (t[p].Kind == TokenKind.Ident && t[p].Text == "for")
            {
                p++;
                if (t[p].Kind != TokenKind.Duration) return ParseResult.Fail("expected-duration");
                expiresMono = MonoClock.Now() + MonoClock.FromSeconds(t[p].DurationSec);
                p++;
            }
            else if (t[p].Kind == TokenKind.Ident && t[p].Text == "until")
            {
                p++;
                // until <constraint-expr> or until rescinded
                if (t[p].Kind == TokenKind.Ident && t[p].Text == "rescinded")
                {
                    p++; // sticky semantics — leave expiresMono 0 / untilExpr null
                }
                else
                {
                    // capture remaining tokens as the until-expr text
                    var sb = new System.Text.StringBuilder();
                    while (t[p].Kind != TokenKind.End && !(t[p].Kind == TokenKind.Ident && t[p].Text == "tag"))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(t[p].Text);
                        p++;
                    }
                    untilExpr = sb.ToString();
                }
            }

            // [tag <name>]
            if (t[p].Kind == TokenKind.Ident && t[p].Text == "tag")
            {
                p++;
                if (t[p].Kind != TokenKind.Ident) return ParseResult.Fail("expected-tag-name");
                tag = t[p].Text;
                p++;
            }

            if (t[p].Kind != TokenKind.End) return ParseResult.Fail("trailing-tokens:" + t[p].Text);

            var d = new Directive(0, tag, verb, subject, op, val, MonoClock.Now(), expiresMono,
                untilExpr, DirectiveSource.Op, null);
            return new ParseResult { Ok = true, Error = null, Directive = d };
        }

        private static ParseResult MakeBare(DirectiveVerb verb, string subject, DirectiveValue val)
        {
            var d = new Directive(0, null, verb, subject, DirectiveOp.None, val, MonoClock.Now(),
                0L, null, DirectiveSource.Op, null);
            return new ParseResult { Ok = true, Error = null, Directive = d };
        }

        private static bool ExpectNumber(List<Token> t, ref int p, out float v)
        {
            v = 0f;
            if (t[p].Kind == TokenKind.Number) { v = t[p].Number; p++; return true; }
            if (t[p].Kind == TokenKind.Rate) { v = t[p].Number; p++; return true; }
            return false;
        }

        private static DirectiveOp MapOp(string s)
        {
            switch (s)
            {
                case "<": return DirectiveOp.Lt;
                case ">": return DirectiveOp.Gt;
                case "<=": return DirectiveOp.Le;
                case ">=": return DirectiveOp.Ge;
                case "=": return DirectiveOp.Eq;
            }
            return DirectiveOp.None;
        }
    }
}
