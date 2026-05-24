using UnityEngine;

namespace TAC_AI
{
    public struct AITimer
    {
        private float deadline;

        public void Set(float seconds) { deadline = Time.time + seconds; }

        public bool Due => Time.time >= deadline;

        public bool Running => Time.time < deadline;

        public float Remaining => deadline > Time.time ? deadline - Time.time : 0f;

        public float Fraction(float duration) => duration <= 0f ? 1f : Mathf.Clamp01(1f - Remaining / duration);

        public void Clear() { deadline = 0f; }
    }

    public struct AIAccumulator
    {
        public float Value;

        public static float PassSeconds(int clockPeriod) => clockPeriod * Time.fixedDeltaTime;

        public void AddPass(int clockPeriod) { Value += clockPeriod * Time.fixedDeltaTime; }

        public void Reset() { Value = 0f; }
    }
}
