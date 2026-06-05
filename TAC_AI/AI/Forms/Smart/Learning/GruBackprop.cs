namespace TAC_AI.AI.Forms.Smart.Learning
{
    /// <summary>
    /// P8 Item 19: backpropagation-through-time for the single-layer GRU used by
    /// <see cref="OpponentIntentClassifier"/>. Allocation-free at training time
    /// (caller supplies the per-timestep cache + the flat gradient accumulator).
    ///
    /// GRU equations (matching <c>OpponentIntentClassifier.GruStep</c>):
    ///   a_r = W_r @ x + U_r @ h_prev + b_r
    ///   a_z = W_z @ x + U_z @ h_prev + b_z
    ///   r   = sigmoid(a_r)
    ///   z   = sigmoid(a_z)
    ///   a_h = W_h @ x + U_h @ (r * h_prev) + b_h
    ///   h_tilde = tanh(a_h)
    ///   h_new   = (1 - z) * h_prev + z * h_tilde
    ///
    /// Parameter slot layout (matches OpponentIntentClassifier.cs:47-58):
    ///   W_g [Hidden × FeatureDim], U_g [Hidden × Hidden], b_g [Hidden] for g ∈ {r, z, h}.
    ///   All row-major. <see cref="GateOffsets"/> carries the 9 offsets so this class
    ///   stays unaware of the dense-head layout.
    /// </summary>
    internal static class GruBackprop
    {
        /// <summary>
        /// Per-timestep cached activations needed by Backward. Allocated once by the
        /// caller; reused across minibatch events to avoid per-event GC.
        /// </summary>
        internal struct CellState
        {
            internal float[] R;       // gate values r_t      length=Hidden
            internal float[] Z;       // gate values z_t      length=Hidden
            internal float[] HTilde;  // candidate h_tilde_t  length=Hidden
            internal float[] H;       // hidden state AFTER step t (h_t)   length=Hidden
        }

        internal struct GateOffsets
        {
            internal int Wr, Ur, Br, Wz, Uz, Bz, Wh, Uh, Bh;
        }

        /// <summary>
        /// Allocate a fresh cache for a given seqLen + hidden. Called once per event by
        /// the classifier; reuse by minibatch is preferable when the classifier holds it.
        /// </summary>
        internal static CellState[] AllocateCache(int seqLen, int hidden)
        {
            var c = new CellState[seqLen];
            for (int t = 0; t < seqLen; t++)
            {
                c[t].R      = new float[hidden];
                c[t].Z      = new float[hidden];
                c[t].HTilde = new float[hidden];
                c[t].H      = new float[hidden];
            }
            return c;
        }

        /// <summary>
        /// Run the GRU forward pass over the sequence, populating <paramref name="cache"/>
        /// with per-timestep gate + hidden values. The hidden state across the sequence is
        /// initialized to zero (matches the classifier's <c>var h = new float[Hidden]</c>
        /// at Evaluate / DenseHeadStep).
        /// </summary>
        internal static void Forward(float[] paramsArr, float[] sequenceRowMajor,
            int seqLen, int featureDim, int hidden,
            GateOffsets offsets, CellState[] cache)
        {
            // Working h vector, evolved in place across timesteps.
            var h = new float[hidden];

            for (int t = 0; t < seqLen; t++)
            {
                int xOff = t * featureDim;
                var rArr = cache[t].R;
                var zArr = cache[t].Z;
                var hTildeArr = cache[t].HTilde;
                var hOut = cache[t].H;

                // a_r, a_z then activate.
                for (int i = 0; i < hidden; i++)
                {
                    float sumR = paramsArr[offsets.Br + i];
                    float sumZ = paramsArr[offsets.Bz + i];
                    int rowF = i * featureDim;
                    int rowH = i * hidden;
                    for (int j = 0; j < featureDim; j++)
                    {
                        float xj = sequenceRowMajor[xOff + j];
                        sumR += paramsArr[offsets.Wr + rowF + j] * xj;
                        sumZ += paramsArr[offsets.Wz + rowF + j] * xj;
                    }
                    for (int j = 0; j < hidden; j++)
                    {
                        sumR += paramsArr[offsets.Ur + rowH + j] * h[j];
                        sumZ += paramsArr[offsets.Uz + rowH + j] * h[j];
                    }
                    rArr[i] = MlpUtil.Sigmoid(sumR);
                    zArr[i] = MlpUtil.Sigmoid(sumZ);
                }
                // a_h, then tanh.
                for (int i = 0; i < hidden; i++)
                {
                    float sumH = paramsArr[offsets.Bh + i];
                    int rowF = i * featureDim;
                    int rowH = i * hidden;
                    for (int j = 0; j < featureDim; j++)
                        sumH += paramsArr[offsets.Wh + rowF + j] * sequenceRowMajor[xOff + j];
                    for (int j = 0; j < hidden; j++)
                        sumH += paramsArr[offsets.Uh + rowH + j] * (rArr[j] * h[j]);
                    hTildeArr[i] = MlpUtil.TanhF(sumH);
                }
                // h_new = (1 - z) * h_prev + z * h_tilde — and store as the cached H for this step.
                for (int i = 0; i < hidden; i++)
                {
                    float newH = (1f - zArr[i]) * h[i] + zArr[i] * hTildeArr[i];
                    hOut[i] = newH;
                    h[i] = newH;
                }
            }
        }

        /// <summary>
        /// Backpropagate <paramref name="dL_dh_final"/> (gradient on the FINAL hidden state)
        /// through the cached sequence. Accumulates additive gradients into
        /// <paramref name="gradAccum"/> at the same slots as <paramref name="offsets"/>.
        /// Does NOT touch dense-head slots (W_o/b_o) — those are accumulated separately
        /// by the caller before calling Backward.
        /// </summary>
        internal static void Backward(float[] paramsArr, float[] sequenceRowMajor,
            int seqLen, int featureDim, int hidden,
            GateOffsets offsets, CellState[] cache,
            float[] dL_dh_final, float[] gradAccum)
        {
            // dh — gradient on the hidden state at the END of step t. Initialized from
            // dL/dh_final at step T-1, then propagated backward to step 0.
            var dh = new float[hidden];
            for (int i = 0; i < hidden; i++) dh[i] = dL_dh_final[i];

            // Scratch vectors (allocated once per Backward call; cheap).
            var dz   = new float[hidden];
            var dhTilde = new float[hidden];
            var daz  = new float[hidden];
            var dah  = new float[hidden];
            var dar  = new float[hidden];
            var dRTimesHprev = new float[hidden];
            var drArr = new float[hidden];
            var dhPrev = new float[hidden];

            for (int t = seqLen - 1; t >= 0; t--)
            {
                int xOff = t * featureDim;
                var rArr = cache[t].R;
                var zArr = cache[t].Z;
                var hTildeArr = cache[t].HTilde;
                // h_prev for step t = h AFTER step t-1, or zero at step 0.
                float[] hPrev = (t == 0) ? null : cache[t - 1].H;

                // Update-formula chain: h_t = (1-z) * h_prev + z * h_tilde
                //   dh_tilde = dh * z
                //   dz       = dh * (h_tilde - h_prev)
                //   dh_prev_from_update = dh * (1 - z)
                // Pre-activation chain through tanh/sigmoid:
                //   dah  = dh_tilde * (1 - h_tilde^2)
                //   daz  = dz * z * (1 - z)
                for (int i = 0; i < hidden; i++)
                {
                    float hp = hPrev != null ? hPrev[i] : 0f;
                    dhTilde[i] = dh[i] * zArr[i];
                    dz[i]      = dh[i] * (hTildeArr[i] - hp);
                    dhPrev[i]  = dh[i] * (1f - zArr[i]);     // partial; will accumulate more below
                    dah[i]     = dhTilde[i] * (1f - hTildeArr[i] * hTildeArr[i]);
                    daz[i]     = dz[i] * zArr[i] * (1f - zArr[i]);
                }

                // Accumulate W_h / U_h / b_h gradients + d(r * h_prev) + dh_prev_from_h_tilde_branch.
                // a_h = W_h @ x + U_h @ (r * h_prev) + b_h
                // dW_h[i,j] += dah[i] * x[j]
                // dU_h[i,j] += dah[i] * (r[j] * h_prev[j])
                // db_h[i]   += dah[i]
                // d(r*h_prev)[j] = sum_i U_h[i,j] * dah[i]
                // From which: dr[j] += d(r*h_prev)[j] * h_prev[j]; dh_prev_from_h_tilde[j] += d(r*h_prev)[j] * r[j]
                for (int j = 0; j < hidden; j++) dRTimesHprev[j] = 0f;
                for (int i = 0; i < hidden; i++)
                {
                    int rowF = i * featureDim;
                    int rowH = i * hidden;
                    float dahi = dah[i];
                    gradAccum[offsets.Bh + i] += dahi;
                    for (int j = 0; j < featureDim; j++)
                        gradAccum[offsets.Wh + rowF + j] += dahi * sequenceRowMajor[xOff + j];
                    for (int j = 0; j < hidden; j++)
                    {
                        float hp = hPrev != null ? hPrev[j] : 0f;
                        gradAccum[offsets.Uh + rowH + j] += dahi * (rArr[j] * hp);
                        dRTimesHprev[j] += paramsArr[offsets.Uh + rowH + j] * dahi;
                    }
                }
                for (int j = 0; j < hidden; j++)
                {
                    float hp = hPrev != null ? hPrev[j] : 0f;
                    drArr[j] = dRTimesHprev[j] * hp;
                    dhPrev[j] += dRTimesHprev[j] * rArr[j];
                }

                // dar = dr * r * (1 - r) — pre-activation gradient for the r gate.
                for (int i = 0; i < hidden; i++)
                    dar[i] = drArr[i] * rArr[i] * (1f - rArr[i]);

                // Accumulate W_r / U_r / b_r gradients + dh_prev_from_r_branch.
                for (int i = 0; i < hidden; i++)
                {
                    int rowF = i * featureDim;
                    int rowH = i * hidden;
                    float dari = dar[i];
                    gradAccum[offsets.Br + i] += dari;
                    for (int j = 0; j < featureDim; j++)
                        gradAccum[offsets.Wr + rowF + j] += dari * sequenceRowMajor[xOff + j];
                    for (int j = 0; j < hidden; j++)
                    {
                        float hp = hPrev != null ? hPrev[j] : 0f;
                        gradAccum[offsets.Ur + rowH + j] += dari * hp;
                        dhPrev[j] += paramsArr[offsets.Ur + rowH + j] * dari;
                    }
                }

                // Accumulate W_z / U_z / b_z gradients + dh_prev_from_z_branch.
                for (int i = 0; i < hidden; i++)
                {
                    int rowF = i * featureDim;
                    int rowH = i * hidden;
                    float dazi = daz[i];
                    gradAccum[offsets.Bz + i] += dazi;
                    for (int j = 0; j < featureDim; j++)
                        gradAccum[offsets.Wz + rowF + j] += dazi * sequenceRowMajor[xOff + j];
                    for (int j = 0; j < hidden; j++)
                    {
                        float hp = hPrev != null ? hPrev[j] : 0f;
                        gradAccum[offsets.Uz + rowH + j] += dazi * hp;
                        dhPrev[j] += paramsArr[offsets.Uz + rowH + j] * dazi;
                    }
                }

                // Roll dh for next step (t-1). dh_prev now holds total gradient on h_{t-1}.
                for (int i = 0; i < hidden; i++) dh[i] = dhPrev[i];
            }
        }
    }
}
