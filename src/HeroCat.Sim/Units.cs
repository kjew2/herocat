namespace HeroCat.Sim
{
    /// <summary>
    /// Unit conversions the sim's formulas use (03 section 7.2: percent fields are percentage points). They are not
    /// balance numbers, so they are code; tools/check_sim_literals.py allows only these named spellings (09 acceptance 1).
    /// Constants fold at compile time, so a formula written with them is bit-identical to one written with the digits.
    /// </summary>
    public static class Units
    {
        /// <summary>Percentage points per whole: <c>40 / Percent</c> is +40% as a fraction.</summary>
        public const double Percent = 100.0;   // literal-ok: percentage points to a fraction

        public const double SecondsPerMinute = 60.0;   // literal-ok: unit conversion

        public const int SecondsPerHour = 3600;   // literal-ok: unit conversion
    }
}
