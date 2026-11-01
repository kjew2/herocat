#!/usr/bin/env python3
"""Content converter: content_src/<table>.csv -> Content/<table>.json + manifest.json (specs/05).

Prototype scope: type checks, key uniqueness, enum values and the handful of cross-table rules the sim
depends on. Version-bump rules (specs/05 section 6.3) and the full error-code catalogue are not built yet.

    python3 tools/content_build.py [--src content_src] [--out Content] [--check] [--copy-to DIR]

Exit codes: 0 ok, 1 validation errors (all listed), 2 usage / IO error. Stdlib only.
"""
import argparse
import csv
import hashlib
import json
import os
import re
import shutil
import sys

SCHEMA_VERSION = 1

ENUMS = {
    "Tier": ["C", "R", "E", "L", "M"],
    "BannerKind": ["hero", "pet"],
    "RngStream": ["combat", "gear_drop", "gear_rarity", "enhance", "gacha_hero", "gacha_pet"],
    "CurveId": ["enemy_hp", "gold_per_kill", "stat_cost"],
    "QuestType": ["login", "kill_mobs", "clear_boss", "do_pulls"],
    "GrantTrigger": ["profile_created", "stage_reached", "first_boss_cleared"],
    "GrantType": ["gems", "free_pulls", "gold", "hero"],
    "ConstantType": ["int", "double", "bool"],
}

# key -> type. Mirrors HeroCat.Sim.Content.Constants. specs/05 section 5.1 plus the specs/00 names it
# did not yet carry (STAGE_CAP, OFFLINE_MIN_GAP_S, DAY_SECONDS, GEAR_SELL_KILLS, IDLE_CHEST_*).
REQUIRED_CONSTANTS = {
    "TICK_HZ": "int", "BASE_ATK": "double", "ATTACK_INTERVAL_TICKS": "int",
    "KILLS_PER_STAGE": "int", "SPAWN_DELAY_TICKS": "int", "BOSS_EVERY": "int", "STAGE_CAP": "int",
    "STAT_PER_LVL": "double", "GEMS_PER_DAY_F2P": "int", "OFFLINE_RATE": "double",
    "OFFLINE_CAP_H": "double", "OFFLINE_MIN_GAP_S": "int", "DAY_SECONDS": "int",
    "GEAR_DROP_P": "double", "TIER_WIDTH": "int", "GEAR_SLOT_G": "double", "GEAR_SELL_KILLS": "double",
    "ENH_MAX": "int", "ENH_PER_LVL": "double", "ENH_SUCCESS_FLOOR": "double",
    "ENH_SUCCESS_STEP": "double", "ENH_COST_GOLD_MULT": "double",
    "HERO_SLOTS": "int", "HERO_LVL_BONUS": "double",
    "IDLE_CHEST_HOURS": "double", "IDLE_CHEST_GEMS": "int",
}

TIERS = ENUMS["Tier"]


def T(columns, key=None, folds=None, required=True):
    """columns: list of (name, type, nullable)."""
    return {"columns": columns, "key": key, "folds": folds or {}, "required": required}


def c(name, typ, nullable=False):
    return (name, typ, nullable)


SCHEMA = {
    "banners": T([c("id", "id"), c("kind", "enum:BannerKind"), c("name_key", "key"), c("pool_id", "id"),
                  c("rate_c", "double"), c("rate_r", "double"), c("rate_e", "double"),
                  c("rate_l", "double"), c("rate_m", "double"),
                  c("pity_soft_start", "int"), c("pity_soft_step", "double"), c("pity_hard", "int"),
                  c("mythic_share_of_lplus", "double"), c("guarantee_batch_size", "int"),
                  c("guarantee_min_tier", "enum:Tier"), c("pull_cost_gems", "int"),
                  c("free_pulls_per_day", "int"), c("rng_stream", "enum:RngStream"), c("enabled", "bool")],
                 key="id", folds={"rates": ["rate_c", "rate_r", "rate_e", "rate_l", "rate_m"]}),
    "boss_rules": T([c("id", "id"), c("from_stage", "int"), c("hp_mult", "double"), c("time_ticks", "int"),
                     c("gold_mult", "double"), c("gear_drop_p", "double"), c("gear_min_rarity", "enum:Tier"),
                     c("first_clear_gems", "int")], key="id"),
    "class_advances": T([c("id", "id"), c("index", "int"), c("stage", "int"), c("mult", "double"),
                         c("name_key", "key"), c("icon_id", "string")], key="id"),
    "constants": T([c("key", "string"), c("type", "enum:ConstantType"), c("value", "constval")], key="key"),
    "curves": T([c("id", "enum:CurveId"), c("base", "double"), c("growth", "double")], key="id"),
    "daily_quests": T([c("id", "id"), c("type", "enum:QuestType"), c("target", "int"), c("reward_gems", "int"),
                       c("sort_order", "int"), c("name_key", "key")], key="id"),
    "gear_rarity": T([c("rarity", "enum:Tier"), c("weight", "double"), c("mult", "double"),
                      c("display_color", "color"), c("name_key", "key")], key="rarity"),
    "gear_slots": T([c("id", "id"), c("sort_order", "int"), c("name_key", "key"), c("icon_id", "string")], key="id"),
    "hero_level_thresholds": T([c("level", "int"), c("cum_copies", "int")], key="level"),
    "hero_tiers": T([c("tier", "enum:Tier"), c("display_color", "color"), c("name_key", "key"),
                     c("shard_value", "int")], key="tier"),
    "heroes": T([c("id", "id"), c("tier", "enum:Tier"), c("pool_id", "id"), c("name_key", "key"),
                 c("desc_key", "key"), c("icon_id", "string"), c("base_pct", "double")], key="id"),
    "progress_targets": T([c("day", "int"), c("stage", "int")], key="day"),
    "strings": T([c("key", "key"), c("en", "string")], key="key", required=False),
    "tutorial_grants": T([c("id", "id"), c("trigger", "enum:GrantTrigger"), c("trigger_value", "int", True),
                          c("grant_type", "enum:GrantType"), c("amount", "int"), c("grant_ref", "string", True),
                          c("sort_order", "int")], key="id"),
}

ID_RE = re.compile(r"^[a-z][a-z0-9_]{0,63}$")
KEY_RE = re.compile(r"^[a-z0-9_]+(\.[a-z0-9_]+)+$")
COLOR_RE = re.compile(r"^#[0-9A-F]{6}$")
INT_RE = re.compile(r"^-?\d+$")


class Errors(list):
    def add(self, code, loc, msg):
        self.append(f"CONTENT {code} {loc}: {msg}")


def parse_cell(typ, text, loc, errs):
    if typ == "int":
        if not INT_RE.match(text):
            errs.add("E010", loc, f"expected int, got '{text}'")
            return None
        return int(text)
    if typ == "double":
        try:
            return float(text)
        except ValueError:
            errs.add("E010", loc, f"expected double, got '{text}'")
            return None
    if typ == "bool":
        if text not in ("true", "false"):
            errs.add("E010", loc, f"expected true/false, got '{text}'")
            return None
        return text == "true"
    if typ.startswith("enum:"):
        values = ENUMS[typ[5:]]
        if text not in values:
            errs.add("E021", loc, f"'{text}' is not one of {', '.join(values)}")
            return None
        return text
    if typ == "id" and not ID_RE.match(text):
        errs.add("E010", loc, f"bad id '{text}'")
    if typ == "key" and not KEY_RE.match(text):
        errs.add("E010", loc, f"bad localisation key '{text}'")
    if typ == "color" and not COLOR_RE.match(text):
        errs.add("E010", loc, f"bad color '{text}' (want #RRGGBB uppercase)")
    return text


def read_table(path, name, schema, errs):
    with open(path, newline="", encoding="utf-8") as f:
        rows = list(csv.reader(f))
    if not rows:
        errs.add("E001", name, "empty file")
        return []
    header = [h.strip() for h in rows[0]]
    cols = {cn: (ct, nul) for cn, ct, nul in schema["columns"]}
    for h in header:
        if h and not h.startswith("#") and h not in cols:
            errs.add("E002", name, f"unknown column '{h}'")
    for cn in cols:
        if cn not in header:
            errs.add("E003", name, f"missing column '{cn}'")
    out, seen = [], set()
    for i, raw in enumerate(rows[1:], start=2):
        cells = [x.strip() for x in raw]
        if not any(cells):
            continue
        rec = dict(zip(header, cells + [""] * (len(header) - len(cells))))
        row = {}
        for cn, ct, nul in schema["columns"]:
            text = rec.get(cn, "")
            loc = f"{name}[row {i}].{cn}"
            if text == "":
                if not nul:
                    errs.add("E011", loc, "empty cell in non-nullable column")
                row[cn] = None
                continue
            if ct == "constval":
                row[cn] = text  # typed after the whole row is read
            else:
                row[cn] = parse_cell(ct, text, loc, errs)
        if name == "constants" and row.get("type") and row.get("value") is not None:
            row["value"] = parse_cell(row["type"], row["value"], f"constants[{row['key']}].value", errs)
        k = row.get(schema["key"])
        if k in seen:
            errs.add("E020", f"{name}[{k}]", "duplicate key")
        seen.add(k)
        out.append(row)
    return out


def validate(t, errs):
    # constants
    keys = {r["key"]: r for r in t["constants"]}
    for k, typ in REQUIRED_CONSTANTS.items():
        if k not in keys:
            errs.add("E040", f"constants[{k}]", "required constant missing")
        elif keys[k]["type"] != typ:
            errs.add("E041", f"constants[{k}]", f"type must be {typ}")
    for k in keys:
        if k not in REQUIRED_CONSTANTS:
            errs.add("E042", f"constants[{k}]", "unknown constant")
    # curves
    ids = {r["id"] for r in t["curves"]}
    for cid in ENUMS["CurveId"]:
        if cid not in ids:
            errs.add("E044", f"curves[{cid}]", "curve missing")
    for r in t["curves"]:
        if r["growth"] is not None and r["growth"] <= 1:
            errs.add("E043", f"curves[{r['id']}]", "growth must be > 1")
    # rarity tables
    for tab, col in (("gear_rarity", "rarity"), ("hero_tiers", "tier")):
        have = [r[col] for r in t[tab]]
        if have != TIERS:
            errs.add("E044", tab, f"must list {', '.join(TIERS)} in order")
    s = sum(r["weight"] or 0 for r in t["gear_rarity"])
    if abs(s - 1) > 1e-9:
        errs.add("E030", "gear_rarity", f"weights sum to {s!r}, abs error {abs(s - 1):.3g}")
    # thresholds
    lv = t["hero_level_thresholds"]
    for i, r in enumerate(lv):
        if r["level"] != i + 1:
            errs.add("E060", f"hero_level_thresholds[{r['level']}]", "levels must be 1..N contiguous")
        if i > 0 and r["cum_copies"] <= lv[i - 1]["cum_copies"]:
            errs.add("E031", f"hero_level_thresholds[{r['level']}]", "cum_copies must strictly increase")
    if lv and lv[0]["cum_copies"] != 1:
        errs.add("E061", "hero_level_thresholds[1]", "level 1 must need exactly 1 copy")
    # gear slots
    orders = sorted(r["sort_order"] for r in t["gear_slots"])
    if orders != list(range(len(orders))):
        errs.add("E047", "gear_slots", "sort_order must be exactly 0..N-1")
    # boss rules
    br = t["boss_rules"]
    if not br or br[0]["from_stage"] != 1:
        errs.add("E045", "boss_rules", "first row must have from_stage = 1")
    # class advances
    ca = t["class_advances"]
    for i, r in enumerate(ca):
        if r["index"] != i + 1:
            errs.add("E060", f"class_advances[{r['id']}]", "index must be 1..N contiguous")
        if i > 0 and r["stage"] <= ca[i - 1]["stage"]:
            errs.add("E032", f"class_advances[{r['id']}]", "stage must strictly increase")
    # banners
    pools = {}
    for h in t["heroes"]:
        pools.setdefault(h["pool_id"], {}).setdefault(h["tier"], []).append(h["id"])
    streams = set()
    for b in t["banners"]:
        loc = f"banners[{b['id']}]"
        rates = [b[f"rate_{x.lower()}"] or 0 for x in TIERS]
        if abs(sum(rates) - 1) > 1e-9:
            errs.add("E030", loc, f"rates sum to {sum(rates)!r}, abs error {abs(sum(rates) - 1):.3g}")
        if b["pool_id"] not in pools:
            errs.add("E022", loc, f"pool_id '{b['pool_id']}' has no heroes")
        else:
            for tier, rate in zip(TIERS, rates):
                if rate > 0 and not pools[b["pool_id"]].get(tier):
                    errs.add("E033", loc, f"tier {tier} has a rate but no heroes in the pool")
        if b["pity_hard"] is not None and b["pity_soft_start"] is not None and b["pity_hard"] <= b["pity_soft_start"]:
            errs.add("E051", loc, "pity_hard must be > pity_soft_start")
        if b["enabled"]:
            if b["rng_stream"] in streams:
                errs.add("E053", loc, "rng_stream shared with another enabled banner")
            streams.add(b["rng_stream"])
    # tutorial grants
    hero_ids = {h["id"] for h in t["heroes"]}
    for g in t["tutorial_grants"]:
        loc = f"tutorial_grants[{g['id']}]"
        if (g["trigger"] == "stage_reached") != (g["trigger_value"] is not None):
            errs.add("E054", loc, "trigger_value required iff trigger = stage_reached")
        if (g["grant_type"] == "hero") != (g["grant_ref"] is not None):
            errs.add("E054", loc, "grant_ref required iff grant_type = hero")
        if g["grant_ref"] is not None and g["grant_ref"] not in hero_ids:
            errs.add("E023", loc, f"grant_ref '{g['grant_ref']}' is not a hero id")


def shape(name, rows, schema):
    """Apply folds and the per-table JSON shape (specs/05 section 6.1)."""
    out = []
    for r in rows:
        o = {}
        folded = {}
        for fold_name, cols in schema["folds"].items():
            for col in cols:
                folded[col] = fold_name
        emitted_folds = set()
        for cn, _, _ in schema["columns"]:
            if cn in folded:
                fn = folded[cn]
                if fn not in emitted_folds:
                    emitted_folds.add(fn)
                    o[fn] = {col.split("_")[-1].upper(): r[col] for col in schema["folds"][fn]}
                continue
            o[cn] = r[cn]
        if name == "strings":
            o = {"key": r["key"], "text": r["en"]}
        out.append(o)
    doc = {"table": name}
    if name == "strings":
        doc["locale"] = "en"
    doc["rows"] = out
    return doc


def dumps(doc):
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def build(src):
    errs = Errors()
    tables = {}
    for name, schema in SCHEMA.items():
        path = os.path.join(src, name + ".csv")
        if not os.path.exists(path):
            if schema["required"]:
                errs.add("E001", name, f"missing {path}")
            continue
        tables[name] = read_table(path, name, schema, errs)
    meta = {}
    meta_path = os.path.join(src, "meta.csv")
    if os.path.exists(meta_path):
        with open(meta_path, newline="", encoding="utf-8") as f:
            for row in list(csv.reader(f))[1:]:
                if len(row) >= 2 and row[0].strip():
                    meta[row[0].strip()] = row[1].strip()
    for k in ("content_version", "source_revision"):
        if k not in meta:
            errs.add("E070", "meta", f"missing {k}")
    if not errs:
        validate(tables, errs)
    if errs:
        return None, errs
    files = {}
    for name in sorted(tables):
        files[name + ".json"] = dumps(shape(name, tables[name], SCHEMA[name])).encode("utf-8")
    entries = {}
    for fname in sorted(files):
        entries[fname[:-5]] = {"file": fname, "sha256": hashlib.sha256(files[fname]).hexdigest()}
    content_hash = hashlib.sha256("".join(e["sha256"] for e in entries.values()).encode()).hexdigest()
    manifest = {
        "schema_version": SCHEMA_VERSION,
        "content_version": meta["content_version"],
        "source_revision": meta["source_revision"],
        "tables": entries,
        "content_hash": content_hash,
    }
    files["manifest.json"] = dumps(manifest).encode("utf-8")
    summary = {"tables": tables, "manifest": manifest}
    return (files, summary), errs


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--src", default="content_src")
    ap.add_argument("--out", default="Content")
    ap.add_argument("--check", action="store_true", help="exit 1 if --out differs from a fresh build")
    ap.add_argument("--copy-to", default=None)
    args = ap.parse_args(argv)

    if not os.path.isdir(args.src):
        print(f"source directory not found: {args.src}", file=sys.stderr)
        return 2
    result, errs = build(args.src)
    if errs:
        for e in errs:
            print(e, file=sys.stderr)
        return 1
    files, summary = result

    if args.check:
        stale = []
        for fname, data in files.items():
            p = os.path.join(args.out, fname)
            if not os.path.exists(p) or open(p, "rb").read() != data:
                stale.append(fname)
        if stale:
            print("Content/ is stale: " + ", ".join(sorted(stale)), file=sys.stderr)
            return 1
        print("Content/ is up to date")
        return 0

    os.makedirs(args.out, exist_ok=True)
    for fname, data in files.items():
        with open(os.path.join(args.out, fname), "wb") as f:
            f.write(data)
    if args.copy_to:
        os.makedirs(args.copy_to, exist_ok=True)
        for fname in files:
            shutil.copyfile(os.path.join(args.out, fname), os.path.join(args.copy_to, fname))

    m = summary["manifest"]
    print(f"content_version {m['content_version']}  content_hash {m['content_hash']}")
    for name, rows in sorted(summary["tables"].items()):
        print(f"  {name:24s} {len(rows):4d} rows")
    pools = {}
    for h in summary["tables"]["heroes"]:
        pools.setdefault(h["pool_id"], {}).setdefault(h["tier"], 0)
        pools[h["pool_id"]][h["tier"]] += 1
    for pid, counts in pools.items():
        print(f"  pool {pid}: " + " / ".join(f"{t} {counts.get(t, 0)}" for t in TIERS))
    return 0


if __name__ == "__main__":
    sys.exit(main())
