using System;

namespace TAC_AI.AI.Forms.Smart.Director.Guards
{
    // Bounded ring buffer specialised for guards. Three companion channels — float
    // value, bool predicate, and a Vector3-less position seed handled by the worker
    // outside this struct. Pure read-side aggregation; the worker is the single writer.
    //
    // Distinct from TrainerStats reservoirs (which are p50/p99/var oriented + sorted on
    // read). Guards care about mean / fraction-above-threshold / fraction-of-predicate
    // over a recent window — no sort needed.
    public sealed class RollingWindow
    {
        private readonly float[] _values;
        private readonly bool[] _flags;
        private readonly int _capacity;
        private int _cursor;
        private bool _wrapped;

        public RollingWindow(int capacity)
        {
            if (capacity <= 0) capacity = 1;
            _capacity = capacity;
            _values = new float[capacity];
            _flags = new bool[capacity];
        }

        public int Capacity => _capacity;
        public int Count => _wrapped ? _capacity : _cursor;

        public void Push(float value, bool predicate)
        {
            _values[_cursor] = value;
            _flags[_cursor] = predicate;
            _cursor++;
            if (_cursor >= _capacity) { _cursor = 0; _wrapped = true; }
        }

        public void Clear()
        {
            _cursor = 0;
            _wrapped = false;
            Array.Clear(_values, 0, _capacity);
            Array.Clear(_flags, 0, _capacity);
        }

        public float Mean()
        {
            int n = Count;
            if (n <= 0) return 0f;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _values[i];
            return (float)(sum / n);
        }

        public float FractionFlag()
        {
            int n = Count;
            if (n <= 0) return 0f;
            int hits = 0;
            for (int i = 0; i < n; i++) if (_flags[i]) hits++;
            return (float)hits / n;
        }

        public float FractionValueAbove(float threshold)
        {
            int n = Count;
            if (n <= 0) return 0f;
            int hits = 0;
            for (int i = 0; i < n; i++) if (_values[i] > threshold) hits++;
            return (float)hits / n;
        }

        public float FractionValueBelow(float threshold)
        {
            int n = Count;
            if (n <= 0) return 0f;
            int hits = 0;
            for (int i = 0; i < n; i++) if (_values[i] < threshold) hits++;
            return (float)hits / n;
        }

        public float Sum()
        {
            int n = Count;
            if (n <= 0) return 0f;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _values[i];
            return (float)sum;
        }
    }
}
