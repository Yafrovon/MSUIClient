"""The vanilla-legality data the Spell Creator's RULES part checks against
(shared_docs/SPELL_CREATOR_PLAN.md §2, P1.2).

    python tools/spellvis/spell_rules_extract.py [GameData/Data]

Read-only. Two sources, neither of them memory:
  1. the CORE (over ssh, ~/vmangos on the box): the effect handler table (SpellEffects.cpp),
     the aura handler table (SpellAuras.cpp) and the implicit-target enum (SpellDefines.h) -
     an effect or aura whose handler is EffectNULL/EffectUnused/HandleNULL does NOTHING on this
     server, whatever the client shows;
  2. the client's STOCK Spell.dbc (patch-3 deliberately excluded): which effect / aura /
     target-A / target-B combinations Blizzard's own spells use, and how often - a combination
     no stock spell uses is not illegal, but the workshop says so.

Writes MSUIClient/Data/vanilla-spell-rules.json (embedded into the client). Re-run after a
Core change to those tables or a DBC change, and commit the regenerated file with it.
"""
import json, os, re, subprocess, sys, collections

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from spellvis import Mount, load

REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(REPO, "MSUIClient", "Data", "vanilla-spell-rules.json")
BOX = "wowvmangos@192.168.0.2"


def box_cat(path):
    return subprocess.run(["ssh", "-o", "BatchMode=yes", BOX, f"cat ~/vmangos/src/game/Spells/{path}"],
                          capture_output=True, text=True, check=True).stdout


def box_head():
    return subprocess.run(["ssh", "-o", "BatchMode=yes", BOX, "cd ~/vmangos && git rev-parse --short HEAD"],
                          capture_output=True, text=True).stdout.strip()


def table(src, owner, start_marker, total):
    """Rows of a handler table, numbered by POSITION (the k-th entry is id k - a comment can be
    missing or wrong, the array index cannot). Ids past the table's last entry have no handler
    at all (the array is zero-filled): they are listed as unimplemented."""
    body = src[src.index(start_marker):]
    body = body[body.index("{") + 1:body.index("};")]
    rows = []
    for line in body.splitlines():
        m = re.search(r"&" + owner + r"::(\w+)", line)
        if not m:
            continue
        comment = line.split("//", 1)[1].strip() if "//" in line else ""
        cm = re.match(r"(\d+)[ 	]*(\w+)?[ 	]*(.*)", comment)
        name = cm.group(2) if cm and cm.group(2) else ""
        note = cm.group(3).strip() if cm else comment
        rows.append(dict(id=len(rows), name=name, handler=m.group(1), note=note))
    for missing in range(len(rows), total):
        rows.append(dict(id=missing, name="", handler="(none - past the end of the table)", note=""))
    return rows


def targets(src):
    body = src[src.index("enum SpellTarget"):]
    body = body[:body.index("};")]
    out = []
    for m in re.finditer(r"^[ 	]*(TARGET_\w+)[ 	]*=[ 	]*(\d+)[ 	]*,?[ 	]*(//.*)?$", body, re.M):
        out.append(dict(id=int(m.group(2)), name=m.group(1), note=(m.group(3) or "").lstrip("/ ").strip()))
    return out


effects_src = box_cat("SpellEffects.cpp")
auras_src = box_cat("SpellAuras.cpp")
defines_src = box_cat("SpellDefines.h")
effects = table(effects_src, "Spell", "pEffect SpellEffects[", 134)
auras = table(auras_src, "Aura", "pAuraHandler AuraHandler[", 193)
target_list = targets(defines_src)
dead_effect = {"EffectNULL", "EffectUnused", "(none - past the end of the table)"}
dead_aura = {"HandleNULL", "HandleUnused", "(none - past the end of the table)"}
for e in effects: e["implemented"] = e["handler"] not in dead_effect
for a in auras: a["implemented"] = a["handler"] not in dead_aura

# the census - stock archives only
mount = Mount(sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "GameData", "Data"))
spell = load(mount, "Spell")
AURA_EFFECTS = {6, 27, 35, 65, 119, 128}           # apply aura, persistent area aura, area auras
effect_uses, aura_uses, target_uses = collections.Counter(), collections.Counter(), collections.Counter()
combos, example = collections.Counter(), {}
for r in spell.rows():
    sid = spell.u32(r, 0)
    if sid >= 40000:                               # never let custom rows vote
        continue
    for i in range(3):
        eff = spell.u32(r, 61 + i)
        if eff == 0:
            continue
        aura = spell.u32(r, 91 + i) if eff in AURA_EFFECTS else 0
        a, b = spell.u32(r, 82 + i), spell.u32(r, 85 + i)
        effect_uses[eff] += 1
        if aura: aura_uses[aura] += 1
        target_uses[a] += 1
        if b: target_uses[b] += 1
        key = (eff, aura, a, b)
        combos[key] += 1
        example.setdefault(key, sid)

for e in effects: e["uses"] = effect_uses.get(e["id"], 0)
for a in auras: a["uses"] = aura_uses.get(a["id"], 0)
for t in target_list: t["uses"] = target_uses.get(t["id"], 0)

doc = dict(
    generatedBy="tools/spellvis/spell_rules_extract.py",
    coreHead=box_head(),
    stockSpells=sum(1 for r in spell.rows() if spell.u32(r, 0) < 40000),
    auraEffects=sorted(AURA_EFFECTS),
    effects=effects,
    auras=auras,
    targets=target_list,
    combos=[dict(effect=k[0], aura=k[1], a=k[2], b=k[3], uses=n, example=example[k])
            for k, n in sorted(combos.items(), key=lambda kv: (-kv[1], kv[0]))],
)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
    json.dump(doc, fh, indent=1)
print(f"effects {len(effects)} ({sum(e['implemented'] for e in effects)} implemented), "
      f"auras {len(auras)} ({sum(a['implemented'] for a in auras)} implemented), "
      f"targets {len(target_list)}, combos {len(combos)} -> {OUT}")
