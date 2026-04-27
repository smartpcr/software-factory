"""Generate the agent allocation manifest from manifest.json + leadership picks.

Reads `src/forge/Forge.UI/src/assets/crews/manifest.json`, allocates each of the
90 characters (plus the operator XD) into one of:
  - leadership (8 human roles)
  - plan-design pod (30 agents in 6 sub-roles)
  - coding pod (30 agents in 6 sub-roles)
  - ai-worker specialists (13 single-seat roles)
  - reserve (anything left over)

Writes `agents/allocation.json` as the canonical source. ForgeSeed.cs reads
this on first boot to seed every character as an AgentRow with crew/role/
SoulPath populated; the UI reads it via `/api/agents`.
"""
import json
import os
import re

MANIFEST = 'src/forge/Forge.UI/src/assets/crews/manifest.json'
OUT = 'agents/allocation.json'

# Curated 20 → existing 2-letter codes that already have soul.md files.
EXISTING = {
    'Yuan Shi Tianzun':       'YS',
    'Taiji Zhenren':          'TY',
    'Li Jing':                'LJ',
    'Wen Zhong':              'WZ',
    'Bi Gan':                 'BG',
    'Ne Zha':                 'NZ',
    'Ao Bing':                'AB',
    'Zhu Rong':               'ZR',
    'Gong Gong':              'GG',
    'Yinglong':               'YL',
    'Mu Bao':                 'MB',
    'Jinzha':                 'JZ',
    'Shen Gongbao':           'SG',
    'Yun Xiao':               'YX',
    'Xiao Yue Lao':           'YL2',
    "Shun Feng'er":           'SF',
    'Qian Kun':               'QK',
    'Hun Dun':                'HD',
    'Zhi Zhi':                'ZZ',
}

LEADERSHIP = [
    ('XD',  'Xiaodong',          'Product Strategist / Product Owner', 'leadership-product',  'product-owner'),
    ('YS',  'Yuan Shi Tianzun',  'AI Factory Architect',                'leadership-factory',  'arch'),
    ('TY',  'Taiji Zhenren',     'Principal Engineer / Domain Expert',  'leadership-principal','arch'),
    ('WZ',  'Wen Zhong',         'Incident Commander / Reliability',    'leadership-incident', 'arch'),
    ('LJ',  'Li Jing',           'Security / Governance Lead',          'leadership-security', 'arch'),
    ('JZ',  'Jinzha',            'DevOps / Platform Lead',              'leadership-devops',   'eng'),
    ('SG',  'Shen Gongbao',      'Prompt / Agent Engineer Lead',        'leadership-prompt',   'rev'),
    ('SF',  "Shun Feng'er",      'QA / Validation Lead',                'leadership-qa',       'qa'),
]

BUCKETS = [
    # role_bucket, count, team, crew_role
    ('plan-architect-generator', 8,  'plan-design', 'arch'),
    ('plan-red-team',            8,  'plan-design', 'arch'),
    ('plan-refinement',          4,  'plan-design', 'arch'),
    ('plan-requirements-judge',  4,  'plan-design', 'arch'),
    ('plan-design-historian',    3,  'plan-design', 'arch'),
    ('plan-orchestrator',        3,  'plan-design', 'arch'),
    ('code-generator',           10, 'coding',      'eng'),
    ('code-breaker',             8,  'coding',      'eng'),
    ('code-unit-test-attacker',  5,  'coding',      'qa'),
    ('code-refactor-defender',   3,  'coding',      'eng'),
    ('code-judge',               2,  'coding',      'rev'),
    ('code-memory-agent',        2,  'coding',      'qa'),
    ('ai-requirements-analyst',     1, 'ai-worker', 'arch'),
    ('ai-ux-designer',              1, 'ai-worker', 'eng'),
    ('ai-systems-architect',        1, 'ai-worker', 'arch'),
    ('ai-backend-engineer',         1, 'ai-worker', 'eng'),
    ('ai-frontend-engineer',        1, 'ai-worker', 'eng'),
    ('ai-test-engineer',            1, 'ai-worker', 'qa'),
    ('ai-security-reviewer',        1, 'ai-worker', 'rev'),
    ('ai-code-reviewer',            1, 'ai-worker', 'rev'),
    ('ai-release-manager',          1, 'ai-worker', 'eng'),
    ('ai-sre',                      1, 'ai-worker', 'qa'),
    ('ai-customer-support',         1, 'ai-worker', 'rev'),
    ('ai-root-cause-investigator',  1, 'ai-worker', 'qa'),
    ('ai-doc-writer',               1, 'ai-worker', 'rev'),
]


def code_for(name, used):
    if name in EXISTING:
        return EXISTING[name]
    n = re.sub(r"\([^)]*\)", "", name)
    n = re.sub(r"'s\b", "", n).strip()
    parts = re.findall(r"[A-Z][a-z]*", n.replace("'", ""))
    if not parts:
        parts = [n]
    if len(parts) >= 2:
        base = (parts[0][0] + parts[1][0]).upper()
    else:
        base = parts[0][:2].upper()
    code = base
    suffix = 2
    while code in used:
        code = base + str(suffix)
        suffix += 1
    return code


# Canonical 4-team taxonomy. Every one of the 91 agents lands in exactly one.
# `team` is the line-of-business; `pod` (formerly the only `team` field) is
# the sub-classification within a team (leadership / plan-design / coding /
# ai-worker / reserve).
TEAM_FOR = {
    'arch':          'Architect',
    'eng':           'Engineer',
    'rev':           'Review',
    'qa':            'QA',
    # The Product Owner is the operator. Folded under Architect because the
    # PO sets product strategy — the same "frame the problem" axis the
    # Architect team lives on. Special-cased rather than introducing a fifth
    # team so the registry stays four.
    'product-owner': 'Architect',
}


def main():
    with open(MANIFEST, 'r', encoding='utf-8') as f:
        manifest = json.load(f)
    used = set(EXISTING.values()) | {'XD'}
    codes = {}
    for entry in manifest:
        name = entry['name']
        c = code_for(name, used)
        codes[name] = c
        used.add(c)

    leadership_names = {name for _, name, _, _, _ in LEADERSHIP}
    remaining = [e for e in manifest if e['name'] not in leadership_names]

    allocation = []
    for code, name, role_label, pod, crew_role in LEADERSHIP:
        avatar = next((e['file'] for e in manifest if e['name'] == name), None)
        allocation.append({
            'id': code,
            'name': name,
            'role_label': role_label,
            'team': TEAM_FOR[crew_role],
            'pod': pod,
            'role_bucket': 'leadership',
            'crew_role': crew_role,
            'avatar': avatar,
        })

    idx = 0
    for bucket, count, pod, crew_role in BUCKETS:
        for _ in range(count):
            if idx >= len(remaining):
                break
            e = remaining[idx]
            idx += 1
            allocation.append({
                'id': codes[e['name']],
                'name': e['name'],
                'role_label': bucket,
                'team': TEAM_FOR[crew_role],
                'pod': pod,
                'role_bucket': bucket,
                'crew_role': crew_role,
                'avatar': e['file'],
            })

    while idx < len(remaining):
        e = remaining[idx]
        idx += 1
        allocation.append({
            'id': codes[e['name']],
            'name': e['name'],
            'role_label': 'reserve',
            'team': TEAM_FOR['eng'],
            'pod': 'reserve',
            'role_bucket': 'reserve',
            'crew_role': 'eng',
            'avatar': e['file'],
        })

    os.makedirs('agents', exist_ok=True)
    with open(OUT, 'w', encoding='utf-8') as f:
        json.dump(allocation, f, ensure_ascii=False, indent=2)

    print(f"wrote {OUT} with {len(allocation)} entries")

    # Also emit a typed TypeScript module the UI can import without leaving
    # the Vite root (tsconfig only includes `src`).
    ts_path = 'src/forge/Forge.UI/src/agentAllocation.ts'
    with open(ts_path, 'w', encoding='utf-8') as f:
        f.write('// AUTO-GENERATED — do not hand-edit.\n')
        f.write('// Source: agents/allocation.json (regenerate via scripts/gen-crew-allocation.py)\n\n')
        f.write("export type Team = 'Architect' | 'Engineer' | 'Review' | 'QA';\n\n")
        f.write('export interface AllocationEntry {\n')
        f.write('  id: string;\n')
        f.write('  name: string;\n')
        f.write('  /** Display label (e.g. "Product Strategist", "code-generator"). */\n')
        f.write('  role_label: string;\n')
        f.write('  /** Canonical team — one of Architect / Engineer / Review / QA. */\n')
        f.write('  team: Team;\n')
        f.write('  /** Pod within the team: leadership | plan-design | coding | ai-worker | reserve. */\n')
        f.write('  pod: string;\n')
        f.write('  /** Fine-grained bucket inside the pod (e.g. plan-red-team, code-judge). */\n')
        f.write('  role_bucket: string;\n')
        f.write('  /** Underlying Phase-11 crew role: arch | eng | rev | qa | product-owner. */\n')
        f.write('  crew_role: string;\n')
        f.write('  avatar: string | null;\n')
        f.write('}\n\n')
        f.write('export const AGENT_ALLOCATION: AllocationEntry[] = ')
        f.write(json.dumps(allocation, ensure_ascii=False, indent=2))
        f.write(';\n')
    print(f"wrote {ts_path}")
    by_team = {}
    by_bucket = {}
    for a in allocation:
        by_team.setdefault(a['team'], 0)
        by_team[a['team']] += 1
        by_bucket.setdefault(a['role_bucket'], 0)
        by_bucket[a['role_bucket']] += 1
    print('teams:')
    for k in sorted(by_team):
        print(f"  {k}: {by_team[k]}")
    print('buckets:')
    for k in sorted(by_bucket):
        print(f"  {k}: {by_bucket[k]}")

    dups = [a['id'] for a in allocation]
    seen = set()
    bad = []
    for d in dups:
        if d in seen:
            bad.append(d)
        seen.add(d)
    if bad:
        print(f"!! duplicate ids: {bad}")
    else:
        print(f"all ids unique ({len(seen)})")


if __name__ == '__main__':
    main()
