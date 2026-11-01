using System;
using System.Collections.Generic;
using System.Linq;
using HeroCat.Runtime.Content;
using HeroCat.Runtime.Profile;
using HeroCat.Runtime.Save;
using HeroCat.Sim;
using HeroCat.Sim.Content;
using HeroCat.Sim.State;
using NUnit.Framework;

namespace HeroCat.Runtime.Tests
{
    /// <summary>
    /// 02 section 6.4 (issue #75), acceptance 9: one table row per repair rule P1-P21 (and P9b) asserting the repaired
    /// value and the emitted rule id, the reject rules R2-R5, the SAVE_REPAIR_MAX boundary and the left-alone list.
    /// </summary>
    public class SaveValidatorTests
    {
        static ContentDb Db => TestContent.Db;
        static Constants K => Db.Constants;
        static long Now => TestContent.T0 + 100;
        static readonly Guid Install = new Guid("1a57a11d-0000-4000-8000-000000000009");

        static SaveValidatorOptions Opt(bool remote = false) => new SaveValidatorOptions { InstallId = Install, RemoteMode = remote };

        static GameState Fresh() => TestContent.NewState();

        /// <summary>A weapon id the content defines other than the tutorial weapon, with the highest AtkPct.</summary>
        static string BestContentWeapon() =>
            Db.Weapons.Values.Where(w => w.Id != Db.TutorialWeaponId).OrderByDescending(w => w.AtkPct).ThenByDescending(w => w.Rarity)
              .First().Id;

        static string AnySkill() => Db.Skills.Keys.OrderBy(k => k, StringComparer.Ordinal).First();

        static OwnedItem Own(int copies = 1) => new OwnedItem { Copies = copies, FirstAcquiredServerTime = TestContent.T0 };

        public sealed class Case
        {
            public Case(string rule, string name, Action<GameState> mutate, Action<GameState> check)
            {
                Rule = rule; Name = name; Mutate = mutate; Check = check;
            }

            public string Rule { get; }
            public string Name { get; }
            public Action<GameState> Mutate { get; }
            public Action<GameState> Check { get; }
            public override string ToString() => Rule + " " + Name;
        }

        public static IEnumerable<Case> Cases()
        {
            // P1: the stage loop
            yield return new Case("P1", "stage below 1", s => s.Combat.Stage = 0, s => Assert.That(s.Combat.Stage, Is.EqualTo(1)));
            yield return new Case("P1", "stage above STAGE_CAP", s => { s.Combat.Stage = K.StageCap + 7; s.Progress.HighestCleared = K.StageCap - 1; },
                                  s => Assert.That(s.Combat.Stage, Is.EqualTo(K.StageCap)));
            yield return new Case("P1", "kills negative", s => s.Combat.KillsInStage = -2, s => Assert.That(s.Combat.KillsInStage, Is.EqualTo(0)));
            yield return new Case("P1", "kills past the stage", s => s.Combat.KillsInStage = K.KillsPerStage,
                                  s => Assert.That(s.Combat.KillsInStage, Is.EqualTo(K.KillsPerStage - 1)));
            yield return new Case("P1", "boss phase on a non-boss stage", s => s.Combat.Boss = BossPhase.Walled,
                                  s => Assert.That(s.Combat.Boss, Is.EqualTo(BossPhase.None)));
            yield return new Case("P1", "boss fails negative", s => s.Combat.BossFailsOnStage = -1, s => Assert.That(s.Combat.BossFailsOnStage, Is.EqualTo(0)));
            yield return new Case("P1", "selected above HighestCleared", s => s.Combat.Selected = 3, s => Assert.That(s.Combat.Selected, Is.Null));
            yield return new Case("P1", "negative cooldowns", s => { s.Combat.SkillCooldowns[2] = -4; s.Combat.CompanionCooldowns[0] = -1; },
                                  s => { Assert.That(s.Combat.SkillCooldowns[2], Is.EqualTo(0)); Assert.That(s.Combat.CompanionCooldowns[0], Is.EqualTo(0)); });
            yield return new Case("P1", "bad missile entries dropped",
                                  s => { s.Combat.MissileQueue.Add(0.5); s.Combat.MissileQueue.Add(double.NaN); s.Combat.MissileQueue.Add(-1); s.Combat.MissileQueue.Add(double.PositiveInfinity); s.Combat.MissileQueue.Add(0); },
                                  s => Assert.That(s.Combat.MissileQueue, Is.EqualTo(new[] { 0.5 })));
            yield return new Case("P1", "Mp not finite", s => s.Combat.Mp = double.NaN, s => Assert.That(s.Combat.Mp, Is.EqualTo(0)));

            // P2: wallet and Hero.Exp
            yield return new Case("P2", "gold NaN", s => s.Wallet.Gold = double.NaN, s => Assert.That(s.Wallet.Gold, Is.EqualTo(0)));
            yield return new Case("P2", "fish coin infinite", s => s.Wallet.FishCoin = double.PositiveInfinity, s => Assert.That(s.Wallet.FishCoin, Is.EqualTo(0)));
            yield return new Case("P2", "diamond and rock candy negative", s => { s.Wallet.Diamond = -3; s.Wallet.RockCandy = -1; s.Wallet.GuildCoin = -1; },
                                  s => { Assert.That(s.Wallet.Diamond, Is.EqualTo(0)); Assert.That(s.Wallet.RockCandy, Is.EqualTo(0)); Assert.That(s.Wallet.GuildCoin, Is.EqualTo(0)); });
            yield return new Case("P2", "tickets negative", s => { s.Wallet.DungeonTicket[1] = -2; s.Wallet.SummonTicket[3] = -1; },
                                  s => { Assert.That(s.Wallet.DungeonTicket[1], Is.EqualTo(0)); Assert.That(s.Wallet.SummonTicket[3], Is.EqualTo(0)); });
            yield return new Case("P2", "hero exp negative", s => s.Hero.Exp = -0.5, s => Assert.That(s.Hero.Exp, Is.EqualTo(0)));

            // P3: hero level, awakening rank
            yield return new Case("P3", "level 0", s => s.Hero.Level = 0, s => Assert.That(s.Hero.Level, Is.EqualTo(1)));
            yield return new Case("P3", "rank past AWAKEN_STAGES", s => s.Hero.AwakenRank = Db.Awakening.Count + 2,
                                  s => Assert.That(s.Hero.AwakenRank, Is.EqualTo(Db.Awakening.Count)));
            yield return new Case("P3", "rank negative", s => s.Hero.AwakenRank = -1, s => Assert.That(s.Hero.AwakenRank, Is.EqualTo(0)));

            // P4: training within [0, TRAIN_CAP]
            yield return new Case("P4", "training negative", s => s.Training.Level[0] = -1, s => Assert.That(s.Training.Level[0], Is.EqualTo(0)));
            var capped = Db.Stats.FirstOrDefault(st => st.Cap.HasValue);
            if (capped != null)
                yield return new Case("P4", "training past TRAIN_CAP", s => s.Training.Level[(int)capped.Id] = capped.Cap.Value + 5,
                                      s => Assert.That(s.Training.Level[(int)capped.Id], Is.EqualTo(capped.Cap.Value)));

            // P5: HighestCleared within [Stage - 1, Stage]
            yield return new Case("P5", "HighestCleared behind the frontier", s => { s.Combat.Stage = 10; s.Progress.HighestCleared = 3; },
                                  s => Assert.That(s.Progress.HighestCleared, Is.EqualTo(9)));
            yield return new Case("P5", "HighestCleared ahead of the frontier", s => { s.Combat.Stage = 4; s.Progress.HighestCleared = 9; },
                                  s => Assert.That(s.Progress.HighestCleared, Is.EqualTo(3)));

            // P6: HighestFirstClearBoss
            yield return new Case("P6", "not a multiple of BOSS_EVERY",
                                  s => { s.Combat.Stage = 2 * K.BossEvery + 1; s.Progress.HighestCleared = 2 * K.BossEvery; s.Progress.HighestFirstClearBoss = K.BossEvery + 1; },
                                  s => Assert.That(s.Progress.HighestFirstClearBoss, Is.EqualTo(K.BossEvery)));
            yield return new Case("P6", "above HighestCleared",
                                  s => { s.Combat.Stage = K.BossEvery + 1; s.Progress.HighestCleared = K.BossEvery; s.Progress.HighestFirstClearBoss = 3 * K.BossEvery; },
                                  s => Assert.That(s.Progress.HighestFirstClearBoss, Is.EqualTo(K.BossEvery)));
            yield return new Case("P6", "negative", s => s.Progress.HighestFirstClearBoss = -K.BossEvery,
                                  s => Assert.That(s.Progress.HighestFirstClearBoss, Is.EqualTo(0)));

            // P7: slot array lengths
            yield return new Case("P7", "skills padded, runes truncated",
                                  s => { s.Skills.Items[AnySkill()] = Own(); s.Loadout.Skills = new[] { AnySkill(), null }; s.Loadout.Runes = new string[K.RuneSlots + 3]; },
                                  s =>
                                  {
                                      Assert.That(s.Loadout.Skills, Has.Length.EqualTo(K.SkillSlots));
                                      Assert.That(s.Loadout.Skills[0], Is.EqualTo(AnySkill()), "kept in place");
                                      Assert.That(s.Loadout.Runes, Has.Length.EqualTo(K.RuneSlots));
                                  });
            yield return new Case("P7", "companions and cooldowns resized",
                                  s => { s.Loadout.Companions = new string[1]; s.Combat.SkillCooldowns = new int[2]; s.Combat.CompanionCooldowns = null; },
                                  s =>
                                  {
                                      Assert.That(s.Loadout.Companions, Has.Length.EqualTo(K.CompanionSlots));
                                      Assert.That(s.Combat.SkillCooldowns, Has.Length.EqualTo(K.SkillSlots));
                                      Assert.That(s.Combat.CompanionCooldowns, Has.Length.EqualTo(K.CompanionSlots));
                                  });

            // P8: equipped ids
            yield return new Case("P8", "unknown weapon re-equips the best owned",
                                  s => { s.Weapons.Items[BestContentWeapon()] = Own(); s.Loadout.Weapon = "weapon_not_in_content"; },
                                  s => Assert.That(s.Loadout.Weapon, Is.EqualTo(BestContentWeapon())));
            yield return new Case("P8", "unowned weapon", s => s.Loadout.Weapon = BestContentWeapon(),
                                  s => Assert.That(s.Loadout.Weapon, Is.EqualTo(Db.TutorialWeaponId)));
            yield return new Case("P8", "null weapon auto-equips (04 AutoEquipBestWeapon)",
                                  s => { s.Weapons.Items[BestContentWeapon()] = Own(); s.Loadout.Weapon = null; },
                                  s => Assert.That(s.Loadout.Weapon, Is.EqualTo(BestContentWeapon())));
            yield return new Case("P8", "unowned skill slot cleared", s => s.Loadout.Skills[1] = AnySkill(), s => Assert.That(s.Loadout.Skills[1], Is.Null));
            yield return new Case("P8", "weapon id in a skill slot (wrong kind)",
                                  s => s.Loadout.Skills[0] = Db.TutorialWeaponId,
                                  s => Assert.That(s.Loadout.Skills[0], Is.Null));
            yield return new Case("P8", "duplicate skill keeps the first",
                                  s => { s.Skills.Items[AnySkill()] = Own(); s.Loadout.Skills[0] = AnySkill(); s.Loadout.Skills[3] = AnySkill(); },
                                  s => { Assert.That(s.Loadout.Skills[0], Is.EqualTo(AnySkill())); Assert.That(s.Loadout.Skills[3], Is.Null); });
            yield return new Case("P8", "unowned companion", s => s.Loadout.Companions[0] = "comp_c_01", s => Assert.That(s.Loadout.Companions[0], Is.Null));

            // P9: the board
            int hard = Db.FishLevels.Count;
            yield return new Case("P9", "negative cell", s => s.Fish.Cells[0] = -1, s => Assert.That(s.Fish.Cells[0], Is.EqualTo(0)));
            yield return new Case("P9", "cell above FISH_MAX_LVL_HARD",
                                  s => s.Fish.Cells[3] = hard + 4,
                                  s =>
                                  {
                                      Assert.That(s.Fish.Cells[3], Is.EqualTo(hard));
                                      Assert.That(s.Fish.DiscoveredLevels, Does.Contain(hard));
                                      Assert.That(s.Fish.HighestLevel, Is.EqualTo(hard));
                                  });
            yield return new Case("P9", "board level missing from DiscoveredLevels", s => s.Fish.Cells[5] = 2,
                                  s => { Assert.That(s.Fish.DiscoveredLevels, Does.Contain(2)); Assert.That(s.Fish.HighestLevel, Is.EqualTo(2)); });
            yield return new Case("P9", "stale HighestLevel", s => { s.Fish.DiscoveredLevels.Add(3); s.Fish.HighestLevel = 1; },
                                  s => Assert.That(s.Fish.HighestLevel, Is.EqualTo(3)));

            // P9b: the tank
            yield return new Case("P9b", "tank count negative", s => s.Fish.TankCount = -1, s => Assert.That(s.Fish.TankCount, Is.EqualTo(0)));
            yield return new Case("P9b", "tank count above the cap", s => s.Fish.TankCount = Db.Tank.Cap0 + 3, s => Assert.That(s.Fish.TankCount, Is.EqualTo(Db.Tank.Cap0)));
            yield return new Case("P9", "DiscoveredLevels outside 1..FISH_MAX_LVL_HARD", s => { s.Fish.DiscoveredLevels.Add(0); s.Fish.DiscoveredLevels.Add(hard + 2); },
                                  s => Assert.That(s.Fish.DiscoveredLevels.All(lv => lv >= 1 && lv <= hard)));
            yield return new Case("P9", "TotalMerges above Stats.Merges", s => { s.Stats.Merges = 4; s.Fish.TotalMerges = 9; },
                                  s => Assert.That(s.Fish.TotalMerges, Is.EqualTo(4)));
            yield return new Case("P9", "TotalMerges negative", s => s.Fish.TotalMerges = -1, s => Assert.That(s.Fish.TotalMerges, Is.EqualTo(0)));
            yield return new Case("P9b", "full tank with refill progress", s => { s.Fish.TankCount = Db.Tank.Cap0; s.Fish.TankRefillTick = 1; },
                                  s => Assert.That(s.Fish.TankRefillTick, Is.EqualTo(0)));
            yield return new Case("P9b", "refill tick at refillTicks", s => { s.Fish.TankCount = 0; s.Fish.TankRefillTick = Db.Tank.RefillTicks0; },
                                  s => Assert.That(s.Fish.TankRefillTick, Is.EqualTo(Db.Tank.RefillTicks0 - 1)));
            yield return new Case("P9b", "refill tick negative", s => { s.Fish.TankCount = 0; s.Fish.TankRefillTick = -5; }, s => Assert.That(s.Fish.TankRefillTick, Is.EqualTo(0)));
            yield return new Case("P9b", "release level 0", s => s.Fish.TankReleaseLevel = 0, s => Assert.That(s.Fish.TankReleaseLevel, Is.EqualTo(1)));
            yield return new Case("P9b", "release level above the content max", s => s.Fish.TankReleaseLevel = Db.Tank.ReleaseLevel + 4,
                                  s => Assert.That(s.Fish.TankReleaseLevel, Is.EqualTo(Db.Tank.ReleaseLevel)));

            // P10: unknown ids and empty entries removed, no refund
            yield return new Case("P10", "unknown weapon id removed, nothing refunded",
                                  s => s.Weapons.Items["weapon_removed_from_content"] = Own(5),
                                  s => { Assert.That(s.Weapons.Items.ContainsKey("weapon_removed_from_content"), Is.False); Assert.That(s.Wallet.RockCandy, Is.EqualTo(0)); });
            yield return new Case("P10", "zero copies removed", s => s.Skills.Items[AnySkill()] = Own(0),
                                  s => Assert.That(s.Skills.Items.ContainsKey(AnySkill()), Is.False));
            yield return new Case("P10", "companion with no copies removed", s => s.Companions.Items["comp_c_01"] = Own(-1),
                                  s => Assert.That(s.Companions.Items, Is.Empty));

            // P11: banners
            yield return new Case("P11", "pity at PITY_HARD", s => s.Banners[Banner.WEAPON].Pity = Db.Banners[Banner.WEAPON].PityHard,
                                  s => Assert.That(s.Banners[Banner.WEAPON].Pity, Is.EqualTo(Db.Banners[Banner.WEAPON].PityHard - 1)));
            yield return new Case("P11", "negative pity and exp", s => { s.Banners[Banner.SKILL].Pity = -1; s.Banners[Banner.SKILL].Exp = -9; },
                                  s => { Assert.That(s.Banners[Banner.SKILL].Pity, Is.EqualTo(0)); Assert.That(s.Banners[Banner.SKILL].Exp, Is.EqualTo(0)); });
            yield return new Case("P11", "missing banner added", s => s.Banners.Remove(Banner.SKILL),
                                  s => Assert.That(s.Banners[Banner.SKILL].TotalPulls, Is.EqualTo(0)));

            // P12-P13: quests and research
            yield return new Case("P12", "quest id unknown to content dropped", s => s.Quests.Daily["d_kill"] = new QuestProgress { Progress = -4 },
                                  s => Assert.That(s.Quests.Daily.ContainsKey("d_kill"), Is.False));
            yield return new Case("P12", "null weekly entry dropped", s => s.Quests.Weekly["w_kills"] = null,
                                  s => Assert.That(s.Quests.Weekly, Is.Empty));
            yield return new Case("P13", "research level below 1 removed", s => s.Research.Level["res_mp_pool"] = 0,
                                  s => Assert.That(s.Research.Level, Is.Empty));

            // P14: copies and limit breaks
            int maxCopies = Db.DupeLevels[Db.DupeLevels.Count - 1];
            yield return new Case("P14", "copies past DUPE_LVL_CUM[last], no Rock Candy",
                                  s => s.Weapons.Items[Db.TutorialWeaponId].Copies = maxCopies + 40,
                                  s => { Assert.That(s.Weapons.Items[Db.TutorialWeaponId].Copies, Is.EqualTo(maxCopies)); Assert.That(s.Wallet.RockCandy, Is.EqualTo(0)); });
            yield return new Case("P14", "limit breaks out of range", s => s.Weapons.Items[Db.TutorialWeaponId].LimitBreaks = K.LimitBreakRanks + 1,
                                  s => Assert.That(s.Weapons.Items[Db.TutorialWeaponId].LimitBreaks, Is.EqualTo(K.LimitBreakRanks)));

            // P15: stream counters follow the stats
            yield return new Case("P15", "crit and combat counters", s => { s.Rng["crit"].Counter = 5; s.Stats.HeroHits = 2; s.Rng["combat"].Counter = 1; },
                                  s => { Assert.That(s.Rng["crit"].Counter, Is.EqualTo(2)); Assert.That(s.Rng["combat"].Counter, Is.EqualTo(0)); });
            yield return new Case("P15", "loot and fish_spawn counters", s => { s.Stats.BossKills = 3; s.Stats.FishReleased = 4; },
                                  s => { Assert.That(s.Rng["loot"].Counter, Is.EqualTo(3)); Assert.That(s.Rng["fish_spawn"].Counter, Is.EqualTo(4)); });
            yield return new Case("P15", "summon counter is 3 per pull in Local mode", s => s.Banners[Banner.WEAPON].TotalPulls = 2,
                                  s => Assert.That(s.Rng["summon_weapon"].Counter, Is.EqualTo(6)));

            // P16: dungeon run abandoned, no refund
            yield return new Case("P16", "run in the save",
                                  s =>
                                  {
                                      s.Wallet.DungeonTicket[1] = 2;
                                      s.Combat.Mode = CombatMode.Dungeon;
                                      s.Combat.DungeonRun = new DungeonRunState { Kind = Dungeon.ROCK_CANDY, Floor = 2, Seed = 7, TicksLeft = 10 };
                                  },
                                  s =>
                                  {
                                      Assert.That(s.Combat.Mode, Is.EqualTo(CombatMode.Stage));
                                      Assert.That(s.Combat.DungeonRun, Is.Null);
                                      Assert.That(s.Wallet.DungeonTicket[1], Is.EqualTo(2), "no refund, nothing debited");
                                  });

            // P17: stale pending intents; receipts kept
            yield return new Case("P17", "summon and dungeon older than PENDING_STALE_DAYS",
                                  s =>
                                  {
                                      long old = Now - SaveConfig.PENDING_STALE_DAYS * K.DaySeconds - 1;
                                      s.Pending.Summon = new PendingSummon("r", Banner.WEAPON, 1, "Diamonds", old);
                                      s.Pending.Dungeon = new PendingDungeonSeed("d", Dungeon.EXP, 1, old);
                                      s.Pending.Receipts.Add(new PendingReceipt("p", "ios", "sku", "t", "x", old));
                                  },
                                  s =>
                                  {
                                      Assert.That(s.Pending.Summon, Is.Null);
                                      Assert.That(s.Pending.Dungeon, Is.Null);
                                      Assert.That(s.Pending.Receipts, Has.Count.EqualTo(1));
                                  });

            // P18-P21
            yield return new Case("P18", "offline reward computed in the future",
                                  s => s.Offline.Pending = new CatchupResult(10, 10, false, 1, 1, 0, 1, 1, Now + 1),
                                  s => Assert.That(s.Offline.Pending, Is.Null));
            yield return new Case("P19", "empty content stamps", s => { s.ContentVersion = ""; s.ContentHash = ""; },
                                  s => { Assert.That(s.ContentVersion, Is.EqualTo(Db.Version)); Assert.That(s.ContentHash, Is.EqualTo(Db.ContentHash)); });
            yield return new Case("P20", "negative SaveSeq", s => s.SaveSeq = -1, s => Assert.That(s.SaveSeq, Is.EqualTo(0)));
            yield return new Case("P20", "empty DeviceId gets this install", s => s.DeviceId = Guid.Empty, s => Assert.That(s.DeviceId, Is.EqualTo(Install)));
            yield return new Case("P21", "tutorial step past TUTORIAL_DONE", s => s.Tutorial.Step = Db.TutorialSteps.Count + 3,
                                  s => Assert.That(s.Tutorial.Step, Is.EqualTo(Db.TutorialSteps.Count)));
            yield return new Case("P21", "tutorial step negative", s => s.Tutorial.Step = -1, s => Assert.That(s.Tutorial.Step, Is.EqualTo(0)));
            yield return new Case("P21", "RecentRuns keeps the newest DUNGEON_RECENT_RUNS_MAX",
                                  s => { for (int i = 0; i < SimConfig.DUNGEON_RECENT_RUNS_MAX + 5; i++) s.Dungeons.RecentRuns.Add(new DungeonRunRecord("g" + i, Dungeon.EXP, 1, true)); },
                                  s =>
                                  {
                                      Assert.That(s.Dungeons.RecentRuns, Has.Count.EqualTo(SimConfig.DUNGEON_RECENT_RUNS_MAX));
                                      Assert.That(s.Dungeons.RecentRuns[0].GrantId, Is.EqualTo("g5"));
                                      Assert.That(s.Dungeons.RecentRuns.Last().GrantId, Is.EqualTo("g" + (SimConfig.DUNGEON_RECENT_RUNS_MAX + 4)));
                                  });
        }

        [TestCaseSource(nameof(Cases))]
        public void RepairRule(Case c)
        {
            var s = Fresh();
            c.Mutate(s);
            var repairs = SaveValidator.Run(s, Now, Db, Opt());
            Assert.That(repairs, Is.EqualTo(new[] { c.Rule }), c.ToString());
            c.Check(s);
            Assert.That(SaveValidator.Run(s, Now, Db, Opt()), Is.Empty, "a repaired state validates clean");
            Assert.That(HeroCat.Sim.Fish.FishBoard.FirstViolation(s, Db), Is.Null, "a repaired state holds the Fish Board invariants (05)");
        }

        [Test]
        public void P12_KnownQuestIds_AreKept_NegativeProgressClamped_UnknownDropped()   // 02 rule P12 (issue #170)
        {
            var mvp = ContentLoader.LoadFromDirectory(TestContent.Dir, Tier.MVP);
            var s = ProfileFactory.Create(mvp, Guid.Empty, TestContent.T0, TestContent.Device, TestContent.Root);
            s.Quests.Daily["d_kills"] = new QuestProgress { Progress = -4, State = QuestStatus.IN_PROGRESS };
            s.Quests.Daily["d_merge"] = new QuestProgress { Progress = 20, State = QuestStatus.COMPLETED };
            s.Quests.Daily["w_kills"] = new QuestProgress { Progress = 5 };   // a weekly id in the daily map
            s.Quests.Weekly["w_kills"] = new QuestProgress { Progress = 5 };
            s.Quests.Achievement["pad_achievement"] = new QuestProgress { Progress = 1 };   // TODO(#171): not checked yet
            Assert.That(SaveValidator.Run(s, Now, mvp, Opt()), Is.EqualTo(new[] { "P12" }));
            Assert.That(s.Quests.Daily.Keys.OrderBy(k => k), Is.EqualTo(new[] { "d_kills", "d_merge" }));
            Assert.That((s.Quests.Daily["d_kills"].Progress, s.Quests.Daily["d_merge"].State), Is.EqualTo((0L, QuestStatus.COMPLETED)));
            Assert.That(s.Quests.Weekly.Keys, Is.EqualTo(new[] { "w_kills" }));
            Assert.That(s.Quests.Achievement.Keys, Is.EqualTo(new[] { "pad_achievement" }));
            Assert.That(SaveValidator.Run(s, Now, mvp, Opt()), Is.Empty);
        }

        [Test]
        public void FreshProfile_HasZeroRepairs()   // #73 acceptance, 02 acceptance 10
        {
            Assert.That(SaveValidator.Run(Fresh(), Now, Db, Opt()), Is.Empty);
            var played = TestContent.PlayedSim().State;
            Assert.That(SaveValidator.Run(played, Now, Db, Opt()), Is.Empty);
        }

        [Test]
        public void P9_AFishAboveTheReachableCap_Stays()   // issue #41: clamp to FISH_MAX_LVL_HARD only
        {
            var s = Fresh();
            int lv = Math.Min(K.FishMaxLvl0 + 1, Db.FishLevels.Count);
            s.Fish.Cells[0] = lv;
            s.Fish.DiscoveredLevels.Add(lv);
            s.Fish.HighestLevel = lv;
            Assert.That(SaveValidator.Run(s, Now, Db, Opt()), Is.Empty);
            Assert.That(s.Fish.Cells[0], Is.EqualTo(lv));
        }

        [Test]
        public void P15_RemoteMode_IgnoresSummonCounters()
        {
            var s = Fresh();
            s.Rng["summon_skill"].Counter = 11;
            Assert.That(SaveValidator.Run(s, Now, Db, Opt(remote: true)), Is.Empty);
            Assert.That(s.Rng["summon_skill"].Counter, Is.EqualTo(11));
        }

        [Test]
        public void P16_IsReportedForTheDungeonAbandonedEvent()
        {
            var s = Fresh();
            s.Combat.Mode = CombatMode.Dungeon;
            s.Combat.DungeonRun = new DungeonRunState { Kind = Dungeon.EXP, Floor = 1 };
            Assert.That(SaveValidator.RunWithReport(s, Now, Db, Opt()).DungeonAbandoned, Is.True);
        }

        [Test]
        public void P17_AFreshIntent_IsKept()
        {
            var s = Fresh();
            s.Pending.Summon = new PendingSummon("r", Banner.WEAPON, 1, "Diamonds", Now - SaveConfig.PENDING_STALE_DAYS * K.DaySeconds);
            Assert.That(SaveValidator.Run(s, Now, Db, Opt()), Is.Empty);
            Assert.That(s.Pending.Summon, Is.Not.Null);
        }

        [Test]
        public void P20_WithoutAnInstallId_LeavesAnEmptyDeviceId()
        {
            var s = Fresh();
            s.DeviceId = Guid.Empty;
            Assert.That(SaveValidator.Run(s, Now, Db), Is.Empty);
        }

        [Test]
        public void LeftAloneOnPurpose_BackwardsClockFields()
        {
            var s = SaveSerializer.Materialize(Json(Fresh(), j => j["Time"]["CreatedServerTime"] = Now + 999));
            s.Time.DayKey = (int)(Now / K.DaySeconds) + 5;
            s.Time.LastSavedServerTime = Now + 3600;
            Assert.That(SaveValidator.Run(s, Now, Db, Opt()), Is.Empty);
            Assert.That(s.Time.DayKey, Is.EqualTo((int)(Now / K.DaySeconds) + 5));
            Assert.That(s.Time.LastSavedServerTime, Is.EqualTo(Now + 3600));
            Assert.That(s.Time.CreatedServerTime, Is.EqualTo(Now + 999));
        }

        [Test]
        public void UnknownStream_IsDroppedWithALog_NotARepair()   // 02 section 6.3
        {
            var s = Fresh();
            s.Rng["rune_roll"] = new RngStreamState { Seed = "0123456789abcdef" };
            var report = SaveValidator.RunWithReport(s, Now, Db, Opt());
            Assert.That(report.Repairs, Is.Empty);
            Assert.That(report.Log.Single(), Does.Contain("rune_roll"));
            Assert.That(s.Rng.ContainsKey("rune_roll"), Is.False);
        }

        [Test]
        public void RepairMax_TwentyDistinctRulesLoad_TwentyOneReject()
        {
            // One mutator per rule, each proven independent by RepairRule above.
            var byRule = Cases().GroupBy(c => c.Rule).Select(g => g.First()).ToList();
            Assert.That(byRule.Select(c => c.Rule), Is.EquivalentTo(SaveValidator.RepairRules));
            // P5 and P6 move the stage; leave them out of the composite so the others stay independent.
            var independent = byRule.Where(c => c.Rule != "P5" && c.Rule != "P6").ToList();
            Assert.That(independent, Has.Count.EqualTo(SaveConfig.SAVE_REPAIR_MAX));

            var ok = Fresh();
            foreach (var c in independent) c.Mutate(ok);
            Assert.That(SaveValidator.Run(ok, Now, Db, Opt()), Has.Count.EqualTo(SaveConfig.SAVE_REPAIR_MAX));

            var tooMany = Fresh();
            foreach (var c in independent) c.Mutate(tooMany);
            tooMany.Progress.HighestFirstClearBoss = -K.BossEvery;   // P6: the 21st distinct rule
            Assert.Throws<SaveInvalidException>(() => SaveValidator.Run(tooMany, Now, Db, Opt()));
        }

        // ---------------------------------------------------------------------------------------------- rejects

        static Newtonsoft.Json.Linq.JObject Json(GameState s, Action<Newtonsoft.Json.Linq.JObject> edit)
        {
            var j = SaveSerializer.ToJson(s);
            edit(j);
            return j;
        }

        public static IEnumerable<TestCaseData> Rejects()
        {
            yield return new TestCaseData("R3", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Hero"] = null));
            yield return new TestCaseData("R3", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Stats"] = null));
            yield return new TestCaseData("R4", (Action<Newtonsoft.Json.Linq.JObject>)(j => ((Newtonsoft.Json.Linq.JObject)j["Rng"]).Remove("loot")));
            yield return new TestCaseData("R4", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Rng"]["crit"]["Seed"] = "12345"));
            yield return new TestCaseData("R4", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Rng"]["crit"]["Seed"] = "zz23456789abcdef"));
            yield return new TestCaseData("R4", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Rng"]["dungeon"]["Counter"] = -1));
            yield return new TestCaseData("R4", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["RngRoot"] = Guid.Empty.ToString()));
            yield return new TestCaseData("R5", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Training"]["Level"] = new Newtonsoft.Json.Linq.JArray(1, 2)));
            yield return new TestCaseData("R5", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Fish"]["Cells"] = new Newtonsoft.Json.Linq.JArray(0, 0, 0)));
            yield return new TestCaseData("R5", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Wallet"]["DungeonTicket"] = new Newtonsoft.Json.Linq.JArray(1)));
            yield return new TestCaseData("R5", (Action<Newtonsoft.Json.Linq.JObject>)(j => j["Wallet"]["SummonTicket"] = new Newtonsoft.Json.Linq.JArray(1, 2, 3)));
        }

        [TestCaseSource(nameof(Rejects))]
        public void RejectRule(string rule, Action<Newtonsoft.Json.Linq.JObject> edit)
        {
            var s = SaveSerializer.Materialize(Json(Fresh(), edit));
            var e = Assert.Throws<SaveInvalidException>(() => SaveValidator.Run(s, Now, Db, Opt()));
            Assert.That(e.Message, Does.StartWith(rule));
        }

        [Test]
        public void R2_SchemaVersionBelow1()   // Materialize refuses it first; the validator still checks
        {
            var s = Fresh();
            s.SchemaVersion = 0;
            Assert.That(Assert.Throws<SaveInvalidException>(() => SaveValidator.Run(s, Now, Db, Opt())).Message, Does.StartWith("R2"));
        }

        [Test]
        public void NullNestedCollection_IsARejectNotACrash()
        {
            var s = SaveSerializer.Materialize(Json(Fresh(), j => j["Combat"]["MissileQueue"] = null));
            Assert.Throws<SaveInvalidException>(() => SaveValidator.Run(s, Now, Db, Opt()));
        }
    }
}
