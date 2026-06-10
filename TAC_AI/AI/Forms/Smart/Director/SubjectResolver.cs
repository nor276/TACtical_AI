using System.Collections.Generic;
using System.Text;
using TAC_AI.AI.Forms.Smart.Identity;
using TAC_AI.AI.Forms.Smart.Learning;
using TAC_AI.AI.Forms.Smart.World;

namespace TAC_AI.AI.Forms.Smart.Director
{
    public enum MetricKind : byte
    {
        IdentityRate = 0,       // identity x OutcomeKind, per-min
        ModelReservoir = 1,     // model reservoir derived (entropy ratio, loss var)
        Scalar = 2,             // system scalar (live_smart_tech_floor, damage_events)
    }

    // Resolved subject row. Maps a dotted subject token to the metric the constraint
    // expression evaluates against.
    public struct SubjectInfo
    {
        public bool Ok;
        public MetricKind Metric;
        public SmartIdentity Identity;
        public OutcomeKind Kind;
        public ModelId Model;
        public string ConstraintName;   // the constraint registry name this subject backs

        public static SubjectInfo Fail()
        {
            return new SubjectInfo { Ok = false };
        }
    }

    // Static subject table. Examples:
    //   hunter.kills_per_min     -> (IdentityRate, Hunter, KillScored)
    //   intent.entropy_ratio     -> (ModelReservoir, Intent)
    public static class SubjectResolver
    {
        private struct Row
        {
            public MetricKind Metric;
            public SmartIdentity Identity;
            public OutcomeKind Kind;
            public ModelId Model;
            public string ConstraintName;
        }

        private static readonly Dictionary<string, Row> Map = BuildMap();

        private static Dictionary<string, Row> BuildMap()
        {
            var m = new Dictionary<string, Row>();

            m["hunter.kills_per_min"] = new Row
            {
                Metric = MetricKind.IdentityRate, Identity = SmartIdentity.Hunter,
                Kind = OutcomeKind.KillScored, ConstraintName = "hunter_kills_per_min",
            };
            m["gatherer.delivery_rate"] = new Row
            {
                Metric = MetricKind.IdentityRate, Identity = SmartIdentity.Gatherer,
                Kind = OutcomeKind.BlockDelivered, ConstraintName = "gatherer_delivery_rate",
            };
            m["ally_protection_rate"] = new Row
            {
                Metric = MetricKind.IdentityRate, Identity = SmartIdentity.AircraftSupport,
                Kind = OutcomeKind.AllyProtected, ConstraintName = "ally_protected_weighted_rate",
            };
            m["base.hp_retention"] = new Row
            {
                Metric = MetricKind.IdentityRate, Identity = SmartIdentity.Base,
                Kind = OutcomeKind.BaseHeld, ConstraintName = "base_hp_retention_rate",
            };

            m["intent.entropy_ratio"] = new Row
            {
                Metric = MetricKind.ModelReservoir, Model = ModelId.Intent,
                ConstraintName = "intent_entropy_ratio",
            };
            m["actionvalue.loss_variance_ratio"] = new Row
            {
                Metric = MetricKind.ModelReservoir, Model = ModelId.ActionValue,
                ConstraintName = "actionvalue_loss_variance_ratio",
            };
            m["actionvalue_loss_variance_ratio"] = m["actionvalue.loss_variance_ratio"];
            m["threat.param_delta_l2"] = new Row
            {
                Metric = MetricKind.ModelReservoir, Model = ModelId.ThreatAssessment,
                ConstraintName = "threat_param_delta_l2_norm",
            };

            m["live_smart_tech_floor"] = new Row
            {
                Metric = MetricKind.Scalar, ConstraintName = "live_smart_tech_floor",
            };
            m["damage_events_per_min"] = new Row
            {
                Metric = MetricKind.Scalar, ConstraintName = "damage_events_per_min",
            };
            m["ttl_discard_rate"] = new Row
            {
                Metric = MetricKind.Scalar, ConstraintName = "ttl_discard_rate",
            };
            return m;
        }

        public static SubjectInfo Resolve(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return SubjectInfo.Fail();
            Row r;
            if (!Map.TryGetValue(subject, out r)) return SubjectInfo.Fail();
            return new SubjectInfo
            {
                Ok = true,
                Metric = r.Metric,
                Identity = r.Identity,
                Kind = r.Kind,
                Model = r.Model,
                ConstraintName = r.ConstraintName,
            };
        }

        // Comma-separated legal-subject list for parse-error formatting.
        public static string LegalSubjects()
        {
            var sb = new StringBuilder(256);
            bool first = true;
            foreach (var kv in Map)
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key);
                first = false;
            }
            return sb.ToString();
        }
    }
}
