using HeroCat.Sim.Content;
using HeroCat.Sim.State;

namespace HeroCat.Client.Core
{
    /// <summary>
    /// The shared Locked presentation (07 section 3 and section 10 rule 8; issue #180): a locked element is shown, never
    /// hidden, greyed with a lock glyph and its unlock condition text, and a tap toasts that condition instead of acting.
    /// Any screen element can own one: the view binds <see cref="Locked"/> (grey + glyph) and <see cref="Condition"/>, and
    /// routes its tap through <see cref="Tap"/>. Conditions come from the 09 <c>feature_unlocks</c> row (the same one the
    /// sim's rejections read, issue #45) or, for a gate with no row, from the caller.
    /// </summary>
    public sealed class LockedVm
    {
        readonly VmContext _c;

        public LockedVm(VmContext c)
        {
            _c = c;
            Condition = c.Label();
        }

        /// <summary>Greyed content and the lock glyph.</summary>
        public readonly Flag Locked = new Flag();

        /// <summary>The unlock condition ("Unlocks at stage 30"); kept while unlocked, shown only while <see cref="Locked"/>.</summary>
        public readonly TextKeyLabel Condition;

        public bool IsLocked => Locked.Value;

        /// <summary>
        /// The <c>feature_unlocks</c> row's <c>locked_text_key</c>, or <see cref="UiKeys.ComingSoon"/> when the feature has no
        /// row in this content tier (an MVP feature in an M0 build) or the row names no text.
        /// </summary>
        public static string ConditionKey(ContentDb db, string feature)
        {
            if (feature != null && db?.FeatureUnlocks != null && db.FeatureUnlocks.TryGetValue(feature, out var f) &&
                !string.IsNullOrEmpty(f.LockedTextKey))
                return f.LockedTextKey;
            return UiKeys.ComingSoon;
        }

        /// <summary>Locked while <c>ContentDb.IsUnlocked(feature, state)</c> is false (a feature with no row is locked).</summary>
        public bool RefreshFeature(string feature, GameState state) =>
            Set(!_c.Db.IsUnlocked(feature, state), ConditionKey(_c.Db, feature));

        /// <summary>A gate with no <c>feature_unlocks</c> row: the caller decides and names the condition key.</summary>
        public bool Set(bool locked, string conditionKey)
        {
            bool a = Locked.Set(locked);
            bool b = Condition.Set(conditionKey);
            return a | b;
        }

        public bool Set(bool locked, string conditionKey, LabelArg arg)
        {
            bool a = Locked.Set(locked);
            bool b = Condition.Set(conditionKey, arg);
            return a | b;
        }

        /// <summary>
        /// Routes a tap: true when unlocked (the caller acts); false after toasting the unlock condition (07 section 4.3:
        /// "a lock glyph and a toast naming the condition"). No command is sent while locked.
        /// </summary>
        public bool Tap()
        {
            if (!Locked.Value) return true;
            _c.Toasts?.Push(Condition.Text, ToastStyle.Reject);
            return false;
        }
    }
}
