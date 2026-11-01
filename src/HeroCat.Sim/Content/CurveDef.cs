using System;

namespace HeroCat.Sim.Content
{
    /// <summary>
    /// <c>Base * Growth^(x + XOffset)</c> (09 sections 5.2 and 9.2), the GeometricTable 03 refers to. Powers come from
    /// repeated multiplication only, never Math.Pow: 00 section 3 requires bit-identical results on x64, ARM64 and IL2CPP.
    ///
    /// The power table is precomputed at build to <c>max(STAGE_CAP + BOSS_EVERY, hero_levels.level_cap) + 1</c> entries
    /// and is immutable afterwards, so one ContentDb is safe to share across threads (bot seeds) without pre-warming.
    /// An index past the table continues the same multiplication chain from its last entry, which yields the same bits a
    /// longer table would. This replaces 09 section 9.2's lazily grown cache (issue #125 note, decided there).
    /// </summary>
    public sealed class CurveDef
    {
        readonly double[] _pow;   // _pow[k] = Growth^k

        public CurveDef(string id, double baseValue, double growth, int xOffset, int precomputeCount)
        {
            Id = id;
            Base = baseValue;
            Growth = growth;
            XOffset = xOffset;
            _pow = new double[Math.Max(1, precomputeCount)];
            _pow[0] = 1.0;
            for (int i = 1; i < _pow.Length; i++) _pow[i] = _pow[i - 1] * growth;
        }

        public string Id { get; }
        public double Base { get; }
        public double Growth { get; }
        public int XOffset { get; }

        /// <summary>Entries held in the precomputed table (diagnostics, tests).</summary>
        public int PrecomputedCount => _pow.Length;

        /// <summary>Growth^k by repeated multiplication. k &lt; 0 is a programmer error.</summary>
        public double Power(int k)
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k));
            if (k < _pow.Length) return _pow[k];
            double v = _pow[_pow.Length - 1];
            for (int i = _pow.Length; i <= k; i++) v *= Growth;
            return v;
        }

        public double Eval(int x) => Base * Power(x + XOffset);

        /// <summary>Sum of Eval(x .. x+m-1) by the closed form over the cached powers; bulk Training cost (03 section 5.1).</summary>
        public double EvalSum(int x, int m)
        {
            if (m <= 0) return 0.0;
            int k = x + XOffset;
            return Base * (Power(k + m) - Power(k)) / (Growth - 1.0);
        }

        /// <summary>Largest m &gt;= 0 with EvalSum(x, m) &lt;= budget. No logarithm: doubling, then bisection over EvalSum.</summary>
        public int MaxAffordable(int x, double budget)
        {
            if (!(budget >= EvalSum(x, 1))) return 0;
            int lo = 1, hi = 2;
            while (hi < int.MaxValue / 2 && EvalSum(x, hi) <= budget)
            {
                lo = hi;
                hi *= 2;
            }
            while (hi - lo > 1)
            {
                int mid = lo + (hi - lo) / 2;
                if (EvalSum(x, mid) <= budget) lo = mid;
                else hi = mid;
            }
            return lo;
        }
    }
}
